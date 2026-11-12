// A system's settings, read and written in its edition's own file, so either app can open it:
//   Windows: vm.ini (key=value lines), Disk 1 <id>.qcow2, more disks <id>-diskN.qcow2.
//   Linux:   vm.json (the Linux edition's names), Disk 1 disk.qcow2, more disks diskN.qcow2.
//   macOS:   config.json (the Mac edition's names), Disk 1 disk.img (raw), more disks diskN.img.
// Everywhere else in the app a setting has its Windows name ("ram", "nettype"...) and is text, as in vm.ini;
// a description is one line with \n for line breaks. JSON keeps the settings the other edition doesn't know.

use serde_json::{Map, Value};
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

pub use crate::paths::system_dirs;

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Format {
    Ini,
    Linux,
    Mac,
}

// This computer's edition.
pub const NATIVE: Format = if cfg!(target_os = "macos") { Format::Mac } else if cfg!(windows) { Format::Ini } else { Format::Linux };

impl Format {
    pub fn file(self) -> &'static str {
        match self {
            Format::Ini => "vm.ini",
            Format::Linux => "vm.json",
            Format::Mac => "config.json",
        }
    }
}

// The settings file a system folder has, made by whichever edition.
pub fn settings_file(dir: &Path) -> Option<(PathBuf, Format)> {
    let mut found = [Format::Ini, Format::Linux, Format::Mac].into_iter().map(|f| (dir.join(f.file()), f)).filter(|(p, _)| p.is_file());
    // This edition's own file first, when a folder somehow has two.
    let all: Vec<_> = found.by_ref().collect();
    all.iter().find(|(_, f)| *f == NATIVE).cloned().or_else(|| all.into_iter().next())
}

// How a Windows name is kept in the JSON editions: their key, and what kind of value.
#[derive(Clone, Copy)]
enum Kind {
    Text,
    Bool,
    Num,
    List(char), // an array, joined with this in vm.ini
    Desc,       // the description: real line breaks in JSON
    MacNet,     // Mac: 0 shared, 1 no network (Windows: 2)
    LinuxGl,    // Linux: gl (3D acceleration) on, or off = CPU rendering (Windows: render 2)
}

const LINUX: &[(&str, &str, Kind)] = &[
    ("name", "display_name", Kind::Text),
    ("description", "description", Kind::Desc),
    ("distro", "distro_id", Kind::Text),
    ("iso", "iso", Kind::Text),
    ("pending", "pending_install", Kind::Bool),
    ("profile", "profile", Kind::Num),
    ("ram", "ram_gb", Kind::Num),
    ("cores", "cores", Kind::Num),
    ("diskgb", "disk_gb", Kind::Num),
    ("diskfull", "disk_full", Kind::Bool),
    ("render", "gl", Kind::LinuxGl),
    ("fullscreen", "fullscreen", Kind::Bool),
    ("shared", "shared_folder", Kind::Text),
    ("sharedwrite", "shared_write", Kind::Bool),
    ("sound", "sound", Kind::Bool),
    ("mic", "mic", Kind::Bool),
    ("cd", "cd", Kind::Text),
    ("nettype", "net_type", Kind::Num),
    ("forwards", "forwards", Kind::List(';')),
    ("disks", "extra_disks", Kind::List('|')),
    ("reclaim", "reclaim", Kind::Bool),
    ("lastrender", "last_boot", Kind::Text),
];

const MAC: &[(&str, &str, Kind)] = &[
    ("name", "displayName", Kind::Text),
    ("description", "description", Kind::Desc),
    ("distro", "distroId", Kind::Text),
    ("iso", "iso", Kind::Text),
    ("pending", "pendingInstall", Kind::Bool),
    ("profile", "profile", Kind::Num),
    ("ram", "ramGB", Kind::Num),
    ("cores", "cores", Kind::Num),
    ("diskgb", "diskGB", Kind::Num),
    ("diskfull", "diskFull", Kind::Bool),
    ("fullscreen", "fullscreen", Kind::Bool),
    ("shared", "sharedFolder", Kind::Text),
    ("sharedwrite", "sharedWrite", Kind::Bool),
    ("sound", "sound", Kind::Bool),
    ("mic", "mic", Kind::Bool),
    ("cd", "cd", Kind::Text),
    ("nettype", "netType", Kind::MacNet),
    ("disks", "extraDisks", Kind::List('|')),
    ("lastrender", "lastBoot", Kind::Text),
];

