// macOS: systems run with Apple's Virtualization framework, configured as the Mac edition configures them
// (VM.swift): EFI with its variables and machine identifier in the system's folder, Disk 1 and any others as
// virtio disks (raw disk.img), the installer or the CD/DVD drive's disc as a USB stick, NAT networking with the
// system's MAC address, virtio graphics at its resolution, USB keyboard and pointer, sound (and the microphone),
// the shared folder and Rosetta as virtio file systems, and clipboard sharing through the SPICE agent.
//
// Each system has its own window (VZVirtualMachineView). Closing it hides it and the system keeps running;
// Virtual Machines and the tray show it. Everything here runs on the main thread, as the framework requires:
// commands hand their work to it with run_on_main_thread.

use crate::config::VmConfig;
use crate::vm::{Problem, Running, Vms};
use block2::RcBlock;
use objc2::rc::Retained;
use objc2::runtime::ProtocolObject;
use objc2::{define_class, msg_send, AnyThread, DefinedClass, MainThreadMarker, MainThreadOnly};
use objc2_app_kit::{NSApplication, NSBackingStoreType, NSResponder, NSView, NSWindow, NSWindowCollectionBehavior, NSWindowDelegate, NSWindowStyleMask};
use objc2_foundation::{NSArray, NSData, NSError, NSObject, NSObjectProtocol, NSPoint, NSRect, NSSize, NSString, NSURL};
use objc2_virtualization::*;
use std::cell::RefCell;
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::OnceLock;
use std::time::{Duration, Instant};
use tauri::{AppHandle, Emitter, Manager};

static APP: OnceLock<AppHandle> = OnceLock::new();

// What a system needs to start, read from its settings before moving to the main thread.
struct Plan {
    id: String,
    name: String,
    dir: PathBuf,
    disks: Vec<PathBuf>,
    iso: PathBuf,
    installer: bool,
    cd: String,
    cores: usize,
    ram_gb: u64,
    width: i64,
    height: i64,
    net: bool,
    sound: bool,
    mic: bool,
    shared: String,
    shared_write: bool,
    clipboard: bool,
    rosetta: bool,
    mac_address: String,
    fullscreen: bool,
}

// A running (or held) system: its machine, window and delegates, and what the Power menu asked for.
struct Session {
    vm: Retained<VZVirtualMachine>,
    window: Option<Retained<NSWindow>>,
    vm_delegate: Retained<VmDelegate>,
    win_delegate: Retained<WinDelegate>,
    name: String,
    dir: PathBuf,
    installer: bool,
    held: bool,
    paused: bool,
    started: Instant,
    restart_asked: Option<Instant>,
    reset_asked: bool,
    quiet: bool,
    width: i64,
    height: i64,
    fullscreen: bool,
}

thread_local! {
    // On the main thread only.
    static SESSIONS: RefCell<HashMap<String, Session>> = RefCell::new(HashMap::new());
}

fn text(e: &NSError) -> String {
    e.localizedDescription().to_string()
}
fn ns_err(e: Retained<NSError>) -> String {
    text(&e)
}
fn url(p: &Path) -> Retained<NSURL> {
    NSURL::fileURLWithPath(&NSString::from_str(&p.to_string_lossy()))
}
fn problem(app: &AppHandle, title: String, text: String) {
    crate::show_main(app);
    let _ = app.emit("problem", Problem { title, text });
}

// macOS 14 or later (automatic display resizing).
fn macos_14() -> bool {
    let out = std::process::Command::new("sw_vers").arg("-productVersion").output().map(|o| String::from_utf8_lossy(&o.stdout).to_string()).unwrap_or_default();
    out.trim().split('.').next().and_then(|m| m.parse::<u32>().ok()).is_some_and(|m| m >= 14)
}

// ---------------------------------------------------------------- delegates

