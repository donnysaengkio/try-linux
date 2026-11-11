// Try Linux as one Tauri app: this Rust side does the work (systems, QEMU, the catalog) and the interface
// (../ui) is the same HTML and CSS on Windows, macOS and Linux.
//
// So far (Windows): Virtual Machines lists the systems the Windows edition made, starts them with WINQ-EMU
// the way it does, and runs the Power menu and the console; Create shows Choose a Linux OS with the catalog.

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod config;
mod live;
mod vm;
mod window;

use config::{Host, VmConfig};
use serde::Serialize;
use std::collections::HashMap;
use std::fs;
use std::sync::Mutex;
use sysinfo::{ProcessesToUpdate, System};
use tauri::{AppHandle, State};
use vm::{Problem, Vms};

const CATALOG: &str = "https://raw.githubusercontent.com/donnysaengkio/try-linux/main/catalog/distros.json";

// The shared Lucide icons (../../icons), built in.
const ICONS: &[(&str, &str)] = &[
    ("arrow-left", include_str!("../../../icons/arrow-left.svg")),
    ("chevron-down", include_str!("../../../icons/chevron-down.svg")),
    ("chevron-right", include_str!("../../../icons/chevron-right.svg")),
    ("menu", include_str!("../../../icons/menu.svg")),
    ("monitor", include_str!("../../../icons/monitor.svg")),
    ("pencil", include_str!("../../../icons/pencil.svg")),
    ("settings", include_str!("../../../icons/settings.svg")),
    ("terminal", include_str!("../../../icons/terminal.svg")),
    ("undo-2", include_str!("../../../icons/undo-2.svg")),
    ("usb", include_str!("../../../icons/usb.svg")),
    ("wrench", include_str!("../../../icons/wrench.svg")),
    ("x", include_str!("../../../icons/x.svg")),
];

#[derive(Serialize)]
struct Vm {
    id: String,
    name: String,
    os: String,
    status: String,
    // "off", "running", "paused", "held" (console open, machine off), or "elsewhere" (another Try Linux runs it)
    state: String,
    cores: usize,
    ram_gb: u64,
    disk: String,
    // Live, while running: CPU as a share of its cores, and the memory QEMU holds.
    cpu: Option<f32>,
    mem: Option<u64>,
    // Live, once Linux reports it: the memory Linux is using (else `mem`), and disk reads and writes per second.
    guest_mem: Option<u64>,
    disk_rate: Option<f64>,
}

// One System kept between calls, so CPU use is measured over the time since the last look.
pub struct Probe(Mutex<System>);

impl Probe {
    pub fn host(&self) -> Host {
        let mut sys = self.0.lock().unwrap();
        sys.refresh_memory();
        Host {
            threads: std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4),
            ram_gb: sys.total_memory() >> 30,
        }
    }
}

// A QEMU process that uses this system's boot disk but wasn't started here (another copy of Try Linux).
fn qemu_for(sys: &System, cfg: &VmConfig) -> Option<u32> {
    let disk = cfg.disk().to_string_lossy().to_lowercase();
    sys.processes()
        .values()
        .find(|p| {
            p.name().to_string_lossy().to_lowercase().starts_with("qemu-system")
                && p.cmd().iter().any(|a| a.to_string_lossy().to_lowercase().contains(&disk))
        })
        .map(|p| p.pid().as_u32())
}

pub fn running_elsewhere(cfg: &VmConfig) -> bool {
    let mut sys = System::new();
    sys.refresh_processes(ProcessesToUpdate::All, true);
    qemu_for(&sys, cfg).is_some()
}

fn size_text(bytes: u64) -> String {
    if bytes >= 1 << 30 {
        format!("{:.1} GB", bytes as f64 / (1u64 << 30) as f64)
    } else {
        format!("{} MB", bytes >> 20)
    }
}

// The distribution names the Windows edition knows (for systems whose guest agent hasn't reported one).
fn distro_name(id: &str) -> Option<&'static str> {
    match id {
        "fedora" => Some("Fedora Workstation"),
        "ubuntu" => Some("Ubuntu Desktop"),
        "debian" => Some("Debian (GNOME)"),
        "mint" => Some("Linux Mint (Cinnamon)"),
        "zorin" => Some("Zorin OS Core"),
        _ => None,
    }
}

