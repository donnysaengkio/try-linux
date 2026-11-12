// Taking over from the Windows edition before 2.0: a single TryLinux.exe in %LOCALAPPDATA%\Programs\TryLinux.
// Its update to 2.0 puts this app's installer in its own place and runs it. On each start (until it's done),
// Try Linux then points that edition's desktop shortcuts and "start at sign-in" entries here, and removes its
// Start menu entry, its entry in Installed apps and its folder. Systems, downloads and preferences are shared
// files, so they need nothing. Nothing happens while anything in that folder is still running.

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

#[cfg(not(windows))]
mod imp {
    pub fn take_over() {}
}

pub use imp::take_over;