define_class!(
    // SAFETY: NSObject has no subclassing requirements, and this class has no Drop.
    #[unsafe(super(NSObject))]
    #[thread_kind = MainThreadOnly]
    #[ivars = String]
    struct VmDelegate;

    unsafe impl NSObjectProtocol for VmDelegate {}

    unsafe impl VZVirtualMachineDelegate for VmDelegate {
        // Linux powered off (or restarted: the framework reports both the same way).
        #[unsafe(method(guestDidStopVirtualMachine:))]
        fn guest_did_stop(&self, _vm: &VZVirtualMachine) {
            finished(self.ivars(), None);
        }

        #[unsafe(method(virtualMachine:didStopWithError:))]
        fn did_stop_with_error(&self, _vm: &VZVirtualMachine, error: &NSError) {
            finished(self.ivars(), Some(text(error)));
        }
    }
);

impl VmDelegate {
    fn new(mtm: MainThreadMarker, id: &str) -> Retained<Self> {
        let this = Self::alloc(mtm).set_ivars(id.to_string());
        unsafe { msg_send![super(this), init] }
    }
}

define_class!(
    // SAFETY: NSObject has no subclassing requirements, and this class has no Drop.
    #[unsafe(super(NSObject))]
    #[thread_kind = MainThreadOnly]
    #[ivars = String]
    struct WinDelegate;

    unsafe impl NSObjectProtocol for WinDelegate {}

    unsafe impl NSWindowDelegate for WinDelegate {
        // Closing the window hides it; the system keeps running. A console that was never powered on closes.
        #[unsafe(method(windowShouldClose:))]
        fn window_should_close(&self, sender: &NSWindow) -> bool {
            let id = self.ivars().clone();
            let held = SESSIONS.with(|s| s.borrow().get(&id).map(|x| x.held)).unwrap_or(false);
            if held {
                SESSIONS.with(|s| if let Some(x) = s.borrow_mut().get_mut(&id) { x.quiet = true });
                finished(&id, None);
                return false;
            }
            if sender.styleMask().contains(NSWindowStyleMask::FullScreen) {
                sender.toggleFullScreen(None);
            }
            sender.orderOut(None);
            if let Some(app) = APP.get() {
                if let Some(vm) = app.state::<Vms>().0.lock().unwrap().get_mut(&id) {
                    vm.hidden = true;
                }
                crate::changed(app);
                crate::show_main(app);
            }
            false
        }
    }
);

impl WinDelegate {
    fn new(mtm: MainThreadMarker, id: &str) -> Retained<Self> {
        let this = Self::alloc(mtm).set_ivars(id.to_string());
        unsafe { msg_send![super(this), init] }
    }
}

// ---------------------------------------------------------------- the configuration

fn machine_identifier(dir: &Path) -> Result<Retained<VZGenericMachineIdentifier>, String> {
    let file = dir.join("machine-id");
    unsafe {
        if let Ok(bytes) = std::fs::read(&file) {
            let data = NSData::with_bytes(&bytes);
            if let Some(id) = VZGenericMachineIdentifier::initWithDataRepresentation(VZGenericMachineIdentifier::alloc(), &data) {
                return Ok(id);
            }
        }
        let id = VZGenericMachineIdentifier::new();
        std::fs::write(&file, id.dataRepresentation().to_vec()).map_err(|e| format!("Couldn't save the machine identifier: {e}"))?;
        Ok(id)
    }
}

fn efi_store(dir: &Path) -> Result<Retained<VZEFIVariableStore>, String> {
    let file = dir.join("efi.vars");
    unsafe {
        if file.exists() {
            Ok(VZEFIVariableStore::initWithURL(VZEFIVariableStore::alloc(), &url(&file)))
        } else {
            VZEFIVariableStore::initCreatingVariableStoreAtURL_options_error(VZEFIVariableStore::alloc(), &url(&file), VZEFIVariableStoreInitializationOptions::empty())
                .map_err(ns_err)
        }
    }
}

fn disk(p: &Path, read_only: bool) -> Result<Retained<VZDiskImageStorageDeviceAttachment>, String> {
    unsafe { VZDiskImageStorageDeviceAttachment::initWithURL_readOnly_error(VZDiskImageStorageDeviceAttachment::alloc(), &url(p), read_only) }
        .map_err(|e| format!("{}: {}", p.to_string_lossy(), text(&e)))
}

