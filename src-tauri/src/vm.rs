// Running systems: starting QEMU (WINQ-EMU on Windows) the way the Windows edition does, the power actions
// over QMP, and what happens when QEMU ends (Linux restarted it, the installer finished, or it failed to start).

use crate::config::{Host, VmConfig};
use serde::Serialize;
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};
use tauri::{AppHandle, Emitter};

pub struct Running {
    pub pid: u32,
    qmp: u16,
    pub paused: bool,
    // Opened from Console while off (QEMU -S): the window is open, the machine isn't powered on.
    pub held: bool,
    cpu: bool,
    installer: bool,
    started: Instant,
    restart_asked: Option<Instant>,
    reset_asked: bool,
    force_stopped: bool,
    reason: Arc<Mutex<Option<String>>>,
    err: Arc<Mutex<String>>,
    // Its QEMU window once found (kept, as a hidden window can't be found again), and whether it's hidden.
    pub window: isize,
    pub hidden: bool,
    // Saving its state to close (Suspend), or loading a saved state (started from Suspended).
    pub suspending: bool,
    pub resuming: bool,
}

impl Running {
    // Its control (QMP) port.
    pub fn port(&self) -> u16 {
        self.qmp
    }

    // How long it has been running, in seconds.
    pub fn uptime(&self) -> u64 {
        self.started.elapsed().as_secs()
    }

    // A system run by Apple's framework (mac.rs): no QEMU process or QMP port, so no live readings.
    #[cfg(target_os = "macos")]
    pub fn mac(held: bool, installer: bool) -> Running {
        Running {
            pid: 0, qmp: 0, paused: false, held, cpu: false, installer, started: Instant::now(), restart_asked: None,
            reset_asked: false, force_stopped: false, reason: Arc::new(Mutex::new(None)), err: Arc::new(Mutex::new(String::new())),
            window: 0, hidden: false, suspending: false, resuming: false,
        }
    }
}

#[derive(Default)]
pub struct Vms(pub Mutex<HashMap<String, Running>>);

// What the interface is told when something goes wrong.
#[derive(Clone, Serialize)]
pub struct Problem {
    pub title: String,
    pub text: String,
}

// WINQ-EMU's folder (Windows): C:\WINQ-EMU or Program Files, with or without bin\.
pub fn winq_bin() -> Option<PathBuf> {
    if !cfg!(windows) {
        return None;
    }
    let pf = std::env::var("ProgramFiles").map(PathBuf::from).unwrap_or_default();
    for root in [PathBuf::from(r"C:\WINQ-EMU"), pf.join("WINQ-EMU")] {
        for dir in [root.join("bin"), root.clone()] {
            if dir.join("qemu-system-x86_64w.exe").exists() {
                return Some(dir);
            }
        }
    }
    None
}

// A program on the PATH (Linux, macOS).
fn which(name: &str) -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    std::env::split_paths(&path).map(|d| d.join(name)).find(|p| p.is_file())
}

// This computer's processor, as QEMU and distributions name it: x86_64 or aarch64.
pub const ARCH: &str = if cfg!(target_arch = "aarch64") { "aarch64" } else { "x86_64" };

// The QEMU that runs systems: WINQ-EMU's on Windows; on Linux the distribution's qemu-system-<arch> (or
// qemu-kvm, as Fedora and RHEL name it). macOS runs systems with Apple's framework instead.
pub fn qemu_system() -> Option<PathBuf> {
    if cfg!(windows) {
        return winq_bin().map(|b| b.join("qemu-system-x86_64w.exe"));
    }
    which(&format!("qemu-system-{ARCH}")).or_else(|| {
        let kvm = PathBuf::from("/usr/libexec/qemu-kvm");
        (ARCH == "x86_64" && kvm.is_file()).then_some(kvm)
    })
}

// qemu-img, for making, resizing, copying and checking disks.
pub fn qemu_img() -> Option<PathBuf> {
    if cfg!(windows) { winq_bin().map(|b| b.join("qemu-img.exe")) } else { which("qemu-img") }
}

// QEMU's version ("9.2.0"), for About.
pub fn qemu_version() -> Option<String> {
    let out = Command::new(qemu_system()?).arg("--version").output().ok()?;
    let text = String::from_utf8_lossy(&out.stdout).to_string();
    let i = text.find("version ")? + 8;
    Some(text[i..].split_whitespace().next()?.trim_end_matches(',').to_string())
}

// Sound on Linux: PipeWire or PulseAudio, whichever is running for you, or none (so the system still starts).
#[cfg(all(unix, not(target_os = "macos")))]
fn audio_backend() -> Option<&'static str> {
    let runtime = std::env::var("XDG_RUNTIME_DIR").map(PathBuf::from).ok()?;
    if runtime.join("pipewire-0").exists() {
        return Some("pipewire");
    }
    runtime.join("pulse").join("native").exists().then_some("pa")
}

// UEFI firmware for ARM64 systems (its code, and a template for its variables), where distributions install it.
#[cfg(all(unix, not(target_os = "macos")))]
fn arm_firmware() -> Option<(PathBuf, Option<PathBuf>)> {
    let pairs: [(&str, Option<&str>); 6] = [
        ("/usr/share/AAVMF/AAVMF_CODE.fd", Some("/usr/share/AAVMF/AAVMF_VARS.fd")),
        ("/usr/share/qemu-efi-aarch64/QEMU_EFI.fd", None),
        ("/usr/share/edk2/aarch64/QEMU_EFI-silent-pflash.raw", Some("/usr/share/edk2/aarch64/vars-template-pflash.raw")),
        ("/usr/share/edk2/aarch64/QEMU_CODE.fd", Some("/usr/share/edk2/aarch64/QEMU_VARS.fd")),
        ("/usr/share/qemu/edk2-aarch64-code.fd", Some("/usr/share/qemu/edk2-arm-vars.fd")),
        ("/usr/share/edk2-armvirt/aarch64/QEMU_CODE.fd", Some("/usr/share/edk2-armvirt/aarch64/QEMU_VARS.fd")),
    ];
    pairs.iter().find(|(c, _)| std::path::Path::new(c).exists())
        .map(|(c, v)| (PathBuf::from(c), v.map(PathBuf::from).filter(|p| p.exists())))
}

