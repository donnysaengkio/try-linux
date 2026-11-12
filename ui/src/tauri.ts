// The Rust side's commands and events (src-tauri/src/main.rs), typed.
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

export type VmState = "off" | "running" | "paused" | "held" | "elsewhere";
export interface Vm {
  id: string;
  name: string;
  os: string;
  status: string;
  state: VmState;
  cores: number;
  ram_gb: number;
  disk: string;
  cpu: number | null;        // percent of its cores, while running
  mem: number | null;        // bytes QEMU holds on this PC
  guest_mem: number | null;  // bytes Linux says it uses, once the balloon reports
  disk_rate: number | null;  // bytes a second
  maybe_installed: boolean;  // waiting to install, but its disk already holds several GB
}
export interface Problem { title?: string; text?: string }
export type PowerAction =
  | "shutdown" | "restart" | "pause" | "resume" | "power-on" | "reset" | "close-console" | "force";

export const systems = () => invoke<Vm[]>("systems");
export const start = (id: string) => invoke("start", { id });
// From now on it starts from its disk, not its installer.
export const markInstalled = (id: string) => invoke("mark_installed", { id });
export const showConsole = (id: string) => invoke("console", { id });
export const power = (id: string, action: PowerAction) => invoke("power", { id, action });
export const quit = () => invoke("quit");
export const catalog = () => invoke<string>("catalog");
export const startPage = () => invoke<string>("start_page");
// A web page, or a Windows Settings page (ms-settings:).
export const openPage = (url: string) => invoke("open_page", { url });

export const onSystemsChanged = (f: () => void) => listen("systems-changed", f);
export const onProblem = (f: (p: Problem) => void) => listen<Problem>("problem", e => f(e.payload));
export const onAskPower = (f: (id: string, action: PowerAction) => void) =>
  listen<{ id: string; action: PowerAction }>("ask-power", e => f(e.payload.id, e.payload.action));
export const onAskQuit = (f: () => void) => listen("ask-quit", f);

// ---- Settings (src-tauri/src/settings.rs)
export interface Settings {
  id: string; name: string; os: string; distro: string; dir: string; running: boolean;
  description: string; fullscreen: boolean; fs_display: string;
  displays: number; render: number; venus: boolean; gpu_mem: number; last_render: string;
  profile: number; ram: number; cores: number;
  disks: string[]; disk_full: boolean; reclaim: boolean; cd: string;
  sound: boolean; mic: boolean; net_type: number; forwards: string[]; shared: string; shared_write: boolean;
  has_shortcut: boolean; shortcut_direct: boolean; sign_in: boolean;
  shortcuts: boolean;  // this computer has shortcuts (macOS hasn't)
  platform: Platform;
  threads: number; total_ram: number; max_cores: number; max_ram: number; max_gpu_mem: number; gpu_name: string;
  balanced: [number, number]; maximum: [number, number];
}
// What the Settings page saves (the rest changes at once, or comes from the system itself).
export type Changes = Pick<Settings, "name" | "description" | "fullscreen" | "fs_display" | "displays" | "render" | "venus"
  | "gpu_mem" | "profile" | "ram" | "cores" | "reclaim" | "sound" | "mic" | "net_type" | "forwards" | "shared" | "shared_write"
  | "shortcut_direct" | "sign_in">;
export interface DiskReport { actual: number | null; size: number | null; snapshots: number; free: number | null; drive: string; error: string | null }

export const settings = (id: string) => invoke<Settings>("settings", { id });
// Rejects with [tab, message] when something can't be saved, so the page can show that tab.
export const saveSettings = (id: string, c: Changes) => invoke("save_settings", { id, c });
// Writes the desktop shortcut (starting the system directly, or opening Try Linux); resolves with its name.
export const addShortcut = (id: string, direct: boolean) => invoke<string>("add_shortcut", { id, direct });
// Disk Utilities: each resolves with a sentence saying what happened (src-tauri/src/disks.rs).
export const diskResize = (id: string, index: number, gb: number) => invoke<string>("disk_resize", { id, index, gb });
export const diskCompact = (id: string, index: number) => invoke<string>("disk_compact", { id, index });
export const diskAdd = (id: string, gb: number | null, file: string | null) => invoke<string>("disk_add", { id, gb, file });
export const diskRemove = (id: string, index: number) => invoke<string>("disk_remove", { id, index });
export const diskInfo = (path: string) => invoke<DiskReport>("disk_info", { path });
// Puts an ISO in the drive (or empties it, with ""), at once; resolves with a note if it applies only from the next start.
export const setDisc = (id: string, iso: string) => invoke<string | null>("set_disc", { id, iso });

