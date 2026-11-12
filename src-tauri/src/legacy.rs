// Taking over from the editions before 2.0, on each start until it's done. Systems, downloads and preferences
// are the same files, so they need nothing.
//
// Windows: 1.x is a single TryLinux.exe in %LOCALAPPDATA%\Programs\TryLinux. Its update to 2.0 puts this app's
// installer in its own place and runs it. Try Linux then points 1.x's desktop shortcuts and "start at sign-in"
// entries here, and removes its Start menu entry, its entry in Installed apps and its folder. Nothing happens
// while anything in that folder is still running.
//
// Linux: Try Linux is an AppImage, which installs nothing itself. Run as one, it puts itself in your apps menu
// (with its icon) and makes the try-linux command in ~/.local/bin start it, wherever the AppImage is; updates
// replace the file in place. 1.0.x was Python in ~/.local/share/try-linux, put there by its install.sh; its
// update to 2.0 is a stand-in that downloads the AppImage into that folder and starts it. Try Linux then points
// 1.0.x's apps-menu and sign-in entries for your systems at the AppImage and removes the Python files.

#[cfg(windows)]
mod imp {
    use std::path::{Path, PathBuf};
    use std::process::Command;
    use std::time::Duration;

    // The folder the old edition installed itself in.
    fn old_dir() -> Option<PathBuf> {
        Some(PathBuf::from(std::env::var_os("LOCALAPPDATA")?).join("Programs").join("TryLinux"))
    }

    // True while a program from `dir` runs: the old edition, or the installer it started.
    fn running_from(dir: &Path) -> bool {
        use sysinfo::{ProcessRefreshKind, ProcessesToUpdate, System, UpdateKind};
        let mut sys = System::new();
        sys.refresh_processes_specifics(ProcessesToUpdate::All, true, ProcessRefreshKind::new().with_exe(UpdateKind::OnlyIfNotSet));
        let dir = dir.to_string_lossy().to_lowercase();
        sys.processes().values().any(|p| p.exe().is_some_and(|e| e.to_string_lossy().to_lowercase().starts_with(&dir)))
    }

    // In the background on start: waits (up to two minutes) for the old folder's programs to close, then takes over.
    pub fn take_over() {
        let (Some(dir), Ok(exe)) = (old_dir(), std::env::current_exe()) else { return };
        if !dir.join("TryLinux.exe").exists() || exe.starts_with(&dir) {
            return;
        }
        std::thread::spawn(move || {
            for _ in 0..24 {
                if !running_from(&dir) {
                    let _ = take_over_from(&dir, &exe, None, None, None);
                    return;
                }
                std::thread::sleep(Duration::from_secs(5));
            }
        });
    }