// UEFI firmware for x86_64 systems (Settings > Hardware > Firmware): the code, and the template for each system's
// own copy of its settings (efivars.fd). The distributions' OVMF packages on Linux; WINQ-EMU's own on Windows.
pub fn uefi_firmware() -> Option<(PathBuf, PathBuf)> {
    let mut pairs: Vec<(PathBuf, PathBuf)> = [
        ("/usr/share/OVMF/OVMF_CODE_4M.fd", "/usr/share/OVMF/OVMF_VARS_4M.fd"),
        ("/usr/share/OVMF/OVMF_CODE.fd", "/usr/share/OVMF/OVMF_VARS.fd"),
        ("/usr/share/edk2/ovmf/OVMF_CODE.fd", "/usr/share/edk2/ovmf/OVMF_VARS.fd"),
        ("/usr/share/edk2/x64/OVMF_CODE.4m.fd", "/usr/share/edk2/x64/OVMF_VARS.4m.fd"),
        ("/usr/share/edk2/x64/OVMF_CODE.fd", "/usr/share/edk2/x64/OVMF_VARS.fd"),
        ("/usr/share/qemu/ovmf-x86_64-code.bin", "/usr/share/qemu/ovmf-x86_64-vars.bin"),
        ("/usr/share/qemu/edk2-x86_64-code.fd", "/usr/share/qemu/edk2-i386-vars.fd"),
    ].iter().map(|(c, v)| (PathBuf::from(c), PathBuf::from(v))).collect();
    if let Some(bin) = winq_bin() {
        for share in [bin.join("share"), bin.join("..").join("share")] {
            pairs.insert(0, (share.join("edk2-x86_64-code.fd"), share.join("edk2-i386-vars.fd")));
        }
    }
    pairs.into_iter().find(|(c, v)| c.is_file() && v.is_file())
}

// Whether this QEMU can share the clipboard (its qemu-vdagent channel, which spice-vdagent in Linux talks to).
// WINQ-EMU is built without it. Asked once.
pub fn clipboard_available() -> bool {
    static ANSWER: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *ANSWER.get_or_init(|| {
        if cfg!(target_os = "macos") {
            return true; // Apple's framework shares it (mac.rs)
        }
        // On Windows the console build answers (the w build has no output).
        let bin = match winq_bin() {
            Some(dir) => dir.join("qemu-system-x86_64.exe"),
            None => match qemu_system() { Some(b) => b, None => return false },
        };
        let mut cmd = Command::new(bin);
        cmd.args(["-chardev", "help"]).stdin(Stdio::null());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            cmd.creation_flags(0x0800_0000);
        }
        cmd.output().map(|o| String::from_utf8_lossy(&o.stdout).contains("qemu-vdagent")).unwrap_or(false)
    })
}

// The options for UEFI (a system's own copy of the firmware's settings, made the first time) and for sharing the
// clipboard, for either computer's QEMU.
#[cfg(not(target_os = "macos"))]
fn uefi_and_clipboard(a: &mut Vec<String>, cfg: &VmConfig, quote: fn(&str) -> String) {
    if crate::firmware(cfg) == "UEFI" && ARCH == "x86_64" {
        if let Some((code, vars)) = uefi_firmware() {
            let own = cfg.dir.join("efivars.fd");
            if !own.exists() {
                let _ = std::fs::copy(&vars, &own);
            }
            add(a, "-drive", format!("if=pflash,format=raw,readonly=on,file={}", quote(&code.to_string_lossy())));
            add(a, "-drive", format!("if=pflash,format=raw,file={}", quote(&own.to_string_lossy())));
        }
    }
    if cfg.get("clipboard") != "0" && clipboard_available() {
        add(a, "-chardev", "qemu-vdagent,id=vdagent,clipboard=on".into());
        add(a, "-device", "virtserialport,chardev=vdagent,name=com.redhat.spice.0,id=vdagentport".into());
    }
}

// A path inside a -drive or -fsdev option, where a comma is written twice.
#[cfg(all(unix, not(target_os = "macos")))]
fn esc(p: &str) -> String {
    p.replace(',', ",,")
}

// Three different free ports for QEMU's channels: each is held until all three are chosen, so the computer can't
// hand out the same one twice.
fn free_ports() -> (u16, u16, u16) {
    let held: Vec<TcpListener> = (0..3).filter_map(|_| TcpListener::bind("127.0.0.1:0").ok()).collect();
    let port = |i: usize| held.get(i).and_then(|l| l.local_addr().ok()).map(|a| a.port()).unwrap_or(0);
    (port(0), port(1), port(2))
}

// A query's reply (its JSON), with optional arguments, on a fresh connection with short timeouts.
pub fn qmp_query(port: u16, command: &str, args: Option<&str>) -> Result<serde_json::Value, String> {
    let mut s = TcpStream::connect_timeout(&format!("127.0.0.1:{port}").parse().unwrap(), Duration::from_millis(500)).map_err(|e| e.to_string())?;
    s.set_read_timeout(Some(Duration::from_millis(1500))).ok();
    let mut r = BufReader::new(s.try_clone().map_err(|e| e.to_string())?);
    let mut line = String::new();
    r.read_line(&mut line).map_err(|e| e.to_string())?;
    s.write_all(b"{\"execute\":\"qmp_capabilities\"}\n").map_err(|e| e.to_string())?;
    line.clear();
    r.read_line(&mut line).map_err(|e| e.to_string())?;
    let a = args.map(|a| format!(",\"arguments\":{a}")).unwrap_or_default();
    s.write_all(format!("{{\"execute\":\"{command}\"{a}}}\n").as_bytes()).map_err(|e| e.to_string())?;
    loop {
        line.clear();
        if r.read_line(&mut line).map_err(|e| e.to_string())? == 0 || !line.contains("\"event\"") {
            break;
        }
    }
    serde_json::from_str(&line).map_err(|e| e.to_string())
}

// One QMP command on a fresh connection; QEMU's error, if it answers with one.
pub fn qmp(port: u16, command: &str) -> Result<(), String> {
    let mut s = TcpStream::connect_timeout(&format!("127.0.0.1:{port}").parse().unwrap(), Duration::from_secs(2)).map_err(|e| e.to_string())?;
    s.set_read_timeout(Some(Duration::from_secs(5))).ok();
    let mut r = BufReader::new(s.try_clone().map_err(|e| e.to_string())?);
    let mut line = String::new();
    r.read_line(&mut line).map_err(|e| e.to_string())?;
    s.write_all(b"{\"execute\":\"qmp_capabilities\"}\n").map_err(|e| e.to_string())?;
    line.clear();
    r.read_line(&mut line).map_err(|e| e.to_string())?;
    s.write_all(format!("{{\"execute\":\"{command}\"}}\n").as_bytes()).map_err(|e| e.to_string())?;
    loop {
        line.clear();
        if r.read_line(&mut line).map_err(|e| e.to_string())? == 0 || !line.contains("\"event\"") {
            break;
        }
    }
    if line.contains("\"error\"") {
        return Err(line.trim().to_string());
    }
    Ok(())
}

