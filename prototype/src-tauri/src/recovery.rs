// Settings > Recovery, as the Windows edition does it: Back up and Restore, Clone, Snapshots, Move and Clean
// (storage), Reset and Boot ISO (repair), and Delete. Each that changes a system needs it shut down; the slow
// ones report progress as "tool-progress" events, and each answers with a sentence to show.

use crate::config::{self, VmConfig};
use crate::create::{bytes, folder_name, qemu_img, qemu_img_progress};
use crate::paths::{self, isos_dir, downloads_dir, vms_dir};
use crate::shortcuts;
use crate::vm::Vms;
use crate::Probe;
use serde::Serialize;
use std::fs;
use std::path::{Path, PathBuf};
use tauri::{AppHandle, Emitter, State};

const GIB: u64 = 1 << 30;

fn find(id: &str) -> Result<VmConfig, String> {
    config::find(id).ok_or_else(|| "This system's folder can't be found any more.".into())
}

fn stopped(vms: &Vms, cfg: &VmConfig) -> Result<(), String> {
    if vms.0.lock().unwrap().contains_key(&cfg.id) || crate::running_elsewhere(cfg) {
        return Err(format!("Shut {} down first. Use the Try Linux tray icon, or shut down from inside Linux.", cfg.name()));
    }
    Ok(())
}

#[derive(Serialize, Clone)]
struct ToolProgress {
    status: String,
    value: f64,
    detail: String,
}
fn reporter(app: &AppHandle) -> impl FnMut(&str, f64) + '_ {
    move |status, value| {
        let _ = app.emit("tool-progress", ToolProgress { status: status.into(), value, detail: "This can take a few minutes for large disks".into() });
    }
}

fn file_name(p: &Path) -> String {
    p.file_name().map(|f| f.to_string_lossy().to_string()).unwrap_or_default()
}

// Copies a disk with qemu-img (any format in, qcow2 out; -c compresses), reporting how far along it is.
fn copy_disk(from: &Path, to: &Path, compress: bool, what: &str, progress: &mut dyn FnMut(&str, f64)) -> Result<(), String> {
    let (f, t) = (from.to_string_lossy().to_string(), to.to_string_lossy().to_string());
    let mut args = vec!["convert", "-p"];
    if compress {
        args.push("-c");
    }
    args.extend(["-O", "qcow2", f.as_str(), t.as_str()]);
    qemu_img_progress(&args, |v| progress(what, v), || false).map_err(|e| format!("Couldn't copy {}.\n\n{e}", file_name(from)))
}

// The other disks' entries in vm.ini ("disks="), in order.
fn extra_entries(cfg: &VmConfig) -> Vec<String> {
    cfg.get("disks").split('|').filter(|e| !e.is_empty()).map(String::from).collect()
}

// ---------------------------------------------------------------- back up and restore

// Where a backup keeps disk number i (0 is Disk 1, the chosen file itself).
fn backup_disk_path(backup: &Path, i: usize) -> PathBuf {
    if i == 0 {
        return backup.to_path_buf();
    }
    let stem = backup.file_stem().map(|s| s.to_string_lossy().to_string()).unwrap_or_default();
    backup.with_file_name(format!("{stem}-disk{}.qcow2", i + 1))
}

pub fn backup_to(cfg: &VmConfig, dest: &Path, progress: &mut dyn FnMut(&str, f64)) -> Result<String, String> {
    let all = cfg.all_disks();
    let mut total = 0;
    for (i, disk) in all.iter().enumerate() {
        if i > 0 && !disk.exists() {
            continue;
        }
        let to = backup_disk_path(dest, i);
        let _ = fs::remove_file(&to);
        copy_disk(disk, &to, true, &format!("Backing up Disk {} of {}", i + 1, all.len()), progress)?;
        total += fs::metadata(&to).map(|m| m.len()).unwrap_or(0);
    }
    fs::copy(cfg.file(), format!("{}.ini", dest.to_string_lossy())).map_err(|e| e.to_string())?;
    Ok(format!("Backed up {}{} to\n{}\n({}, compressed).", cfg.name(),
        if all.len() > 1 { format!(" and its {} disks", all.len()) } else { String::new() }, dest.to_string_lossy(), bytes(total)))
}

#[tauri::command(async)]
pub fn backup(app: AppHandle, vms: State<Vms>, id: String, dest: String) -> Result<String, String> {
    let cfg = find(&id)?;
    stopped(&vms, &cfg)?;
    backup_to(&cfg, Path::new(&dest), &mut reporter(&app))
}

