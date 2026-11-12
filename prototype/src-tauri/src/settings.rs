// Settings: a system's settings for the Settings page, saving them back to its vm.ini (only the keys Settings
// changes, so the Windows edition reads the file as before), and what Hardware > Disks shows about a disk.

use crate::config;
use crate::shortcuts;
use crate::vm::Vms;
use crate::Probe;
use serde::Serialize;
use std::collections::HashMap;
use std::path::Path;
use std::process::Command;
use tauri::State;

#[derive(Serialize)]
pub struct Settings {
    id: String,
    name: String,
    os: String,
    distro: String,
    dir: String,
    running: bool,
    description: String,
    fullscreen: bool,
    fs_display: String,
    displays: u64,
    render: u64,
    venus: bool,
    gpu_mem: u64,
    last_render: String,
    profile: u64,
    ram: u64,
    cores: u64,
    disks: Vec<String>,
    disk_full: bool,
    reclaim: bool,
    cd: String,
    sound: bool,
    mic: bool,
    net_type: u64,
    forwards: Vec<String>,
    shared: String,
    shared_write: bool,
    // Its desktop shortcut (whether there is one, and whether it starts the system) and starting at sign-in.
    has_shortcut: bool,
    shortcuts: bool, // this computer has shortcuts (macOS hasn't)
    platform: &'static str, // "windows", "linux" or "macos", for what the page says
    shortcut_direct: bool,
    sign_in: bool,
    // This PC: what's left for this system once Windows and other running systems keep theirs.
    threads: usize,
    total_ram: u64,
    max_cores: usize,
    max_ram: u64,
    max_gpu_mem: u64,
    gpu_name: String,
    // What Balanced and Maximum would give, shown greyed in the fields until Manual is chosen.
    balanced: (usize, u64),
    maximum: (usize, u64),
}

// Off the main thread: finding the desktop folder and the sign-in entry asks Windows.
#[tauri::command(async)]
pub fn settings(probe: State<Probe>, vms: State<Vms>, id: String) -> Result<Settings, String> {
    let cfg = config::find(&id).ok_or("This system's folder can't be found any more.")?;
    let host = probe.host();
    let running = vms.0.lock().unwrap().contains_key(&id) || crate::running_elsewhere(&cfg);
    // Other running systems' cores and memory aren't free to give this one.
    let (mut other_cores, mut other_ram) = (0, 0);
    for (other, r) in vms.0.lock().unwrap().iter() {
        if other == &id || r.held {
            continue;
        }
        if let Some(c) = config::find(other) {
            let (n, gb) = c.resources(&host);
            other_cores += n;
            other_ram += gb;
        }
    }
    let max_cores = host.max_cores().saturating_sub(other_cores).max(1);
    let max_ram = host.max_ram().saturating_sub(other_ram).max(1);
    let (max_gpu_mem, gpu_name) = gpu(host.ram_gb);
    let forwards = if cfg.get("forwards").is_empty() {
        vec![format!("tcp:{}:22", cfg.get("sshport").parse::<u16>().unwrap_or(2223))]
    } else {
        cfg.get("forwards").split(';').map(String::from).collect()
    };
    let num = |k: &str, d: u64| cfg.get(k).parse().unwrap_or(d);
    Ok(Settings {
        id: cfg.id.clone(),
        name: cfg.name(),
        os: crate::os_label(&cfg),
        distro: cfg.get("distro").into(),
        dir: cfg.dir.to_string_lossy().into(),
        running,
        description: unescape(cfg.get("description")),
        fullscreen: cfg.fullscreen(),
        fs_display: cfg.get("fsdisplay").into(),
        displays: cfg.displays(),
        render: cfg.render().min(2),
        venus: cfg.venus(),
        gpu_mem: cfg.gpu_mem_gb(),
        last_render: cfg.get("lastrender").into(),
        profile: num("profile", 0).min(2),
        ram: num("ram", 8),
        cores: num("cores", 4),
        disks: cfg.all_disks().iter().map(|d| d.to_string_lossy().into()).collect(),
        disk_full: cfg.get("diskfull") == "1",
        reclaim: cfg.reclaim(),
        cd: cfg.cd().into(),
        sound: cfg.sound(),
        mic: cfg.mic(),
        net_type: cfg.net_type(),
        forwards,
        shared: cfg.shared().into(),
        shared_write: cfg.shared_write(),
        has_shortcut: shortcuts::shortcut_exists(&cfg.id, &cfg.name()),
        shortcuts: shortcuts::HAS_SHORTCUTS,
        platform: std::env::consts::OS,
        shortcut_direct: cfg.get("shortcutdirect") != "0",
        sign_in: shortcuts::runs_at_sign_in(&cfg.id),
        threads: host.threads,
        total_ram: host.ram_gb,
        max_cores,
        max_ram,
        max_gpu_mem,
        gpu_name,
        balanced: ((host.threads / 2).max(2), (host.ram_gb / 2).clamp(2, 16)),
        maximum: (host.max_cores(), host.max_ram()),
    })
}