fn configuration(p: &Plan) -> Result<Retained<VZVirtualMachineConfiguration>, String> {
    unsafe {
        let conf = VZVirtualMachineConfiguration::new();
        conf.setCPUCount(p.cores);
        conf.setMemorySize(p.ram_gb << 30);

        let platform = VZGenericPlatformConfiguration::new();
        platform.setMachineIdentifier(&machine_identifier(&p.dir)?);
        let platform_ref: &VZPlatformConfiguration = &platform;
        conf.setPlatform(platform_ref);

        let boot = VZEFIBootLoader::new();
        boot.setVariableStore(Some(&efi_store(&p.dir)?));
        let boot_ref: &VZBootLoader = &boot;
        conf.setBootLoader(Some(boot_ref));

        // Disk 1 first, so it's the one EFI starts from; a missing extra disk is left out.
        let mut storage: Vec<Retained<VZStorageDeviceConfiguration>> = vec![];
        for (i, d) in p.disks.iter().enumerate() {
            if i == 0 || d.exists() {
                let att = disk(d, false)?;
                let att_ref: &VZStorageDeviceAttachment = &att;
                let dev = VZVirtioBlockDeviceConfiguration::initWithAttachment(VZVirtioBlockDeviceConfiguration::alloc(), att_ref);
                storage.push(Retained::into_super(dev));
            }
        }
        // The installer appears as a USB stick (the empty disk can't boot, so EFI starts from it); otherwise
        // the CD/DVD drive's disc, read-only, and the disk still boots first.
        let stick = if p.installer { Some(p.iso.clone()) } else if !p.cd.is_empty() && Path::new(&p.cd).exists() { Some(PathBuf::from(&p.cd)) } else { None };
        if let Some(iso) = stick {
            let att = disk(&iso, true)?;
            let att_ref: &VZStorageDeviceAttachment = &att;
            let dev = VZUSBMassStorageDeviceConfiguration::initWithAttachment(VZUSBMassStorageDeviceConfiguration::alloc(), att_ref);
            storage.push(Retained::into_super(dev));
        }
        conf.setStorageDevices(&NSArray::from_retained_slice(&storage));

        // No network: no network card at all, so Linux is fully offline.
        if p.net {
            let net = VZVirtioNetworkDeviceConfiguration::new();
            let nat = VZNATNetworkDeviceAttachment::new();
            let nat_ref: &VZNetworkDeviceAttachment = &nat;
            net.setAttachment(Some(nat_ref));
            if let Some(mac) = VZMACAddress::initWithString(VZMACAddress::alloc(), &NSString::from_str(&p.mac_address)) {
                net.setMACAddress(&mac);
            }
            conf.setNetworkDevices(&NSArray::from_retained_slice(&[Retained::into_super(net)]));
        }

        let gpu = VZVirtioGraphicsDeviceConfiguration::new();
        let scanout = VZVirtioGraphicsScanoutConfiguration::initWithWidthInPixels_heightInPixels(VZVirtioGraphicsScanoutConfiguration::alloc(), p.width as _, p.height as _);
        gpu.setScanouts(&NSArray::from_retained_slice(&[scanout]));
        conf.setGraphicsDevices(&NSArray::from_retained_slice(&[Retained::into_super(gpu)]));
        conf.setKeyboards(&NSArray::from_retained_slice(&[Retained::into_super(VZUSBKeyboardConfiguration::new())]));
        conf.setPointingDevices(&NSArray::from_retained_slice(&[Retained::into_super(VZUSBScreenCoordinatePointingDeviceConfiguration::new())]));
        conf.setEntropyDevices(&NSArray::from_retained_slice(&[Retained::into_super(VZVirtioEntropyDeviceConfiguration::new())]));
        conf.setMemoryBalloonDevices(&NSArray::from_retained_slice(&[Retained::into_super(VZVirtioTraditionalMemoryBalloonDeviceConfiguration::new())]));

        if p.sound {
            let sound = VZVirtioSoundDeviceConfiguration::new();
            let out = VZVirtioSoundDeviceOutputStreamConfiguration::new();
            let sink = VZHostAudioOutputStreamSink::new();
            let sink_ref: &VZAudioOutputStreamSink = &sink;
            out.setSink(Some(sink_ref));
            let mut streams: Vec<Retained<VZVirtioSoundDeviceStreamConfiguration>> = vec![Retained::into_super(out)];
            if p.mic {
                let input = VZVirtioSoundDeviceInputStreamConfiguration::new();
                let source = VZHostAudioInputStreamSource::new();
                let source_ref: &VZAudioInputStreamSource = &source;
                input.setSource(Some(source_ref));
                streams.push(Retained::into_super(input));
            }
            sound.setStreams(&NSArray::from_retained_slice(&streams));
            conf.setAudioDevices(&NSArray::from_retained_slice(&[Retained::into_super(sound)]));
        }

        let mut shares: Vec<Retained<VZDirectorySharingDeviceConfiguration>> = vec![];
        if !p.shared.is_empty() && Path::new(&p.shared).is_dir() {
            let fs = VZVirtioFileSystemDeviceConfiguration::initWithTag(VZVirtioFileSystemDeviceConfiguration::alloc(), &NSString::from_str("host"));
            let dir = VZSharedDirectory::initWithURL_readOnly(VZSharedDirectory::alloc(), &url(Path::new(&p.shared)), !p.shared_write);
            let share = VZSingleDirectoryShare::initWithDirectory(VZSingleDirectoryShare::alloc(), &dir);
            let share_ref: &VZDirectoryShare = &share;
            fs.setShare(Some(share_ref));
            shares.push(Retained::into_super(fs));
        }
        if p.rosetta && VZLinuxRosettaDirectoryShare::availability() == VZLinuxRosettaAvailability::Installed {
            let fs = VZVirtioFileSystemDeviceConfiguration::initWithTag(VZVirtioFileSystemDeviceConfiguration::alloc(), &NSString::from_str("rosetta"));
            let rosetta = VZLinuxRosettaDirectoryShare::initWithError(VZLinuxRosettaDirectoryShare::alloc()).map_err(ns_err)?;
            let share_ref: &VZDirectoryShare = &rosetta;
            fs.setShare(Some(share_ref));
            shares.push(Retained::into_super(fs));
        }
        conf.setDirectorySharingDevices(&NSArray::from_retained_slice(&shares));

        if p.clipboard {
            // The SPICE agent port lets spice-vdagent in Linux share the clipboard with macOS.
            let console = VZVirtioConsoleDeviceConfiguration::new();
            let port = VZVirtioConsolePortConfiguration::new();
            port.setName(Some(&VZSpiceAgentPortAttachment::spiceAgentPortName()));
            let agent = VZSpiceAgentPortAttachment::new();
            agent.setSharesClipboard(true);
            let agent_ref: &VZSerialPortAttachment = &agent;
            port.setAttachment(Some(agent_ref));
            console.ports().setObject_atIndexedSubscript(Some(&port), 0);
            conf.setConsoleDevices(&NSArray::from_retained_slice(&[Retained::into_super(console)]));
        }

        conf.validateWithError().map_err(ns_err)?;
        Ok(conf)
    }
}