// Makes a new system's folder with `ini`'s settings (or the original's), its own copies of the disks, and a name.
fn new_system_from(ini: &Path, disks: &[PathBuf], id: &str, name: &str, parent: &Path, compress: bool,
                   what: &str, progress: &mut dyn FnMut(&str, f64)) -> Result<VmConfig, String> {
    let dir = parent.join(id);
    fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let made = (|| {
        let format = VmConfig::load_ini(ini, &dir).ok_or("The settings can't be read.")?.format;
        fs::copy(ini, dir.join(format.file())).map_err(|e| format!("Couldn't copy the settings: {e}"))?;
        let mut entries = vec![];
        for (i, from) in disks.iter().enumerate() {
            if !from.exists() {
                if i == 0 {
                    return Err(format!("{} is missing.", from.to_string_lossy()));
                }
                continue;
            }
            let name = config::disk_name(format, id, if i == 0 { 1 } else { entries.len() + 2 });
            copy_disk(from, &dir.join(&name), compress, &format!("{what} (Disk {} of {})", i + 1, disks.len()), progress)?;
            if i > 0 {
                entries.push(name);
            }
        }
        let cfg = VmConfig::load(&dir).ok_or("The copy's settings can't be read.")?;
        cfg.set_all(&[("name", name.to_string()), ("pending", "0".into()), ("disks", entries.join("|"))]).map_err(|e| e.to_string())?;
        VmConfig::load(&dir).ok_or_else(|| "The copy's settings can't be read.".to_string())
    })();
    if made.is_err() {
        let _ = fs::remove_dir_all(&dir);
    }
    made
}

// A backup comes back as a new system next to the others; the original stays as it is.
pub fn restore_from(cfg: &VmConfig, src: &Path, parent: &Path, progress: &mut dyn FnMut(&str, f64)) -> Result<String, String> {
    let ini = PathBuf::from(format!("{}.ini", src.to_string_lossy()));
    let ini = if ini.exists() { ini } else { cfg.file() };
    let saved = VmConfig::load_ini(&ini, &cfg.dir).ok_or("The backup's settings can't be read.")?;
    let count = 1 + extra_entries(&saved).len();
    let disks: Vec<PathBuf> = (0..count).map(|i| backup_disk_path(src, i)).collect();
    let when = fs::metadata(src).and_then(|m| m.modified()).map(local_date).unwrap_or_default();
    let name = format!("{} (restored {when})", saved.name());
    let id = folder_name(&format!("{}-restored", cfg.id), parent);
    let copy = new_system_from(&ini, &disks, &id, &name, parent, false, "Restoring", progress)?;
    Ok(format!("Restored as \"{}\". It uses the same port forwards as the original, so run one at a time or change them in Settings.", copy.name()))
}

fn local_date(t: std::time::SystemTime) -> String {
    // Days since 1970 to a calendar date (UTC is close enough for a label).
    let days = t.duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs() / 86400).unwrap_or(0) as i64;
    let (z, era) = (days + 719_468, (days + 719_468).div_euclid(146_097));
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    format!("{}-{m:02}-{d:02}", yoe + era * 400 + i64::from(m <= 2))
}

#[tauri::command(async)]
pub fn restore(app: AppHandle, id: String, src: String) -> Result<String, String> {
    let cfg = find(&id)?;
    restore_from(&cfg, Path::new(&src), &vms_dir(), &mut reporter(&app))
}

// ---------------------------------------------------------------- clone

// A full copy as a new system: each disk copied (snapshots aren't carried over), its settings, and what it runs.
pub fn clone_to(cfg: &VmConfig, parent: &Path, progress: &mut dyn FnMut(&str, f64)) -> Result<String, String> {
    let id = folder_name(&format!("{}-clone", cfg.id), parent);
    let copy = new_system_from(&cfg.file(), &cfg.all_disks(), &id, &format!("{} (clone)", cfg.name()), parent, false,
        &format!("Cloning {}", cfg.name()), progress)?;
    let guest = cfg.dir.join("guest-os.txt");
    if guest.exists() {
        let _ = fs::copy(&guest, copy.dir.join("guest-os.txt"));
    }
    Ok(format!("Cloned as \"{}\". It uses the same port forwards as the original, so run one at a time or change them in Settings.", copy.name()))
}

