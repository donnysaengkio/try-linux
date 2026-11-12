// What this PC needs before Linux can run, as the Windows edition checks it: hardware virtualization (in the
// processor and turned on in the BIOS/UEFI), Windows Hypervisor Platform (turned on, and running after a
// restart), and WINQ-EMU (installed from its GitHub releases). Checking needs no admin rights; turning the
// feature on and installing WINQ-EMU ask Windows for permission.

use serde::Serialize;
use std::process::Command;
use std::sync::atomic::{AtomicBool, Ordering};

// Turned on in this session; it runs after the next restart, so it isn't asked again until then.
static RESTART_PENDING: AtomicBool = AtomicBool::new(false);

fn quiet(cmd: &mut Command) -> &mut Command {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000);
    }
    cmd
}

// ---------------------------------------------------------------- the hypervisor, through WinHvPlatform.dll

#[cfg(windows)]
mod whp {
    #[link(name = "kernel32")]
    extern "system" {
        fn LoadLibraryW(name: *const u16) -> isize;
        fn GetProcAddress(module: isize, name: *const u8) -> isize;
    }
    type GetCapability = unsafe extern "system" fn(code: u32, buf: *mut i32, size: u32, written: *mut u32) -> i32;
    type CreatePartition = unsafe extern "system" fn(partition: *mut isize) -> i32;
    type DeletePartition = unsafe extern "system" fn(partition: isize) -> i32;

    // Loaded when asked for, as a PC without the feature hasn't got the DLL.
    fn proc(name: &str) -> Option<isize> {
        let dll: Vec<u16> = "WinHvPlatform.dll".encode_utf16().chain([0]).collect();
        let module = unsafe { LoadLibraryW(dll.as_ptr()) };
        if module == 0 {
            return None;
        }
        let f = unsafe { GetProcAddress(module, format!("{name}\0").as_ptr()) };
        (f != 0).then_some(f)
    }

    // A hypervisor is running at all (Hyper-V counts, even without the platform's API).
    pub fn present() -> bool {
        let Some(f) = proc("WHvGetCapability") else { return false };
        let get: GetCapability = unsafe { std::mem::transmute(f) };
        let (mut present, mut written) = (0i32, 0u32);
        unsafe { get(0, &mut present, 4, &mut written) >= 0 && present != 0 }
    }

    // QEMU can use the platform: an empty VM partition can be made (and is deleted at once).
    pub fn ready() -> bool {
        if !present() {
            return false;
        }
        let (Some(c), Some(d)) = (proc("WHvCreatePartition"), proc("WHvDeletePartition")) else { return false };
        let (create, delete): (CreatePartition, DeletePartition) = unsafe { (std::mem::transmute(c), std::mem::transmute(d)) };
        let mut part = 0isize;
        if unsafe { create(&mut part) } < 0 {
            return false;
        }
        unsafe { delete(part) };
        true
    }
}
#[cfg(not(windows))]
mod whp {
    pub fn present() -> bool { true }
    pub fn ready() -> bool { true }
}

pub const BIOS_STEPS: &str = "To turn it on:\n\
1. Open Settings > System > Recovery, and next to Advanced startup click Restart now.\n\
2. Choose Troubleshoot > Advanced options > UEFI Firmware Settings > Restart.\n   (Or press Del, F2, F10 or Esc while the PC starts.)\n\
3. Turn on Intel Virtualization Technology (VT-x) or AMD SVM Mode. It's often under Advanced, CPU Configuration or Security.\n\
4. Save, exit, and open Try Linux again.";

#[derive(Serialize, Clone, Copy, PartialEq, Debug)]
#[serde(rename_all = "kebab-case")]
pub enum Virt {
    Ready,
    NoCpu,
    FirmwareOff,
    FeatureOff,
    NotRunning,
    KvmMissing, // Linux: no /dev/kvm (virtualization off in the firmware, or not offered by the kernel)
    KvmDenied,  // Linux: /dev/kvm is there, but you're not in the kvm group
    MacUnsupported, // macOS: needs Apple silicon and macOS 13 or later
}

// ---------------------------------------------------------------- Linux: KVM and QEMU

// You can use KVM: /dev/kvm opens for reading and writing.
pub fn kvm_usable() -> bool {
    cfg!(all(unix, not(target_os = "macos"))) && std::fs::OpenOptions::new().read(true).write(true).open("/dev/kvm").is_ok()
}