// ---------------------------------------------------------------- starting, and the window

// Starts a system (or with `held`, opens its window with it still off). The work happens on the main thread;
// problems are reported as "problem" events.
pub fn start(app: &AppHandle, _vms: &Vms, cfg: &VmConfig, held: bool) -> Result<(), String> {
    let _ = APP.set(app.clone());
    let host = app.state::<crate::Probe>().host();
    let (cores, ram_gb) = cfg.resources(&host);
    let num = |k: &str, d: i64| cfg.get(k).parse::<i64>().unwrap_or(d);
    // A size asked for in Settings or Create (diskgb) that's larger than Disk 1: it grows to it.
    let gb = cfg.get("diskgb").parse::<u64>().unwrap_or(0);
    if gb > 0 {
        let d = cfg.disk();
        if std::fs::metadata(&d).is_ok_and(|m| m.len() < gb << 30) {
            if let Err(e) = std::fs::OpenOptions::new().write(true).open(&d).and_then(|f| f.set_len(gb << 30)) {
                let _ = app.emit("problem", Problem { title: "Couldn't grow the disk".into(), text: format!("It keeps its current size.\n\n{e}") });
            }
        }
    }
    let plan = Plan {
        id: cfg.id.clone(),
        name: cfg.name(),
        dir: cfg.dir.clone(),
        disks: cfg.all_disks(),
        iso: cfg.iso(),
        installer: cfg.pending() && cfg.iso().exists(),
        cd: cfg.cd().to_string(),
        cores,
        ram_gb,
        width: num("width", 1920),
        height: num("height", 1200),
        net: cfg.net_type() != 2,
        sound: cfg.sound(),
        mic: cfg.mic(),
        shared: cfg.shared().to_string(),
        shared_write: cfg.shared_write(),
        clipboard: cfg.get("clipboard") != "0",
        rosetta: cfg.get("rosetta") == "1",
        mac_address: cfg.get("macAddress").to_string(),
        fullscreen: cfg.fullscreen(),
    };
    let app2 = app.clone();
    app.run_on_main_thread(move || {
        if let Err(e) = open(&app2, plan, held) {
            // `open` says which system in its message.
            problem(&app2, "Couldn't start it".into(), e);
        }
    })
    .map_err(|e| e.to_string())
}

