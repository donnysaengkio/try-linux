// Where Try Linux keeps things, and its own preferences, as each edition does it, so the app opens the systems
// that edition made and shares its choices:
//   Windows: %LOCALAPPDATA%\TryLinux (vms, isos, downloads); preferences in %APPDATA%\TryLinux\settings.ini.
//   Linux:   ~/.local/share/TryLinux (vms, isos); preferences in ~/.config/TryLinux/settings.json (XDG folders).
//   macOS:   ~/Library/Application Support/TryLinux (vms, isos); preferences in the Mac edition's defaults
//            (io.github.donnysaengkio.trylinux).
// Preferences use the Windows names here: theme ("dark" or "light"), followsystem and checkupdates ("1" or
// "0"), and storage.

use std::fs;
use std::path::{Path, PathBuf};

#[cfg(unix)]
fn home() -> PathBuf {
    std::env::var_os("HOME").or_else(|| std::env::var_os("USERPROFILE")).map(PathBuf::from).unwrap_or_default()
}

// ---------------------------------------------------------------- Windows: settings.ini

#[cfg(windows)]
mod store {
    use super::*;

    pub fn ini() -> PathBuf {
        std::env::var("APPDATA").map(PathBuf::from).unwrap_or_default().join("TryLinux").join("settings.ini")
    }
    fn lines() -> Vec<String> {
        fs::read_to_string(ini()).map(|t| t.lines().map(String::from).collect()).unwrap_or_default()
    }
    fn write(lines: &[String]) -> std::io::Result<()> {
        if let Some(p) = ini().parent() {
            fs::create_dir_all(p)?;
        }
        fs::write(ini(), lines.join("\r\n") + "\r\n")
    }

    pub fn get(key: &str) -> Option<String> {
        lines().iter().find_map(|l| l.strip_prefix(&format!("{key}=")).map(|v| v.trim().to_string()))
    }
    pub fn set(key: &str, value: &str) -> std::io::Result<()> {
        let mut found = false;
        let mut l: Vec<String> = lines()
            .into_iter()
            .map(|l| if l.starts_with(&format!("{key}=")) { found = true; format!("{key}={value}") } else { l })
            .collect();
        if !found {
            l.push(format!("{key}={value}"));
        }
        write(&l)
    }

    pub fn root() -> PathBuf {
        match get("storage").filter(|s| !s.is_empty()) {
            Some(s) => PathBuf::from(s),
            None => std::env::var("LOCALAPPDATA").map(PathBuf::from).unwrap_or_default().join("TryLinux"),
        }
    }
    pub fn vms() -> PathBuf {
        root().join("vms")
    }
    pub fn others() -> Vec<String> {
        get("vmdirs").map(|v| v.split('|').map(str::trim).filter(|d| !d.is_empty()).map(String::from).collect()).unwrap_or_default()
    }
    pub fn set_others(dirs: &[String]) -> std::io::Result<()> {
        set("vmdirs", &dirs.join("|"))
    }
}

// ---------------------------------------------------------------- Linux: settings.json (XDG)

#[cfg(all(unix, not(target_os = "macos")))]
mod store {
    use super::*;
    use serde_json::{Map, Value};

    fn xdg(var: &str, default: &str) -> PathBuf {
        match std::env::var(var) {
            Ok(v) if Path::new(&v).is_absolute() => PathBuf::from(v),
            _ => home().join(default),
        }
    }
    pub fn data() -> PathBuf {
        xdg("XDG_DATA_HOME", ".local/share").join("TryLinux")
    }
    pub fn file() -> PathBuf {
        xdg("XDG_CONFIG_HOME", ".config").join("TryLinux").join("settings.json")
    }
    fn read() -> Map<String, Value> {
        fs::read_to_string(file()).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default()
    }
    fn write(m: &Map<String, Value>) -> std::io::Result<()> {
        if let Some(p) = file().parent() {
            fs::create_dir_all(p)?;
        }
        fs::write(file(), serde_json::to_string_pretty(m).unwrap_or_default())
    }

    // The Linux edition's names and types for the shared preferences.
    pub fn get(key: &str) -> Option<String> {
        let m = read();
        let b = |k: &str| m.get(k).and_then(Value::as_bool).map(|v| if v { "1" } else { "0" }.to_string());
        match key {
            "theme" => m.get("theme").and_then(Value::as_str).map(String::from),
            "followsystem" => b("follow_system"),
            "checkupdates" => b("check_updates"),
            "storage" => m.get("storage").and_then(Value::as_str).map(String::from),
            _ => None,
        }
    }
    pub fn set(key: &str, value: &str) -> std::io::Result<()> {
        let mut m = read();
        let (k, v) = match key {
            "followsystem" => ("follow_system", Value::Bool(value == "1")),
            "checkupdates" => ("check_updates", Value::Bool(value == "1")),
            k => (k, Value::String(value.into())),
        };
        m.insert(k.into(), v);
        write(&m)
    }

    pub fn root() -> PathBuf {
        data()
    }
    // "storage" is the systems folder itself on Linux ("" is the default, inside the data folder).
    pub fn vms() -> PathBuf {
        get("storage").filter(|s| !s.is_empty()).map(PathBuf::from).unwrap_or_else(|| data().join("vms"))
    }
    pub fn others() -> Vec<String> {
        read().get("systems").and_then(Value::as_array).map(|a| a.iter().filter_map(Value::as_str).map(String::from).collect()).unwrap_or_default()
    }
    pub fn set_others(dirs: &[String]) -> std::io::Result<()> {
        let mut m = read();
        m.insert("systems".into(), Value::Array(dirs.iter().cloned().map(Value::String).collect()));
        write(&m)
    }
}

