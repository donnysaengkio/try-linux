// Try Linux as one Tauri app: this Rust side does the work (systems, QEMU, the catalog) and the interface
// (../ui) is the same HTML and CSS on Windows, macOS and Linux.
//
// So far (Windows): Virtual Machines lists the systems the Windows edition made, starts them with WINQ-EMU
// the way it does, and runs the Power menu and the console; Create shows Choose a Linux OS with the catalog.

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod app;
mod config;
mod paths;
mod create;
mod disks;
mod legacy;
mod live;
mod log;
#[cfg(target_os = "macos")]
mod mac;
mod recovery;
mod settings;
mod setup;
mod shortcuts;
mod vm;
mod window;

use config::{Host, VmConfig};
use serde::Serialize;
use std::collections::HashMap;
use std::fs;
use std::sync::Mutex;
use sysinfo::{ProcessesToUpdate, System};
use tauri::menu::{Menu, MenuItem, PredefinedMenuItem, Submenu};
use tauri::tray::{TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Emitter, Manager, State, WindowEvent, Wry};
use vm::{Problem, Vms};

const CATALOG: &str = "https://raw.githubusercontent.com/donnysaengkio/try-linux/main/catalog/distros.json";

// The shared Lucide icons (the repository's icons/ folder), built in.
const ICONS: &[(&str, &str)] = &[
    ("arrow-left", include_str!("../../icons/arrow-left.svg")),
    ("chevron-down", include_str!("../../icons/chevron-down.svg")),
    ("chevron-right", include_str!("../../icons/chevron-right.svg")),
    ("menu", include_str!("../../icons/menu.svg")),
    ("monitor", include_str!("../../icons/monitor.svg")),
    ("pencil", include_str!("../../icons/pencil.svg")),
    ("settings", include_str!("../../icons/settings.svg")),
    ("terminal", include_str!("../../icons/terminal.svg")),
    ("undo-2", include_str!("../../icons/undo-2.svg")),
    ("usb", include_str!("../../icons/usb.svg")),
    ("wrench", include_str!("../../icons/wrench.svg")),
    ("x", include_str!("../../icons/x.svg")),
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
    // Waiting to install, but its disk already holds several GB (installed while Try Linux was closed?).
    maybe_installed: bool,
}

// Systems left running by a Try Linux that closed (or crashed) while they ran: taken over, so they're shown and
// controlled here, and what happens when they stop (an install finishing, a restart) is handled as for any other.
// One started by a Try Linux that's still open stays with it.
fn adopt_orphans(app: &AppHandle, probe: &Probe, vms: &Vms) {
    if cfg!(target_os = "macos") {
        return; // systems run inside Try Linux there, so they end with it
    }
    let mut found = vec![];
    {
        let mut sys = probe.0.lock().unwrap();
        refresh_processes(&mut sys);
        let mine = vms.0.lock().unwrap();
        for dir in config::system_dirs() {
            let Some(cfg) = VmConfig::load(&dir) else { continue };
            if mine.contains_key(&cfg.id) {
                continue;
            }
            if let Some(p) = qemu_for(&sys, &cfg).filter(|p| !controlled_elsewhere(&sys, p)) {
                let args: Vec<String> = p.cmd().iter().map(|a| a.to_string_lossy().to_string()).collect();
                found.push((cfg.id.clone(), p.pid().as_u32(), args, p.run_time()));
            }
        }
    }
    #[cfg(not(target_os = "macos"))]
    for (id, pid, args, secs) in found {
        vm::adopt(app, vms, &id, pid, &args, std::time::Duration::from_secs(secs));
    }
    #[cfg(target_os = "macos")]
    let _ = (app, found);
}

// Install, for a system whose disk looks installed already: start it from its disk from now on.
#[tauri::command]
fn mark_installed(id: String) -> Result<(), String> {
    config::find(&id).ok_or("This system's folder can't be found any more.")?.set_all(&[("pending", "0".into())]).map_err(|e| e.to_string())
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

// Processes with their command lines (a plain refresh leaves those out, and a running system is recognised by
// the disk on QEMU's command line), memory and CPU.
fn refresh_processes(sys: &mut System) {
    use sysinfo::{ProcessRefreshKind, UpdateKind};
    let kind = ProcessRefreshKind::new().with_memory().with_cpu().with_cmd(UpdateKind::OnlyIfNotSet).with_exe(UpdateKind::OnlyIfNotSet);
    sys.refresh_processes_specifics(ProcessesToUpdate::All, true, kind);
}

// A QEMU process that uses this system's boot disk but wasn't started here.
fn qemu_for<'a>(sys: &'a System, cfg: &VmConfig) -> Option<&'a sysinfo::Process> {
    let disk = cfg.disk().to_string_lossy().to_lowercase();
    sys.processes().values().find(|p| {
        p.name().to_string_lossy().to_lowercase().starts_with("qemu-system")
            && p.cmd().iter().any(|a| a.to_string_lossy().to_lowercase().contains(&disk))
    })
}