// A description is one line in vm.ini: a backslash and a line break are written as \\ and \n.
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
fn escape(v: &str) -> String {
    v.replace("\r\n", "\n").replace('\\', "\\\\").replace('\n', "\\n")
}

// "tcp:2223:22", or "lan tcp:8080:80" for other devices on the network too.
fn valid_forward(line: &str) -> bool {
    let rest = match line.strip_prefix("lan") {
        Some(r) if r.starts_with(char::is_whitespace) => r.trim(),
        _ => line,
    };
    let p: Vec<&str> = rest.split(':').collect();
    p.len() == 3
        && ["tcp", "udp"].contains(&p[0].to_lowercase().as_str())
        && p[1..].iter().all(|n| n.len() <= 5 && n.parse::<u32>().map(|n| (1..=65535).contains(&n)).unwrap_or(false))
}

#[derive(serde::Deserialize)]
pub struct Changes {
    name: String,
    description: String,
    fullscreen: bool,
    fs_display: String,
    displays: u64,
    render: u64,
    venus: bool,
    gpu_mem: u64,
    profile: u64,
    ram: u64,
    cores: u64,
    reclaim: bool,
    sound: bool,
    mic: bool,
    net_type: u64,
    forwards: Vec<String>,
    shared: String,
    shared_write: bool,
    shortcut_direct: bool,
    sign_in: bool,
}

// Problems come back as (tab, message), so the page can show the tab the problem is on.
#[tauri::command(async)]
pub fn save_settings(app: tauri::AppHandle, id: String, c: Changes) -> Result<(), (String, String)> {
    let cfg = config::find(&id).ok_or(("".into(), "This system's folder can't be found any more.".into()))?;
    let forwards: Vec<String> = c.forwards.iter().map(|l| l.trim().to_string()).filter(|l| !l.is_empty()).collect();
    if let Some(bad) = forwards.iter().find(|l| !valid_forward(l)) {
        return Err(("devices".into(), format!("\"{bad}\" isn't a port forward. Use tcp:HOST:GUEST, e.g. tcp:2223:22.")));
    }
    let shared = c.shared.trim();
    if !shared.is_empty() && !Path::new(shared).is_dir() {
        return Err(("devices".into(), "The shared folder doesn't exist.".into()));
    }
    if shared.contains(',') {
        return Err(("devices".into(), "QEMU can't share a folder whose path contains a comma.".into()));
    }
    let name = c.name.trim();
    let b = |v: bool| if v { "1" } else { "0" }.to_string();
    let values: Vec<(&str, String)> = vec![
        ("name", if name.is_empty() { cfg.name() } else { name.to_string() }),
        ("description", escape(c.description.trim())),
        ("fullscreen", b(c.fullscreen)),
        ("fsdisplay", c.fs_display.clone()),
        ("displays", c.displays.clamp(1, 4).to_string()),
        ("render", c.render.min(2).to_string()),
        ("venus", b(c.venus)),
        ("gpumem", c.gpu_mem.max(1).to_string()),
        ("profile", c.profile.min(2).to_string()),
        ("ram", c.ram.max(1).to_string()),
        ("cores", c.cores.max(1).to_string()),
        ("reclaim", b(c.reclaim)),
        ("sound", b(c.sound)),
        ("mic", b(c.mic)),
        ("nettype", c.net_type.min(2).to_string()),
        ("forwards", forwards.join(";")),
        ("shared", shared.to_string()),
        ("sharedwrite", b(c.shared_write)),
        ("shortcutdirect", b(c.shortcut_direct)),
    ];
    let old_name = cfg.name();
    cfg.set_all(&values).map_err(|e| ("".to_string(), format!("Couldn't save the settings: {e}")))?;
    crate::changed(&app);

    // Starting at sign-in, and the desktop shortcut kept up to date (renamed with the system).
    let new_name = values[0].1.clone();
    let mut extra = vec![];
    if let Err(e) = shortcuts::set_run_at_sign_in(&cfg.id, &new_name, c.sign_in) {
        extra.push(format!("starting at sign-in couldn't be changed ({e})"));
    }
    if shortcuts::shortcut_exists(&cfg.id, &old_name) {
        if old_name != new_name {
            shortcuts::remove_shortcut(&cfg.id, &old_name);
        }
        if let Err(e) = shortcuts::write_shortcut(&cfg.id, &new_name, c.shortcut_direct) {
            extra.push(format!("the desktop shortcut couldn't be updated ({e})"));
        }
    }
    if !extra.is_empty() {
        return Err(("general".into(), format!("The settings were saved, but {}.", extra.join(", and "))));
    }
    Ok(())
}

