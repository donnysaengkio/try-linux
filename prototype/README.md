# Try Linux on Tauri (in progress)

Try Linux as **one app for Windows, macOS and Linux**. The interface in [`ui/`](ui) is React and TypeScript, the
same on every platform, and [`src-tauri/`](src-tauri) is the Rust side that does the work. It uses each
platform's own web view (WebView2, WKWebView, WebKitGTK), so the app stays small (about 8 MB on Windows).

## Each platform

On ARM64 (Apple silicon, ARM Linux) the built-in distributions and the catalog are the ARM64 ones, as on the Mac
edition. Each edition's own folders and files, so the app opens the systems that edition made (see `src-tauri/src/paths.rs`
and `config.rs`):

| | Windows | Linux | macOS |
| --- | --- | --- | --- |
| Systems run with | WINQ-EMU (QEMU, WHPX) | QEMU with KVM, in its GTK window | Apple's Virtualization framework |
| A system's settings | `vm.ini` | `vm.json` | `config.json` |
| Disk 1 | `<id>.qcow2` | `disk.qcow2` | `disk.img` (raw) |
| Try Linux's preferences | `%APPDATA%\TryLinux\settings.ini` | `~/.config/TryLinux/settings.json` | the Mac edition's defaults |
| Shortcut / at sign-in | desktop `.lnk` / Run key | app launcher / autostart `.desktop` | none / login agent |
| Status | works | builds and passes its tests in CI | builds in CI (signed for the Virtualization framework); not yet run on a Mac |

[`.github/workflows/prototype.yml`](../.github/workflows/prototype.yml) builds it on Linux (x86_64 and ARM64) and
macOS, runs the tests (on Linux also the disk and Recovery tests, on scratch systems), and keeps each build as an
artifact.

## What works so far (Windows)

- **Virtual Machines** lists the systems the Windows edition made (their `vm.ini` files) and starts them with
  WINQ-EMU the same way, with the Power menu (shut down, restart, pause, force stop, reset) and the console.
  Running systems show live CPU, the memory Linux is using and disk activity. A system started by the Windows
  edition shows as running here, but only that copy controls it.
- **Ctrl+K** opens a command menu to start, stop or open any system by typing.
- **The main menu (☰)**: Check for Updates (WINQ-EMU, as the prototype has no releases yet), Preferences
  (Dark, Light or Follow system, kept in `settings.ini` so both editions match), Keyboard Shortcuts, About, and
  Quit, with the Windows edition's shortcuts (Ctrl+N, Ctrl+F, Ctrl+U, Ctrl+comma, Ctrl+?, Ctrl+Q, Alt+Left).
- A system's close button hides its window; it keeps running, and the tray icon's menu controls it.
  Closing Try Linux while systems run keeps it in the tray.