#[tauri::command(async)]
pub fn clone_system(app: AppHandle, vms: State<Vms>, id: String) -> Result<String, String> {
    let cfg = find(&id)?;
    stopped(&vms, &cfg)?;
    clone_to(&cfg, &vms_dir(), &mut reporter(&app))
}

// ---------------------------------------------------------------- snapshots (of Disk 1, kept inside its file)

#[derive(Serialize)]
pub struct Snapshot {
    name: String,
    when: String,
}

pub fn list_snapshots(cfg: &VmConfig) -> Result<Vec<Snapshot>, String> {
    let img = crate::vm::qemu_img().ok_or("qemu-img isn't installed.")?;
    let mut cmd = std::process::Command::new(img);
    cmd.args(["info", "--force-share", "--output=json", &cfg.disk().to_string_lossy()]);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000);
    }
    let out = cmd.output().map_err(|e| e.to_string())?;
    if !out.status.success() {
        return Err(String::from_utf8_lossy(&out.stderr).trim().to_string());
    }
    let v: serde_json::Value = serde_json::from_slice(&out.stdout).map_err(|e| e.to_string())?;
    Ok(v["snapshots"].as_array().map(|a| a.iter().map(|s| {
        let secs = s["date-sec"].as_u64().unwrap_or(0);
        let t = std::time::UNIX_EPOCH + std::time::Duration::from_secs(secs);
        let hm = { let m = (secs / 60) % 1440; format!("{:02}:{:02} UTC", m / 60, m % 60) };
        Snapshot { name: s["name"].as_str().unwrap_or("").into(), when: format!("{} {hm}", local_date(t)) }
    }).collect()).unwrap_or_default())
}

#[tauri::command(async)]
pub fn snapshots(id: String) -> Result<Vec<Snapshot>, String> {
    list_snapshots(&find(&id)?)
}

// take (a new one, named by the time), revert (to `name`) or delete (`name`).
pub fn snapshot_action(cfg: &VmConfig, action: &str, name: &str) -> Result<(), String> {
    let disk = cfg.disk().to_string_lossy().to_string();
    let stamp;
    let (flag, target) = match action {
        "take" => {
            let secs = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
            let t = secs % 86400;
            stamp = format!("snapshot-{}-{:02}{:02}{:02}", local_date(std::time::SystemTime::now()).replace('-', ""), t / 3600, t / 60 % 60, t % 60);
            ("-c", stamp.as_str())
        }
        "revert" => ("-a", name),
        "delete" => ("-d", name),
        _ => return Err(format!("{action} isn't a snapshot action.")),
    };
    qemu_img(&["snapshot", flag, target, &disk])
}

#[tauri::command(async)]
pub fn snapshot(vms: State<Vms>, id: String, action: String, name: String) -> Result<(), String> {
    let cfg = find(&id)?;
    stopped(&vms, &cfg)?;
    snapshot_action(&cfg, &action, &name)
}

// ---------------------------------------------------------------- storage: move and clean

fn tree_size(dir: &Path) -> u64 {
    fs::read_dir(dir).map(|r| r.flatten().map(|e| {
        let p = e.path();
        if p.is_dir() { tree_size(&p) } else { e.metadata().map(|m| m.len()).unwrap_or(0) }
    }).sum()).unwrap_or(0)
}

fn copy_tree(from: &Path, to: &Path, done: &mut u64, total: u64, progress: &mut dyn FnMut(&str, f64)) -> Result<(), String> {
    fs::create_dir_all(to).map_err(|e| e.to_string())?;
    for e in fs::read_dir(from).map_err(|e| e.to_string())?.flatten() {
        let (src, dst) = (e.path(), to.join(e.file_name()));
        if src.is_dir() {
            copy_tree(&src, &dst, done, total, progress)?;
        } else {
            progress(&format!("Copying {}", file_name(&src)), *done as f64 / total.max(1) as f64);
            fs::copy(&src, &dst).map_err(|err| format!("Couldn't copy {}: {err}", src.to_string_lossy()))?;
            *done += e.metadata().map(|m| m.len()).unwrap_or(0);
        }
    }
    Ok(())
}

