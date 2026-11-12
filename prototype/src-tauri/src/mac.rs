// macOS: systems run with Apple's Virtualization framework, as the Mac edition runs them (raw disk.img, EFI,
// virtio devices, NAT, Rosetta and clipboard sharing), each in its own window.
//
// Not built yet: the next step of the prototype fills this in with the framework's Objective-C classes
// (objc2-virtualization). Until then, starting a system says so.

use crate::config::VmConfig;
use crate::vm::Vms;
use tauri::AppHandle;

pub fn start(_app: &AppHandle, _vms: &Vms, cfg: &VmConfig, _held: bool) -> Result<(), String> {
    Err(format!("Running systems on a Mac isn't in the prototype yet. Open {} with the Mac edition of Try Linux for now.", cfg.name()))
}