fn table(f: Format) -> &'static [(&'static str, &'static str, Kind)] {
    match f {
        Format::Linux => LINUX,
        Format::Mac => MAC,
        Format::Ini => &[],
    }
}

fn escape(v: &str) -> String {
    v.replace("\r\n", "\n").replace('\\', "\\\\").replace('\n', "\\n")
}
fn unescape(v: &str) -> String {
    let mut out = String::new();
    let mut chars = v.chars();
    while let Some(c) = chars.next() {
        if c == '\\' {
            match chars.next() {
                Some('n') => out.push('\n'),
                Some(x) => out.push(x),
                None => out.push('\\'),
            }
        } else {
            out.push(c);
        }
    }
    out
}

// A JSON value as vm.ini text.
fn to_text(v: &Value, kind: Kind) -> String {
    let b = |x: bool| if x { "1" } else { "0" }.to_string();
    match kind {
        Kind::Bool => v.as_bool().map(b).unwrap_or_default(),
        Kind::LinuxGl => v.as_bool().map(|gl| if gl { "0" } else { "2" }.to_string()).unwrap_or_default(),
        Kind::MacNet => v.as_i64().map(|n| if n >= 1 { "2" } else { "0" }.to_string()).unwrap_or_default(),
        Kind::Num => v.as_i64().map(|n| n.to_string()).unwrap_or_default(),
        Kind::List(sep) => v.as_array().map(|a| a.iter().filter_map(Value::as_str).collect::<Vec<_>>().join(&sep.to_string())).unwrap_or_default(),
        Kind::Desc => v.as_str().map(escape).unwrap_or_default(),
        Kind::Text => match v {
            Value::String(s) => s.clone(),
            Value::Bool(x) => b(*x),
            Value::Null => String::new(),
            other => other.to_string(),
        },
    }
}

// vm.ini text as a JSON value.
fn to_json(t: &str, kind: Kind) -> Value {
    match kind {
        Kind::Bool => Value::Bool(t == "1"),
        Kind::LinuxGl => Value::Bool(t != "2"),
        Kind::MacNet => Value::from(if t == "2" { 1 } else { 0 }),
        Kind::Num => t.parse::<i64>().map(Value::from).unwrap_or(Value::from(0)),
        Kind::List(sep) => Value::Array(t.split(sep).filter(|s| !s.is_empty()).map(|s| Value::String(s.into())).collect()),
        Kind::Desc => Value::String(unescape(t)),
        Kind::Text => Value::String(t.into()),
    }
}

pub struct VmConfig {
    pub dir: PathBuf,
    pub id: String,
    pub format: Format,
    file: PathBuf,
    ini: HashMap<String, String>,
}

impl VmConfig {
    pub fn load(dir: &Path) -> Option<VmConfig> {
        let (file, _) = settings_file(dir)?;
        Self::load_ini(&file, dir)
    }

