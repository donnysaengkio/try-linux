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
}

impl Running {
    // Its control (QMP) port.
    pub fn port(&self) -> u16 {
        self.qmp
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

// A path inside a -drive or -fsdev option, where a comma is written twice.
#[cfg(all(unix, not(target_os = "macos")))]
fn esc(p: &str) -> String {
    p.replace(',', ",,")
}

fn free_port() -> u16 {
    TcpListener::bind("127.0.0.1:0").and_then(|l| l.local_addr()).map(|a| a.port()).unwrap_or(0)
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
    let cpu = cpu.unwrap_or(cfg.render() == 2);
    let installer = cfg.pending() && cfg.iso().exists();
    let (qmp_port, events, agent) = (free_port(), free_port(), free_port());
    let args = arguments(cfg, host, cpu, installer, held, qmp_port, events, agent);

    let mut cmd = Command::new(&bin);
    cmd.args(&args).current_dir(&cfg.dir).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::piped());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    let mut child: Child = cmd.spawn().map_err(|e| problem("Couldn't start QEMU", e.to_string()))?;

    // QEMU's error output, for the message if it fails to start.
    let err = Arc::new(Mutex::new(String::new()));
    if let Some(stderr) = child.stderr.take() {
        let err = err.clone();
        thread::spawn(move || {
            for line in BufReader::new(stderr).lines().map_while(Result::ok) {
                let mut e = err.lock().unwrap();
                e.push_str(&line);
                e.push('\n');
            }
        });
    }
    // Why QEMU stopped, from its SHUTDOWN event: "guest-shutdown" for a power-off, "guest-reset" for a restart.
    let reason = Arc::new(Mutex::new(None));
    {
        let reason = reason.clone();
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
    }

    let pid = child.id();
    vms.0.lock().unwrap().insert(
        id.to_string(),
        Running {
            pid, qmp: qmp_port, paused: false, held, cpu, installer, started: Instant::now(),
            restart_asked: None, reset_asked: false, force_stopped: false, reason, err, window: 0, hidden: false,
        },
    );
    crate::changed(app);
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

// A system's window to the front, shown again if it was hidden.
pub fn show_window(app: &AppHandle, vms: &Vms, id: &str) {
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
    // An installer session that ran a while has most likely installed the system.
    if vm.installer && secs > 60 && !vm.held {
        if let Some(c) = &cfg {
            c.set("pending", "0");
        }
    }
    let reason = vm.reason.lock().unwrap().clone().unwrap_or_default();
    let restart_asked = reason == "guest-shutdown" && vm.restart_asked.map(|t| t.elapsed() < Duration::from_secs(300)).unwrap_or(false);
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