fn open(app: &AppHandle, mut p: Plan, held: bool) -> Result<(), String> {
    let mtm = MainThreadMarker::new().ok_or("Not on the main thread.")?;
    if SESSIONS.with(|s| s.borrow().contains_key(&p.id)) {
        show(app, &p.id);
        return Ok(());
    }
    // A MAC address of its own, made the first time it starts (as the Mac edition does).
    if p.mac_address.is_empty() {
        p.mac_address = unsafe { VZMACAddress::randomLocallyAdministeredAddress().string() }.to_string();
        if let Some(c) = VmConfig::load(&p.dir) {
            c.set("macAddress", &p.mac_address);
        }
    }
    let conf = configuration(&p).map_err(|e| format!("{} couldn't start.\n\n{e}", p.name))?;
    let vm = unsafe { VZVirtualMachine::initWithConfiguration(VZVirtualMachine::alloc(), &conf) };
    let vm_delegate = VmDelegate::new(mtm, &p.id);
    unsafe { vm.setDelegate(Some(ProtocolObject::from_ref(&*vm_delegate))) };
    let session = Session {
        vm: vm.clone(),
        window: None,
        vm_delegate,
        win_delegate: WinDelegate::new(mtm, &p.id),
        name: p.name.clone(),
        dir: p.dir.clone(),
        installer: p.installer,
        held,
        paused: false,
        started: Instant::now(),
        restart_asked: None,
        reset_asked: false,
        quiet: false,
        width: p.width,
        height: p.height,
        fullscreen: p.fullscreen,
    };
    SESSIONS.with(|s| s.borrow_mut().insert(p.id.clone(), session));
    app.state::<Vms>().0.lock().unwrap().insert(p.id.clone(), Running::mac(held, p.installer));
    crate::changed(app);

    if held {
        window(&p.id, mtm, Some(false));
        return Ok(());
    }
    let (id, name, app2) = (p.id.clone(), p.name.clone(), app.clone());
    let done = RcBlock::new(move |err: *mut NSError| {
        if err.is_null() {
            if let Some(mtm) = MainThreadMarker::new() {
                window(&id, mtm, None);
            }
            record_boot(&id);
        } else {
            let msg = unsafe { text(&*err) };
            SESSIONS.with(|s| s.borrow_mut().remove(&id));
            app2.state::<Vms>().0.lock().unwrap().remove(&id);
            crate::changed(&app2);
            problem(&app2, format!("{name} couldn't start"), msg);
        }
    });
    unsafe { vm.startWithCompletionHandler(&done) };
    Ok(())
}

// When it last started, for Settings > Graphics (the Mac edition's lastBoot).
fn record_boot(id: &str) {
    let dir = SESSIONS.with(|s| s.borrow().get(id).map(|x| x.dir.clone()));
    if let Some(c) = dir.and_then(|d| VmConfig::load(&d)) {
        c.set("lastrender", &crate::config::now());
    }
}