// Add (or update) desktop shortcut: saves whether it starts the system directly, then writes it.
#[tauri::command(async)]
pub fn add_shortcut(id: String, direct: bool) -> Result<String, String> {
    let cfg = config::find(&id).ok_or("This system's folder can't be found any more.")?;
    cfg.set_all(&[("shortcutdirect", if direct { "1" } else { "0" }.into())]).map_err(|e| e.to_string())?;
    shortcuts::write_shortcut(&cfg.id, &cfg.name(), direct)?;
    Ok(cfg.name())
}

// Puts an ISO in the CD/DVD drive, or empties it (an empty path), at once, like a real drive: QEMU changes the
// disc while the system runs, and vm.ini keeps it for the next start. Returns a note when only the latter worked.
#[tauri::command]
pub fn set_disc(vms: State<Vms>, id: String, iso: String) -> Result<Option<String>, String> {
    let cfg = config::find(&id).ok_or("This system's folder can't be found any more.")?;
    if iso.contains(',') {
        return Err("QEMU can't open a disc whose path contains a comma.".into());
    }
    let port = vms.0.lock().unwrap().get(&id).filter(|r| !r.held).map(|r| r.port());
    let mut note = None;
    if let Some(port) = port {
        let path = serde_json::to_string(&iso).unwrap();
        let reply = if iso.is_empty() {
            crate::vm::qmp_query(port, "eject", Some("{\"id\":\"cd\",\"force\":true}"))
        } else {
            crate::vm::qmp_query(port, "blockdev-change-medium",
                Some(&format!("{{\"id\":\"cd\",\"filename\":{path},\"format\":\"raw\",\"read-only-mode\":\"read-only\"}}")))
        };
        let failed = match reply {
            Ok(v) => v.get("error").map(|e| e["desc"].as_str().unwrap_or("QEMU refused").to_string()),
            Err(e) => Some(e),
        };
        if let Some(e) = failed {
            note = Some(format!("The drive changes the next time {} starts. ({e})", cfg.name()));
        }
    }
    cfg.set_all(&[("cd", iso)]).map_err(|e| format!("Couldn't save the drive's disc: {e}"))?;
    Ok(note)
}

#[derive(Serialize, Default)]
pub struct DiskReport {
    pub actual: Option<u64>,
    pub size: Option<u64>,
    pub snapshots: usize,
    free: Option<u64>,
    drive: String,
    pub error: Option<String>,
}