    // Does the takeover and lists what it changed. `desktop`, `start_menu` and `registry` (a key under HKCU
    // holding Run and Uninstall) are Windows' own unless given, for tests.
    pub fn take_over_from(old: &Path, exe: &Path, desktop: Option<&Path>, start_menu: Option<&Path>, registry: Option<&str>) -> Result<Vec<String>, String> {
        // The values go in as environment variables, so no quoting can break the script.
        let script = r#"
$ErrorActionPreference = 'Continue'
$old = Join-Path $env:TL_OLD 'TryLinux.exe'
$exe = $env:TL_EXE
$desktop = if ($env:TL_DESKTOP) { $env:TL_DESKTOP } else { [Environment]::GetFolderPath('Desktop') }
$menu = if ($env:TL_MENU) { $env:TL_MENU } else { [Environment]::GetFolderPath('Programs') }
$reg = if ($env:TL_REG) { 'HKCU:\' + $env:TL_REG } else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion' }
function Same($a, $b) { try { [IO.Path]::GetFullPath($a.Trim('"')) -ieq [IO.Path]::GetFullPath($b.Trim('"')) } catch { $false } }
$sh = New-Object -ComObject WScript.Shell
foreach ($f in Get-ChildItem -LiteralPath $desktop -Filter *.lnk -ErrorAction SilentlyContinue) {
    $l = $sh.CreateShortcut($f.FullName)
    if (Same $l.TargetPath $old) {
        $l.TargetPath = $exe; $l.WorkingDirectory = Split-Path $exe; $l.IconLocation = $exe + ',0'; $l.Save()
        "desktop shortcut: $($f.Name)"
    }
}
$link = Join-Path $menu 'Try Linux.lnk'
if ((Test-Path -LiteralPath $link) -and (Same $sh.CreateShortcut($link).TargetPath $old)) { Remove-Item -LiteralPath $link; 'Start menu entry' }
$run = "$reg\Run"
$k = Get-Item -LiteralPath $run -ErrorAction SilentlyContinue
if ($k) {
    foreach ($n in $k.GetValueNames() | Where-Object { $_ -like 'TryLinux-*' }) {
        $v = [string]$k.GetValue($n)
        if ($v -match '^"([^"]*)"(.*)$' -and (Same $Matches[1] $old)) {
            Set-ItemProperty -LiteralPath $run -Name $n -Value ('"' + $exe + '"' + $Matches[2]); "start at sign-in: $n"
        }
    }
}
$u = "$reg\Uninstall\TryLinux"
if ((Test-Path -LiteralPath $u) -and (Same ([string](Get-ItemProperty -LiteralPath $u).InstallLocation) $env:TL_OLD)) {
    Remove-Item -LiteralPath $u -Recurse; 'Installed apps entry'
}
# Only the edition's own files: TryLinux.exe, and the TryLinux.exe.<ticks>.old copies its updates leave.
Get-ChildItem -LiteralPath $env:TL_OLD -Filter 'TryLinux.exe*' -File | Remove-Item -Force -ErrorAction SilentlyContinue
if (-not (Get-ChildItem -LiteralPath $env:TL_OLD -Force)) { Remove-Item -LiteralPath $env:TL_OLD; 'folder' }
elseif (Test-Path -LiteralPath $old) { throw "Couldn't remove $old." }
"#;
        use std::os::windows::process::CommandExt;
        let mut cmd = Command::new("powershell");
        cmd.args(["-NoProfile", "-NonInteractive", "-Command", script])
            .creation_flags(0x0800_0000) // CREATE_NO_WINDOW
            .env("TL_OLD", old)
            .env("TL_EXE", exe)
            .env("TL_DESKTOP", desktop.map(|p| p.as_os_str().to_owned()).unwrap_or_default())
            .env("TL_MENU", start_menu.map(|p| p.as_os_str().to_owned()).unwrap_or_default())
            .env("TL_REG", registry.unwrap_or_default());
        let out = cmd.output().map_err(|e| e.to_string())?;
        let done = String::from_utf8_lossy(&out.stdout).lines().map(str::to_string).filter(|l| !l.is_empty()).collect();
        if out.status.success() {
            Ok(done)
        } else {
            Err(String::from_utf8_lossy(&out.stderr).trim().to_string())
        }
    }

    // TRYLINUX_TEST_DIR=folder cargo test takes_over_old_edition -- --ignored --nocapture
    // Uses that folder for the old edition, the desktop and the Start menu, and HKCU\Software\TryLinuxTest.
    #[cfg(test)]
    mod tests {
        use super::*;
        use std::fs;

        fn reg(args: &[&str]) -> String {
            let o = Command::new("reg").args(args).output().unwrap();
            String::from_utf8_lossy(&o.stdout).to_string()
        }