    // Settings from a given file (a backup's copy), for a system kept in `dir`. The format is the file's own.
    pub fn load_ini(file: &Path, dir: &Path) -> Option<VmConfig> {
        let text = fs::read_to_string(file).ok()?;
        let id = dir.file_name()?.to_string_lossy().to_string();
        let (format, ini) = if text.trim_start().starts_with('{') {
            let m: Map<String, Value> = serde_json::from_str(&text).ok()?;
            let format = if m.contains_key("displayName") || m.contains_key("ramGB") { Format::Mac } else { Format::Linux };
            let t = table(format);
            let mut ini = HashMap::new();
            for (k, v) in &m {
                match t.iter().find(|(_, j, _)| j == k) {
                    Some((w, _, kind)) => {
                        ini.insert(w.to_string(), to_text(v, *kind));
                    }
                    // A setting only Windows has (kept in the JSON under its Windows name), or the other
                    // edition's own (kept as it is).
                    None => {
                        ini.insert(k.clone(), to_text(v, Kind::Text));
                    }
                }
            }
            (format, ini)
        } else {
            let ini = text.lines().filter_map(|l| l.split_once('=').map(|(k, v)| (k.trim().to_string(), v.trim().to_string()))).collect();
            (Format::Ini, ini)
        };
        Some(VmConfig { dir: dir.to_path_buf(), id, format, file: file.to_path_buf(), ini })
    }

    // Its settings file.
    pub fn file(&self) -> PathBuf {
        self.file.clone()
    }

    pub fn get(&self, k: &str) -> &str {
        self.ini.get(k).map(String::as_str).unwrap_or("")
    }
    fn num(&self, k: &str, d: u64) -> u64 {
        self.get(k).parse().unwrap_or(d)
    }
    fn flag(&self, k: &str, d: bool) -> bool {
        match self.get(k) {
            "" => d,
            v => v != "0",
        }
    }

    pub fn name(&self) -> String {
        if self.get("name").is_empty() { self.id.clone() } else { self.get("name").to_string() }
    }
    pub fn disk(&self) -> PathBuf {
        self.dir.join(disk_name(self.format, &self.id, 1))
    }
    // The name a new disk number `n` (2 or more) gets in its folder.
    pub fn new_disk_name(&self, n: usize) -> String {
        disk_name(self.format, &self.id, n)
    }
    // The boot disk first, then the others in the order they were added (a name in its folder, or a full path).
    pub fn all_disks(&self) -> Vec<PathBuf> {
        let mut v = vec![self.disk()];
        for e in self.get("disks").split('|').filter(|e| !e.is_empty()) {
            let p = PathBuf::from(e);
            v.push(if p.is_absolute() { p } else { self.dir.join(p) });
        }
        v
    }
    pub fn pending(&self) -> bool {
        self.get("pending") == "1"
    }
    pub fn iso(&self) -> PathBuf {
        PathBuf::from(self.get("iso"))
    }
    pub fn render(&self) -> u64 {
        self.num("render", 0)
    }
    pub fn venus(&self) -> bool {
        self.flag("venus", true)
    }
    pub fn gpu_mem_gb(&self) -> u64 {
        self.num("gpumem", 4).max(1)
    }
    pub fn displays(&self) -> u64 {
        self.num("displays", 1).clamp(1, 4)
    }
    pub fn net_type(&self) -> u64 {
        self.num("nettype", 0).min(2)
    }
    pub fn reclaim(&self) -> bool {
        self.flag("reclaim", true)
    }
    pub fn sound(&self) -> bool {
        self.flag("sound", true)
    }
    pub fn mic(&self) -> bool {
        self.flag("mic", true)
    }
    pub fn fullscreen(&self) -> bool {
        self.get("fullscreen") == "1"
    }
    pub fn cd(&self) -> &str {
        self.get("cd")
    }
    pub fn shared(&self) -> &str {
        self.get("shared")
    }
    pub fn shared_write(&self) -> bool {
        self.get("sharedwrite") == "1"
    }

