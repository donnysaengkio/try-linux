// The main menu (☰) and what it opens: Check for Updates, Preferences, Keyboard Shortcuts and About, as on the
// Windows edition, with their keyboard shortcuts; and the theme Preferences chooses.
import { Dialog } from "@base-ui/react/dialog";
import { Menu } from "@base-ui/react/menu";
import { useEffect, useState, type ReactNode } from "react";
import { toast } from "sonner";
import { ask, problem } from "./Ask";
import { Check, Choice, Note } from "./Form";
import { Icon } from "./Icon";
import { Logo } from "./Logo";
import { listen } from "@tauri-apps/api/event";
import { relaunch } from "@tauri-apps/plugin-process";
import { check, type Update } from "@tauri-apps/plugin-updater";
import { appInfo, checkUpdates, openPage, openStorage, quit, setPreference, systems, updateWinq, type AppInfo, type Progress } from "./tauri";
import "./settings.css";

// ---- the theme: Dark or Light, or as Windows shows apps (Follow system)
export function applyTheme(info: Pick<AppInfo, "theme" | "follow_system">) {
  document.documentElement.dataset.theme = info.follow_system ? "system" : info.theme;
  dispatchEvent(new Event("themechange"));
}

// ---- actions, also on keyboard shortcuts
type Shown = "preferences" | "shortcuts" | "about" | null;
let show: (d: Shown) => void = () => {};
export const openPreferences = () => show("preferences");
export const openShortcuts = () => show("shortcuts");
export const openAbout = () => show("about");

// ---- Try Linux's own updates (the updater plugin): a signed installer from the tauri-updates release, checked
// against the public key built into the app.

// A newer Try Linux, or null when there isn't one. Throws when GitHub can't be reached.
async function findAppUpdate(): Promise<Update | null> {
  return await check({ timeout: 20000 });
}

// Asks, downloads with its progress in a notification, installs (the installer closes Try Linux) and starts
// the new version.
export async function installAppUpdate(update: Update) {
  const running = (await systems()).some(v => v.state === "running" || v.state === "paused" || v.state === "held");
  const notes = update.body?.trim() ? `\n\n${update.body.trim()}` : "";
  if (!await ask(`Try Linux ${update.version} is available`,
    `You have ${update.currentVersion}. Try Linux downloads it, closes, installs it and opens again.` +
    (running ? "\n\nRunning systems keep running, but without the tray menu until Try Linux is back." : "") + notes, "Update")) return;
  const id = toast.loading(`Downloading Try Linux ${update.version}…`);
  let got = 0, total = 0;
  try {
    await update.downloadAndInstall(e => {
      if (e.event === "Started") total = e.data.contentLength ?? 0;
      else if (e.event === "Progress") {
        got += e.data.chunkLength;
        if (total) toast.loading(`Downloading Try Linux ${update.version}…`, { id, description: `${Math.round(got / total * 100)}%` });
      } else if (e.event === "Finished") toast.loading("Installing…", { id, description: undefined });
    });
    await relaunch();
  } catch (e) {
    toast.dismiss(id);
    problem({ title: "Couldn't update Try Linux", text: String(e) });
  }
}

// Check for Updates (Ctrl+U): a newer Try Linux first, then (on Windows) WINQ-EMU.
export async function checkForUpdatesNow() {
  const info = await appInfo();
  const id = toast.loading("Checking for updates…");
  try {
    const update = await findAppUpdate();
    if (update) {
      toast.dismiss(id);
      return installAppUpdate(update);
    }
    if (info.platform !== "windows") {
      toast.dismiss(id);
      return ask("Try Linux is up to date", `You have Try Linux ${info.version}, the latest, with ${info.engine}.`, null);
    }
    const u = await checkUpdates();
    toast.dismiss(id);
    if (u.newer) {
      if (await ask(`WINQ-EMU ${u.latest} is available`,
        `You have WINQ-EMU ${u.winq}. Try Linux downloads the new installer from GitHub and runs it; Windows asks for permission. Shut every system down first.`,
        "Update")) updateWinqNow();
      return;
    }
    await ask("Try Linux is up to date",
      `You have Try Linux ${info.version}` + (u.winq ? ` and WINQ-EMU ${u.winq}, the latest of both.` : ", the latest. WINQ-EMU isn't installed."), null);
  } catch (e) {
    toast.dismiss(id);
    problem({ title: "Couldn't check for updates", text: String(e) });
  }
}