        #[test]
        #[ignore]
        fn takes_over_old_edition() {
            let root = PathBuf::from(std::env::var("TRYLINUX_TEST_DIR").unwrap()).join("legacy");
            let _ = fs::remove_dir_all(&root);
            let (old, desk, menu, new) = (root.join("Programs").join("TryLinux"), root.join("Desktop"), root.join("Start"), root.join("Try Linux"));
            for d in [&old, &desk, &menu, &new] {
                fs::create_dir_all(d).unwrap();
            }
            fs::write(old.join("TryLinux.exe"), "old").unwrap();
            fs::write(old.join("TryLinux.exe.638000000000000000.old"), "older").unwrap();
            let exe = new.join("TryLinux.exe");
            fs::write(&exe, "new").unwrap();
            let old_exe = old.join("TryLinux.exe");
            let link = |p: &Path, target: &Path, args: &str| {
                let s = "$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:L); $s.TargetPath = $env:T; $s.Arguments = $env:A; $s.Save()";
                assert!(Command::new("powershell").args(["-NoProfile", "-Command", s]).env("L", p).env("T", target).env("A", args).status().unwrap().success());
            };
            link(&desk.join("Fedora.lnk"), &old_exe, "--start fedora");
            link(&desk.join("Other app.lnk"), &root.join("other.exe"), "");
            link(&menu.join("Try Linux.lnk"), &old_exe, "");
            let key = r"HKCU\Software\TryLinuxTest";
            let _ = reg(&["delete", key, "/f"]);
            let old_run = format!("\"{}\" --start fedora", old_exe.display());
            reg(&["add", &format!(r"{key}\Run"), "/v", "TryLinux-fedora", "/d", &old_run, "/f"]);
            reg(&["add", &format!(r"{key}\Run"), "/v", "Something-else", "/d", &old_run, "/f"]);
            reg(&["add", &format!(r"{key}\Uninstall\TryLinux"), "/v", "InstallLocation", "/d", &old.to_string_lossy(), "/f"]);

            let done = take_over_from(&old, &exe, Some(&desk), Some(&menu), Some(r"Software\TryLinuxTest")).unwrap();
            println!("{done:#?}");

            let target = |p: &Path| {
                let o = Command::new("powershell")
                    .args(["-NoProfile", "-Command", "$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:L); $s.TargetPath + '|' + $s.Arguments"])
                    .env("L", p)
                    .output()
                    .unwrap();
                String::from_utf8_lossy(&o.stdout).trim().to_string()
            };
            assert_eq!(target(&desk.join("Fedora.lnk")), format!("{}|--start fedora", exe.display()));
            assert!(target(&desk.join("Other app.lnk")).starts_with(&*root.join("other.exe").to_string_lossy()));
            assert!(!menu.join("Try Linux.lnk").exists());
            let run = reg(&["query", &format!(r"{key}\Run")]);
            assert!(run.contains(&format!("\"{}\" --start fedora", exe.display())), "{run}");
            assert!(run.contains(&old_run), "another program's entry is left alone: {run}");
            assert!(reg(&["query", &format!(r"{key}\Uninstall\TryLinux")]).is_empty());
            assert!(!old.exists());
            assert!(exe.exists());
            let _ = reg(&["delete", key, "/f"]);
        }
    }
}

#[cfg(all(unix, not(target_os = "macos")))]
mod imp {
    use std::fs;
    use std::path::{Path, PathBuf};

    const ID: &str = "io.github.donnysaengkio.TryLinux";
    const ICON: &[u8] = include_bytes!("../icons/trylinux.svg");

    fn xdg(var: &str, default: &str) -> PathBuf {
        match std::env::var(var) {
            Ok(v) if Path::new(&v).is_absolute() => PathBuf::from(v),
            _ => home().join(default),
        }
    }
    fn home() -> PathBuf {
        std::env::var_os("HOME").map(PathBuf::from).unwrap_or_default()
    }

    // The AppImage this copy runs from, if it's one ($APPIMAGE, set by the AppImage's runtime).
    pub fn appimage() -> Option<PathBuf> {
        std::env::var_os("APPIMAGE").map(PathBuf::from).filter(|p| p.is_file())
    }

    pub fn take_over() {
        let Some(app) = appimage() else { return };
        std::thread::spawn(move || {
            let _ = integrate(&xdg("XDG_DATA_HOME", ".local/share"), &xdg("XDG_CONFIG_HOME", ".config"), &home().join(".local/bin"), &app);
        });
    }