// ---------------------------------------------------------------- macOS: the Mac edition's defaults

#[cfg(target_os = "macos")]
mod store {
    use super::*;
    use std::process::Command;

    const DOMAIN: &str = "io.github.donnysaengkio.trylinux";

    fn read(key: &str) -> Option<String> {
        let out = Command::new("defaults").args(["read", DOMAIN, key]).output().ok()?;
        out.status.success().then(|| String::from_utf8_lossy(&out.stdout).trim().to_string())
    }
    fn write(key: &str, kind: &str, value: &str) -> std::io::Result<()> {
        let ok = Command::new("defaults").args(["write", DOMAIN, key, kind, value]).status()?.success();
        if ok { Ok(()) } else { Err(std::io::Error::other(format!("couldn't save {key}"))) }
    }

    // The Mac edition's names and types for the shared preferences (booleans read back as 1 or 0).
    pub fn get(key: &str) -> Option<String> {
        match key {
            "theme" => read("theme"),
            "followsystem" => read("followSystem"),
            "checkupdates" => read("checkUpdates"),
            "storage" => read("storage"),
            _ => None,
        }
    }
    pub fn set(key: &str, value: &str) -> std::io::Result<()> {
        match key {
            "followsystem" => write("followSystem", "-bool", if value == "1" { "true" } else { "false" }),
            "checkupdates" => write("checkUpdates", "-bool", if value == "1" { "true" } else { "false" }),
            k => write(k, "-string", value),
        }
    }

    pub fn root() -> PathBuf {
        get("storage").filter(|s| !s.is_empty()).map(PathBuf::from)
            .unwrap_or_else(|| home().join("Library").join("Application Support").join("TryLinux"))
    }
    pub fn vms() -> PathBuf {
        root().join("vms")
    }
    // vmDirs is an array: `defaults read` prints it as ( "a", "b" ).
    pub fn others() -> Vec<String> {
        read("vmDirs")
            .map(|t| t.trim_matches(|c| c == '(' || c == ')').split(',').map(|s| s.trim().trim_matches('"').to_string()).filter(|s| !s.is_empty()).collect())
            .unwrap_or_default()
    }
    pub fn set_others(dirs: &[String]) -> std::io::Result<()> {
        let mut cmd = Command::new("defaults");
        cmd.args(["write", DOMAIN, "vmDirs", "-array"]).args(dirs);
        if cmd.status()?.success() { Ok(()) } else { Err(std::io::Error::other("couldn't save vmDirs")) }
    }
}

// ---------------------------------------------------------------- the same everywhere

pub fn pref(key: &str) -> Option<String> {
    store::get(key)
}
pub fn set_pref(key: &str, value: &str) -> std::io::Result<()> {
    store::set(key, value)
}

// Try Linux's own folder: installers in isos/, downloads in downloads/ (and the systems in vms/, except where
// Linux keeps them elsewhere).
pub fn storage() -> PathBuf {
    store::root()
}
pub fn vms_dir() -> PathBuf {
    store::vms()
}
pub fn isos_dir() -> PathBuf {
    storage().join("isos")
}
pub fn downloads_dir() -> PathBuf {
    storage().join("downloads")
}

// What Recovery > Move moves: the whole storage folder, or on Linux the systems folder (the Linux edition's
// "storage"); and pointing Try Linux at where it went.
pub fn movable() -> PathBuf {
    if cfg!(all(unix, not(target_os = "macos"))) { vms_dir() } else { storage() }
}
pub fn set_moved(to: &Path) -> std::io::Result<()> {
    set_pref("storage", &to.to_string_lossy())
}

// Systems made outside the systems folder (Create > Location), so Virtual Machines finds them.
pub fn other_dirs() -> Vec<PathBuf> {
    store::others().into_iter().map(PathBuf::from).collect()
}
fn same(a: &Path, b: &Path) -> bool {
    let n = |p: &Path| {
        let s = p.to_string_lossy().trim_end_matches(['\\', '/']).to_string();
        if cfg!(windows) { s.to_lowercase() } else { s }
    };
    n(a) == n(b)
}
pub fn remember(dir: &Path) -> std::io::Result<()> {
    if dir.parent().is_some_and(|p| same(p, &vms_dir())) {
        return Ok(());
    }
    let mut dirs = store::others();
    if !dirs.iter().any(|d| same(Path::new(d), dir)) {
        dirs.push(dir.to_string_lossy().into());
        store::set_others(&dirs)?;
    }
    Ok(())
}
pub fn forget(dir: &Path) -> std::io::Result<()> {
    let dirs = store::others();
    let kept: Vec<String> = dirs.iter().filter(|d| !same(Path::new(d), dir)).cloned().collect();
    if kept.len() != dirs.len() { store::set_others(&kept) } else { Ok(()) }
}

// Every system's folder: the systems folder's, then any kept elsewhere.
pub fn system_dirs() -> Vec<PathBuf> {
    let mut dirs: Vec<PathBuf> = fs::read_dir(vms_dir())
        .map(|r| r.filter_map(|e| e.ok()).map(|e| e.path()).filter(|p| p.is_dir()).collect())
        .unwrap_or_default();
    dirs.sort();
    for d in other_dirs() {
        if !dirs.iter().any(|x| same(x, &d)) {
            dirs.push(d);
        }
    }
    dirs
}