// Updates WINQ-EMU, with its progress in a notification.
async function updateWinqNow() {
  const id = toast.loading("Updating WINQ-EMU…");
  const off = listen<Progress>("tool-progress", e => toast.loading(e.payload.status, { id, description: e.payload.detail }));
  try { toast.success(await updateWinq(), { id, description: undefined }); }
  catch (e) { toast.dismiss(id); problem({ title: "Couldn't update WINQ-EMU", text: String(e) }); }
  (await off)();
}

// Quit Try Linux (Ctrl+Q): asks first while systems run, as they lose the tray menu.
export async function quitApp() {
  const running = (await systems()).some(v => v.state === "running" || v.state === "paused" || v.state === "held");
  if (!running || await ask("Quit Try Linux?", "Running systems keep running, but without the tray menu.", "Quit")) quit();
}

// Once a day is the Windows edition's rule; the prototype checks each time it opens, quietly: a newer Try
// Linux, then (on Windows) WINQ-EMU.
export async function checkQuietly() {
  try {
    const info = await appInfo();
    if (!info.check_updates) return;
    const update = await findAppUpdate().catch(() => null);
    if (update) {
      toast(`Try Linux ${update.version} is available`, {
        description: `You have ${update.currentVersion}.`, duration: 15000, action: { label: "Update", onClick: () => installAppUpdate(update) },
      });
      return;
    }
    if (info.platform !== "windows" || !info.winq) return;
    const u = await checkUpdates();
    if (u.newer) toast(`WINQ-EMU ${u.latest} is available`, {
      description: `You have ${u.winq}.`, duration: 10000, action: { label: "Update", onClick: () => updateWinqNow() },
    });
  } catch { /* offline: no news */ }
}

// Every keyboard shortcut, as (keys, what it does); Keyboard Shortcuts lists them.
const SHORTCUTS: [string, string][] = [
  ["General", ""],
  ["Ctrl+N", "Create a virtual machine"],
  ["Ctrl+K", "Command menu: start, stop or open a system"],
  ["Ctrl+F", "Search distributions (on Choose a Linux OS)"],
  ["Ctrl+U", "Check for updates"],
  ["Ctrl+,", "Preferences"],
  ["Ctrl+?", "Keyboard shortcuts"],
  ["Ctrl+Q", "Quit Try Linux"],
  ["Navigation", ""],
  ["Alt+←", "Back"],
  ["Esc", "Close a dialog or menu"],
];

// The app-wide shortcuts. `onCreate` is null while Create can't be opened (setting up a system).
export function useShortcuts(onCreate: (() => void) | null) {
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      const k = e.key.toLowerCase();
      if (e.altKey && e.key === "ArrowLeft") {
        // The page's Back button.
        const back = document.querySelector<HTMLButtonElement>("[data-back]:not(:disabled)");
        if (back) { e.preventDefault(); back.click(); }
        return;
      }
      if (!e.ctrlKey || e.altKey) return;
      const act = k === "n" ? onCreate : k === "u" ? checkForUpdatesNow : k === "," ? openPreferences
        : k === "?" || k === "/" ? openShortcuts : k === "q" ? quitApp : null;
      if (act) { e.preventDefault(); act(); }
    };
    addEventListener("keydown", key);
    return () => removeEventListener("keydown", key);
  }, [onCreate]);
}