// Checks a move of the storage folder `from` into `<parent>\TryLinux`, giving where it goes and how much moves.
pub fn plan_move(from: &Path, parent: &Path) -> Result<(PathBuf, u64), String> {
    let to = parent.join("TryLinux");
    let (f, t) = (from.to_string_lossy().trim_end_matches('\\').to_lowercase(), to.to_string_lossy().to_lowercase());
    if t == f || t.starts_with(&format!("{f}\\")) {
        return Err("Choose a folder outside the current storage location.".into());
    }
    if to.exists() && fs::read_dir(&to).map(|mut r| r.next().is_some()).unwrap_or(false) {
        return Err(format!("{} already exists and isn't empty.", to.to_string_lossy()));
    }
    let need = tree_size(from);
    if let Some((free, drive)) = crate::settings::free_on(&to.to_string_lossy()) {
        if free < need + GIB {
            return Err(format!("Not enough space on {drive}: {} needed.", bytes(need)));
        }
    }
    Ok((to, need))
}

// Copies everything, then points Try Linux (`point_to`) and each system's installer path at the new place,
// then deletes the old folder. Nothing is deleted until the copy is complete.
pub fn move_tree(from: &Path, to: &Path, point_to: &dyn Fn(&Path) -> std::io::Result<()>, progress: &mut dyn FnMut(&str, f64)) -> Result<(), String> {
    let total = tree_size(from);
    let mut done = 0;
    if let Err(e) = copy_tree(from, to, &mut done, total, progress) {
        let _ = fs::remove_dir_all(to);
        return Err(e);
    }
    progress("Finishing", -1.0);
    point_to(to).map_err(|e| e.to_string())?;
    // Installers that were in the old folder are in the new one now.
    let old = from.to_string_lossy().to_lowercase();
    // The systems moved too: in vms/ of the new storage folder, or (Linux) the folder itself.
    let moved_vms = if to.join("vms").is_dir() { to.join("vms") } else { to.to_path_buf() };
    let vms = fs::read_dir(moved_vms).map(|r| r.flatten().map(|e| e.path()).collect::<Vec<_>>()).unwrap_or_default();
    for dir in vms.into_iter().chain(config::system_dirs()) {
        if let Some(c) = VmConfig::load(&dir) {
            let iso = c.get("iso").to_string();
            if iso.to_lowercase().starts_with(&old) {
                let _ = c.set_all(&[("iso", format!("{}{}", to.to_string_lossy(), &iso[old.len()..]))]);
            }
        }
    }
    fs::remove_dir_all(from).map_err(|e| format!("Moved, but the old folder couldn't be removed completely: {e}"))
}

// With `go` false, only checks and says how much would move (for the question); with it true, moves.
#[tauri::command(async)]
pub fn move_storage(app: AppHandle, vms: State<Vms>, parent: String, go: bool) -> Result<String, String> {
    let from = paths::movable();
    let anything_on = !vms.0.lock().unwrap().is_empty()
        || config::system_dirs().iter().filter_map(|d| VmConfig::load(d)).any(|c| crate::running_elsewhere(&c));
    if anything_on {
        return Err("Shut every system down before moving storage.".into());
    }
    let (to, need) = plan_move(&from, Path::new(&parent))?;
    if !go {
        return Ok(format!("{} of systems and downloads move from\n{}\nto\n{}", bytes(need), from.to_string_lossy(), to.to_string_lossy()));
    }
    move_tree(&from, &to, &paths::set_moved, &mut reporter(&app))?;
    Ok(format!("Moved {} to {}.", bytes(need), to.to_string_lossy()))
}

#[derive(Serialize)]
pub struct Cleanable {
    files: Vec<String>,
    total: u64,
}

// Downloaded installers no system still needs (one waiting to install keeps its own).
fn cleanable(folders: &[PathBuf], needed: &[String]) -> Vec<PathBuf> {
    folders.iter().flat_map(|d| fs::read_dir(d).map(|r| r.flatten().map(|e| e.path()).collect::<Vec<_>>()).unwrap_or_default())
        .filter(|p| p.is_file() && !needed.iter().any(|n| n.eq_ignore_ascii_case(&p.to_string_lossy())))
        .collect()
}
fn needed_isos() -> Vec<String> {
    config::system_dirs().iter().filter_map(|d| VmConfig::load(d)).filter(|c| c.pending()).map(|c| c.get("iso").to_string()).collect()
}

#[tauri::command(async)]
pub fn clean_list() -> Cleanable {
    let files = cleanable(&[isos_dir(), downloads_dir()], &needed_isos());
    Cleanable { total: files.iter().filter_map(|f| fs::metadata(f).ok()).map(|m| m.len()).sum(), files: files.iter().map(|f| file_name(f)).collect() }
}