// QEMU's arguments on Linux, as the Linux edition gives them (KVM, its own GTK window, 3D acceleration unless
// CPU rendering, a SCSI CD/DVD drive, PipeWire or PulseAudio sound, UEFI on ARM64), with the same control
// channels as on Windows (QMP and the guest agent over local TCP, balloon statistics).
#[cfg(all(unix, not(target_os = "macos")))]
fn arguments(cfg: &VmConfig, host: &Host, cpu: bool, installer: bool, held: bool, qmp: u16, events: u16, agent: u16) -> Vec<String> {
    let (cores, ram) = cfg.resources(host);
    let arm = ARCH == "aarch64";
    let kvm = crate::setup::kvm_usable();
    let gl = !cpu;
    let mut a: Vec<String> = vec![];
    add(&mut a, "-name", cfg.name());
    if arm {
        add(&mut a, "-machine", format!("virt,accel={},gic-version=max", if kvm { "kvm" } else { "tcg" }));
    } else {
        add(&mut a, "-machine", format!("q35,accel={}", if kvm { "kvm" } else { "tcg" }));
    }
    add(&mut a, "-cpu", if kvm { "host" } else { "max" }.into());
    add(&mut a, "-smp", cores.to_string());
    add(&mut a, "-m", format!("{ram}G"));
    add(&mut a, "-display", format!("gtk{},window-close=off", if gl { ",gl=on" } else { "" }));
    let gpu = match (arm, gl) {
        (false, true) => "virtio-vga-gl",
        (false, false) => "virtio-vga",
        (true, true) => "virtio-gpu-gl-pci",
        (true, false) => "virtio-gpu-pci",
    };
    let outputs = if cfg.displays() > 1 { format!(",max_outputs={}", cfg.displays()) } else { String::new() };
    add(&mut a, "-device", format!("{gpu}{outputs}"));
    if arm {
        if let Some((code, vars)) = arm_firmware() {
            // The system's own copy of the UEFI variables, made the first time it starts.
            let own = cfg.dir.join("efivars.fd");
            if !own.exists() {
                let _ = match vars {
                    Some(t) => std::fs::copy(t, &own).map(|_| ()),
                    None => std::fs::File::create(&own).and_then(|f| f.set_len(std::fs::metadata(&code).map(|m| m.len()).unwrap_or(64 << 20))),
                };
            }
            add(&mut a, "-drive", format!("if=pflash,format=raw,readonly=on,file={}", esc(&code.to_string_lossy())));
            add(&mut a, "-drive", format!("if=pflash,format=raw,file={}", esc(&own.to_string_lossy())));
        }
    }
    // The boot disk first; a missing extra disk is left out.
    let discard = if cfg.reclaim() { ",discard=unmap,detect-zeroes=unmap" } else { "" };
    for (i, d) in cfg.all_disks().iter().enumerate() {
        if i == 0 || d.exists() {
            add(&mut a, "-drive", format!("file={},format=qcow2,if=none,id=hd{i}{discard}", esc(&d.to_string_lossy())));
            add(&mut a, "-device", format!("virtio-blk-pci,drive=hd{i}{}", if i == 0 { ",bootindex=1" } else { "" }));
        }
    }
    if cfg.net_type() == 2 {
        add(&mut a, "-nic", "none".into());
    } else {
        let restrict = if cfg.net_type() == 1 { ",restrict=on" } else { "" };
        let fwd: String = cfg.host_forwards().iter().map(|f| format!(",{f}")).collect();
        add(&mut a, "-netdev", format!("user,id=net0{restrict}{fwd}"));
        // ARM64 systems boot with UEFI, which doesn't need the network card's boot ROM (often not installed).
        add(&mut a, "-device", format!("virtio-net-pci,netdev=net0{}", if arm { ",romfile=" } else { "" }));
    }
    add(&mut a, "-device", "qemu-xhci".into());
    add(&mut a, "-device", "usb-tablet".into());
    if arm {
        add(&mut a, "-device", "usb-kbd".into());
    }
    // A CD/DVD drive whose disc can be changed while it runs (QMP id "cd"); the installer is in it, and boots
    // first, while installing.
    let disc = if installer { cfg.iso().to_string_lossy().to_string() } else { cfg.cd().to_string() };
    let file = if !disc.is_empty() && PathBuf::from(&disc).exists() { format!(",format=raw,file={}", esc(&disc)) } else { String::new() };
    add(&mut a, "-device", "virtio-scsi-pci,id=scsi0".into());
    add(&mut a, "-drive", format!("if=none,id=cd0,media=cdrom,readonly=on{file}"));
    add(&mut a, "-device", format!("scsi-cd,drive=cd0,id=cd{}", if installer { ",bootindex=0" } else { "" }));
    if cfg.sound() {
        if let Some(backend) = audio_backend() {
            add(&mut a, "-audiodev", format!("{backend},id=snd0"));
            add(&mut a, "-device", "intel-hda".into());
            add(&mut a, "-device", format!("{},audiodev=snd0", if cfg.mic() { "hda-duplex" } else { "hda-output" }));
        }
    }
    let shared = cfg.shared();
    if !shared.is_empty() && PathBuf::from(shared).is_dir() {
        let ro = if cfg.shared_write() { "" } else { ",readonly=on" };
        add(&mut a, "-virtfs", format!("local,path={},mount_tag=host,security_model=mapped-xattr,id=fs0{ro}", esc(shared)));
    }
    if cfg.fullscreen() && !held {
        a.push("-full-screen".into());
    }
    if held {
        a.push("-S".into());
    }
    add(&mut a, "-qmp", format!("tcp:127.0.0.1:{qmp},server=on,wait=off"));
    add(&mut a, "-qmp", format!("tcp:127.0.0.1:{events},server=on,wait=off"));
    add(&mut a, "-device", "virtio-serial".into());
    add(&mut a, "-chardev", format!("socket,id=qga0,host=127.0.0.1,port={agent},server=on,wait=off"));
    add(&mut a, "-device", "virtserialport,chardev=qga0,name=org.qemu.guest_agent.0".into());
    add(&mut a, "-device", "virtio-balloon-pci,id=balloon0".into());
    // Restarting Linux ends QEMU, and Try Linux starts it again (without the installer once it's installed).
    add(&mut a, "-action", "reboot=shutdown".into());
    uefi_and_clipboard(&mut a, cfg, esc);
    a
}