// ---- the menu itself, on Virtual Machines' header bar
export function MainMenu() {
  const items: ([string, string, () => void] | null)[] = [
    ["Check for Updates", "Ctrl+U", checkForUpdatesNow],
    null,
    ["Preferences", "Ctrl+,", openPreferences],
    ["Keyboard Shortcuts", "Ctrl+?", openShortcuts],
    ["About Try Linux", "", openAbout],
    null,
    ["Quit Try Linux", "Ctrl+Q", quitApp],
  ];
  return (
    <Menu.Root>
      <Menu.Trigger className="btn icon" title="Main menu" aria-label="Main menu"><Icon name="menu" /></Menu.Trigger>
      <Menu.Portal>
        <Menu.Positioner className="menu-positioner" sideOffset={4} align="end">
          <Menu.Popup className="menu main-menu">
            {items.map((it, i) => it === null ? <Menu.Separator key={i} className="menu-line" />
              : <Menu.Item key={it[0]} className="menu-item with-keys" onClick={it[2]}>
                  <span>{it[0]}</span>{it[1] && <kbd className="menu-keys">{it[1]}</kbd>}
                </Menu.Item>)}
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}

// ---- the windows it opens
function Window({ open, title, wide, children }: { open: boolean; title: string; wide?: boolean; children: ReactNode }) {
  return (
    <Dialog.Root open={open} onOpenChange={o => { if (!o) show(null); }}>
      <Dialog.Portal>
        <Dialog.Backdrop className="modal-back" />
        <Dialog.Viewport className="modal-viewport">
          <Dialog.Popup className={"modal app-window" + (wide ? " wide" : "")}>
            <div className="app-window-head">
              <Dialog.Title className="modal-title">{title}</Dialog.Title>
              <Dialog.Close className="btn icon small" aria-label="Close"><Icon name="x" /></Dialog.Close>
            </div>
            {children}
          </Dialog.Popup>
        </Dialog.Viewport>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function Keys({ keys }: { keys: string }) {
  return <span className="keys">{keys.split("+").map((k, i) => <span key={i}>{i > 0 && <span className="plus">+</span>}<kbd className="keycap">{k}</kbd></span>)}</span>;
}

// Preferences (Ctrl+,): Try Linux as a whole. Changes apply straight away.
function Preferences({ info, onChange }: { info: AppInfo; onChange: (i: AppInfo) => void }) {
  const change = async (key: "theme" | "followsystem" | "checkupdates", value: string, next: AppInfo) => {
    try { await setPreference(key, value); onChange(next); applyTheme(next); } catch (e) { problem({ title: "Couldn't save", text: String(e) }); }
  };
  return <>
    <div className="pref-heading">Appearance</div>
    <div className="row">
      <span className="row-label short">Theme</span>
      <Choice width={160} value={info.theme} disabled={info.follow_system}
        options={[{ value: "dark", label: "Dark" }, { value: "light", label: "Light" }]}
        onChange={v => change("theme", v, { ...info, theme: v })} />
    </div>
    <Check label="Follow system" checked={info.follow_system} onChange={v => change("followsystem", v ? "1" : "0", { ...info, follow_system: v })} />
    <Note>Light while {info.platform === "macos" ? "macOS" : info.platform === "linux" ? "your desktop" : "Windows"} shows apps in light mode, and Dark otherwise. The {info.platform === "macos" ? "Mac" : info.platform === "linux" ? "Linux" : "Windows"} edition uses the same choice.</Note>
    <div className="pref-heading">Updates</div>
    <Check label={`Check for updates${info.platform === "windows" ? " (Try Linux and WINQ-EMU)" : ""} when Try Linux opens`} checked={info.check_updates}
      onChange={v => change("checkupdates", v ? "1" : "0", { ...info, check_updates: v })} />
    <Note>You have Try Linux {info.version} (preview), with {info.engine}.</Note>
    <div className="buttons"><button className="btn small" onClick={() => { show(null); checkForUpdatesNow(); }}>Check For Updates</button></div>
  </>;
}

function About({ info }: { info: AppInfo }) {
  const links: [string, () => void][] = [
    ["Website", () => openPage("https://github.com/donnysaengkio/try-linux/tree/main/prototype")],
    ["Report an Issue", () => openPage("https://github.com/donnysaengkio/try-linux/issues")],
    ...(info.platform === "windows" ? [["WINQ-EMU Releases", () => openPage("https://github.com/cmspam/winq-emu/releases")] as [string, () => void]] : []),
    ["Open Storage Folder", () => openStorage().catch(e => problem({ title: "Couldn't open the folder", text: String(e) }))],
  ];
  return <div className="about">
    <Logo px={5} />
    <div className="about-version">Prototype {info.version}</div>
    <p className="about-text">Try a Linux desktop on {info.platform === "macos" ? "your Mac" : info.platform === "linux" ? "another Linux" : "Windows"} in a virtual machine, from the official installer, checked against its published checksum.</p>
    <p className="about-small">{info.engine}<br /><span title={info.storage}>Storage: {info.storage}</span></p>
    <div className="about-links">{links.map(([label, go]) => <button key={label} className="btn" onClick={go}>{label}</button>)}</div>
  </div>;
}

// Sits once in the app and draws whichever window is open.
export function MainMenuWindows() {
  const [shown, setShown] = useState<Shown>(null);
  const [info, setInfo] = useState<AppInfo | null>(null);
  useEffect(() => {
    show = d => { setShown(d); if (d) appInfo().then(setInfo).catch(problem); };
    return () => { show = () => {}; };
  }, []);
  return <>
    <Window open={shown === "preferences" && !!info} title="Preferences">{info && <Preferences info={info} onChange={setInfo} />}</Window>
    <Window open={shown === "shortcuts"} title="Keyboard Shortcuts">
      <div className="shortcuts">
        {SHORTCUTS.map(([keys, what]) => what === ""
          ? <div key={keys} className="pref-heading">{keys}</div>
          : <div key={keys} className="shortcut"><Keys keys={keys} /><span>{what}</span></div>)}
      </div>
    </Window>
    <Window open={shown === "about" && !!info} title="About Try Linux">{info && <About info={info} />}</Window>
  </>;
}