// The packages to install QEMU (and ARM64 firmware) with, for the usual distributions, as the Linux edition says.
pub fn install_hint() -> String {
    let release = std::fs::read_to_string("/etc/os-release").unwrap_or_default().to_lowercase();
    let like = release.lines().filter(|l| l.starts_with("id=") || l.starts_with("id_like=")).collect::<Vec<_>>().join(" ").replace('"', "");
    let arm = crate::vm::ARCH == "aarch64";
    if ["fedora", "rhel", "centos"].iter().any(|d| like.contains(d)) {
        format!("sudo dnf install qemu-kvm qemu-img qemu-ui-gtk qemu-ui-opengl qemu-audio-pipewire {}",
            if arm { "qemu-device-display-virtio-gpu-gl edk2-aarch64" } else { "qemu-device-display-virtio-vga-gl" })
    } else if like.contains("arch") {
        format!("sudo pacman -S qemu-desktop{}", if arm { " edk2-aarch64" } else { "" })
    } else if like.contains("suse") {
        format!("sudo zypper install qemu-kvm qemu-tools qemu-ui-gtk qemu-ui-opengl qemu-audio-pipewire{}", if arm { " qemu-uefi-aarch64" } else { "" })
    } else {
        format!("sudo apt install qemu-utils qemu-system-gui {}", if arm { "qemu-system-arm qemu-efi-aarch64" } else { "qemu-system-x86" })
    }
}

// ---------------------------------------------------------------- macOS: Apple silicon and macOS 13

fn mac_supported() -> bool {
    if !cfg!(target_os = "macos") || !cfg!(target_arch = "aarch64") {
        return false;
    }
    let out = Command::new("sw_vers").arg("-productVersion").output().map(|o| String::from_utf8_lossy(&o.stdout).to_string()).unwrap_or_default();
    out.trim().split('.').next().and_then(|m| m.parse::<u32>().ok()).is_some_and(|m| m >= 13)
}

impl Virt {
    pub fn message(self) -> String {
        match self {
            Virt::Ready => String::new(),
            Virt::NoCpu => "This PC's processor doesn't support hardware virtualization, which Try Linux needs to run Linux.".into(),
            Virt::FirmwareOff => format!("Hardware virtualization is turned off in your PC's BIOS/UEFI settings, so Linux can't run yet.\n\n{BIOS_STEPS}"),
            Virt::FeatureOff => "Windows Hypervisor Platform is turned off. Turn it on in 'Turn Windows features on or off', restart, then try again.".into(),
            Virt::NotRunning => format!("Windows Hypervisor Platform is turned on but isn't running yet. Restart Windows, then try again.\n\n\
                If this message comes back after restarting, hardware virtualization is probably turned off in your PC's BIOS/UEFI settings.\n\n{BIOS_STEPS}"),
            Virt::KvmMissing => if crate::vm::ARCH == "x86_64" {
                "KVM isn't available, so Linux can't run at full speed. Turn on virtualization (Intel VT-x or AMD-V) in your PC's firmware settings, then restart.".into()
            } else {
                "This PC's kernel doesn't offer KVM, so Linux can't run at full speed.".into()
            },
            Virt::KvmDenied => "You don't have access to KVM (/dev/kvm). Add yourself to the kvm group (sudo usermod -aG kvm $USER), then sign out and back in.".into(),
            Virt::MacUnsupported => "This Mac can't run virtual machines. Try Linux needs a Mac with Apple silicon (M1 or later) running macOS 13 or later.".into(),
        }
    }
}

// The processor's virtualization flags and the feature's state, from Windows (no admin rights needed).
// Each is None when Windows doesn't say.
fn wmi() -> (Option<bool>, Option<bool>, Option<bool>) {
    let script = "$p = Get-CimInstance Win32_Processor | Select-Object -First 1; \
                  $f = Get-CimInstance Win32_OptionalFeature -Filter \"Name='HypervisorPlatform'\"; \
                  \"$($p.VMMonitorModeExtensions),$($p.VirtualizationFirmwareEnabled),$(if ($f) { $f.InstallState } else { '' })\"";
    let out = quiet(Command::new("powershell").args(["-NoProfile", "-NonInteractive", "-Command", script])).output();
    let text = out.map(|o| String::from_utf8_lossy(&o.stdout).trim().to_string()).unwrap_or_default();
    let p: Vec<&str> = text.split(',').collect();
    let flag = |s: Option<&&str>| match s.map(|s| s.trim().to_lowercase()) {
        Some(s) if s == "true" => Some(true),
        Some(s) if s == "false" => Some(false),
        _ => None,
    };
    let feature = p.get(2).and_then(|s| s.trim().parse::<i32>().ok()).map(|n| n == 1);
    (flag(p.first()), flag(p.get(1)), feature)
}

