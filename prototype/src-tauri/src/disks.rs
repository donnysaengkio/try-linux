// Settings > Hardware > Disks > Disk Utilities: resizing, compacting, adding and removing a system's disks, as
// the Windows edition does them. Each needs the system shut down, and each answers with a sentence to show.

use crate::config::{self, VmConfig};
use crate::create::{bytes, qemu_img, qemu_img_progress};
use crate::settings::{disk_info, free_on};
use crate::vm::Vms;
use serde::Serialize;
use std::fs;
use std::path::{Path, PathBuf};
use tauri::{AppHandle, Emitter, State};

const GIB: u64 = 1 << 30;

fn find(id: &str) -> Result<VmConfig, String> {
    config::find(id).ok_or_else(|| "This system's folder can't be found any more.".into())
}

// Running here or in another copy of Try Linux: its disks are in use.
fn stopped(vms: &Vms, cfg: &VmConfig, doing: &str) -> Result<(), String> {
    if vms.0.lock().unwrap().contains_key(&cfg.id) || crate::running_elsewhere(cfg) {
        return Err(format!("Shut {} down before {doing}. Use the Try Linux tray icon, or shut down from inside Linux.", cfg.name()));
    }
    Ok(())
}

fn disk_at(cfg: &VmConfig, index: usize) -> Result<PathBuf, String> {
    cfg.all_disks().get(index).cloned().ok_or_else(|| "That disk isn't part of the system any more.".into())
}

fn same_file(a: &Path, b: &Path) -> bool {
    let full = |p: &Path| fs::canonicalize(p).unwrap_or_else(|_| p.to_path_buf()).to_string_lossy().to_lowercase();
    full(a) == full(b)
}

// ---------------------------------------------------------------- resize

pub fn resize_disk(cfg: &VmConfig, index: usize, gb: u64) -> Result<String, String> {
    let path = disk_at(cfg, index)?;
    let info = disk_info(path.to_string_lossy().into());
    if let Some(e) = info.error {
        return Err(e);
    }
    if info.snapshots > 0 {
        return Err("A disk with snapshots can't be resized. Delete its snapshots first (Settings > Recovery).".into());
    }
    let current = info.size.unwrap_or(0);
    let used = info.actual.unwrap_or(0);
    // Never below what Linux has already written, plus a little room.
    if gb * GIB < used + GIB {
        return Err(format!("{} GB is less than the disk already holds ({}).", gb, bytes(used)));
    }
    let shrink = gb * GIB < current;
    let p = path.to_string_lossy().to_string();
    let size = format!("{gb}G");
    let mut args = vec!["resize"];
    if shrink {
        args.push("--shrink");
    }
    args.extend([p.as_str(), size.as_str()]);
    qemu_img(&args).map_err(|e| format!("Couldn't resize the disk.\n\n{e}"))?;
    if index == 0 {
        // The new size is the disk's own now; nothing is left to grow at the next start.
        cfg.set_all(&[("diskgb", "0".into())]).map_err(|e| e.to_string())?;
    }
    Ok(format!("Disk {} is now {gb} GB.{}", index + 1, if shrink { "" } else { " Linux may need to expand its partition to use the new space." }))
}

#[tauri::command(async)]
pub fn disk_resize(vms: State<Vms>, id: String, index: usize, gb: u64) -> Result<String, String> {
    let cfg = find(&id)?;
    stopped(&vms, &cfg, "resizing its disks")?;
    resize_disk(&cfg, index, gb)
}

// ---------------------------------------------------------------- compact

#[derive(Serialize, Clone)]
struct ToolProgress {
    status: String,
    value: f64,
    detail: String,
}