    // Port forwards as QEMU hostfwd rules: "tcp:2223:22" on this PC only, "lan tcp:8080:80" for the network too.
    // Systems set up before forwards existed get their SSH port.
    pub fn host_forwards(&self) -> Vec<String> {
        let lines: Vec<String> = if self.get("forwards").is_empty() {
            vec![format!("tcp:{}:22", self.num("sshport", 2223))]
        } else {
            self.get("forwards").split(';').map(String::from).collect()
        };
        lines
            .iter()
            .filter_map(|l| {
                let l = l.trim();
                let (lan, rest) = match l.strip_prefix("lan") {
                    Some(r) if r.starts_with(char::is_whitespace) => (true, r.trim()),
                    _ => (false, l),
                };
                let mut p = rest.split(':');
                let (proto, host, guest) = (p.next()?.to_lowercase(), p.next()?.parse::<u16>().ok()?, p.next()?.parse::<u16>().ok()?);
                if (proto != "tcp" && proto != "udp") || host == 0 || guest == 0 || p.next().is_some() {
                    return None;
                }
                Some(format!("hostfwd={proto}:{}:{host}-:{guest}", if lan { "0.0.0.0" } else { "127.0.0.1" }))
            })
            .collect()
    }

    // Cores and memory, by the same rules as the Windows edition: Balanced, Maximum, or set by hand.
    pub fn resources(&self, host: &Host) -> (usize, u64) {
        match self.num("profile", 0) {
            1 => (host.max_cores(), host.max_ram()),
            2 => ((self.num("cores", 4) as usize).clamp(1, host.max_cores()), self.num("ram", 8).clamp(1, host.max_ram())),
            _ => ((host.threads / 2).max(2), (host.ram_gb / 2).clamp(2, 16)),
        }
    }

    // Changes one setting, leaving every other as it is.
    pub fn set(&self, key: &str, value: &str) {
        let _ = self.set_all(&[(key, value.to_string())]);
    }

    // Changes several settings in one write, leaving every other as it is.
    pub fn set_all(&self, values: &[(&str, String)]) -> std::io::Result<()> {
        let text = fs::read_to_string(&self.file)?;
        fs::write(&self.file, apply(self.format, &text, values))
    }
}

// The file's text with `values` (Windows names, vm.ini text) changed or added.
fn apply(format: Format, text: &str, values: &[(&str, String)]) -> String {
    if format == Format::Ini {
        let mut done = vec![false; values.len()];
        let mut lines: Vec<String> = text
            .lines()
            .map(|l| {
                let key = l.split_once('=').map(|(k, _)| k.trim());
                match values.iter().position(|(k, _)| Some(*k) == key) {
                    Some(i) => {
                        done[i] = true;
                        format!("{}={}", values[i].0, values[i].1)
                    }
                    None => l.to_string(),
                }
            })
            .collect();
        for (i, (k, v)) in values.iter().enumerate() {
            if !done[i] {
                lines.push(format!("{k}={v}"));
            }
        }
        return lines.join("\r\n") + "\r\n";
    }
    let mut m: Map<String, Value> = serde_json::from_str(text).unwrap_or_default();
    let t = table(format);
    for (k, v) in values {
        match t.iter().find(|(w, _, _)| w == k) {
            Some((_, j, kind)) => m.insert(j.to_string(), to_json(v, *kind)),
            None => m.insert(k.to_string(), Value::String(v.clone())),
        };
    }
    serde_json::to_string_pretty(&m).unwrap_or_default()
}

// Makes a new system's settings file in `dir` (this computer's edition's format) from Windows-named values.
pub fn create(dir: &Path, values: &[(&str, String)]) -> std::io::Result<VmConfig> {
    let id = dir.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default();
    let start = match NATIVE {
        Format::Ini => String::new(),
        Format::Linux => serde_json::json!({ "id": id, "created": now() }).to_string(),
        Format::Mac => serde_json::json!({ "id": id }).to_string(),
    };
    fs::create_dir_all(dir)?;
    let file = dir.join(NATIVE.file());
    fs::write(&file, apply(NATIVE, &start, values))?;
    VmConfig::load(dir).ok_or_else(|| std::io::Error::other("the new settings can't be read"))
}