#[tauri::command(async)]
pub fn clean_run() -> String {
    let files = cleanable(&[isos_dir(), downloads_dir()], &needed_isos());
    let (mut freed, mut kept) = (0, 0);
    for f in files {
        let len = fs::metadata(&f).map(|m| m.len()).unwrap_or(0);
        if fs::remove_file(&f).is_ok() { freed += len } else { kept += 1 }
    }
    format!("Freed {}.{}", bytes(freed), if kept > 0 { format!(" {kept} file(s) were in use and kept.") } else { String::new() })
}

// ---------------------------------------------------------------- repair: reset and boot an ISO

// Erases Disk 1 (a new empty one the same size) and sets the installer to run at the next start.
pub fn reset_system(cfg: &VmConfig, iso: &str) -> Result<String, String> {
    if !Path::new(iso).is_file() {
        return Err(format!("{iso} can't be found."));
    }
    let disk = cfg.disk();
    let size = crate::settings::disk_info(disk.to_string_lossy().into()).size.unwrap_or(0) / GIB;
    let gb = size.max(cfg.get("diskgb").parse().unwrap_or(0)).max(16);
    let _ = fs::remove_file(&disk);
    qemu_img(&["create", "-f", "qcow2", &disk.to_string_lossy(), &format!("{gb}G")]).map_err(|e| format!("Couldn't make the new disk.\n\n{e}"))?;
    cfg.set_all(&[("iso", iso.into()), ("pending", "1".into())]).map_err(|e| e.to_string())?;
    Ok(format!("{} was reset. Click Install on Virtual Machines to run the installer.", cfg.name()))
}

#[tauri::command(async)]
pub fn reset(vms: State<Vms>, id: String, iso: String) -> Result<String, String> {
    let cfg = find(&id)?;
    stopped(&vms, &cfg)?;
    reset_system(&cfg, &iso)
}

// The installer an existing system was made with, if it's still there (Reset offers it).
#[tauri::command]
pub fn installer_of(id: String) -> Option<String> {
    let iso = find(&id).ok()?.get("iso").to_string();
    Path::new(&iso).is_file().then_some(iso)
}

// Starts the system from an ISO (a rescue disk, say), as its installer; Disk 1 is left as it is.
#[tauri::command(async)]
pub fn boot_iso(app: AppHandle, probe: State<Probe>, vms: State<Vms>, id: String, iso: String) -> Result<(), String> {
    let cfg = find(&id)?;
    stopped(&vms, &cfg)?;
    if iso.contains(',') {
        return Err("QEMU can't open an ISO whose path contains a comma.".into());
    }
    cfg.set_all(&[("iso", iso), ("pending", "1".into())]).map_err(|e| e.to_string())?;
    crate::vm::start(&app, &vms, &probe.host(), &id, None, false).map_err(|p| format!("{}\n\n{}", p.title, p.text))
}

// ---------------------------------------------------------------- delete

// The system's folder goes (permanently), with its shortcut (if it starts the system) and sign-in entry; disks
// attached from other folders and downloaded installers stay.
pub fn delete_system(cfg: &VmConfig) -> Result<String, String> {
    let _ = shortcuts::set_run_at_sign_in(&cfg.id, &cfg.name(), false);
    if cfg.get("shortcutdirect") != "0" && shortcuts::shortcut_exists(&cfg.id, &cfg.name()) {
        shortcuts::remove_shortcut(&cfg.id, &cfg.name());
    }
    fs::remove_dir_all(&cfg.dir).map_err(|e| format!("Couldn't delete it completely: {e}"))?;
    // A system in a location of its own is no longer looked for there.
    let _ = paths::forget(&cfg.dir);
    Ok(format!("{} was deleted.", cfg.name()))
}

#[tauri::command(async)]
pub fn delete(app: AppHandle, vms: State<Vms>, id: String) -> Result<String, String> {
    let cfg = find(&id)?;
    stopped(&vms, &cfg)?;
    let r = delete_system(&cfg);
    crate::changed(&app);
    r
}