    fn write_if_changed(path: &Path, bytes: &[u8]) -> Result<bool, String> {
        if fs::read(path).ok().as_deref() == Some(bytes) {
            return Ok(false);
        }
        if let Some(p) = path.parent() {
            fs::create_dir_all(p).map_err(|e| e.to_string())?;
        }
        fs::write(path, bytes).map_err(|e| format!("{}: {e}", path.display()))?;
        Ok(true)
    }

    fn quoted(p: &Path) -> String {
        format!("\"{}\"", p.to_string_lossy().replace('"', "\\\""))
    }

    // An Exec= line's program, unquoted, and the rest of the line.
    fn split_exec(exec: &str) -> (String, &str) {
        let exec = exec.trim_start();
        if let Some(rest) = exec.strip_prefix('"') {
            let end = rest.find('"').unwrap_or(rest.len());
            (rest[..end].replace("\\\"", "\""), rest.get(end + 1..).unwrap_or(""))
        } else {
            let end = exec.find(' ').unwrap_or(exec.len());
            (exec[..end].to_string(), &exec[end..])
        }
    }

    // Puts Try Linux (this AppImage) in the apps menu and on the command line, and takes over 1.0.x. `data`,
    // `config` and `bin` are ~/.local/share, ~/.config and ~/.local/bin (given, for tests). Lists what it changed.
    pub fn integrate(data: &Path, config: &Path, bin: &Path, app: &Path) -> Result<Vec<String>, String> {
        let mut done = Vec::new();
        if write_if_changed(&data.join("icons/hicolor/scalable/apps").join(format!("{ID}.svg")), ICON)? {
            done.push("icon".to_string());
        }
        let entry = format!(
            "[Desktop Entry]\nType=Application\nName=Try Linux\nGenericName=Virtual Machines\n\
             Comment=Try a Linux distribution in a virtual machine, from its official installer\nExec={}\nIcon={ID}\n\
             Terminal=false\nCategories=System;Emulator;\nKeywords=virtual machine;vm;qemu;kvm;linux;distribution;iso;\n\
             StartupNotify=true\n",
            quoted(app)
        );
        if write_if_changed(&data.join("applications").join(format!("{ID}.desktop")), entry.as_bytes())? {
            done.push("apps menu entry".to_string());
        }
        // ~/.local/bin/try-linux: a link to the AppImage (replacing 1.0.x's link to its launcher).
        let cmd = bin.join("try-linux");
        if fs::read_link(&cmd).ok().as_deref() != Some(app) {
            fs::create_dir_all(bin).map_err(|e| e.to_string())?;
            let _ = fs::remove_file(&cmd);
            std::os::unix::fs::symlink(app, &cmd).map_err(|e| format!("{}: {e}", cmd.display()))?;
            done.push("try-linux command".to_string());
        }
        // Your systems' entries (trylinux-<id>.desktop) that start 1.0.x's try-linux now start the AppImage.
        for dir in [data.join("applications"), config.join("autostart")] {
            for f in fs::read_dir(&dir).into_iter().flatten().flatten() {
                let name = f.file_name().to_string_lossy().to_string();
                if !name.starts_with("trylinux-") || !name.ends_with(".desktop") {
                    continue;
                }
                let Ok(text) = fs::read_to_string(f.path()) else { continue };
                let mut changed = false;
                let lines: Vec<String> = text
                    .lines()
                    .map(|l| match l.strip_prefix("Exec=") {
                        Some(exec) => {
                            let (prog, rest) = split_exec(exec);
                            if Path::new(&prog).file_name().is_some_and(|n| n == "try-linux") {
                                changed = true;
                                format!("Exec={}{rest}", quoted(app))
                            } else {
                                l.to_string()
                            }
                        }
                        None => l.to_string(),
                    })
                    .collect();
                if changed {
                    fs::write(f.path(), lines.join("\n") + "\n").map_err(|e| e.to_string())?;
                    done.push(format!("{}: {name}", if dir.ends_with("autostart") { "start at sign-in" } else { "apps menu" }));
                }
            }
        }
        // 1.0.x's own files in ~/.local/share/try-linux (the AppImage may be there too, so the folder stays).
        let old = data.join("try-linux");
        if old.join("trylinux").is_dir() || old.join(".installed").exists() {
            for f in ["trylinux", "trylinux.old"] {
                let _ = fs::remove_dir_all(old.join(f));
            }
            for f in ["try-linux", "install.sh", ".installed", "README.md", "LICENSE"] {
                let _ = fs::remove_file(old.join(f));
            }
            let _ = fs::remove_dir(&old); // only if nothing else is there
            done.push("1.0.x files".to_string());
        }
        Ok(done)
    }