// QEMU's arguments, as the Windows edition gives them (WHPX, SDL window, virtio devices, QMP, guest agent,
// balloon statistics, and restarts ending QEMU so Try Linux starts it again cleanly).
#[cfg(not(all(unix, not(target_os = "macos"))))]
fn arguments(cfg: &VmConfig, host: &Host, cpu: bool, installer: bool, held: bool, qmp: u16, events: u16, agent: u16) -> Vec<String> {
    let (cores, ram) = cfg.resources(host);
    let outputs = if cfg.displays() > 1 { format!(",max_outputs={}", cfg.displays()) } else { String::new() };
    let gpu = if cpu {
        format!("virtio-vga{outputs}")
    } else if cfg.venus() {
        format!("virtio-vga-gl,blob=on,hostmem={}G,venus=on{outputs}", cfg.gpu_mem_gb())
    } else {
        format!("virtio-vga-gl{outputs}")
    };
    let mut a: Vec<String> = vec![];
    add(&mut a, "-machine", "q35,accel=whpx".into());
    add(&mut a, "-cpu", "host".into());
    add(&mut a, "-smp", cores.to_string());
    add(&mut a, "-m", format!("{ram}G"));
    add(&mut a, "-display", format!("{},window-close=off", if cpu { "sdl" } else { "sdl,gl=on" }));
    add(&mut a, "-device", gpu);
    // The boot disk first, so it's the one the BIOS starts from; a missing extra disk is left out.
    let boot = cfg.disk();
    for d in cfg.all_disks() {
        if d == boot || d.exists() {
            let extra = if cfg.reclaim() { ",discard=unmap,detect-zeroes=unmap" } else { "" };
            add(&mut a, "-drive", format!("file={},format=qcow2,if=virtio{extra}", d.display()));
        }
    }
    if cfg.net_type() == 2 {
        add(&mut a, "-nic", "none".into());
    } else {
        add(&mut a, "-device", "virtio-net-pci,netdev=net0".into());
    }
    a.push("-usb".into());
    add(&mut a, "-device", "usb-tablet".into());
    add(&mut a, "-name", cfg.name());
    add(&mut a, "-qmp", format!("tcp:127.0.0.1:{qmp},server=on,wait=off"));
    add(&mut a, "-qmp", format!("tcp:127.0.0.1:{events},server=on,wait=off"));
    add(&mut a, "-device", "virtio-serial".into());
    add(&mut a, "-chardev", format!("socket,id=qga0,host=127.0.0.1,port={agent},server=on,wait=off"));
    add(&mut a, "-device", "virtserialport,chardev=qga0,name=org.qemu.guest_agent.0".into());
    add(&mut a, "-device", "virtio-balloon-pci,id=balloon0".into());
    add(&mut a, "-action", "reboot=shutdown".into());
    uefi_and_clipboard(&mut a, cfg, |p| p.to_string());
    if cfg.net_type() != 2 {
        let restrict = if cfg.net_type() == 1 { ",restrict=on" } else { "" };
        let fwd: String = cfg.host_forwards().iter().map(|f| format!(",{f}")).collect();
        add(&mut a, "-netdev", format!("user,id=net0{restrict}{fwd}"));
    }
    // A CD/DVD drive whose disc can be changed while the system runs.
    if !installer {
        let disc = cfg.cd();
        let file = if !disc.is_empty() && PathBuf::from(disc).exists() { format!(",format=raw,file={disc}") } else { String::new() };
        add(&mut a, "-drive", format!("if=none,id=cd0,media=cdrom,readonly=on{file}"));
        add(&mut a, "-device", "ide-cd,drive=cd0,id=cd".into());
    }
    if cfg.sound() {
        add(&mut a, "-device", "intel-hda".into());
        add(&mut a, "-device", if cfg.mic() { "hda-duplex" } else { "hda-output" }.into());
    }
    if cfg.fullscreen() && !held {
        a.push("-full-screen".into());
    }
    if held {
        a.push("-S".into());
    }
    // BIOS boot; once=d boots the installer this time only, so the post-install reboot goes to disk.
    if installer {
        add(&mut a, "-cdrom", cfg.iso().display().to_string());
        add(&mut a, "-boot", "once=d".into());
    }
    let shared = cfg.shared();
    if !shared.is_empty() && PathBuf::from(shared).is_dir() {
        let ro = if cfg.shared_write() { "" } else { ",readonly=on" };
        add(&mut a, "-fsdev", format!("local,id=fs0,path={shared},security_model=mapped-xattr{ro}"));
        add(&mut a, "-device", "virtio-9p-pci,fsdev=fs0,mount_tag=host".into());
    }
    a
}

fn add(a: &mut Vec<String>, k: &str, v: String) {
    a.push(k.to_string());
    a.push(v);
}

// Starts a system. `cpu`: CPU rendering (None: as its settings say). `held`: window open, machine off.
pub fn start(app: &AppHandle, vms: &Vms, host: &Host, id: &str, cpu: Option<bool>, held: bool) -> Result<(), Problem> {
    let problem = |title: &str, text: String| Problem { title: title.into(), text };
    let cfg = crate::config::find(id).ok_or_else(|| problem("Couldn't start it", "This system's folder wasn't found.".into()))?;
    if vms.0.lock().unwrap().contains_key(id) {
        return Ok(());
    }
    // When it last ran, for Virtual Machines' Last Run column.
    cfg.set("lastrun", &chrono::Local::now().format("%Y-%m-%d %H:%M").to_string());
    if crate::running_elsewhere(&cfg) {
        return Err(problem(&format!("{} is already running", cfg.name()), "It was started by another copy of Try Linux. Use that one to control it.".into()));
    }
    // Linux needs hardware virtualization (and on Windows, Windows Hypervisor Platform); say what's missing
    // rather than letting it fail.
    let virt = crate::setup::virt();
    if virt != crate::setup::Virt::Ready {
        return Err(problem("Linux can't run on this computer yet", virt.message()));
    }
    // macOS runs systems with Apple's framework (mac.rs), not QEMU.
    #[cfg(target_os = "macos")]
    {
        let _ = (host, cpu);
        crate::mac::start(app, vms, &cfg, held).map_err(|e| problem(&format!("{} couldn't start", cfg.name()), e))
    }
    #[cfg(not(target_os = "macos"))]
    start_qemu(app, vms, host, &cfg, cpu, held)
}

