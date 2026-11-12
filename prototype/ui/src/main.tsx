// Try Linux's interface (prototype): the same pages on Windows, macOS and Linux. The Rust side (src-tauri)
// starts and stops systems and reports on them; see tauri.ts.
import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { Toaster } from "sonner";
import { ask, AskHost, problem } from "./Ask";
import { Choose } from "./Choose";
import { Create, type Source } from "./Create";
import { OwnInstaller } from "./OwnInstaller";
import { RECOMMENDED } from "./distros";
import { Home } from "./Home";
import { applyTheme, checkQuietly, MainMenuWindows, openAbout, openPreferences, openShortcuts, useShortcuts } from "./MainMenu";
import { act } from "./power";
import { SettingsPage } from "./Settings";
import { appInfo, onAskPower, onAskQuit, onProblem, quit, settings, startPage, systems, type Settings, type Vm } from "./tauri";
import "./style.css";

type Page = { kind: "loading" } | { kind: "home"; vms: Vm[] } | { kind: "choose"; back: boolean }
  | { kind: "settings"; s: Settings; tab?: string; openAll?: boolean; tool?: "resize" | "add" | "snapshots" } | { kind: "create"; source: Source; back: boolean; step?: "general" | "resources" | "summary" }
  | { kind: "own"; back: boolean; last?: Source };

function App() {
  const [page, setPage] = useState<Page>({ kind: "loading" });

  // Home, or Choose a Linux OS when there are no systems yet.
  const home = async () => {
    const vms = await systems();
    setPage(vms.length ? { kind: "home", vms } : { kind: "choose", back: false });
  };

  const openSettings = (id: string, tab?: string, openAll?: boolean, tool?: "resize" | "add" | "snapshots") =>
    settings(id).then(s => setPage({ kind: "settings", s, tab, openAll, tool })).catch(e => problem({ title: "Couldn't open Settings", text: String(e) }));

  // Ctrl+N and the other shortcuts; Create can't be opened while a system is being set up.
  useShortcuts(page.kind === "create" ? null : () => setPage({ kind: "choose", back: true }));

  useEffect(() => {
    // The theme chosen in Preferences, then a quiet look for a newer WINQ-EMU.
    appInfo().then(applyTheme).catch(() => {});
    setTimeout(checkQuietly, 3000);
    // For screenshots: TRYLINUX_PAGE=choose opens Choose a Linux OS first; settings[:TAB[:open]] opens the
    // first system's Settings on that tab, with every section open.
    startPage().then(async p => {
      if (p === "choose") return setPage({ kind: "choose", back: true });
      // menu:preferences, menu:shortcuts or menu:about opens that window over Virtual Machines.
      if (p.startsWith("menu:")) {
        await home();
        const w = p.slice(5);
        return setTimeout(w === "preferences" ? openPreferences : w === "about" ? openAbout : openShortcuts, 500);
      }
      // create:STEP opens Create for Fedora on General, Resources or Summary (never further: that installs).
      if (p.startsWith("create:")) return setPage({ kind: "create", source: { kind: "distro", distro: RECOMMENDED[0] }, back: true, step: p.slice(7) as "general" });
      if (p === "own") return setPage({ kind: "own", back: true });
      if (p.startsWith("settings")) {
        const [, tab, all, tool] = p.split(":");
        const first = (await systems())[0];
        if (first) return openSettings(first.id, tab || undefined, all === "open", (tool || undefined) as "resize" | undefined);
      }
      home();
    }).catch(problem);
    // From the Rust side: something went wrong (e.g. QEMU couldn't start), or the tray asks to confirm.
    const off = [
      onProblem(problem),
      onAskPower(async (id, action) => {
        const vm = (await systems()).find(v => v.id === id);
        if (vm) act(vm, action);
      }),
      onAskQuit(async () => {
        if (await ask("Quit Try Linux?", "Running systems keep running, but without the tray menu.", "Quit")) quit();
      }),
    ];
    return () => off.forEach(p => p.then(f => f()));
  }, []);

  return (
    <>
      {page.kind === "home" && <Home first={page.vms} onCreate={() => setPage({ kind: "choose", back: true })} onSettings={id => openSettings(id)} />}
      {page.kind === "settings" && <SettingsPage first={page.s} tab={page.tab} openAll={page.openAll} startTool={page.tool} onBack={home} />}
      {page.kind === "choose" && <Choose onBack={page.back ? home : null}
        onNext={distro => setPage({ kind: "create", source: { kind: "distro", distro }, back: page.back })}
        onOwn={() => setPage({ kind: "own", back: page.back })} />}
      {page.kind === "own" && <OwnInstaller last={page.last} onBack={() => setPage({ kind: "choose", back: page.back })}
        onNext={source => setPage({ kind: "create", source, back: page.back })} />}
      {page.kind === "create" && <Create source={page.source} startStep={page.step} onDone={home}
        onBack={() => setPage(page.source.kind === "distro" ? { kind: "choose", back: page.back } : { kind: "own", back: page.back, last: page.source })} />}
      <MainMenuWindows />
      <AskHost />
      <Toaster position="bottom-center" offset={24} duration={2600} />
    </>
  );
}

createRoot(document.getElementById("root")!).render(<StrictMode><App /></StrictMode>);