// "2026-11-12 09:30", as the Linux edition records when a system was made (UTC).
pub fn now() -> String {
    let secs = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
    let days = (secs / 86400) as i64;
    let (z, era) = (days + 719_468, (days + 719_468).div_euclid(146_097));
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let (d, m) = (doy - (153 * mp + 2) / 5 + 1, if mp < 10 { mp + 3 } else { mp - 9 });
    format!("{}-{m:02}-{d:02} {:02}:{:02}", yoe + era * 400 + i64::from(m <= 2), secs / 3600 % 24, secs / 60 % 60)
}

// Disk number `n` (1 is the boot disk) of a system with this id, in its edition's naming.
pub fn disk_name(format: Format, id: &str, n: usize) -> String {
    match (format, n) {
        (Format::Ini, 1) => format!("{id}.qcow2"),
        (Format::Ini, n) => format!("{id}-disk{n}.qcow2"),
        (Format::Linux, 1) => "disk.qcow2".into(),
        (Format::Linux, n) => format!("disk{n}.qcow2"),
        (Format::Mac, 1) => "disk.img".into(),
        (Format::Mac, n) => format!("disk{n}.img"),
    }
}

pub struct Host {
    pub threads: usize,
    pub ram_gb: u64,
}

impl Host {
    pub fn max_cores(&self) -> usize {
        self.threads.saturating_sub(2).max(self.threads.min(2))
    }
    pub fn max_ram(&self) -> u64 {
        (self.ram_gb * 3 / 4).max(1)
    }
}

pub fn find(id: &str) -> Option<VmConfig> {
    system_dirs().iter().filter_map(|d| VmConfig::load(d)).find(|c| c.id == id)
}

// Each edition's file read and written back with a change, keeping what the other edition doesn't know.
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn reads_and_writes_each_edition() {
        let linux = r#"{"id":"fed","display_name":"Fedora","description":"line 1\nline 2","gl":false,"net_type":1,
            "forwards":["tcp:2222:22"],"extra_disks":["disk2.qcow2"],"pending_install":true,"ram_gb":6,"created":"x"}"#;
        let dir = std::env::temp_dir().join("trylinux-config-test").join("fed");
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("vm.json"), linux).unwrap();
        let c = VmConfig::load(&dir).unwrap();
        assert_eq!(c.format, Format::Linux);
        assert_eq!((c.name().as_str(), c.get("description"), c.render(), c.net_type(), c.pending()), ("Fedora", "line 1\\nline 2", 2, 1, true));
        assert_eq!(c.all_disks()[1], dir.join("disk2.qcow2"));
        assert_eq!(c.disk(), dir.join("disk.qcow2"));
        c.set_all(&[("ram", "8".into()), ("render", "0".into()), ("venus", "1".into()), ("description", "a\\nb".into())]).unwrap();
        let v: Value = serde_json::from_str(&fs::read_to_string(dir.join("vm.json")).unwrap()).unwrap();
        assert_eq!((v["ram_gb"].as_i64(), v["gl"].as_bool(), v["venus"].as_str(), v["description"].as_str(), v["created"].as_str()),
            (Some(8), Some(true), Some("1"), Some("a\nb"), Some("x")));

        let mac = r#"{"id":"deb","displayName":"Debian","netType":1,"extraDisks":["disk2.img"],"width":1920,"rosetta":true}"#;
        fs::remove_file(dir.join("vm.json")).unwrap();
        fs::write(dir.join("config.json"), mac).unwrap();
        let c = VmConfig::load(&dir).unwrap();
        assert_eq!((c.format, c.name().as_str(), c.net_type()), (Format::Mac, "Debian", 2));
        c.set_all(&[("nettype", "0".into()), ("name", "Debian 13".into())]).unwrap();
        let v: Value = serde_json::from_str(&fs::read_to_string(dir.join("config.json")).unwrap()).unwrap();
        assert_eq!((v["netType"].as_i64(), v["displayName"].as_str(), v["width"].as_i64(), v["rosetta"].as_bool()),
            (Some(0), Some("Debian 13"), Some(1920), Some(true)));
        let _ = fs::remove_dir_all(dir.parent().unwrap());
    }
}