#[cfg(not(target_os = "macos"))]
fn start_qemu(app: &AppHandle, vms: &Vms, host: &Host, cfg: &VmConfig, cpu: Option<bool>, held: bool) -> Result<(), Problem> {
    let problem = |title: &str, text: String| Problem { title: title.into(), text };
    let id = cfg.id.as_str();
    let bin = qemu_system().ok_or_else(|| if cfg!(windows) {
        problem("WINQ-EMU isn't installed", "Create a system to install it, or install it from github.com/cmspam/winq-emu/releases.".into())
    } else {
        problem("QEMU isn't installed", format!("Install it, then try again:\n\n{}", crate::setup::install_hint()))
    })?;
    if crate::firmware(cfg) == "UEFI" && ARCH == "x86_64" && uefi_firmware().is_none() {
        return Err(problem("UEFI firmware isn't installed", if cfg!(windows) {
            "WINQ-EMU's UEFI firmware (share\\edk2-x86_64-code.fd) wasn't found. Reinstall WINQ-EMU, or choose BIOS in Settings > Hardware > Firmware.".into()
        } else {
            "Install your distribution's OVMF package (ovmf, or edk2-ovmf), or choose BIOS in Settings > Hardware > Firmware.".into()
        }));
    }
    let mut cpu = cpu.unwrap_or(cfg.render() == 2);
    let mut installer = cfg.pending() && cfg.iso().exists();
    // Suspended: started exactly as it was (the same rendering and drives), loading its saved state.
    let saved = if held { None } else { suspended(cfg) };
    if let Some(s) = &saved {
        cpu = s["cpu"].as_bool().unwrap_or(cpu);
        installer = s["installer"].as_bool().unwrap_or(installer);
    }
    let (qmp_port, events, agent) = free_ports();
    let mut args = arguments(cfg, host, cpu, installer, held, qmp_port, events, agent);
    if saved.is_some() {
        // QEMU can't save the clipboard channel, so it was unplugged to suspend; it's plugged in again once resumed.
        if let Some(i) = args.iter().position(|a| a.starts_with("qemu-vdagent")) {
            args.drain(i - 1..=i);
        }
        if let Some(i) = args.iter().position(|a| a.contains("chardev=vdagent")) {
            args.drain(i - 1..=i);
        }
        args.push("-incoming".into());
        args.push(format!("file:{}", state_file(&cfg.dir).to_string_lossy().replace('\\', "/")));
    }

    let mut cmd = Command::new(&bin);
    cmd.args(&args).current_dir(&cfg.dir).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::piped());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    crate::log::started(&cfg.dir, &format!("{} {}: {}, {} rendering{}", if saved.is_some() { "Resumed" } else { "Started" }, cfg.name(),
        if saved.is_some() { "where it was suspended" } else if installer { "from its installer" } else { "from its disk" }, if cpu { "CPU" } else { "GPU" },
        if held { ", console only (machine off)" } else { "" }));
    crate::log::line(&cfg.dir, &format!("Command: {} {}", bin.display(), args.join(" ")));
    let mut child: Child = cmd.spawn().map_err(|e| {
        crate::log::line(&cfg.dir, &format!("QEMU couldn't start: {e}"));
        problem("Couldn't start QEMU", e.to_string())
    })?;

    // QEMU's error output: in the log, and for the message if it fails to start.
    let err = Arc::new(Mutex::new(String::new()));
    if let Some(stderr) = child.stderr.take() {
        let err = err.clone();
        let dir = cfg.dir.clone();
        thread::spawn(move || {
            for line in BufReader::new(stderr).lines().map_while(Result::ok) {
                crate::log::line(&dir, &format!("QEMU: {line}"));
                let mut e = err.lock().unwrap();
                e.push_str(&line);
                e.push('\n');
            }
        });
    }
    let reason = watch_shutdown(events);
    let pid = child.id();
    vms.0.lock().unwrap().insert(
        id.to_string(),
        Running {
            pid, qmp: qmp_port, paused: false, held, cpu, installer, started: Instant::now(),
            restart_asked: None, reset_asked: false, force_stopped: false, reason, err, window: 0, hidden: false,
            suspending: false, resuming: saved.is_some(),
        },
    );
    crate::changed(app);
    if saved.is_some() {
        let (app, id, dir) = (app.clone(), id.to_string(), cfg.dir.clone());
        thread::spawn(move || finish_resume(&app, &id, &dir, qmp_port));
    }
    crate::window::watch_clicks({
        let app = app.clone();
        move |x, y| close_clicked(&app, x, y)
    });

    // When QEMU ends.
    let id2 = id.to_string();
    let app2 = app.clone();
    let (app, id) = (app.clone(), id.to_string());
    thread::spawn(move || {
        let status = child.wait();
        thread::sleep(Duration::from_millis(600)); // the SHUTDOWN event can arrive just after the exit
        exited(&app, &id, status.map(|s| s.success()).unwrap_or(false));
    });
    // The window, once QEMU shows it: kept, and brought to the front.
    thread::spawn(move || {
        use tauri::Manager;
        for _ in 0..240 {
            thread::sleep(Duration::from_millis(250));
            if let Some(h) = crate::window::find(pid) {
                if let Some(vm) = app2.state::<Vms>().0.lock().unwrap().get_mut(&id2) {
                    vm.window = h;
                }
                crate::window::front(h);
                break;
            }
        }
    });
    Ok(())
}

// Why QEMU stopped, from its SHUTDOWN event on its events channel: "guest-shutdown" for a power-off,
// "guest-reset" for a restart.
#[cfg(not(target_os = "macos"))]
fn watch_shutdown(events: u16) -> Arc<Mutex<Option<String>>> {
    let reason = Arc::new(Mutex::new(None));
    let out = reason.clone();
    thread::spawn(move || {
        for _ in 0..240 {
            if let Ok(s) = TcpStream::connect(format!("127.0.0.1:{events}")) {
                let mut w = s.try_clone().unwrap();
                let mut r = BufReader::new(s);
                let mut line = String::new();
                let _ = r.read_line(&mut line);
                let _ = w.write_all(b"{\"execute\":\"qmp_capabilities\"}\n");
                for l in r.lines().map_while(Result::ok) {
                    if l.contains("\"SHUTDOWN\"") {
                        if let Some(i) = l.find("\"reason\"") {
                            let v: String = l[i + 8..].split('"').nth(1).unwrap_or("").to_string();
                            *reason.lock().unwrap() = Some(v);
                        }
                    }
                }
                return;
            }
            thread::sleep(Duration::from_millis(250));
        }
    });
    out
}

// Whether a process is still running.
#[cfg(not(target_os = "macos"))]
fn alive(pid: u32) -> bool {
    let mut sys = sysinfo::System::new();
    let p = sysinfo::Pid::from_u32(pid);
    sys.refresh_processes(sysinfo::ProcessesToUpdate::Some(&[p]), true);
    sys.process(p).is_some()
}

// Takes over a system whose QEMU outlived the Try Linux that started it (closed, crashed, or replaced by an
// update): its control channels are on QEMU's command line, so the Power menu, the console, live activity and
// what happens when it stops (an installer finishing, Linux restarting) work as if it had been started here.
#[cfg(not(target_os = "macos"))]
pub fn adopt(app: &AppHandle, vms: &Vms, id: &str, pid: u32, args: &[String], running_for: Duration) {
    // -qmp tcp:127.0.0.1:PORT,... : the control channel, then the events channel.
    let ports: Vec<u16> = args
        .windows(2)
        .filter(|w| w[0] == "-qmp")
        .filter_map(|w| w[1].strip_prefix("tcp:127.0.0.1:").and_then(|r| r.split(',').next()).and_then(|p| p.parse().ok()))
        .collect();
    let Some(&control) = ports.first() else { return };
    // Started from its installer: the ISO as a CD that boots once (Windows) or first (Linux).
    let installer = args.iter().any(|a| a == "-cdrom" || a.contains("bootindex=0"));
    let cpu = !args.iter().any(|a| a.contains("gl=on"));
    let status = qmp_query(control, "query-status", None).ok().and_then(|v| v["return"]["status"].as_str().map(String::from)).unwrap_or_default();
    let reason = match ports.get(1) {
        Some(&events) => watch_shutdown(events),
        None => Arc::new(Mutex::new(None)),
    };
    // Its visible window, or one a closed Try Linux had hidden.
    let window = crate::window::find(pid).or_else(|| crate::window::find_any(pid));
    {
        let mut map = vms.0.lock().unwrap();
        if map.contains_key(id) {
            return;
        }
        if let Some(c) = crate::config::find(id) {
            crate::log::line(&c.dir, "Try Linux took it over: it kept running while Try Linux was closed.");
        }
        map.insert(id.to_string(), Running {
            pid, qmp: control, paused: status == "paused", held: status == "prelaunch", cpu, installer,
            started: Instant::now().checked_sub(running_for).unwrap_or_else(Instant::now),
            restart_asked: None, reset_asked: false, force_stopped: false, reason, err: Arc::new(Mutex::new(String::new())),
            window: window.unwrap_or(0), hidden: window.is_some_and(|h| !crate::window::visible(h)),
            suspending: false, resuming: false,
        });
    }
    crate::window::watch_clicks({
        let app = app.clone();
        move |x, y| close_clicked(&app, x, y)
    });
    // When it ends (there's no handle to wait on, so its process is watched).
    let (app, id) = (app.clone(), id.to_string());
    thread::spawn(move || {
        while alive(pid) {
            thread::sleep(Duration::from_secs(1));
        }
        thread::sleep(Duration::from_millis(600)); // the SHUTDOWN event can arrive just after the exit
        exited(&app, &id, true);
    });
}