// The room left on the drive a path is on, and the drive's name ("C:").
pub fn free_on(path: &str) -> Option<(u64, String)> {
    let disks = sysinfo::Disks::new_with_refreshed_list();
    let lower = path.to_lowercase();
    disks
        .iter()
        .filter(|d| lower.starts_with(&d.mount_point().to_string_lossy().to_lowercase()))
        .max_by_key(|d| d.mount_point().as_os_str().len())
        .map(|d| (d.available_space(), d.mount_point().to_string_lossy().trim_end_matches('\\').to_string()))
}

// What qemu-img says about a disk (its file's size, its maximum size, its snapshots) and the room left on
// its drive. Off the main thread: qemu-img takes a moment.
#[tauri::command(async)]
pub fn disk_info(path: String) -> DiskReport {
    let mut r = DiskReport::default();
    let p = Path::new(&path);
    if !p.exists() {
        r.error = Some("The file is missing. It may have been moved or deleted.".into());
        return r;
    }
    if let Some((free, drive)) = free_on(&path) {
        r.free = Some(free);
        r.drive = drive;
    }
    let Some(img) = crate::vm::qemu_img() else {
        // A raw disk (macOS) without qemu-img: its size is the file's length, and what it takes on the drive
        // is the space the file really uses (it's sparse).
        #[cfg(unix)]
        if let Ok(m) = std::fs::metadata(p) {
            use std::os::unix::fs::MetadataExt;
            r.size = Some(m.len());
            r.actual = Some(m.blocks() * 512);
            return r;
        }
        r.error = Some("qemu-img isn't installed.".into());
        return r;
    };
    let mut cmd = Command::new(img);
    cmd.args(["info", "--force-share", "--output=json", &path]);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    match cmd.output() {
        Ok(o) if o.status.success() => {
            let v: serde_json::Value = serde_json::from_slice(&o.stdout).unwrap_or_default();
            r.actual = v["actual-size"].as_u64();
            r.size = v["virtual-size"].as_u64();
            r.snapshots = v["snapshots"].as_array().map_or(0, |a| a.len());
        }
        Ok(o) => r.error = Some(format!("Couldn't read the disk: {}", String::from_utf8_lossy(&o.stderr).trim())),
        Err(e) => r.error = Some(format!("Couldn't read the disk: {e}")),
    }
    r
}

// The GPU memory Linux can be given: a dedicated card's own memory, else half of this PC's (shared).
// Read as the Windows edition does, from the display adapters' registry keys.
fn gpu(ram_gb: u64) -> (u64, String) {
    let (mut best, mut name) = (0u64, String::new());
    #[cfg(windows)]
    {
        let class = r"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        let mut cmd = Command::new("reg");
        cmd.args(["query", class, "/s", "/v", "HardwareInformation.qwMemorySize"]);
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000);
        let mut cmd2 = Command::new("reg");
        cmd2.args(["query", class, "/s", "/v", "DriverDesc"]);
        cmd2.creation_flags(0x0800_0000);
        // Each adapter's key, then its value: pair them up by key.
        let read = |c: &mut Command| -> HashMap<String, String> {
            let text = c.output().map(|o| String::from_utf8_lossy(&o.stdout).to_string()).unwrap_or_default();
            let mut key = String::new();
            let mut out = HashMap::new();
            for line in text.lines() {
                if line.starts_with("HKEY_") {
                    key = line.trim().to_string();
                } else if let Some(i) = line.find("REG_") {
                    let v = line[i..].split_whitespace().skip(1).collect::<Vec<_>>().join(" ");
                    out.insert(key.clone(), v);
                }
            }
            out
        };
        let sizes = read(&mut cmd);
        let names = read(&mut cmd2);
        for (key, v) in sizes {
            let size = u64::from_str_radix(v.trim_start_matches("0x"), 16).unwrap_or(0);
            if size > best {
                best = size;
                name = names.get(&key).cloned().unwrap_or_default();
            }
        }
    }
    let dedicated = (best + (1 << 29)) >> 30; // to the nearest GiB: a "16 GB" card reports a little under 16
    if dedicated >= 2 { (dedicated, name) } else { ((ram_gb / 2).max(1), String::new()) }
}