// Off the main thread: reading a running system's activity asks QEMU, which can take a moment.
#[tauri::command(async)]
fn systems(probe: State<Probe>, vms: State<Vms>, live: State<live::Live>) -> Vec<Vm> {
    let host = probe.host();
    let mut asks = vec![];   // (index, id, QMP port) of the systems to read activity from, after the locks are let go
    let mut sys = probe.0.lock().unwrap();
    sys.refresh_processes(ProcessesToUpdate::All, true);
    let mine = vms.0.lock().unwrap();
    let mut out = vec![];
    for dir in config::system_dirs() {
        let Some(cfg) = VmConfig::load(&dir) else { continue };
        let (cores, ram_gb) = cfg.resources(&host);
        let used: u64 = cfg.all_disks().iter().filter_map(|d| fs::metadata(d).ok()).map(|m| m.len()).sum();
        let os = fs::read_to_string(dir.join("guest-os.txt"))
            .ok()
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty())
            .or_else(|| distro_name(cfg.get("distro")).map(String::from))
            .unwrap_or_else(|| "Linux".into());
        let (state, status, pid) = match mine.get(&cfg.id) {
            Some(r) if r.held => ("held", "Off, console open", Some(r.pid)),
            Some(r) if r.paused => ("paused", "Paused", Some(r.pid)),
            Some(r) => ("running", "Running", Some(r.pid)),
            None => match qemu_for(&sys, &cfg) {
                Some(p) => ("elsewhere", "Running", Some(p)),
                None if cfg.pending() => ("off", "Ready to install", None),
                None => ("off", "Powered off", None),
            },
        };
        let process = pid.and_then(|p| sys.process(sysinfo::Pid::from_u32(p)));
        let live = process.filter(|_| state == "running" || state == "elsewhere" || state == "paused");
        if live.is_some() {
            let port = if state == "elsewhere" {
                live.and_then(|p| live::port_from_args(&p.cmd().iter().map(|a| a.to_string_lossy().to_string()).collect::<Vec<_>>()))
            } else {
                mine.get(&cfg.id).map(|r| r.port())
            };
            if let Some(port) = port {
                asks.push((out.len(), cfg.id.clone(), port));
            }
        }
        out.push(Vm {
            name: cfg.name(),
            id: cfg.id.clone(),
            os,
            status: status.into(),
            state: state.into(),
            cores,
            ram_gb,
            disk: size_text(used),
            cpu: live.map(|p| (p.cpu_usage() / cores as f32).clamp(0.0, 100.0)),
            mem: live.map(|p| p.memory()),
            guest_mem: None,
            disk_rate: None,
        });
    }
    drop(mine);
    drop(sys);
    for (i, id, port) in asks {
        let r = live.read(&id, port);
        out[i].guest_mem = r.guest_used;
        out[i].disk_rate = r.disk_rate;
    }
    out.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
    out
}

#[tauri::command]
fn start(app: AppHandle, probe: State<Probe>, vms: State<Vms>, id: String) -> Result<(), Problem> {
    vm::start(&app, &vms, &probe.host(), &id, None, false)
}

// Console: a running system's window to the front; for one that's off, its window with the machine still off.
#[tauri::command]
fn console(app: AppHandle, probe: State<Probe>, vms: State<Vms>, id: String) -> Result<(), Problem> {
    let pid = vms.0.lock().unwrap().get(&id).map(|r| r.pid);
    if let Some(pid) = pid {
        window::front(pid);
        return Ok(());
    }
    vm::start(&app, &vms, &probe.host(), &id, None, true)
}

#[tauri::command]
fn power(app: AppHandle, vms: State<Vms>, id: String, action: String) -> Result<(), String> {
    vm::power(&app, &vms, &id, &action)
}

// The catalog's text, as published in the repository.
#[tauri::command]
async fn catalog() -> Result<String, String> {
    tauri::async_runtime::spawn_blocking(|| {
        ureq::get(CATALOG).call().map_err(|e| e.to_string())?.into_string().map_err(|e| e.to_string())
    })
    .await
    .map_err(|e| e.to_string())?
}

// The page to open on (TRYLINUX_PAGE), for screenshots; empty for Virtual Machines.
#[tauri::command]
fn start_page() -> String {
    std::env::var("TRYLINUX_PAGE").unwrap_or_default()
}

#[tauri::command]
fn icons() -> HashMap<&'static str, &'static str> {
    ICONS.iter().copied().collect()
}

// A distribution's web page, in the default browser (https only).
#[tauri::command]
fn open_page(url: String) -> Result<(), String> {
    if !url.starts_with("https://") {
        return Err("Only https pages open.".into());
    }
    #[cfg(target_os = "windows")]
    let r = std::process::Command::new("explorer").arg(&url).spawn();
    #[cfg(target_os = "macos")]
    let r = std::process::Command::new("open").arg(&url).spawn();
    #[cfg(all(unix, not(target_os = "macos")))]
    let r = std::process::Command::new("xdg-open").arg(&url).spawn();
    r.map(|_| ()).map_err(|e| e.to_string())
}

fn main() {
    tauri::Builder::default()
        .manage(Probe(Mutex::new(System::new())))
        .manage(Vms::default())
        .manage(live::Live::default())
        .invoke_handler(tauri::generate_handler![systems, start, console, power, catalog, icons, open_page, start_page])
        .run(tauri::generate_context!())
        .expect("Try Linux couldn't start");
}