// A system's window to the front, shown again if it was hidden.
pub fn show_window(app: &AppHandle, vms: &Vms, id: &str) {
    #[cfg(target_os = "macos")]
    {
        let _ = vms;
        crate::mac::show(app, id)
    }
    #[cfg(not(target_os = "macos"))]
    show_qemu_window(app, vms, id)
}

#[cfg(not(target_os = "macos"))]
fn show_qemu_window(app: &AppHandle, vms: &Vms, id: &str) {
    let h = {
        let mut map = vms.0.lock().unwrap();
        let Some(vm) = map.get_mut(id) else { return };
        if vm.window == 0 {
            vm.window = crate::window::find(vm.pid).unwrap_or(0);
        }
        vm.hidden = false;
        vm.window
    };
    crate::window::front(h);
    crate::changed(app);
}

// A click on a system window's close button hides the window; the system keeps running and Virtual
// Machines (and the tray) show it. A console opened while off just closes, as nothing is running.
fn close_clicked(app: &AppHandle, x: i32, y: i32) {
    use tauri::Manager;
    let vms = app.state::<Vms>();
    let hit = vms.0.lock().unwrap().iter()
        .find(|(_, vm)| vm.window != 0 && !vm.hidden && crate::window::on_close_button(vm.window, x, y))
        .map(|(id, vm)| (id.clone(), vm.held, vm.window));
    let Some((id, held, h)) = hit else { return };
    if held {
        let _ = power(app, &vms, &id, "close-console");
        return;
    }
    crate::window::hide(h);
    if let Some(vm) = vms.0.lock().unwrap().get_mut(&id) {
        vm.hidden = true;
    }
    crate::changed(app);
    crate::show_main(app);
}

fn exited(app: &AppHandle, id: &str, ok: bool) {
    use tauri::Manager;
    let vms = app.state::<Vms>();
    let Some(vm) = vms.0.lock().unwrap().remove(id) else { return };
    app.state::<crate::live::Live>().forget(id);
    let secs = vm.started.elapsed().as_secs();
    let cfg = crate::config::find(id);
    // Suspended: its state is saved, so it waits as it is.
    if vm.suspending && cfg.as_ref().is_some_and(|c| suspended(c).is_some()) {
        if let Some(c) = &cfg {
            crate::log::line(&c.dir, &format!("Suspended after {}", crate::log::duration(secs)));
        }
        crate::changed(app);
        if vms.0.lock().unwrap().is_empty() {
            crate::show_main(app);
        }
        return;
    }
    // Its saved state couldn't be loaded (its settings changed since, or QEMU changed): ask what to do.
    if vm.resuming {
        let msg = vm.err.lock().unwrap().trim().to_string();
        if let Some(c) = &cfg {
            crate::log::line(&c.dir, &format!("Couldn't resume: {}", if msg.is_empty() { "QEMU stopped" } else { &msg }));
        }
        crate::show_main(app);
        let _ = app.emit("resume-failed", Problem {
            title: cfg.as_ref().map(|c| c.name()).unwrap_or_else(|| id.to_string()),
            text: if msg.is_empty() { "QEMU stopped while loading where it was suspended.".into() } else { msg },
        });
        crate::changed(app);
        return;
    }
    // An installer session that ran a while has most likely installed the system.
    if vm.installer && secs > 60 && !vm.held {
        if let Some(c) = &cfg {
            c.set("pending", "0");
        }
    }
    let reason = vm.reason.lock().unwrap().clone().unwrap_or_default();
    let restart_asked = reason == "guest-shutdown" && vm.restart_asked.map(|t| t.elapsed() < Duration::from_secs(300)).unwrap_or(false);
    if let Some(c) = &cfg {
        let why = if vm.force_stopped { "Force stopped".to_string() }
            else if vm.reset_asked { "Reset".to_string() }
            else if reason == "guest-reset" || restart_asked { "Linux restarted".to_string() }
            else if reason == "guest-shutdown" { "Linux shut down".to_string() }
            else if !reason.is_empty() { format!("Stopped ({reason})") }
            else if ok { "QEMU closed".to_string() }
            else { "QEMU stopped with an error".to_string() };
        crate::log::line(&c.dir, &format!("{why} after {}", crate::log::duration(secs)));
    }
    let host = app.state::<crate::Probe>().host();
    if reason == "guest-reset" || restart_asked || vm.reset_asked {
        // Linux restarted (or Restart / Reset): start it again, with any changed settings.
        if let Err(p) = start(app, &vms, &host, id, Some(vm.cpu), false) {
            let _ = app.emit("problem", p);
        }
        return;
    }
    if !vm.force_stopped && !ok && secs < 20 {
        let msg = vm.err.lock().unwrap().trim().to_string();
        let msg = if msg.is_empty() { "QEMU stopped as it started.".to_string() } else { msg };
        let port_clash = msg.to_lowercase().contains("hostfwd") || msg.to_lowercase().contains("host forwarding");
        let automatic = cfg.as_ref().map(|c| c.render() == 0).unwrap_or(false);
        if !vm.cpu && automatic && !port_clash {
            // Automatic rendering: this PC couldn't use the GPU path, so try again on the CPU.
            if let Err(p) = start(app, &vms, &host, id, Some(true), vm.held) {
                let _ = app.emit("problem", p);
            }
            return;
        }
        let name = cfg.map(|c| c.name()).unwrap_or_else(|| id.to_string());
        let hint = if port_clash { "\n\nA forwarded port is already in use. Change the port forwards in Settings > Devices." } else { "" };
        crate::show_main(app);
        let _ = app.emit("problem", Problem { title: format!("{name} couldn't start"), text: format!("{msg}{hint}") });
    }
    crate::changed(app);
    // The last system stopped while Try Linux was in the tray: it comes back, as on the Windows edition.
    if vms.0.lock().unwrap().is_empty() {
        crate::show_main(app);
    }
}