- **Settings** has the Windows edition's four tabs and sections. Save writes the settings back to the system's
  `vm.ini` (only the keys Settings changes), so either edition reads them; a disc goes in or out of the drive
  at once. A desktop shortcut and starting at sign-in use the same names as the Windows edition's, and run
  Try Linux with `--start <system>`, which starts it and stays in the tray (a second Try Linux hands over to
  the one running). The disk utilities resize, compact (checked against the original before it's replaced),
  add and remove disks. Recovery backs up and restores, clones, takes and reverts snapshots, moves the storage
  folder (deleting the old one only once the copy is complete), cleans up installers, resets, boots an ISO and
  deletes, as the Windows edition does.
- **Create → Choose a Linux OS** shows the recommended distributions and searches the full catalog from
  [`../catalog/distros.json`](../catalog/distros.json). Then General, Resources and Summary, and Install:
  it finds the current installer on the distribution's mirror, downloads it (resuming a partial download),
  checks it against the official SHA-256 or SHA-512 checksum, makes the disk and `vm.ini` as the Windows
  edition does, and starts the installer. **Use Your Own Installer** takes an ISO file, a URL to one (downloaded
  first), or an existing virtual disk (qcow2, VMDK, VHD/VHDX, VDI or raw), copied into a new system with the
  name, memory and cores saved beside it by Try Linux, VMware or VirtualBox.
- **Before Install**, as on Windows: Linux needs hardware virtualization (checked without admin rights); Windows
  Hypervisor Platform is turned on with Windows' permission (one restart); WINQ-EMU is downloaded from GitHub and
  installed during setup, and Check for Updates can update it. A system made before Linux can run waits, ready
  to install.
- Icons are the shared Lucide set in [`../icons`](../icons); fonts and colours match the Windows edition.

Not yet: the macOS and Linux back ends.

## The interface's libraries

Unstyled building blocks, drawn with Try Linux's own colours and fonts (`ui/src/style.css`):

| Library | Used for |
| --- | --- |
| [Base UI](https://base-ui.com) | Menus, dialogs, tabs, drop-downs, check boxes and number fields: keyboard use, focus and screen readers |
| [Sonner](https://sonner.emilkowal.ski) | Notifications |
| [cmdk](https://cmdk.paco.me) | The Ctrl+K command menu |

| File | What it does |
| --- | --- |
| `src-tauri/src/main.rs` | The commands the interface calls, and the tray |
| `src-tauri/src/config.rs` | A system's settings, in each edition's own file (`vm.ini`, `vm.json`, `config.json`) |
| `src-tauri/src/paths.rs` | Where each platform keeps systems, installers and Try Linux's preferences |
| `src-tauri/src/mac.rs` | macOS: running systems with Apple's Virtualization framework, each in its own window |
| `src-tauri/src/vm.rs` | Starting QEMU, the power actions, and what happens when it stops |
| `src-tauri/src/disks.rs` | Disk Utilities: resizing, compacting, adding and removing disks |
| `src-tauri/src/live.rs` | Memory and disk activity from QEMU (balloon statistics, block statistics) |
| `src-tauri/src/app.rs` | Preferences, About's details, and checking WINQ-EMU for updates |
| `src-tauri/src/create.rs` | Create: finding, downloading and checking installers, and making the system |
| `src-tauri/src/recovery.rs` | Recovery: back up, restore, clone, snapshots, move, clean, reset, boot an ISO, delete |
| `src-tauri/src/settings.rs` | Settings: reading and saving them, a disk's details, the disc in the drive |
| `src-tauri/src/setup.rs` | What the PC needs: virtualization, Windows Hypervisor Platform, and installing WINQ-EMU |
| `src-tauri/src/shortcuts.rs` | A system's desktop shortcut and starting it at sign-in |
| `src-tauri/src/window.rs` | Systems' windows: bringing them to the front, hiding them from their close button |
| `ui/src/main.tsx` | Which page shows, and messages from the Rust side |
| `ui/src/Home.tsx`, `ui/src/Choose.tsx` | Virtual Machines, and Choose a Linux OS |
| `ui/src/Create.tsx`, `ui/src/distros.ts` | Create's steps and progress; the distributions and where their installers are |
| `ui/src/OwnInstaller.tsx` | Use Your Own Installer |
| `ui/src/Settings.tsx`, `ui/src/Form.tsx` | Settings, and its sections and controls |
| `ui/src/MainMenu.tsx` | The main menu, its windows and the keyboard shortcuts |
| `ui/src/DiskTools.tsx` | The Disk Utilities' dialogs |
| `ui/src/Commands.tsx` | The command menu |
| `ui/src/Ask.tsx`, `ui/src/power.ts` | Questions and problems; the power commands and which ask first |
| `ui/src/tauri.ts` | The Rust side's commands and events, typed |

## Build and run (Windows)

Needs [Rust](https://rustup.rs), Visual Studio Build Tools with the C++ workload, and [Node.js](https://nodejs.org)
(LTS).

```bash
cd prototype/ui
npm install
npm run build
cd ../src-tauri
cargo run --release
```

`npm run build` puts the interface in `ui/dist`, which the app includes when Cargo builds it, so run it again
after changing anything in `ui/`.

The fonts in `ui/public/fonts` are under the SIL Open Font License (see the `OFL-*.txt` files).