// Its window: made the first time, shown again if hidden, in front. `fullscreen`: None for its setting.
fn window(id: &str, mtm: MainThreadMarker, fullscreen: Option<bool>) {
    let made = SESSIONS.with(|s| {
        let mut map = s.borrow_mut();
        let x = map.get_mut(id)?;
        if let Some(w) = &x.window {
            return Some((w.clone(), false, false));
        }
        let w = (x.width as f64 / 1.5).min(1600.0);
        let h = w * x.height as f64 / x.width.max(1) as f64;
        let rect = NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(w, h));
        let view = unsafe { VZVirtualMachineView::initWithFrame(VZVirtualMachineView::alloc(mtm), rect) };
        unsafe {
            view.setVirtualMachine(Some(&x.vm));
            view.setCapturesSystemKeys(true);
            if macos_14() {
                view.setAutomaticallyReconfiguresDisplay(true);
            }
        }
        let style = NSWindowStyleMask::Titled | NSWindowStyleMask::Closable | NSWindowStyleMask::Miniaturizable | NSWindowStyleMask::Resizable;
        let win = unsafe { NSWindow::initWithContentRect_styleMask_backing_defer(NSWindow::alloc(mtm), rect, style, NSBackingStoreType::Buffered, false) };
        win.setTitle(&NSString::from_str(&if x.held { format!("{} (off)", x.name) } else { x.name.clone() }));
        unsafe { win.setReleasedWhenClosed(false) };
        let view_ref: &NSView = &view;
        win.setContentView(Some(view_ref));
        win.setDelegate(Some(ProtocolObject::from_ref(&*x.win_delegate)));
        win.setCollectionBehavior(win.collectionBehavior() | NSWindowCollectionBehavior::FullScreenPrimary);
        win.center();
        let responder: &NSResponder = &view;
        win.makeFirstResponder(Some(responder));
        x.window = Some(win.clone());
        Some((win, true, fullscreen.unwrap_or(x.fullscreen)))
    });
    let Some((win, new, full)) = made else { return };
    win.makeKeyAndOrderFront(None);
    NSApplication::sharedApplication(mtm).activateIgnoringOtherApps(true);
    if new && full {
        win.toggleFullScreen(None);
    }
}

// A system's window to the front, shown again if it was hidden.
pub fn show(app: &AppHandle, id: &str) {
    let (app2, id) = (app.clone(), id.to_string());
    let _ = app.run_on_main_thread(move || {
        if let Some(mtm) = MainThreadMarker::new() {
            window(&id, mtm, Some(false));
        }
        if let Some(vm) = app2.state::<Vms>().0.lock().unwrap().get_mut(&id) {
            vm.hidden = false;
        }
        crate::changed(&app2);
    });
}

// ---------------------------------------------------------------- the Power menu

pub fn power(app: &AppHandle, id: &str, action: &str) -> Result<(), String> {
    let (app2, id, action) = (app.clone(), id.to_string(), action.to_string());
    app.run_on_main_thread(move || {
        if let Err(e) = power_now(&app2, &id, &action) {
            problem(&app2, "Couldn't do that".into(), e);
        }
    })
    .map_err(|e| e.to_string())
}

fn set_vm(app: &AppHandle, id: &str, f: impl FnOnce(&mut Running)) {
    if let Some(vm) = app.state::<Vms>().0.lock().unwrap().get_mut(id) {
        f(vm);
    }
    crate::changed(app);
}

