// A system's shortcut, and starting it when you sign in, named exactly as each edition names them, so either
// app sees the other's. Each runs Try Linux with --start <id>.
//   Windows: "<display name>.lnk" on the desktop; "TryLinux-<id>" under HKCU\...\Run.
//   Linux:   trylinux-<id>.desktop in your apps (~/.local/share/applications); the same in ~/.config/autostart.
//   macOS:   no shortcut (the Mac edition has none); a login agent, io.github.donnysaengkio.trylinux.<id>.plist
//            in ~/Library/LaunchAgents.

#[cfg(windows)]
mod imp {
    use std::path::{Path, PathBuf};
    use std::process::Command;
    use std::sync::OnceLock;

    const RUN_KEY: &str = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";

    fn quiet(cmd: &mut Command) -> &mut Command {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
        cmd
    }

    // The desktop folder, wherever Windows keeps it (it may be in OneDrive), asked of Windows once.
    fn desktop() -> Option<PathBuf> {
        static DESKTOP: OnceLock<Option<PathBuf>> = OnceLock::new();
        DESKTOP
            .get_or_init(|| {
                let out = quiet(Command::new("powershell").args(["-NoProfile", "-NonInteractive", "-Command", "[Environment]::GetFolderPath('Desktop')"]))
                    .output()
                    .ok()?;
                let p = String::from_utf8_lossy(&out.stdout).trim().to_string();
                (!p.is_empty()).then(|| PathBuf::from(p))
            })
            .clone()
    }

    pub const HAS_SHORTCUTS: bool = true;

    fn shortcut_path(display_name: &str) -> Option<PathBuf> {
        let bad: &[char] = &['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
        Some(desktop()?.join(format!("{}.lnk", display_name.replace(bad, "_"))))
    }

    pub fn shortcut_exists(_id: &str, display_name: &str) -> bool {
        shortcut_path(display_name).is_some_and(|p| p.exists())
    }

    // Writes (or rewrites) the shortcut: it starts the system directly, or just opens Try Linux.
    pub fn write_shortcut(id: &str, display_name: &str, direct: bool) -> Result<(), String> {
        let path = shortcut_path(display_name).ok_or("Couldn't find your desktop folder.")?;
        let exe = std::env::current_exe().map_err(|e| e.to_string())?;
        write_link(&path, &exe, id, display_name, direct)
    }

    pub fn write_link(path: &Path, exe: &Path, id: &str, display_name: &str, direct: bool) -> Result<(), String> {
        let dir = exe.parent().map(|d| d.to_string_lossy().to_string()).unwrap_or_default();
        // The values go in as environment variables, so no quoting can break the script.
        let script = "$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:TL_LNK); \
                      $s.TargetPath = $env:TL_EXE; $s.Arguments = $env:TL_ARGS; $s.WorkingDirectory = $env:TL_DIR; \
                      $s.IconLocation = $env:TL_EXE + ',0'; $s.Description = $env:TL_DESC; $s.Save()";
        let out = quiet(Command::new("powershell").args(["-NoProfile", "-NonInteractive", "-Command", script]))
            .env("TL_LNK", path)
            .env("TL_EXE", exe)
            .env("TL_ARGS", if direct { format!("--start {id}") } else { String::new() })
            .env("TL_DIR", dir)
            .env("TL_DESC", if direct { format!("Start {display_name}") } else { format!("Open Try Linux for {display_name}") })
            .output()
            .map_err(|e| e.to_string())?;
        if out.status.success() && path.exists() {
            Ok(())
        } else {
            Err(format!("Couldn't make the shortcut: {}", String::from_utf8_lossy(&out.stderr).trim()))
        }
    }

    pub fn remove_shortcut(_id: &str, display_name: &str) {
        if let Some(p) = shortcut_path(display_name) {
            let _ = std::fs::remove_file(p);
        }
    }

    pub fn runs_at_sign_in(id: &str) -> bool {
        quiet(Command::new("reg").args(["query", RUN_KEY, "/v", &format!("TryLinux-{id}")]))
            .output()
            .is_ok_and(|o| o.status.success())
    }

    pub fn set_run_at_sign_in(id: &str, _display_name: &str, on: bool) -> Result<(), String> {
        if on == runs_at_sign_in(id) {
            return Ok(());
        }
        let name = format!("TryLinux-{id}");
        let out = if on {
            let exe = std::env::current_exe().map_err(|e| e.to_string())?;
            let value = format!("\"{}\" --start {id}", exe.to_string_lossy());
            quiet(Command::new("reg").args(["add", RUN_KEY, "/v", &name, "/t", "REG_SZ", "/d", &value, "/f"])).output()
        } else {
            quiet(Command::new("reg").args(["delete", RUN_KEY, "/v", &name, "/f"])).output()
        };
        match out {
            Ok(o) if o.status.success() => Ok(()),
            Ok(o) => Err(String::from_utf8_lossy(&o.stderr).trim().to_string()),
            Err(e) => Err(e.to_string()),
        }
    }
}

#[cfg(all(unix, not(target_os = "macos")))]
mod imp {
    use std::fs;
    use std::path::{Path, PathBuf};

    pub const HAS_SHORTCUTS: bool = true;

    fn xdg(var: &str, default: &str) -> PathBuf {
        match std::env::var(var) {
            Ok(v) if Path::new(&v).is_absolute() => PathBuf::from(v),
            _ => std::env::var_os("HOME").map(PathBuf::from).unwrap_or_default().join(default),
        }
    }
    fn launcher(id: &str) -> PathBuf {
        xdg("XDG_DATA_HOME", ".local/share").join("applications").join(format!("trylinux-{id}.desktop"))
    }
    fn autostart(id: &str) -> PathBuf {
        xdg("XDG_CONFIG_HOME", ".config").join("autostart").join(format!("trylinux-{id}.desktop"))
    }