    // TRYLINUX_TEST_DIR=folder cargo test takes_over_linux_edition -- --ignored --nocapture
    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        #[ignore]
        fn takes_over_linux_edition() {
            let root = PathBuf::from(std::env::var("TRYLINUX_TEST_DIR").unwrap()).join("legacy-linux");
            let _ = fs::remove_dir_all(&root);
            let (data, config, bin) = (root.join("share"), root.join("config"), root.join("bin"));
            let old = data.join("try-linux");
            fs::create_dir_all(old.join("trylinux/ui")).unwrap();
            fs::write(old.join("trylinux/__init__.py"), "").unwrap();
            fs::write(old.join("try-linux"), "#!/usr/bin/env python3\n").unwrap();
            fs::write(old.join(".installed"), "").unwrap();
            fs::write(old.join("install.sh"), "").unwrap();
            let app = old.join("TryLinux.AppImage");
            fs::write(&app, "appimage").unwrap();
            fs::create_dir_all(&bin).unwrap();
            std::os::unix::fs::symlink(old.join("try-linux"), bin.join("try-linux")).unwrap();
            fs::create_dir_all(data.join("applications")).unwrap();
            fs::create_dir_all(config.join("autostart")).unwrap();
            let sys = format!("[Desktop Entry]\nType=Application\nName=Fedora\nExec={} --start fedora\nIcon={ID}\n", bin.join("try-linux").display());
            fs::write(data.join("applications/trylinux-fedora.desktop"), &sys).unwrap();
            fs::write(config.join("autostart/trylinux-fedora.desktop"), &sys).unwrap();
            fs::write(data.join("applications/other.desktop"), "[Desktop Entry]\nExec=try-linux\n").unwrap();

            let done = integrate(&data, &config, &bin, &app).unwrap();
            println!("{done:#?}");
            let want = format!("Exec=\"{}\" --start fedora", app.display());
            assert!(fs::read_to_string(data.join("applications/trylinux-fedora.desktop")).unwrap().contains(&want));
            assert!(fs::read_to_string(config.join("autostart/trylinux-fedora.desktop")).unwrap().contains(&want));
            assert_eq!(fs::read_to_string(data.join("applications/other.desktop")).unwrap(), "[Desktop Entry]\nExec=try-linux\n");
            assert!(fs::read_to_string(data.join(format!("applications/{ID}.desktop"))).unwrap().contains(&format!("Exec=\"{}\"", app.display())));
            assert!(data.join(format!("icons/hicolor/scalable/apps/{ID}.svg")).exists());
            assert_eq!(fs::read_link(bin.join("try-linux")).unwrap(), app);
            assert!(!old.join("trylinux").exists() && !old.join("try-linux").exists() && !old.join(".installed").exists());
            assert!(app.exists());
            // Again: nothing left to do.
            assert!(integrate(&data, &config, &bin, &app).unwrap().is_empty());
        }
    }
}

#[cfg(target_os = "macos")]
mod imp {
    pub fn take_over() {}
}

pub use imp::take_over;
#[cfg(all(unix, not(target_os = "macos")))]
pub use imp::appimage;