// Why Linux can't run yet, worked out as each edition does: Windows Hypervisor Platform on Windows, KVM on
// Linux, Apple silicon and macOS 13 on a Mac.
pub fn virt() -> Virt {
    if cfg!(target_os = "macos") {
        return if mac_supported() { Virt::Ready } else { Virt::MacUnsupported };
    }
    if cfg!(unix) {
        let dev = std::path::Path::new("/dev/kvm");
        return if kvm_usable() { Virt::Ready } else if dev.exists() { Virt::KvmDenied } else { Virt::KvmMissing };
    }
    if whp::ready() {
        return Virt::Ready;
    }
    let (cpu, firmware, feature) = wmi();
    let feature_on = feature == Some(true) || RESTART_PENDING.load(Ordering::Relaxed);
    // A hypervisor (e.g. Hyper-V) is running, so the CPU and BIOS are fine; only the platform is missing.
    if whp::present() {
        return if feature_on { Virt::NotRunning } else { Virt::FeatureOff };
    }
    if cpu == Some(false) {
        return Virt::NoCpu;
    }
    if firmware == Some(false) {
        return Virt::FirmwareOff;
    }
    if feature_on { Virt::NotRunning } else { Virt::FeatureOff }
}

#[derive(Serialize)]
pub struct Readiness {
    virt: Virt,
    message: String,
    winq: bool,             // what runs systems is there: WINQ-EMU (Windows), QEMU (Linux), or always (macOS)
    can_install: bool,      // Try Linux can install it itself (WINQ-EMU, on Windows)
    install_message: String, // otherwise, how to install it
}

#[tauri::command(async)]
pub fn readiness() -> Readiness {
    let v = virt();
    let engine = cfg!(target_os = "macos") || crate::vm::qemu_system().is_some() && crate::vm::qemu_img().is_some();
    Readiness {
        virt: v,
        message: v.message(),
        winq: engine,
        can_install: cfg!(windows),
        install_message: if cfg!(windows) { String::new() } else { format!("Try Linux runs systems with QEMU, which isn't installed. Install it, then try again:\n\n{}", install_hint()) },
    }
}

// Turns Windows Hypervisor Platform on (Windows asks for permission). Ok(()) when it'll run after a restart.
#[tauri::command(async)]
pub fn enable_hypervisor() -> Result<(), String> {
    let script = "try { $p = Start-Process dism.exe -ArgumentList '/online','/enable-feature','/featurename:HypervisorPlatform','/all','/norestart','/quiet' \
                  -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ErrorAction Stop; exit $p.ExitCode } catch { exit 1223 }";
    let code = quiet(Command::new("powershell").args(["-NoProfile", "-NonInteractive", "-Command", script]))
        .status()
        .map_err(|e| e.to_string())?
        .code()
        .unwrap_or(-1);
    match code {
        0 | 3010 => {
            RESTART_PENDING.store(true, Ordering::Relaxed);
            Ok(())
        }
        1223 => Err("declined".into()), // the permission prompt was turned down
        c => Err(format!("Windows couldn't turn on Windows Hypervisor Platform (code {c}).\n\n\
            If hardware virtualization is turned off in your PC's BIOS/UEFI settings, turn it on there first.\n\n{BIOS_STEPS}")),
    }
}

// ---------------------------------------------------------------- WINQ-EMU

// The installer of WINQ-EMU's newest release (its -Setup.exe), from GitHub.
pub fn winq_installer_url() -> Result<String, String> {
    let text = ureq::get("https://api.github.com/repos/cmspam/winq-emu/releases/latest")
        .set("User-Agent", "TryLinux/1.0")
        .timeout(std::time::Duration::from_secs(30))
        .call()
        .map_err(|e| format!("Couldn't reach GitHub: {e}"))?
        .into_string()
        .map_err(|e| e.to_string())?;
    let json: serde_json::Value = serde_json::from_str(&text).map_err(|e| e.to_string())?;
    json["assets"].as_array().into_iter().flatten()
        .filter_map(|a| a["browser_download_url"].as_str())
        .find(|u| u.to_lowercase().ends_with("-setup.exe"))
        .map(String::from)
        .ok_or_else(|| "Couldn't find the WINQ-EMU installer on GitHub. Install it from github.com/cmspam/winq-emu/releases, then try again.".into())
}