// Rewrites a disk without its unused clusters; the original stays until the copy is checked against it.
pub fn compact_disk(cfg: &VmConfig, index: usize, mut progress: impl FnMut(&str, f64)) -> Result<String, String> {
    let path = disk_at(cfg, index)?;
    let info = disk_info(path.to_string_lossy().into());
    if let Some(e) = info.error {
        return Err(e);
    }
    if info.snapshots > 0 {
        return Err("A disk with snapshots can't be compacted, because compacting would remove them. Delete its snapshots first (Settings > Recovery).".into());
    }
    let before = fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
    if let Some((free, drive)) = free_on(&path.to_string_lossy()) {
        if free < before {
            return Err(format!("Compacting needs room for a second copy while it works: {}, but drive {drive} has {} free.", bytes(before), bytes(free)));
        }
    }
    // A preallocated boot disk stops being preallocated once it's rewritten as a sparse file.
    if index == 0 && cfg.get("diskfull") == "1" {
        cfg.set_all(&[("diskfull", "0".into())]).map_err(|e| e.to_string())?;
    }
    let temp = PathBuf::from(format!("{}.compacting", path.to_string_lossy()));
    let (p, t) = (path.to_string_lossy().to_string(), temp.to_string_lossy().to_string());
    let _ = fs::remove_file(&temp);
    let result = qemu_img_progress(&["convert", "-p", "-O", "qcow2", &p, &t], |v| progress("Copying the used space", v), || false)
        .and_then(|_| {
            progress("Checking the copy matches", -1.0);
            qemu_img(&["compare", "-q", &p, &t])   // same contents, or stop here
        });
    if let Err(e) = result {
        let _ = fs::remove_file(&temp);
        return Err(format!("Couldn't compact the disk; it's unchanged.\n\n{e}"));
    }
    fs::remove_file(&path).map_err(|e| e.to_string())?;
    fs::rename(&temp, &path).map_err(|e| e.to_string())?;
    let after = fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
    let saved = before.saturating_sub(after);
    Ok(format!("Disk {} was compacted from {} to {}{}", index + 1, bytes(before), bytes(after),
        if saved > 1 << 20 { format!(", giving {} back to Windows.", bytes(saved)) } else { ". There was little unused space to give back.".into() }))
}

// Off the main thread, with progress as "tool-progress" events.
#[tauri::command(async)]
pub fn disk_compact(app: AppHandle, vms: State<Vms>, id: String, index: usize) -> Result<String, String> {
    let cfg = find(&id)?;
    stopped(&vms, &cfg, "compacting its disks")?;
    compact_disk(&cfg, index, |status, value| {
        let _ = app.emit("tool-progress", ToolProgress { status: status.into(), value, detail: "This can take a few minutes for large disks".into() });
    })
}

// ---------------------------------------------------------------- add and remove

