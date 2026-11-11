# Try Linux on Tauri (in progress)

Try Linux as **one app for Windows, macOS and Linux**: the interface is HTML and CSS in [`ui/`](ui), the same on
every platform, and [`src-tauri/`](src-tauri) is the Rust side that does the work. It uses each platform's own
web view (WebView2, WKWebView, WebKitGTK), so the app stays small (about 8 MB on Windows).

## What works so far (Windows)

- **Virtual Machines** lists the systems the Windows edition made (their `vm.ini` files) and starts them with
  WINQ-EMU the same way, with the Power menu (shut down, restart, pause, force stop, reset) and the console.
  Running systems show live CPU, the memory Linux is using and disk activity. A system started by the Windows
  edition shows as running here, but only that copy controls it.
- **Create → Choose a Linux OS** shows the recommended distributions and searches the full catalog from
  [`../catalog/distros.json`](../catalog/distros.json).
- Icons are the shared Lucide set in [`../icons`](../icons); fonts and colours match the Windows edition.

Not yet: hiding a system's window with its close button, the tray icon, Settings, the rest of Create, the main
menu, and the macOS and Linux back ends.

| File | What it does |
| --- | --- |
| `src-tauri/src/main.rs` | The commands the interface calls |
| `src-tauri/src/config.rs` | A system's settings (`vm.ini`) and where systems are kept |
| `src-tauri/src/vm.rs` | Starting QEMU, the power actions, and what happens when it stops |
| `src-tauri/src/live.rs` | Memory and disk activity from QEMU (balloon statistics, block statistics) |
| `src-tauri/src/window.rs` | Bringing a system's window to the front |
| `ui/app.js`, `ui/style.css` | The pages |

## Build and run (Windows)

Needs [Rust](https://rustup.rs) and Visual Studio Build Tools with the C++ workload.

```bash
cd prototype/src-tauri
cargo run --release
```

The fonts in `ui/fonts` are under the SIL Open Font License (see the `OFL-*.txt` files).
