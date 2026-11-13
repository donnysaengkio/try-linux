// Try Linux as a whole, for the main menu: Preferences (kept where this computer's edition keeps them, so both
// look the same: see paths.rs), About's details, and checking WINQ-EMU for a newer release (Windows).

use crate::paths::{pref as read, set_pref, storage};
use serde::Serialize;
use std::fs;

// What runs the systems, for About: WINQ-EMU's version (Windows), QEMU's (Linux), or Apple's framework.
fn engine() -> String {
    if cfg!(target_os = "macos") {
        return "Apple Virtualization".into();
    }
    if cfg!(windows) {
        return winq_version().map(|v| format!("WINQ-EMU {v}")).unwrap_or_else(|| "WINQ-EMU isn't installed".into());
    }
    crate::vm::qemu_version().map(|v| format!("QEMU {v}")).unwrap_or_else(|| "QEMU isn't installed".into())
}

// The version in WINQ-EMU's README.txt ("WINQ-EMU Alpha 10" on its first line), as the Windows edition reads it.
fn winq_version() -> Option<String> {
    let bin = crate::vm::winq_bin()?;
    [bin.clone(), bin.parent()?.to_path_buf()].iter().find_map(|d| {
        let first = fs::read_to_string(d.join("README.txt")).ok()?.lines().next()?.trim().to_string();
        let rest = first.get(..8).filter(|p| p.eq_ignore_ascii_case("WINQ-EMU")).map(|_| first[8..].trim().to_string())?;
        (!rest.is_empty()).then_some(rest)
    })
}

#[derive(Serialize)]
pub struct AppInfo {
    version: String,
    theme: String,        // "dark" or "light"
    follow_system: bool,  // light or dark as Windows shows apps, instead of `theme`
    check_updates: bool,  // check WINQ-EMU once a day, when Try Linux opens
    storage: String,
    winq: Option<String>, // WINQ-EMU's version, or None when it isn't installed (or not on Windows)
    engine: String,       // what runs the systems, with its version
    platform: &'static str, // "windows", "linux" or "macos"
}

#[tauri::command]
pub fn app_info() -> AppInfo {
    AppInfo {
        version: env!("CARGO_PKG_VERSION").into(),
        theme: if read("theme").as_deref() == Some("light") { "light".into() } else { "dark".into() },
        follow_system: read("followsystem").as_deref() == Some("1"),
        check_updates: read("checkupdates").as_deref() != Some("0"),
        storage: storage().to_string_lossy().into(),
        winq: if cfg!(windows) { winq_version() } else { None },
        engine: engine(),
        platform: std::env::consts::OS,
    }
}

// Changes one preference, leaving the others as they are.
#[tauri::command]
pub fn set_preference(key: String, value: String) -> Result<(), String> {
    let ok = match key.as_str() {
        "theme" => value == "dark" || value == "light",
        "followsystem" | "checkupdates" => value == "0" || value == "1",
        _ => false,
    };
    if !ok {
        return Err(format!("{key} isn't a preference."));
    }
    set_pref(&key, &value).map_err(|e| format!("Couldn't save the preference: {e}"))
}

// How Virtual Machines is arranged, as the interface saves it (Home.tsx): {"order": [ids], "groups": [names],
// "member": {id: group}, "folded": [names], "sort": {"col", "dir"} or null}. Kept with the preferences.
#[tauri::command]
pub fn layout() -> String {
    read("layout").filter(|s| serde_json::from_str::<serde_json::Value>(s).is_ok()).unwrap_or_else(|| "{}".into())
}

#[tauri::command]
pub fn set_layout(layout: String) -> Result<(), String> {
    let v: serde_json::Value = serde_json::from_str(&layout).map_err(|e| e.to_string())?;
    set_pref("layout", &v.to_string()).map_err(|e| format!("Couldn't save how Virtual Machines is arranged: {e}"))
}

// Where a system comes in the tray menu: its group (A to Z, systems in no group last), then its place in the
// arranged order.
pub fn layout_rank() -> impl Fn(&str) -> (usize, usize) {
    let v: serde_json::Value = serde_json::from_str(&layout()).unwrap_or_default();
    let strings = |k: &str| v[k].as_array().map(|a| a.iter().filter_map(|x| x.as_str().map(String::from)).collect::<Vec<_>>()).unwrap_or_default();
    let mut groups = strings("groups");
    groups.sort_by_key(|g| g.to_lowercase());
    let order = strings("order");
    let member = v["member"].clone();
    move |id: &str| {
        let g = member[id].as_str().and_then(|g| groups.iter().position(|x| x == g)).unwrap_or(groups.len());
        (g, order.iter().position(|x| x == id).unwrap_or(usize::MAX))
    }
}

#[derive(Serialize)]
pub struct Updates {
    winq: Option<String>,   // installed
    latest: Option<String>, // newest on GitHub
    newer: bool,
    page: String,
}

// WINQ-EMU versions read like "Alpha 10": the stage (alpha < beta < rc < release) first, then the numbers.
fn newer_winq(candidate: &str, installed: &str) -> bool {
    let stage = |v: &str| {
        let v = v.to_lowercase();
        if v.contains("alpha") { 0 } else if v.contains("beta") { 1 } else if v.split(|c: char| !c.is_alphanumeric()).any(|w| w.starts_with("rc")) { 2 } else { 3 }
    };
    let nums = |v: &str| v.split(|c: char| !c.is_ascii_digit()).filter(|s| !s.is_empty()).map(|s| s.parse::<u64>().unwrap_or(0)).collect::<Vec<_>>();
    let (a, b) = (stage(candidate), stage(installed));
    if a != b {
        return a > b;
    }
    nums(candidate) > nums(installed)
}

// Asks GitHub for WINQ-EMU's newest release. Off the main thread.
#[tauri::command(async)]
pub fn check_updates() -> Result<Updates, String> {
    let text = ureq::get("https://api.github.com/repos/cmspam/winq-emu/releases/latest")
        .set("User-Agent", "TryLinux/1.0")
        .timeout(std::time::Duration::from_secs(20))
        .call()
        .map_err(|e| format!("Couldn't reach GitHub: {e}"))?
        .into_string()
        .map_err(|e| e.to_string())?;
    let json: serde_json::Value = serde_json::from_str(&text).map_err(|e| e.to_string())?;
    let latest = json["name"].as_str().filter(|n| !n.is_empty()).or(json["tag_name"].as_str()).map(String::from);
    let winq = winq_version();
    Ok(Updates {
        newer: matches!((&latest, &winq), (Some(l), Some(w)) if newer_winq(l, w)),
        page: json["html_url"].as_str().unwrap_or("https://github.com/cmspam/winq-emu/releases").into(),
        winq,
        latest,
    })
}

// Opens Try Linux's storage folder in Explorer.
#[tauri::command]
pub fn open_storage() -> Result<(), String> {
    let dir = storage();
    fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    #[cfg(windows)]
    let r = std::process::Command::new("explorer").arg(&dir).spawn();
    #[cfg(target_os = "macos")]
    let r = std::process::Command::new("open").arg(&dir).spawn();
    #[cfg(all(unix, not(target_os = "macos")))]
    let r = std::process::Command::new("xdg-open").arg(&dir).spawn();
    r.map(|_| ()).map_err(|e| e.to_string())
}
