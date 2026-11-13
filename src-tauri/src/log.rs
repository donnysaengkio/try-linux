// Each system's log, trylinux.log in its folder: when it started and how (installer or disk, GPU or CPU, the
// command line), what QEMU said while it ran (warnings and errors), the Power menu's commands, and when and why
// it stopped. Settings > Recovery > Repair shows it. Only the last few runs are kept.

use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};

const FILE: &str = "trylinux.log";
const KEEP: u64 = 256 * 1024; // past this, a new run drops the oldest runs
const START: &str = "=== ";

pub fn file(dir: &Path) -> PathBuf {
    dir.join(FILE)
}

fn now() -> String {
    chrono::Local::now().format("%Y-%m-%d %H:%M:%S").to_string()
}

// One line, with the time.
pub fn line(dir: &Path, text: &str) {
    let text = text.trim_end();
    if text.is_empty() {
        return;
    }
    if let Ok(mut f) = OpenOptions::new().create(true).append(true).open(file(dir)) {
        let _ = writeln!(f, "{} {}", now(), text.replace('\n', "\n                    "));
    }
}

// The start of a run: a heading line, after trimming the oldest runs if the log has grown large.
pub fn started(dir: &Path, text: &str) {
    trim(&file(dir));
    if let Ok(mut f) = OpenOptions::new().create(true).append(true).open(file(dir)) {
        let _ = writeln!(f, "\n{START}{} {}", now(), text.trim_end());
    }
}

// Keeps the newest runs that fit in KEEP / 2, cut at the start of a run.
fn trim(path: &Path) {
    let Ok(meta) = fs::metadata(path) else { return };
    if meta.len() <= KEEP {
        return;
    }
    let Ok(text) = fs::read_to_string(path) else { return };
    let mut from = text.len().saturating_sub((KEEP / 2) as usize);
    while !text.is_char_boundary(from) {
        from += 1;
    }
    let cut = text[from..].find(&format!("\n{START}")).map(|i| from + i + 1);
    let _ = fs::write(path, cut.map(|i| &text[i..]).unwrap_or(""));
}

// How long it ran, for the log: "45 seconds", "12 minutes", "2 hours 5 minutes".
pub fn duration(secs: u64) -> String {
    let plural = |n: u64, w: &str| format!("{n} {w}{}", if n == 1 { "" } else { "s" });
    match secs {
        0..60 => plural(secs, "second"),
        60..3600 => plural(secs / 60, "minute"),
        _ if secs % 3600 < 60 => plural(secs / 3600, "hour"),
        _ => format!("{} {}", plural(secs / 3600, "hour"), plural(secs % 3600 / 60, "minute")),
    }
}

// The Power menu's commands as the menu names them.
pub fn action(a: &str) -> &str {
    match a {
        "shutdown" => "Shut down",
        "restart" => "Restart",
        "pause" => "Pause",
        "resume" => "Resume",
        "power-on" => "Power on",
        "reset" => "Reset",
        "close-console" => "Close console",
        "force" => "Force stop",
        other => other,
    }
}

// The log's text, or what to show when there's none yet.
#[tauri::command]
pub fn system_log(id: String) -> Result<String, String> {
    let cfg = crate::config::find(&id).ok_or("This system's folder wasn't found.")?;
    Ok(fs::read_to_string(file(&cfg.dir)).unwrap_or_default().trim().to_string())
}

// Opens the log in the computer's text editor.
#[tauri::command]
pub fn open_log(id: String) -> Result<(), String> {
    let cfg = crate::config::find(&id).ok_or("This system's folder wasn't found.")?;
    let path = file(&cfg.dir);
    if !path.exists() {
        return Err("There's no log yet: it starts the next time this system runs.".into());
    }
    #[cfg(windows)]
    let r = std::process::Command::new("notepad").arg(&path).spawn();
    #[cfg(target_os = "macos")]
    let r = std::process::Command::new("open").arg("-t").arg(&path).spawn();
    #[cfg(all(unix, not(target_os = "macos")))]
    let r = std::process::Command::new("xdg-open").arg(&path).spawn();
    r.map(|_| ()).map_err(|e| format!("Couldn't open the log: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keeps_the_newest_runs() {
        let dir = std::env::temp_dir().join(format!("trylinux-log-test-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        for run in 0..400 {
            started(&dir, &format!("Started run {run}"));
            for _ in 0..20 {
                line(&dir, "QEMU: a warning that takes up a line of the log");
            }
            line(&dir, &format!("Stopped run {run}"));
        }
        let text = fs::read_to_string(file(&dir)).unwrap();
        assert!(text.len() as u64 <= KEEP, "the log grew to {} bytes", text.len());
        assert!(text.starts_with(START), "the log doesn't start at a run");
        assert!(text.contains("Stopped run 399"));
        assert!(!text.contains("Started run 0\n"));
        let _ = fs::remove_dir_all(&dir);
    }
}