    // The entry both use, as the Linux edition writes it (Exec quoted, as the path may have spaces).
    pub fn entry(id: &str, display_name: &str, direct: bool) -> Result<String, String> {
        let exe = std::env::current_exe().map_err(|e| e.to_string())?;
        let exec = format!("\"{}\"{}", exe.to_string_lossy().replace('"', "\\\""), if direct { format!(" --start {id}") } else { String::new() });
        let name = display_name.replace('\n', " ");
        Ok(format!("[Desktop Entry]\nType=Application\nName={name}\nComment={} {name} in Try Linux\nExec={exec}\n\
                    Icon=io.github.donnysaengkio.TryLinux\nCategories=System;Emulator;\n", if direct { "Start" } else { "Open" }))
    }
    fn write(path: &Path, text: &str) -> Result<(), String> {
        if let Some(p) = path.parent() {
            fs::create_dir_all(p).map_err(|e| e.to_string())?;
        }
        fs::write(path, text).map_err(|e| format!("Couldn't write {}: {e}", path.to_string_lossy()))
    }

    pub fn shortcut_exists(id: &str, _display_name: &str) -> bool {
        launcher(id).exists()
    }
    pub fn write_shortcut(id: &str, display_name: &str, direct: bool) -> Result<(), String> {
        write(&launcher(id), &entry(id, display_name, direct)?)
    }
    pub fn remove_shortcut(id: &str, _display_name: &str) {
        let _ = fs::remove_file(launcher(id));
    }
    pub fn runs_at_sign_in(id: &str) -> bool {
        autostart(id).exists()
    }
    pub fn set_run_at_sign_in(id: &str, display_name: &str, on: bool) -> Result<(), String> {
        if on {
            write(&autostart(id), &entry(id, display_name, true)?)
        } else {
            let _ = fs::remove_file(autostart(id));
            Ok(())
        }
    }
}

#[cfg(target_os = "macos")]
mod imp {
    use std::fs;
    use std::path::PathBuf;

    pub const HAS_SHORTCUTS: bool = false;

    fn agent(id: &str) -> PathBuf {
        std::env::var_os("HOME").map(PathBuf::from).unwrap_or_default()
            .join("Library").join("LaunchAgents").join(format!("io.github.donnysaengkio.trylinux.{id}.plist"))
    }
    fn xml(s: &str) -> String {
        s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;")
    }

    pub fn shortcut_exists(_id: &str, _display_name: &str) -> bool {
        false
    }
    pub fn write_shortcut(_id: &str, _display_name: &str, _direct: bool) -> Result<(), String> {
        Err("Shortcuts aren't available on macOS. Keep Try Linux in the Dock instead.".into())
    }
    pub fn remove_shortcut(_id: &str, _display_name: &str) {}
    pub fn runs_at_sign_in(id: &str) -> bool {
        agent(id).exists()
    }
    // A login agent that opens Try Linux with --start <id> when you log in.
    pub fn set_run_at_sign_in(id: &str, _display_name: &str, on: bool) -> Result<(), String> {
        let path = agent(id);
        if !on {
            let _ = fs::remove_file(&path);
            return Ok(());
        }
        let exe = std::env::current_exe().map_err(|e| e.to_string())?;
        let plist = format!("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\
            <!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n\
            <plist version=\"1.0\"><dict>\n<key>Label</key><string>io.github.donnysaengkio.trylinux.{0}</string>\n\
            <key>ProgramArguments</key><array><string>{1}</string><string>--start</string><string>{0}</string></array>\n\
            <key>RunAtLoad</key><true/>\n</dict></plist>\n", xml(id), xml(&exe.to_string_lossy()));
        if let Some(p) = path.parent() {
            fs::create_dir_all(p).map_err(|e| e.to_string())?;
        }
        fs::write(&path, plist).map_err(|e| e.to_string())
    }
}

pub use imp::*;

// The system to start from the command line (--start <id>), if any.
pub fn start_arg(args: &[String]) -> Option<String> {
    args.windows(2).find(|w| w[0] == "--start").map(|w| w[1].clone())
}

#[cfg(test)]
mod tests {
    #[test]
    fn reads_start_arg() {
        let a = |v: &[&str]| v.iter().map(|s| s.to_string()).collect::<Vec<_>>();
        assert_eq!(super::start_arg(&a(&["tl.exe", "--start", "fedora"])), Some("fedora".into()));
        assert_eq!(super::start_arg(&a(&["tl.exe"])), None);
        assert_eq!(super::start_arg(&a(&["tl.exe", "--start"])), None);
    }

    // Writes a shortcut into a scratch folder (never the desktop) and reads it back.
    // TRYLINUX_TEST_DIR=folder cargo test writes_link -- --ignored --nocapture
    #[cfg(windows)]
    #[test]
    #[ignore]
    fn writes_link() {
        let dir = std::path::PathBuf::from(std::env::var("TRYLINUX_TEST_DIR").unwrap());
        let lnk = dir.join("Fedora's Test & Co.lnk");
        let exe = std::env::current_exe().unwrap();
        super::write_link(&lnk, &exe, "fedora-test", "Fedora's \"Test\" & Co", true).unwrap();
        let out = std::process::Command::new("powershell")
            .args(["-NoProfile", "-Command", "$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:L); $s.TargetPath; $s.Arguments; $s.Description"])
            .env("L", &lnk)
            .output()
            .unwrap();
        println!("{}", String::from_utf8_lossy(&out.stdout));
    }
}