// The Power menu's actions.
pub fn power(app: &AppHandle, vms: &Vms, id: &str, action: &str) -> Result<(), String> {
    if let Some(c) = crate::config::find(id) {
        crate::log::line(&c.dir, &format!("Power menu: {}", crate::log::action(action)));
    }
    #[cfg(target_os = "macos")]
    {
        if !vms.0.lock().unwrap().contains_key(id) {
            return Err("It isn't running.".into());
        }
        crate::mac::power(app, id, action)
    }
    #[cfg(not(target_os = "macos"))]
    qemu_power(app, vms, id, action)
}

#[cfg(not(target_os = "macos"))]
fn qemu_power(app: &AppHandle, vms: &Vms, id: &str, action: &str) -> Result<(), String> {
    let (port, pid, paused, window) = {
        let mut map = vms.0.lock().unwrap();
        let vm = map.get_mut(id).ok_or("It isn't running.")?;
        match action {
            "restart" => vm.restart_asked = Some(Instant::now()),
            "reset" => vm.reset_asked = true,
            "force" | "close-console" => vm.force_stopped = true,
            _ => {}
        }
        (vm.qmp, vm.pid, vm.paused, vm.window)
    };
    let set = |f: &dyn Fn(&mut Running)| {
        if let Some(vm) = vms.0.lock().unwrap().get_mut(id) {
            f(vm);
        }
    };
    match action {
        // Presses the power button: Linux shuts down cleanly (its desktop may ask first).
        "shutdown" | "restart" => {
            if paused {
                qmp(port, "cont")?;
            }
            // Some desktops ask before powering off, so the window must be showing to answer.
            if window != 0 {
                crate::window::front(window);
            }
            qmp(port, "system_powerdown")?;
            set(&|vm| {
                vm.paused = false;
                vm.hidden = false;
            });
        }
        "pause" => {
            qmp(port, "stop")?;
            set(&|vm| vm.paused = true);
        }
        "resume" => {
            qmp(port, "cont")?;
            set(&|vm| vm.paused = false);
        }
        "power-on" => {
            qmp(port, "cont")?;
            set(&|vm| {
                vm.held = false;
                vm.started = Instant::now();
            });
            if window != 0 {
                crate::window::front(window);
            }
        }
        // Ending QEMU and starting it again is a cleaner reset than resetting in place.
        "reset" | "close-console" => qmp(port, "quit")?,
        // Suspend: its state is saved to a file in its folder (off to the side, as a large memory takes a while),
        // then QEMU closes; Start resumes it.
        "suspend" => {
            set(&|vm| vm.suspending = true);
            crate::changed(app);
            let (app, id) = (app.clone(), id.to_string());
            thread::spawn(move || {
                if let Err(e) = suspend(&app, port, &id) {
                    use tauri::Manager;
                    if let Some(vm) = app.state::<Vms>().0.lock().unwrap().get_mut(&id) {
                        vm.suspending = false;
                    }
                    if let Some(c) = crate::config::find(&id) {
                        crate::log::line(&c.dir, &format!("Couldn't suspend it: {e}"));
                    }
                    let name = crate::config::find(&id).map(|c| c.name()).unwrap_or(id.clone());
                    let _ = app.emit("problem", Problem { title: format!("Couldn't suspend {name}"), text: e });
                    crate::changed(&app);
                }
            });
            return Ok(());
        }
        // Send Keys: keys the computer would keep for itself (Ctrl+Alt+Del, Ctrl+Alt+F1 to F6), pressed in Linux.
        a if a.starts_with("keys:") => {
            let keys: Vec<String> = a[5..].split('-').map(|k| format!("{{\"type\":\"qcode\",\"data\":\"{k}\"}}")).collect();
            let r = qmp_query(port, "send-key", Some(&format!("{{\"keys\":[{}]}}", keys.join(","))))?;
            if let Some(e) = r.get("error") {
                return Err(e["desc"].as_str().unwrap_or("QEMU didn't take the keys.").to_string());
            }
            return Ok(());
        }
        "force" => {
            #[cfg(windows)]
            let _ = Command::new("taskkill").args(["/PID", &pid.to_string(), "/F"]).creation_flags_none().status();
            #[cfg(all(unix, not(target_os = "macos")))]
            let _ = Command::new("kill").args(["-9", &pid.to_string()]).status();
        }
        _ => return Err(format!("Unknown action {action}")),
    }
    let _ = pid;
    crate::changed(app);
    Ok(())
}

// Quitting Try Linux: each running system as its Settings say (General > Display and startup): suspended,
// shut down (Linux is asked, and powers off by itself), or left running.
#[cfg(not(target_os = "macos"))]
pub fn before_quit(app: &AppHandle) {
    use tauri::Manager;
    let running: Vec<(String, u16)> = app.state::<Vms>().0.lock().unwrap().iter()
        .filter(|(_, r)| !r.held && !r.suspending && !r.resuming)
        .map(|(id, r)| (id.clone(), r.qmp)).collect();
    let mut waits = vec![];
    for (id, port) in running {
        let Some(cfg) = crate::config::find(&id) else { continue };
        match cfg.get("onquit") {
            "suspend" => {
                crate::log::line(&cfg.dir, "Try Linux is quitting: suspending it");
                let app = app.clone();
                waits.push(thread::spawn(move || {
                    if let Err(e) = suspend(&app, port, &id) {
                        if let Some(c) = crate::config::find(&id) {
                            crate::log::line(&c.dir, &format!("Couldn't suspend it, so it keeps running: {e}"));
                        }
                    }
                }));
            }
            "shutdown" => {
                crate::log::line(&cfg.dir, "Try Linux is quitting: shutting Linux down");
                let _ = qmp(port, "system_powerdown");
            }
            _ => {}
        }
    }
    for w in waits {
        let _ = w.join();
    }
}

// ---------------------------------------------------------------- Suspend and Resume

const STATE: &str = "suspend.state";
const STATE_INFO: &str = "suspend.json";

pub fn state_file(dir: &std::path::Path) -> PathBuf {
    dir.join(STATE)
}

// A suspended system's saved state, with how it was started (its rendering, whether from its installer).
pub fn suspended(cfg: &VmConfig) -> Option<serde_json::Value> {
    if !state_file(&cfg.dir).is_file() {
        return None;
    }
    std::fs::read_to_string(cfg.dir.join(STATE_INFO)).ok().and_then(|t| serde_json::from_str(&t).ok())
}

// Forgets a suspended state (Start fresh, Reset, or a snapshot reverted), so it starts from its disk.
pub fn discard_suspended(dir: &std::path::Path) {
    let _ = std::fs::remove_file(dir.join(STATE));
    let _ = std::fs::remove_file(dir.join(STATE_INFO));
    let _ = std::fs::remove_file(dir.join(format!("{STATE}.part")));
}