// A new empty disk of `gb` (a file in the system's folder), or an existing .qcow2 file attached where it is.
pub fn add_disk(cfg: &VmConfig, gb: Option<u64>, file: Option<String>) -> Result<String, String> {
    let mut entries: Vec<String> = cfg.get("disks").split('|').filter(|e| !e.is_empty()).map(String::from).collect();
    let note;
    if let Some(file) = file {
        let chosen = PathBuf::from(&file);
        if cfg.all_disks().iter().any(|d| same_file(d, &chosen)) {
            return Err(format!("That disk is already part of {}.", cfg.name()));
        }
        let others = config::system_dirs().into_iter().filter_map(|d| VmConfig::load(&d)).filter(|c| c.id != cfg.id);
        for c in others {
            if c.all_disks().iter().any(|d| same_file(d, &chosen)) {
                return Err("That disk belongs to another system. Two systems can't share a disk.".into());
            }
        }
        if let Some(e) = disk_info(file.clone()).error {
            return Err(e);
        }
        entries.push(fs::canonicalize(&chosen).map(|p| p.to_string_lossy().trim_start_matches(r"\\?\").to_string()).unwrap_or(file));
        note = "";
    } else {
        let gb = gb.unwrap_or(64).clamp(1, 4096);
        let mut n = 2;
        let name = loop {
            let name = cfg.new_disk_name(n);
            if !entries.iter().any(|e| e.eq_ignore_ascii_case(&name)) && !cfg.dir.join(&name).exists() {
                break name;
            }
            n += 1;
        };
        let path = cfg.dir.join(&name).to_string_lossy().to_string();
        qemu_img(&["create", "-f", "qcow2", &path, &format!("{gb}G")]).map_err(|e| format!("Couldn't make the disk.\n\n{e}"))?;
        entries.push(name);
        note = " Partition and format it in Linux (with GNOME Disks, for example) before using it.";
    }
    cfg.set_all(&[("disks", entries.join("|"))]).map_err(|e| e.to_string())?;
    Ok(format!("Disk {} was added to {}.{note}", entries.len() + 1, cfg.name()))
}

#[tauri::command(async)]
pub fn disk_add(vms: State<Vms>, id: String, gb: Option<u64>, file: Option<String>) -> Result<String, String> {
    let cfg = find(&id)?;
    stopped(&vms, &cfg, "adding a disk")?;
    add_disk(&cfg, gb, file)
}

// A disk Try Linux made goes to the Recycle Bin with everything on it; an attached file stays where it is.
pub fn remove_disk(cfg: &VmConfig, index: usize) -> Result<String, String> {
    let mut entries: Vec<String> = cfg.get("disks").split('|').filter(|e| !e.is_empty()).map(String::from).collect();
    if index == 0 || index > entries.len() {
        return Err("Disk 1 is the one the system starts from, so it can't be removed.".into());
    }
    let entry = entries.remove(index - 1);
    let ours = !Path::new(&entry).is_absolute();
    if ours {
        let path = cfg.dir.join(&entry);
        if path.exists() {
            trash::delete(&path).map_err(|e| format!("Couldn't move the disk to the Recycle Bin: {e}"))?;
        }
    }
    cfg.set_all(&[("disks", entries.join("|"))]).map_err(|e| e.to_string())?;
    Ok(format!("Disk {} was removed from {}.{}", index + 1, cfg.name(),
        if ours { " Its file is in the Recycle Bin." } else { " The file is kept where it was." }))
}

#[tauri::command(async)]
pub fn disk_remove(vms: State<Vms>, id: String, index: usize) -> Result<String, String> {
    let cfg = find(&id)?;
    stopped(&vms, &cfg, "removing a disk")?;
    remove_disk(&cfg, index)
}

// Each utility on a scratch system (a folder with a vm.ini and a small disk), never a real one.
// TRYLINUX_TEST_DIR=folder cargo test disk_utilities -- --ignored --nocapture
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore]
    fn disk_utilities() {
        let root = PathBuf::from(std::env::var("TRYLINUX_TEST_DIR").unwrap());
        let dir = root.join("test-sys");
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("vm.ini"), "name=Test System\r\ndisks=\r\ndiskfull=0\r\n").unwrap();
        let d1 = dir.join("test-sys.qcow2").to_string_lossy().to_string();
        qemu_img(&["create", "-f", "qcow2", &d1, "2G"]).unwrap();
        let outside = root.join("outside.qcow2").to_string_lossy().to_string();
        qemu_img(&["create", "-f", "qcow2", &outside, "1G"]).unwrap();
        let cfg = || VmConfig::load(&dir).unwrap();

        println!("add new: {:?}", add_disk(&cfg(), Some(1), None));
        println!("add file: {:?}", add_disk(&cfg(), None, Some(outside.clone())));
        println!("add same again: {:?}", add_disk(&cfg(), None, Some(outside.clone())));
        println!("disks= {}", cfg().get("disks"));
        println!("resize up: {:?}", resize_disk(&cfg(), 0, 3));
        println!("resize down: {:?}", resize_disk(&cfg(), 0, 2));
        println!("size now: {:?}", disk_info(d1.clone()).size.map(|s| s >> 30));
        println!("compact: {:?}", compact_disk(&cfg(), 0, |s, v| if v < 0.0 { println!("  {s}") }));
        println!("remove Disk 1: {:?}", remove_disk(&cfg(), 0));
        println!("remove file: {:?}", remove_disk(&cfg(), 2));
        println!("outside kept: {}", Path::new(&outside).exists());
        println!("remove new: {:?}", remove_disk(&cfg(), 1));
        println!("new disk gone: {}", !dir.join("test-sys-disk2.qcow2").exists());
        println!("disks= {:?}", cfg().get("disks"));
    }
}
