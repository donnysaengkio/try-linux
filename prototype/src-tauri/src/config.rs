// A system's settings, read from its vm.ini exactly as the Windows edition writes it, and where systems live.

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

pub struct VmConfig {
    pub dir: PathBuf,
    pub id: String,
    ini: HashMap<String, String>,
}

impl VmConfig {
    pub fn load(dir: &Path) -> Option<VmConfig> {
        let text = fs::read_to_string(dir.join("vm.ini")).ok()?;
        let ini = text
            .lines()
            .filter_map(|l| l.split_once('=').map(|(k, v)| (k.trim().to_string(), v.trim().to_string())))
            .collect::<HashMap<_, _>>();
        let id = dir.file_name()?.to_string_lossy().to_string();
        Some(VmConfig { dir: dir.to_path_buf(), id, ini })
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
        self.dir.join(format!("{}.qcow2", self.id))
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

    // Changes one setting in vm.ini, leaving every other line as it is.
    pub fn set(&self, key: &str, value: &str) {
        let path = self.dir.join("vm.ini");
        let Ok(text) = fs::read_to_string(&path) else { return };
        let mut found = false;
        let mut lines: Vec<String> = text
            .lines()
            .map(|l| {
                if l.split_once('=').map(|(k, _)| k.trim() == key).unwrap_or(false) {
                    found = true;
                    format!("{key}={value}")
                } else {
                    l.to_string()
                }
            })
            .collect();
        if !found {
            lines.push(format!("{key}={value}"));
        }
        let _ = fs::write(&path, lines.join("\r\n") + "\r\n");
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

// The storage folder (Settings can move it) and any systems kept in a folder of their own.
pub fn system_dirs() -> Vec<PathBuf> {
    let local = std::env::var("LOCALAPPDATA").map(PathBuf::from).unwrap_or_default();
    let mut store = local.join("TryLinux");
    let mut extra = vec![];
    let ini = std::env::var("APPDATA").map(|a| PathBuf::from(a).join("TryLinux").join("settings.ini")).unwrap_or_default();
    if let Ok(text) = fs::read_to_string(ini) {
        for line in text.lines() {
            if let Some(v) = line.strip_prefix("storage=") {
                if !v.trim().is_empty() {
                    store = PathBuf::from(v.trim());
                }
            }
            if let Some(v) = line.strip_prefix("vmdirs=") {
                extra.extend(v.split('|').map(str::trim).filter(|d| !d.is_empty()).map(PathBuf::from));
            }
        }
    }
    let mut dirs: Vec<PathBuf> = fs::read_dir(store.join("vms"))
        .map(|r| r.filter_map(|e| e.ok()).map(|e| e.path()).filter(|p| p.is_dir()).collect())
        .unwrap_or_default();
    for d in extra {
        if !dirs.iter().any(|x| x.to_string_lossy().eq_ignore_ascii_case(&d.to_string_lossy())) {
            dirs.push(d);
        }
    }
    dirs
}

pub fn find(id: &str) -> Option<VmConfig> {
    system_dirs().iter().filter_map(|d| VmConfig::load(d)).find(|c| c.id == id)
}