// ---- Create (src-tauri/src/create.rs)
export interface Defaults {
  name: string; location: string; taken: string[];
  threads: number; total_ram: number; max_cores: number; max_ram: number;
  balanced: [number, number]; maximum: [number, number];
}
export interface Free { bytes: number | null; drive: string }
// Exactly one of distro (download it), iso (a file on this PC), url (download it) or disk (import it).
export interface Plan {
  distro: { id: string; name: string; base: string; steps: string[]; checksum: string | null } | null;
  iso: string | null; url: string | null; disk: string | null;
  name: string; description: string; location: string;
  profile: number; ram: number; cores: number; disk_gb: number; disk_full: boolean;
  install_winq: boolean;  // install WINQ-EMU first (asked on the page)
}
export interface Progress { status: string; value: number; detail: string }  // value: 0 to 1, or -1 when unknown

export const createDefaults = (seed: string) => invoke<Defaults>("create_defaults", { seed });
export const freeSpace = (path: string) => invoke<Free>("free_space", { path });
// Resolves with null once it has started, or a note when it's ready but Linux can't run yet; rejects with
// "cancelled" after Cancel.
export const create = (plan: Plan) => invoke<string | null>("create", { plan });
export const cancelCreate = () => invoke("cancel_create");
export const onCreateProgress = (f: (p: Progress) => void) => listen<Progress>("create-progress", e => f(e.payload));

// What an existing disk says about itself, and the settings found beside it (Try Linux, VMware or VirtualBox).
export interface DiskSource { format: string; size: number | null; name: string; description: string; ram_gb: number; cores: number; from: string }
export const readDisk = (path: string) => invoke<DiskSource>("read_disk", { path });

// ---- The main menu (src-tauri/src/app.rs)
export type Platform = "windows" | "linux" | "macos";
export interface AppInfo {
  version: string; theme: "dark" | "light"; follow_system: boolean; check_updates: boolean; storage: string;
  winq: string | null;  // WINQ-EMU's version (Windows)
  engine: string;       // what runs the systems, with its version
  platform: Platform;
}
export interface Updates { winq: string | null; latest: string | null; newer: boolean; page: string }
export const appInfo = () => invoke<AppInfo>("app_info");
export const setPreference = (key: "theme" | "followsystem" | "checkupdates", value: string) => invoke("set_preference", { key, value });
export const checkUpdates = () => invoke<Updates>("check_updates");
export const openStorage = () => invoke("open_storage");

// ---- Recovery (src-tauri/src/recovery.rs): each resolves with a sentence saying what happened
export interface Snapshot { name: string; when: string }
export const backupSystem = (id: string, dest: string) => invoke<string>("backup", { id, dest });
export const restoreBackup = (id: string, src: string) => invoke<string>("restore", { id, src });
export const cloneSystem = (id: string) => invoke<string>("clone_system", { id });
export const listSnapshots = (id: string) => invoke<Snapshot[]>("snapshots", { id });
export const snapshotAction = (id: string, action: "take" | "revert" | "delete", name = "") => invoke("snapshot", { id, action, name });
// With go false, only checks and describes the move; with it true, moves.
export const moveStorage = (parent: string, go: boolean) => invoke<string>("move_storage", { parent, go });
export const cleanList = () => invoke<{ files: string[]; total: number }>("clean_list");
export const cleanRun = () => invoke<string>("clean_run");
export const resetSystem = (id: string, iso: string) => invoke<string>("reset", { id, iso });
export const installerOf = (id: string) => invoke<string | null>("installer_of", { id });
export const bootIso = (id: string, iso: string) => invoke("boot_iso", { id, iso });
export const deleteSystem = (id: string) => invoke<string>("delete", { id });

// ---- What this PC needs before Linux can run (src-tauri/src/setup.rs)
export type Virt = "ready" | "no-cpu" | "firmware-off" | "feature-off" | "not-running" | "kvm-missing" | "kvm-denied" | "mac-unsupported";
// winq: what runs systems is installed; can_install: Try Linux can install it (WINQ-EMU, on Windows), otherwise
// install_message says how.
export interface Readiness { virt: Virt; message: string; winq: boolean; can_install: boolean; install_message: string }
export const readiness = () => invoke<Readiness>("readiness");
// Rejects with "declined" when the Windows permission prompt is turned down.
export const enableHypervisor = () => invoke("enable_hypervisor");
export const updateWinq = () => invoke<string>("update_winq");