// Saves a running system's state: paused, written out with QEMU's migration to a file, then QEMU closes. On a
// problem (a device that can't be saved, such as a mounted shared folder or the GPU's Vulkan), it carries on.
#[cfg(not(target_os = "macos"))]
fn suspend(app: &AppHandle, port: u16, id: &str) -> Result<(), String> {
    use tauri::Manager;
    let cfg = crate::config::find(id).ok_or("Its folder wasn't found.")?;
    // How it was started, to resume it the same way.
    let (cpu, installer) = app.state::<Vms>().0.lock().unwrap().get(id).map(|r| (r.cpu, r.installer))
        .unwrap_or((cfg.render() == 2, cfg.pending() && cfg.iso().exists()));
    let part = cfg.dir.join(format!("{STATE}.part"));
    let _ = std::fs::remove_file(&part);
    let failed = |e: String| {
        let _ = qmp(port, "cont");
        let _ = std::fs::remove_file(&part);
        Err(e)
    };
    let desc = |v: &serde_json::Value| v["error"]["desc"].as_str().map(String::from);
    let r = qmp_query(port, "stop", None)?;
    if let Some(e) = desc(&r) {
        return failed(e);
    }
    // The clipboard channel can't be saved: unplugged now (it's plugged in again when it resumes).
    let clipboard = qmp_query(port, "device_del", Some(r#"{"id":"vdagentport"}"#)).is_ok_and(|r| r.get("return").is_some());
    if clipboard {
        thread::sleep(Duration::from_millis(300));
        let _ = qmp_query(port, "chardev-remove", Some(r#"{"id":"vdagent"}"#));
    }
    let failed = |e: String| {
        if clipboard {
            plug_clipboard(port);
        }
        failed(e)
    };
    let uri = serde_json::json!({ "uri": format!("file:{}", part.to_string_lossy().replace('\\', "/")) }).to_string();
    let r = qmp_query(port, "migrate", Some(&uri))?;
    if let Some(e) = desc(&r) {
        return failed(e);
    }
    // Saving takes about as long as writing its memory to disk.
    let since = Instant::now();
    loop {
        thread::sleep(Duration::from_millis(400));
        let r = qmp_query(port, "query-migrate", None).unwrap_or_default();
        match r["return"]["status"].as_str() {
            Some("completed") => break,
            Some("failed") | Some("cancelled") => {
                return failed(r["return"]["error-desc"].as_str().unwrap_or("Saving its state failed.").to_string());
            }
            _ if since.elapsed() > Duration::from_secs(1800) => return failed("Saving its state took too long.".into()),
            _ => {}
        }
    }
    let info = serde_json::json!({ "cpu": cpu, "installer": installer, "saved": chrono::Local::now().format("%Y-%m-%d %H:%M").to_string() });
    std::fs::write(cfg.dir.join(STATE_INFO), info.to_string()).map_err(|e| e.to_string())?;
    std::fs::rename(&part, state_file(&cfg.dir)).map_err(|e| e.to_string())?;
    let mb = std::fs::metadata(state_file(&cfg.dir)).map(|m| m.len() >> 20).unwrap_or(0);
    crate::log::line(&cfg.dir, &format!("Saved its state for Suspend ({mb} MB)"));
    // QEMU closes as it answers, so its answer may not arrive.
    let _ = qmp(port, "quit");
    Ok(())
}

// The clipboard channel, plugged in while it runs (after Resume, or when Suspend didn't go ahead).
#[cfg(not(target_os = "macos"))]
fn plug_clipboard(port: u16) {
    let _ = qmp_query(port, "chardev-add",
        Some(r#"{"id":"vdagent","backend":{"type":"qemu-vdagent","data":{"clipboard":true}}}"#));
    let _ = qmp_query(port, "device_add",
        Some(r#"{"driver":"virtserialport","chardev":"vdagent","name":"com.redhat.spice.0","id":"vdagentport"}"#));
}

// Resuming: once QEMU has loaded the saved state (it waits, paused), Linux carries on and the file goes.
#[cfg(not(target_os = "macos"))]
fn finish_resume(app: &AppHandle, id: &str, dir: &std::path::Path, port: u16) {
    use tauri::Manager;
    let since = Instant::now();
    while since.elapsed() < Duration::from_secs(1800) {
        thread::sleep(Duration::from_millis(400));
        if !app.state::<Vms>().0.lock().unwrap().contains_key(id) {
            return; // QEMU stopped; exited() says why
        }
        let r = qmp_query(port, "query-status", None).unwrap_or_default();
        match r["return"]["status"].as_str() {
            Some("inmigrate") | None => continue,
            Some("paused") | Some("postmigrate") | Some("prelaunch") => {
                let _ = qmp(port, "cont");
            }
            _ => {}
        }
        discard_suspended(dir);
        if let Some(vm) = app.state::<Vms>().0.lock().unwrap().get_mut(id) {
            vm.resuming = false;
        }
        if crate::config::find(id).is_some_and(|c| c.get("clipboard") != "0") && clipboard_available() {
            plug_clipboard(port);
        }
        crate::log::line(dir, "Resumed where it was suspended");
        crate::changed(app);
        return;
    }
}

// Take Screenshot: the system's screen as a PNG in Pictures\Try Linux (QEMU's screendump), named after it and
// the time. Returns the file's path.
#[cfg(not(target_os = "macos"))]
pub fn screenshot(vms: &Vms, id: &str, folder: PathBuf) -> Result<PathBuf, String> {
    let port = vms.0.lock().unwrap().get(id).map(|r| r.qmp).ok_or("It isn't running.")?;
    let name = crate::config::find(id).map(|c| c.name()).unwrap_or_else(|| id.to_string());
    let safe: String = name.chars().map(|c| if "\\/:*?\"<>|".contains(c) { '-' } else { c }).collect();
    std::fs::create_dir_all(&folder).map_err(|e| format!("Couldn't make {}: {e}", folder.display()))?;
    let file = folder.join(format!("{safe} {}.png", chrono::Local::now().format("%Y-%m-%d %H.%M.%S")));
    let args = serde_json::json!({ "filename": file.to_string_lossy(), "format": "png" }).to_string();
    let r = qmp_query(port, "screendump", Some(&args))?;
    if let Some(e) = r.get("error") {
        return Err(e["desc"].as_str().unwrap_or("QEMU couldn't take a screenshot.").to_string());
    }
    // QEMU writes it as it answers; wait a moment for the whole file.
    for _ in 0..30 {
        if std::fs::metadata(&file).is_ok_and(|m| m.len() > 0) {
            return Ok(file);
        }
        thread::sleep(Duration::from_millis(100));
    }
    Err("QEMU didn't save the screenshot.".into())
}

// Keeps taskkill from flashing a console window.
trait NoWindow {
    fn creation_flags_none(&mut self) -> &mut Self;
}
impl NoWindow for Command {
    fn creation_flags_none(&mut self) -> &mut Self {
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            self.creation_flags(0x0800_0000);
        }
        self
    }
}