fn power_now(app: &AppHandle, id: &str, action: &str) -> Result<(), String> {
    let mtm = MainThreadMarker::new().ok_or("Not on the main thread.")?;
    let (vm, paused, held) = SESSIONS.with(|s| {
        let mut map = s.borrow_mut();
        let x = map.get_mut(id)?;
        match action {
            "restart" => x.restart_asked = Some(Instant::now()),
            "reset" => x.reset_asked = true,
            "force" | "close-console" => x.quiet = true,
            _ => {}
        }
        Some((x.vm.clone(), x.paused, x.held))
    })
    .ok_or("It isn't running.")?;
    let stopped_block = |id: &str| {
        let id = id.to_string();
        RcBlock::new(move |err: *mut NSError| finished(&id, if err.is_null() { None } else { Some(unsafe { text(&*err) }) }))
    };
    match action {
        // Asks Linux to power off, like pressing a power button; a paused system is resumed first, since it
        // can't answer while paused. The window comes to the front, as the desktop may ask first.
        "shutdown" | "restart" => {
            window(id, mtm, Some(false));
            if paused {
                let (vm2, app2, id2) = (vm.clone(), app.clone(), id.to_string());
                let then = RcBlock::new(move |_err: *mut NSError| {
                    SESSIONS.with(|s| if let Some(x) = s.borrow_mut().get_mut(&id2) { x.paused = false });
                    set_vm(&app2, &id2, |r| r.paused = false);
                    let _ = unsafe { vm2.requestStopWithError() };
                });
                unsafe { vm.resumeWithCompletionHandler(&then) };
            } else {
                unsafe { vm.requestStopWithError() }.map_err(ns_err)?;
            }
        }
        "pause" | "resume" => {
            let pause = action == "pause";
            let (app2, id2) = (app.clone(), id.to_string());
            let then = RcBlock::new(move |err: *mut NSError| {
                if err.is_null() {
                    SESSIONS.with(|s| if let Some(x) = s.borrow_mut().get_mut(&id2) { x.paused = pause });
                    set_vm(&app2, &id2, |r| r.paused = pause);
                }
            });
            unsafe { if pause { vm.pauseWithCompletionHandler(&then) } else { vm.resumeWithCompletionHandler(&then) } };
        }
        // Starts a held console's system in its window.
        "power-on" => {
            let (app2, id2) = (app.clone(), id.to_string());
            let then = RcBlock::new(move |err: *mut NSError| {
                if err.is_null() {
                    let title = SESSIONS.with(|s| {
                        let mut map = s.borrow_mut();
                        let x = map.get_mut(&id2)?;
                        x.held = false;
                        x.started = Instant::now();
                        Some((x.window.clone(), x.name.clone()))
                    });
                    if let Some((Some(w), name)) = title {
                        w.setTitle(&NSString::from_str(&name));
                    }
                    set_vm(&app2, &id2, |r| r.held = false);
                    record_boot(&id2);
                } else {
                    problem(&app2, "Couldn't start it".into(), unsafe { text(&*err) });
                }
            });
            unsafe { vm.startWithCompletionHandler(&then) };
        }
        // Turns it off at once (Reset then starts it again); a console that was never powered on just closes.
        "force" | "reset" | "close-console" => {
            if held || !unsafe { vm.canStop() } {
                finished(id, None);
            } else {
                unsafe { vm.stopWithCompletionHandler(&stopped_block(id)) };
            }
        }
        _ => return Err(format!("Unknown action {action}")),
    }
    Ok(())
}

// ---------------------------------------------------------------- when it stops

// It stopped (Linux powered off or restarted, Force stop or Reset, or an error): its window closes, and as the
// Mac edition does: an installer session that ran a while has installed the system; Restart and Reset start it
// again; an error is shown unless it was forced to stop.
fn finished(id: &str, error: Option<String>) {
    let Some(x) = SESSIONS.with(|s| s.borrow_mut().remove(id)) else { return };
    if let Some(w) = &x.window {
        w.setDelegate(None);
        w.close();
    }
    unsafe { x.vm.setDelegate(None) };
    let _ = &x.vm_delegate;
    let Some(app) = APP.get() else { return };
    let vms = app.state::<Vms>();
    vms.0.lock().unwrap().remove(id);
    if x.installer && !x.held && x.started.elapsed() > Duration::from_secs(60) {
        if let Some(c) = VmConfig::load(&x.dir) {
            c.set("pending", "0");
        }
    }
    let again = x.reset_asked || (error.is_none() && x.restart_asked.is_some_and(|t| t.elapsed() < Duration::from_secs(300)));
    if again {
        let host = app.state::<crate::Probe>().host();
        if let Err(p) = crate::vm::start(app, &vms, &host, id, None, false) {
            let _ = app.emit("problem", p);
        }
        return;
    }
    if let Some(e) = error.filter(|_| !x.quiet) {
        problem(app, format!("{} stopped with an error", x.name), e);
    }
    crate::changed(app);
    if vms.0.lock().unwrap().is_empty() {
        crate::show_main(app);
    }
}