// Every Recovery action on a scratch storage folder (systems, isos, settings.ini of its own), never the real one.
// TRYLINUX_TEST_DIR=folder cargo test recovery_actions -- --ignored --nocapture
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore]
    fn recovery_actions() {
        let root = PathBuf::from(std::env::var("TRYLINUX_TEST_DIR").unwrap());
        let _ = fs::remove_dir_all(&root);
        let store = root.join("store");
        let vms = store.join("vms");
        let dir = vms.join("test-sys");
        fs::create_dir_all(&dir).unwrap();
        fs::create_dir_all(store.join("isos")).unwrap();
        let iso = store.join("isos").join("linux.iso");
        fs::write(&iso, vec![0u8; 4096]).unwrap();
        fs::write(store.join("isos").join("old.iso"), vec![0u8; 2048]).unwrap();
        fs::write(dir.join("vm.ini"), format!("name=Test System\r\niso={}\r\npending=0\r\ndisks=test-sys-disk2.qcow2\r\n", iso.to_string_lossy())).unwrap();
        qemu_img(&["create", "-f", "qcow2", &dir.join("test-sys.qcow2").to_string_lossy(), "2G"]).unwrap();
        qemu_img(&["create", "-f", "qcow2", &dir.join("test-sys-disk2.qcow2").to_string_lossy(), "1G"]).unwrap();
        fs::write(dir.join("guest-os.txt"), "Test Linux 1").unwrap();
        let ini = root.join("settings.ini");
        fs::write(&ini, "storage=\r\ntheme=dark\r\n").unwrap();
        let cfg = || VmConfig::load(&dir).unwrap();
        let mut quiet = |_: &str, _: f64| {};

        let backup = root.join("backups").join("test-backup.qcow2");
        fs::create_dir_all(backup.parent().unwrap()).unwrap();
        println!("backup: {:?}", backup_to(&cfg(), &backup, &mut quiet));
        println!("  files: {:?}", fs::read_dir(backup.parent().unwrap()).unwrap().flatten().map(|e| e.file_name()).collect::<Vec<_>>());
        println!("restore: {:?}", restore_from(&cfg(), &backup, &vms, &mut quiet));
        println!("clone: {:?}", clone_to(&cfg(), &vms, &mut quiet));
        for d in fs::read_dir(&vms).unwrap().flatten() {
            let c = VmConfig::load(&d.path()).unwrap();
            println!("  {} | name={} | disks={} | pending={} | files={:?}", c.id, c.name(), c.get("disks"), c.get("pending"),
                fs::read_dir(d.path()).unwrap().flatten().map(|e| e.file_name()).collect::<Vec<_>>());
        }

        println!("take: {:?}", snapshot_action(&cfg(), "take", ""));
        let snaps = list_snapshots(&cfg()).unwrap();
        println!("  snapshots: {:?}", snaps.iter().map(|s| (&s.name, &s.when)).collect::<Vec<_>>());
        println!("revert: {:?}", snapshot_action(&cfg(), "revert", &snaps[0].name));
        println!("delete: {:?}", snapshot_action(&cfg(), "delete", &snaps[0].name));
        println!("  left: {}", list_snapshots(&cfg()).unwrap().len());

        println!("clean (none pending): {:?}", cleanable(&[store.join("isos")], &[]).iter().map(|p| file_name(p)).collect::<Vec<_>>());
        println!("clean (linux.iso needed): {:?}", cleanable(&[store.join("isos")], &[iso.to_string_lossy().into()]).iter().map(|p| file_name(p)).collect::<Vec<_>>());

        println!("reset: {:?}", reset_system(&cfg(), &iso.to_string_lossy()));
        println!("  pending={} size={:?} GB", cfg().get("pending"), crate::settings::disk_info(cfg().disk().to_string_lossy().into()).size.map(|s| s >> 30));

        println!("move into itself: {:?}", plan_move(&store, &store).err());
        let (to, need) = plan_move(&store, &root.join("elsewhere")).unwrap();
        println!("move plan: {} ({need} bytes)", file_name(&to));
        let point = |p: &Path| fs::write(&ini, format!("storage={}\r\ntheme=dark\r\n", p.to_string_lossy()));
        println!("move: {:?}", move_tree(&store, &to, &point, &mut quiet));
        println!("  old gone: {}  settings.ini: {:?}", !store.exists(), fs::read_to_string(&ini).unwrap());
        let moved = VmConfig::load(&to.join("vms").join("test-sys")).unwrap();
        println!("  iso now under new storage: {}", moved.get("iso").starts_with(&*to.to_string_lossy()));

        println!("delete: {:?}", delete_system(&moved));
        println!("  folder gone: {}", !to.join("vms").join("test-sys").exists());
    }
}
