# Developing Try Linux

Try Linux is **one app for Windows, macOS and Linux**, built with [Tauri](https://tauri.app). The interface in
[`ui/`](../ui) is React and TypeScript, the same on every platform, and [`src-tauri/`](../src-tauri) is the Rust
side that does the work. It uses each platform's own web view (WebView2, WKWebView, WebKitGTK), so the app stays
small (about 12 MB on Windows).

## The repository

| Folder | What's in it |
| --- | --- |
| [`src-tauri/`](../src-tauri) | The Rust side: QEMU and Apple's Virtualization framework, Create, Settings, Recovery, updates |
| [`ui/`](../ui) | The interface: React, TypeScript and CSS, with the fonts in `ui/public/fonts` |
| [`scripts/`](../scripts) | `release-windows.ps1` and `release-linux.sh`: build and sign a release's files |
| [`packaging/linux-handover/`](../packaging/linux-handover) | The stand-in Try Linux 1.0.x for Linux updates to, which installs 2.x |
| [`catalog/`](../catalog) | The distribution lists. **Keep them at this path:** every released app, old and new, downloads them from `main` |
| [`icons/`](../icons) | The shared Lucide icons |
| [`docs/`](.) | The guides for Windows and Linux, this page, and their screenshots |
| [`.github/workflows/build.yml`](../.github/workflows/build.yml) | Builds and tests every platform on each push |

Until 2.0 each platform had its own app (`windows/` in C#, `linux/` in Python, `macos/` in Swift). They're in the
repository's history, at the tags of their last releases (`v1.1.13`, `linux-v1.0.7-preview`,
`macos-v1.0.10-preview`), and unreleased work for Windows 1.1.14 is on the `archive/unreleased-1.x` branch.

## Each platform

On ARM64 (Apple silicon, ARM Linux) the built-in distributions and the catalog are the ARM64 ones. Each platform
keeps the files its 1.x edition used, so 2.x opens the systems 1.x made (see `src-tauri/src/paths.rs` and
`config.rs`):

| | Windows | Linux | macOS |
| --- | --- | --- | --- |
| Systems run with | WINQ-EMU (QEMU, WHPX) | QEMU with KVM, in its GTK window | Apple's Virtualization framework |
| A system's settings | `vm.ini` | `vm.json` | `config.json` |
| Disk 1 | `<id>.qcow2` | `disk.qcow2` | `disk.img` (raw) |
| Try Linux's preferences | `%APPDATA%\TryLinux\settings.ini` | `~/.config/TryLinux/settings.json` | the defaults domain `io.github.donnysaengkio.trylinux` |
| Shortcut / at sign-in | desktop `.lnk` / Run key | app launcher / autostart `.desktop` | none / login agent |
| Released as | NSIS installer | AppImage, x86_64 and ARM64 | not yet |
| Status | released (2.0.0) | released (2.0.0) | builds and passes its tests in CI; not yet run on a Mac |

## The code

| File | What it does |
| --- | --- |
| `src-tauri/src/main.rs` | The commands the interface calls, the tray, and taking over systems a closed Try Linux left running |
| `src-tauri/src/config.rs` | A system's settings, in each platform's own file (`vm.ini`, `vm.json`, `config.json`) |
| `src-tauri/src/paths.rs` | Where each platform keeps systems, installers and Try Linux's preferences |
| `src-tauri/src/vm.rs` | Starting QEMU, the power actions, and what happens when it stops |
| `src-tauri/src/mac.rs` | macOS: running systems with Apple's Virtualization framework, each in its own window |
| `src-tauri/src/create.rs` | Create: finding, downloading and checking installers, and making the system |
| `src-tauri/src/settings.rs` | Settings: reading and saving them, a disk's details, the disc in the drive |
| `src-tauri/src/disks.rs` | Disk Utilities: resizing, compacting, adding and removing disks |
| `src-tauri/src/recovery.rs` | Recovery: back up, restore, clone, snapshots, move, clean, reset, boot an ISO, delete |
| `src-tauri/src/live.rs` | Memory and disk activity from QEMU (balloon statistics, block statistics) |
| `src-tauri/src/log.rs` | Each system's log (`trylinux.log` in its folder): starts, stops and why, Power menu commands, QEMU's messages |
| `src-tauri/src/setup.rs` | What the computer needs: virtualization, Windows Hypervisor Platform, WINQ-EMU, KVM |
| `src-tauri/src/shortcuts.rs` | A system's shortcut and starting it at sign-in |
| `src-tauri/src/window.rs` | Systems' windows on Windows: bringing them forward, hiding them from their close button |
| `src-tauri/src/legacy.rs` | Taking over from Windows 1.x and Linux 1.0.x; the Linux AppImage adding itself to the apps menu |
| `src-tauri/src/app.rs` | Preferences, About's details, and checking WINQ-EMU for updates |
| `ui/src/main.tsx` | Which page shows, and messages from the Rust side |
| `ui/src/Home.tsx`, `ui/src/Choose.tsx` | Virtual Machines (sortable columns, groups, dragging; the arrangement is the `layout` preference, which the tray follows), and Choose a Linux OS |
| `ui/src/Create.tsx`, `ui/src/distros.ts` | Create's steps and progress; the built-in distributions and where their installers are |
| `ui/src/OwnInstaller.tsx` | Use Your Own Installer |
| `ui/src/Settings.tsx`, `ui/src/Form.tsx` | Settings, and its sections and controls |
| `ui/src/MainMenu.tsx` | The main menu, its windows, updates and the keyboard shortcuts |
| `ui/src/DiskTools.tsx` | The Disk Utilities' dialogs |
| `ui/src/Ask.tsx`, `ui/src/power.ts` | Questions and problems; the power commands and which ask first |
| `ui/src/tauri.ts` | The Rust side's commands and events, typed |

The interface uses [Base UI](https://base-ui.com) (menus, dialogs, tabs, drop-downs, check boxes and number fields:
keyboard use, focus and screen readers) and [Sonner](https://sonner.emilkowal.ski) (notifications), drawn with Try
Linux's own colours and fonts (`ui/src/style.css`).

## Build and run

Needs [Rust](https://rustup.rs) and [Node.js](https://nodejs.org) (LTS). On Windows, also Visual Studio Build Tools
with the C++ workload; on Linux, WebKitGTK and the other libraries in
[Tauri's prerequisites](https://tauri.app/start/prerequisites/), and QEMU.

```bash
cd ui
npm install
npm run build
cd ../src-tauri
cargo run --release
```

`npm run build` puts the interface in `ui/dist`, which the app includes when Cargo builds it, so run it again after
changing anything in `ui/`.

**Testing without touching your own systems:** `TRYLINUX_HOME=<folder>` makes Try Linux keep its systems and
preferences in that folder instead, and `TRYLINUX_PAGE=<page>` opens it on a given page (for screenshots). The
tests that change files (`disk_utilities`, `recovery_actions`, `takes_over_linux_edition`, `takes_over_old_edition`)
are `#[ignore]`d and run with `TRYLINUX_TEST_DIR=<folder> cargo test <name> -- --ignored`.

## Releasing

Each platform has its own release and its own update feed in the `tauri-updates` release, so their versions can
move separately. Raise `version` in `src-tauri/tauri.conf.json` and `src-tauri/Cargo.toml` first.

Releases are signed for updates with the private key in `%USERPROFILE%\.tauri\try-linux-updates.key` (on Linux,
`~/.tauri/try-linux-updates.key`, or `TAURI_KEY_FILE`). Keep it safe and out of the repository: installed copies only
accept updates signed with it, so without it they can't be updated. Its public half is in `tauri.conf.json`.

### Windows

1. Run `scripts/release-windows.ps1 -Notes "What's new"`. It builds the installer into `dist/`, signs it for updates,
   and writes `SHA256SUMS.txt` and `latest.json`.
2. Publish a release tagged `v<version>` (Latest) with `TryLinux-<version>-Setup.exe`, `TryLinux.exe` and
   `SHA256SUMS.txt`, and upload `latest.json` to the `tauri-updates` release (replacing the last one).

`TryLinux.exe` is the same installer under the name Windows 1.x updates to: 1.x swaps its `TryLinux.exe` for the
newest `v…` release's (checked against `SHA256SUMS.txt`) and runs it, which installs 2.x. On its first start (and
each start until it's done) Try Linux points 1.x's desktop shortcuts and start-at-sign-in entries to itself and
removes 1.x's Start menu entry, its Installed apps entry and `%LOCALAPPDATA%\Programs\TryLinux` (`legacy.rs`).

### Linux

1. On an x86_64 Linux PC (or WSL), run `scripts/release-linux.sh "What's new"`. It builds the x86_64 AppImage into
   `dist-linux/`, signs it for updates, and writes the 1.0.x stand-in (`TryLinux-<version>-linux.tar.gz`),
   `SHA256SUMS.txt` and `latest-linux.json`.
2. Take the ARM64 AppImage from the Build workflow's `try-linux-appimage-linux-aarch64` artifact and add it with
   `scripts/release-linux.sh --add <file> aarch64 "What's new"`, which signs it here (the key never goes to GitHub)
   and rewrites the checksums and the feed.
3. Publish a release tagged `linux-v<version>` (not Latest) with both AppImages, the `.tar.gz` and `SHA256SUMS.txt`,
   and upload `latest-linux.json` to the `tauri-updates` release.

Linux 1.0.x updates to the newest `linux-v…` release by putting its `.tar.gz` in place of its Python code (checked
against `SHA256SUMS.txt`) and starting it again. That's the stand-in in `packaging/linux-handover/`: it downloads the
AppImage for its processor into `~/.local/share/try-linux`, checks it and starts it. Run as an AppImage, Try Linux
adds itself to the apps menu and `~/.local/bin`, points 1.0.x's shortcuts and sign-in entries at itself, and
removes 1.0.x's Python files (`legacy.rs`). Publish both AppImages at once: 1.0.x on either processor takes the
update.

### macOS

Not released yet. Mac 1.x (`macos-v…` releases) will need a move to 2.x like the other two, written from how its
`Install.swift` updates (in the repository's history).

The fonts in `ui/public/fonts` are under the SIL Open Font License (see the `OFL-*.txt` files).