// Whether a Try Linux that's still open runs this QEMU (the Windows, Mac or Linux edition): its parent process is
// alive, is Try Linux, and started before it (so it isn't another program that got the same process id).
fn controlled_elsewhere(sys: &System, p: &sysinfo::Process) -> bool {
    let Some(parent) = p.parent().and_then(|pid| sys.process(pid)) else { return false };
    if parent.pid().as_u32() == std::process::id() || parent.start_time() > p.start_time() {
        return false;
    }
    let what = format!("{} {}", parent.name().to_string_lossy(), parent.cmd().iter().map(|a| a.to_string_lossy()).collect::<Vec<_>>().join(" "));
    let what = what.to_lowercase();
    what.contains("trylinux") || what.contains("try-linux")
}

// Some QEMU is using this system's disk (so it mustn't be started again).
pub fn running_elsewhere(cfg: &VmConfig) -> bool {
    let mut sys = System::new();
    refresh_processes(&mut sys);
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

// What it runs: as its guest agent last reported, else the distribution it was installed from.
pub fn os_label(cfg: &VmConfig) -> String {
    fs::read_to_string(cfg.dir.join("guest-os.txt"))
        .ok()
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
        .or_else(|| distro_name(cfg.get("distro")).map(String::from))
        .unwrap_or_else(|| "Linux".into())
}

// Off the main thread: reading a running system's activity asks QEMU, which can take a moment.
#[tauri::command(async)]
fn systems(app: AppHandle, probe: State<Probe>, vms: State<Vms>, live: State<live::Live>) -> Vec<Vm> {
    adopt_orphans(&app, &probe, &vms);
    let host = probe.host();
    let mut asks = vec![];   // (index, id, QMP port) of the systems to read activity from, after the locks are let go
    let mut sys = probe.0.lock().unwrap();
    refresh_processes(&mut sys);
    let mine = vms.0.lock().unwrap();
    let mut out = vec![];
    for dir in config::system_dirs() {
        let Some(cfg) = VmConfig::load(&dir) else { continue };
        let (cores, ram_gb) = cfg.resources(&host);
        let used: u64 = cfg.all_disks().iter().filter_map(|d| fs::metadata(d).ok()).map(|m| m.len()).sum();
        let os = os_label(&cfg);
        let (state, status, pid) = match mine.get(&cfg.id) {
            Some(r) if r.held => ("held", "Off, console open", Some(r.pid)),
            Some(r) if r.paused => ("paused", if r.hidden { "Paused, window closed" } else { "Paused" }, Some(r.pid)),
            Some(r) => ("running", if r.hidden { "Running, window closed" } else { "Running" }, Some(r.pid)),
            None => match qemu_for(&sys, &cfg) {
                Some(p) => ("elsewhere", "Running", Some(p.pid().as_u32())),
                None if cfg.pending() => ("off", "Ready to install", None),
                None => ("off", "Powered off", None),
            },
        };
        // Waiting to install, but Linux has already written several GB to its disk: most likely it was installed
        // while Try Linux was closed, so Install asks first.
        let maybe_installed = state == "off" && cfg.pending() && fs::metadata(cfg.disk()).map(|m| m.len()).unwrap_or(0) > 2 << 30;
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
            maybe_installed,
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
    if vms.0.lock().unwrap().contains_key(&id) {
        vm::show_window(&app, &vms, &id);
        return Ok(());
    }
    vm::start(&app, &vms, &probe.host(), &id, None, true)
}

#[tauri::command]
fn power(app: AppHandle, vms: State<Vms>, id: String, action: String) -> Result<(), String> {
    vm::power(&app, &vms, &id, &action)
}

// Quits Try Linux (systems keep running, without the tray menu).
#[tauri::command]
fn quit(app: AppHandle) {
    app.exit(0);
}

// ---- the window and the tray

// Something about the running systems changed: the interface and the tray menu catch up.
pub fn changed(app: &AppHandle) {
    let _ = app.emit("systems-changed", ());
    if let (Some(tray), Ok(menu)) = (app.tray_by_id("main"), tray_menu(app)) {
        let _ = tray.set_menu(Some(menu));
    }
}

pub fn show_main(app: &AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.show();
        let _ = w.unminimize();
        let _ = w.set_focus();
    }
}

// The tray menu: each running system with its window and power commands, then Show and Quit.
fn tray_menu(app: &AppHandle) -> tauri::Result<Menu<Wry>> {
    let menu = Menu::new(app)?;
    let vms = app.state::<Vms>();
    let mut running: Vec<(String, String, bool, bool)> = vms.0.lock().unwrap().iter()
        .map(|(id, r)| (id.clone(), config::find(id).map(|c| c.name()).unwrap_or_else(|| id.clone()), r.held, r.paused))
        .collect();
    // In the order Virtual Machines shows them: by group, then as arranged (app.rs, layout).
    let rank = app::layout_rank();
    running.sort_by(|a, b| rank(&a.0).cmp(&rank(&b.0)).then_with(|| a.1.to_lowercase().cmp(&b.1.to_lowercase())));
    let item = |id: &str, action: &str, text: &str| MenuItem::with_id(app, format!("{action}|{id}"), text, true, None::<&str>);
    for (id, name, held, paused) in &running {
        let sub = Submenu::new(app, name, true)?;
        sub.append(&item(id, "open", "Open window")?)?;
        sub.append(&PredefinedMenuItem::separator(app)?)?;
        if *held {
            sub.append(&item(id, "power-on", "Power on")?)?;
            sub.append(&item(id, "close-console", "Close console")?)?;
        } else {
            sub.append(&item(id, "shutdown", "Shut down")?)?;
            sub.append(&item(id, "restart", "Restart")?)?;
            sub.append(&if *paused { item(id, "resume", "Resume")? } else { item(id, "pause", "Pause")? })?;
            sub.append(&PredefinedMenuItem::separator(app)?)?;
            sub.append(&item(id, "force", "Force stop")?)?;
            sub.append(&item(id, "reset", "Reset")?)?;
        }
        menu.append(&sub)?;
    }
    if !running.is_empty() {
        menu.append(&PredefinedMenuItem::separator(app)?)?;
    }
    menu.append(&MenuItem::with_id(app, "show", "Show Try Linux", true, None::<&str>)?)?;
    menu.append(&MenuItem::with_id(app, "quit", "Quit Try Linux", true, None::<&str>)?)?;
    Ok(menu)
}

#[derive(Clone, Serialize)]
struct AskPower {
    id: String,
    action: String,
}

fn tray_clicked(app: &AppHandle, item: &str) {
    let (action, id) = item.split_once('|').unwrap_or((item, ""));
    match action {
        "show" => show_main(app),
        // Quitting, and the commands that lose work or ask Linux, are confirmed in the window first.
        "quit" => {
            show_main(app);
            let _ = app.emit("ask-quit", ());
        }
        "shutdown" | "restart" | "force" | "reset" => {
            show_main(app);
            let _ = app.emit("ask-power", AskPower { id: id.into(), action: action.into() });
        }
        "open" => vm::show_window(app, &app.state::<Vms>(), id),
        _ => {
            if let Err(e) = vm::power(app, &app.state::<Vms>(), id, action) {
                let _ = app.emit("problem", Problem { title: "Couldn't do that".into(), text: e });
            }
        }
    }
}

// The catalog's text, as published in the repository.
#[tauri::command]
async fn catalog() -> Result<String, String> {
    tauri::async_runtime::spawn_blocking(|| {
        let get = |url: &str| ureq::get(url).call().map_err(|e| e.to_string())?.into_string().map_err(|e| e.to_string());
        let main = get(CATALOG)?;
        if vm::ARCH != "aarch64" {
            return Ok(main);
        }
        // ARM64 (Apple silicon, ARM Linux), as the Mac edition does it: distros-aarch64.json's downloads, then
        // distros.json's website-only entries (its downloads are x86_64 installers, so they're left out).
        let arm = get(&CATALOG.replace("distros.json", "distros-aarch64.json")).unwrap_or_default();
        let list = |text: &str| -> Vec<serde_json::Value> {
            serde_json::from_str::<serde_json::Value>(text).ok().and_then(|v| v["distros"].as_array().cloned()).unwrap_or_default()
        };
        let mut out = list(&arm);
        for d in list(&main) {
            let website_only = d.get("base").is_none() && d.get("steps").is_none();
            if website_only && !out.iter().any(|o| o["id"] == d["id"]) {
                out.push(d);
            }
        }
        Ok(serde_json::json!({ "version": 2, "distros": out }).to_string())
    })
    .await
    .map_err(|e| e.to_string())?
}

// This computer's processor ("x86_64" or "aarch64"), for which installers to offer.
#[tauri::command]
fn arch() -> &'static str {
    vm::ARCH
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
    // Web pages, and on Windows its Settings pages (Sound, Microphone privacy).
    let settings_page = cfg!(windows) && url.starts_with("ms-settings:") && url[12..].chars().all(|c| c.is_ascii_alphanumeric() || c == '-');
    if !url.starts_with("https://") && !settings_page {
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

// --start <id> (a desktop shortcut, or signing in to Windows): the system starts and Try Linux stays in the
// tray. One already running is brought to the front instead. If it can't start, the window opens and says why.
fn start_from_arg(app: &AppHandle, id: &str) {
    let vms = app.state::<Vms>();
    if vms.0.lock().unwrap().contains_key(id) {
        vm::show_window(app, &vms, id);
        return;
    }
    let host = app.state::<Probe>().host();
    if let Err(p) = vm::start(app, &vms, &host, id, None, false) {
        show_main(app);
        let _ = app.emit("problem", p);
    }
}

fn main() {
    let mut builder = tauri::Builder::default();
    // One Try Linux at a time: opening it again (or a shortcut) goes to the one that's running. Screenshots
    // (TRYLINUX_PAGE) run a copy of their own.
    if std::env::var("TRYLINUX_PAGE").unwrap_or_default().is_empty() {
        builder = builder.plugin(tauri_plugin_single_instance::init(|app, argv, _cwd| match shortcuts::start_arg(&argv) {
            Some(id) => start_from_arg(app, &id),
            None => show_main(app),
        }));
    }
    builder
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(tauri_plugin_process::init())
        .manage(Probe(Mutex::new(System::new())))
        .manage(Vms::default())
        .manage(live::Live::default())
        .invoke_handler(tauri::generate_handler![systems, start, console, power, quit, catalog, arch, mark_installed, icons, open_page, start_page, settings::settings, settings::save_settings, settings::disk_info, settings::set_disc, create::create_defaults, create::free_space, create::create, create::cancel_create, create::read_disk, app::app_info, app::set_preference, app::check_updates, app::open_storage, app::layout, app::set_layout, log::system_log, log::open_log, settings::add_shortcut, disks::disk_resize, disks::disk_compact, disks::disk_add, disks::disk_remove, recovery::backup, recovery::restore, recovery::clone_system, recovery::snapshots, recovery::snapshot, recovery::move_storage, recovery::clean_list, recovery::clean_run, recovery::reset, recovery::installer_of, recovery::boot_iso, recovery::delete, setup::readiness, setup::enable_hypervisor, setup::update_winq])
        .setup(|app| {
            let handle = app.handle().clone();
            TrayIconBuilder::with_id("main")
                .icon(app.default_window_icon().cloned().expect("the app's icon"))
                .tooltip("Try Linux")
                .menu(&tray_menu(&handle)?)
                .show_menu_on_left_click(false)
                .on_menu_event(|app, e| tray_clicked(app, e.id().as_ref()))
                .on_tray_icon_event(|tray, e| {
                    if let TrayIconEvent::DoubleClick { .. } = e {
                        show_main(tray.app_handle());
                    }
                })
                .build(app)?;
            // The window starts hidden (tauri.conf.json), so --start can go straight to the tray.
            let args: Vec<String> = std::env::args().collect();
            match shortcuts::start_arg(&args) {
                Some(id) => {
                    let h = handle.clone();
                    std::thread::spawn(move || start_from_arg(&h, &id));
                }
                None => show_main(&handle),
            }
            // What the Windows edition before 2.0 left (not for screenshot copies, TRYLINUX_PAGE).
            if std::env::var("TRYLINUX_PAGE").unwrap_or_default().is_empty() {
                legacy::take_over();
            }
            Ok(())
        })
        // Closing the window while systems run keeps Try Linux in the tray, where they can be shut down.
        .on_window_event(|window, e| {
            if let WindowEvent::CloseRequested { api, .. } = e {
                if !window.state::<Vms>().0.lock().unwrap().is_empty() {
                    api.prevent_close();
                    let _ = window.hide();
                }
            }
        })
        .run(tauri::generate_context!())
        .expect("Try Linux couldn't start");
}