// Downloads the installer into the downloads folder (resuming one already started), then runs it with
// Windows' permission and waits for it to finish. `progress` gets (status, 0 to 1 or -1, detail).
pub fn install_winq(progress: &mut dyn FnMut(&str, f64, &str), cancelled: &dyn Fn() -> bool) -> Result<(), String> {
    progress("Downloading WINQ-EMU", -1.0, "github.com/cmspam/winq-emu");
    let url = winq_installer_url()?;
    let dir = crate::paths::downloads_dir();
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let name = url.rsplit('/').next().unwrap_or("WINQ-EMU-Setup.exe").to_string();
    let file = dir.join(&name);
    if !file.exists() {
        let part = dir.join(format!("{name}.part"));
        crate::create::download_with(&url, &part, None, &mut |got, total, rate| {
            progress("Downloading WINQ-EMU", total.map_or(-1.0, |t| got as f64 / t as f64), &crate::create::transfer(got, total, rate))
        }, cancelled)?;
        std::fs::rename(&part, &file).map_err(|e| e.to_string())?;
    }
    progress("Installing WINQ-EMU", -1.0, "Approve the Windows prompt, then finish the installer");
    let script = "try { $p = Start-Process -FilePath $env:TL_SETUP -Verb RunAs -Wait -PassThru -ErrorAction Stop; exit $p.ExitCode } catch { exit 1223 }";
    let code = quiet(Command::new("powershell").args(["-NoProfile", "-NonInteractive", "-Command", script]))
        .env("TL_SETUP", &file)
        .status()
        .map_err(|e| e.to_string())?
        .code()
        .unwrap_or(-1);
    if code == 1223 {
        return Err("WINQ-EMU wasn't installed, so setup stopped.".into());
    }
    if crate::vm::winq_bin().is_none() {
        return Err("WINQ-EMU wasn't found in C:\\WINQ-EMU after installing. Install it there, then try again.".into());
    }
    Ok(())
}

#[derive(Serialize, Clone)]
struct ToolProgress {
    status: String,
    value: f64,
    detail: String,
}

// Check for Updates' Update WINQ-EMU: every system must be off, as the installer replaces QEMU itself.
#[tauri::command(async)]
pub fn update_winq(app: tauri::AppHandle, vms: tauri::State<crate::vm::Vms>) -> Result<String, String> {
    use tauri::Emitter;
    if !vms.0.lock().unwrap().is_empty() {
        return Err("Shut every system down before updating WINQ-EMU.".into());
    }
    install_winq(&mut |status, value, detail| {
        let _ = app.emit("tool-progress", ToolProgress { status: status.into(), value, detail: detail.into() });
    }, &|| false)?;
    Ok("WINQ-EMU is up to date.".into())
}

// Checks what this PC detects, without changing anything.
// cargo test detects_virtualization -- --ignored --nocapture
#[cfg(test)]
mod tests {
    #[test]
    #[ignore]
    fn detects_virtualization() {
        println!("present={} ready={} wmi={:?} state={:?}", super::whp::present(), super::whp::ready(), super::wmi(), super::virt());
        println!("winq installer: {:?}", super::winq_installer_url());
    }
}

// Downloads WINQ-EMU's installer into a scratch folder with the shared download code (never runs it).
// TRYLINUX_TEST_DIR=folder cargo test downloads_winq -- --ignored --nocapture
#[cfg(test)]
mod download_tests {
    #[test]
    #[ignore]
    fn downloads_winq() {
        let dir = std::path::PathBuf::from(std::env::var("TRYLINUX_TEST_DIR").unwrap());
        std::fs::create_dir_all(&dir).unwrap();
        let url = super::winq_installer_url().unwrap();
        let part = dir.join("winq-setup.exe.part");
        let _ = std::fs::remove_file(&part);
        let mut last = String::new();
        let r = crate::create::download_with(&url, &part, None, &mut |got, total, rate| last = crate::create::transfer(got, total, rate), &|| false);
        println!("result={r:?} size={:?} last progress: {last}", std::fs::metadata(&part).map(|m| m.len()));
        let _ = std::fs::remove_file(&part);
    }
}
