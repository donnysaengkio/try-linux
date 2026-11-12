// Settings: one system's settings in four tabs (General, Hardware, Devices, Recovery), as on the Windows
// edition. Save writes them to its vm.ini; a disc goes in or out of the drive at once, like a real drive.
import { Tabs } from "@base-ui/react/tabs";
import { availableMonitors, type Monitor } from "@tauri-apps/api/window";
import { open as pick, save as pickSave } from "@tauri-apps/plugin-dialog";
import { Fragment, useEffect, useMemo, useRef, useState } from "react";
import { toast } from "sonner";
import { ask, problem } from "./Ask";
import { AddDiskDialog, ResizeDialog, SnapshotsDialog, WorkingDialog } from "./DiskTools";
import { findDistro, type Distro } from "./distros";
import { Action, Check, Choice, Note, NumberBox, Radios, Row, Section, SectionsContext, Segmented, SubHeading } from "./Form";
import { Header } from "./Header";
import { Icon, type IconName } from "./Icon";
import { addShortcut, appInfo, backupSystem, bootIso, cleanList, cleanRun, cloneSystem, deleteSystem, installerOf, moveStorage, resetSystem, restoreBackup, diskAdd, diskCompact, diskInfo, diskRemove, diskResize, openPage, saveSettings, setDisc, settings, type Changes, type DiskReport, type Settings } from "./tauri";
import "./settings.css";

const TABS: [string, string, IconName][] = [
  ["general", "General", "wrench"], ["hardware", "Hardware", "monitor"], ["devices", "Devices", "usb"], ["recovery", "Recovery", "undo-2"],
];
const PROFILES = ["Balanced", "Maximum", "Manual"];
const NET_TYPES = ["Shared with this PC (NAT)", "Port forwards only", "No network"];
const NET_NOTES = [
  "Linux reaches the internet through this PC's connection. Changes apply the next time it starts.",
  "Linux can't reach the internet, your network or this PC, but the port forwards below still reach it. Changes apply the next time it starts.",
  "No network card: Linux is fully offline and port forwards are off. Changes apply the next time it starts.",
];
const GiB = 2 ** 30;
const size = (b: number) => b >= 1024 * GiB ? (b / 1024 / GiB).toFixed(2) + " TB" : b >= GiB ? (b / GiB).toFixed(1) + " GB" : Math.round(b / 2 ** 20) + " MB";
const fileName = (p: string) => p.slice(Math.max(p.lastIndexOf("\\"), p.lastIndexOf("/")) + 1);


export function SettingsPage({ first, onBack, tab: startTab, openAll, startTool }:
  { first: Settings; onBack: (saved: boolean) => void; tab?: string; openAll?: boolean; startTool?: "resize" | "add" | "snapshots" }) {
  const [s, setS] = useState(first);
  const who = s.name;
  // What the page says differs a little on Linux and macOS.
  const win = s.platform === "windows", mac = s.platform === "macos";
  const pc = mac ? "this Mac" : win ? "Windows" : "this PC";
  const [f, setF] = useState<Changes>(() => ({
    name: s.name, description: s.description, fullscreen: s.fullscreen, fs_display: s.fs_display, displays: s.displays,
    render: s.render, venus: s.venus, gpu_mem: Math.min(s.gpu_mem, s.max_gpu_mem), profile: s.profile, ram: s.ram, cores: s.cores,
    reclaim: s.reclaim, sound: s.sound, mic: s.mic, net_type: s.net_type, forwards: s.forwards, shared: s.shared, shared_write: s.shared_write,
    shortcut_direct: s.shortcut_direct, sign_in: s.sign_in,
  }));
  const set = <K extends keyof Changes>(k: K, v: Changes[K]) => setF(o => ({ ...o, [k]: v }));
  const [tab, setTab] = useState(startTab ?? "general");
  // Which sections are open, and the one opened last (the only one highlighted): for this visit only, so every
  // section is closed again the next time Settings opens.
  const [open, setOpen] = useState<Set<string>>(() => openAll ? new Set(["*"]) : new Set());
  const [last, setLast] = useState<string | null>(null);
  // The app's own version, for the subtitle.
  const [version, setVersion] = useState("");
  useEffect(() => { appInfo().then(i => setVersion(i.version)).catch(() => {}); }, []);
  const sections = useMemo(() => ({
    open: open.has("*") ? { has: () => true } as unknown as Set<string> : open,
    last,
    toggle: (title: string, isOpen: boolean) => {
      setOpen(o => {
        const next = new Set(o);
        if (isOpen) next.add(title); else next.delete(title);
        return next;
      });
      setLast(l => isOpen ? title : l === title ? null : l);
    },
  }), [open, last]);

  // ---- General: the distribution's home page, and the displays to open fullscreen on
  const [distro, setDistro] = useState<Distro>();
  const [monitors, setMonitors] = useState<Monitor[]>([]);
  useEffect(() => {
    findDistro(s.distro).then(setDistro);
    availableMonitors().then(setMonitors).catch(() => {});
  }, []);

  // ---- Hardware: what each disk holds and the room left on its drive, read when it's chosen
  const [disk, setDisk] = useState(0);
  const [report, setReport] = useState<DiskReport | null>(null);
  const [reread, setReread] = useState(0);
  useEffect(() => {
    let alive = true;
    setReport(null);
    diskInfo(s.disks[disk]).then(r => alive && setReport(r));
    return () => { alive = false; };
  }, [disk, reread]);

  // ---- Disk Utilities: each needs the system shut down; the disks are read again afterwards.
  const [tool, setTool] = useState<"resize" | "add" | "compact" | null>(startTool === "snapshots" ? null : startTool ?? null);
  const diskName = "Disk " + (disk + 1);
  const afterTool = async (message: string, select?: number) => {
    toast(message, { duration: 7000 });
    const fresh = await settings(s.id);
    setS(fresh);
    setDisk(Math.max(0, Math.min(select ?? disk, fresh.disks.length - 1)));
    setReread(n => n + 1);
  };
  const failed = (what: string) => (e: unknown) => problem({ title: "Couldn't " + what, text: String(e) });
  const resized = async (gb: number | null) => {
    setTool(null);
    if (gb === null) return;
    const current = Math.ceil((report?.size ?? 0) / 2 ** 30);
    if (gb < current && !await ask(`Shrink ${diskName} to ${gb} GB?`,
      `Anything Linux stored past ${gb} GB is lost. Only continue if you've shrunk its partition to fit.`, "Shrink", true)) return;
    diskResize(s.id, disk, gb).then(m => afterTool(m)).catch(failed("resize the disk"));
  };
  const compact = async () => {
    const preallocated = disk === 0 && s.disk_full;
    if (!await ask(`Compact ${diskName}?`,
      "Try Linux rewrites the disk file without its unused space. Linux and your files are unchanged." +
      (preallocated ? " The disk stops being preallocated: afterwards it takes space only as Linux stores data." : "") +
      `\n\nIt needs ${report?.actual != null ? size(report.actual) : "the disk's size"} free while it works` +
      (f.reclaim ? "." : ". Turn on Reclaim free space and start Linux once first to free the most."), "Compact")) return;
    setTool("compact");
    try { await afterTool(await diskCompact(s.id, disk)); } catch (e) { failed("compact the disk")(e); }
    setTool(null);
  };
  const added = (choice: { gb?: number; file?: string } | null) => {
    setTool(null);
    if (!choice) return;
    diskAdd(s.id, choice.gb ?? null, choice.file ?? null).then(m => afterTool(m, s.disks.length)).catch(failed("add the disk"));
  };
  const remove = async () => {
    const entry = s.disks[disk];
    const ours = entry.toLowerCase().startsWith(s.dir.toLowerCase() + "\\");
    if (!await ask(`Remove ${diskName} from ${who}?`, ours
      ? "Its file goes to the Recycle Bin, with everything Linux stored on it.\n\nTo get it back, restore the file from the Recycle Bin and add it with Add disk."
      : `The disk file is kept where it is:\n${entry}\n\nYou can add it again with Add disk.`, "Remove", ours)) return;
    diskRemove(s.id, disk).then(m => afterTool(m, disk - 1)).catch(failed("remove the disk"));
  };

  // Balanced and Maximum show what they'll give, greyed out; Manual starts from there.
  const manual = f.profile === 2;
  const shown = manual ? [f.cores, f.ram] : f.profile === 1 ? s.maximum : s.balanced;
  const pickProfile = (p: number) => setF(o => p === 2 && o.profile !== 2
    ? { ...o, profile: p, cores: shown[0], ram: shown[1] } : { ...o, profile: p });
  const coreTop = s.max_cores >= 2 ? Math.floor(s.max_cores / 2) * 2 : 1;

  // ---- Devices: the disc in the drive changes at once
  const [cd, setCd] = useState(s.cd);
  const changeDisc = async (iso: string) => {
    try {
      const note = await setDisc(s.id, iso);
      setCd(iso);
      if (note) toast(note);
    } catch (e) { problem({ title: "Couldn't change the disc", text: String(e) }); }
  };
  const insert = async () => {
    const iso = await pick({ title: "Insert a disc into " + who, filters: [{ name: "Disc images", extensions: ["iso"] }, { name: "All files", extensions: ["*"] }] });
    if (typeof iso === "string") changeDisc(iso);
  };
  const browse = async () => {
    const dir = await pick({ title: "Choose a folder to share with " + who, directory: true });
    if (typeof dir === "string") set("shared", dir);
  };

  // ---- Recovery: the slow ones run behind a progress window; afterwards this page shows the system as it is
  // now (or Virtual Machines, once it's deleted or started).
  const [working, setWorking] = useState<string | null>(null);
  const [snapsOpen, setSnapsOpen] = useState(startTool === "snapshots");
  const isoFilter = [{ name: "Disc images", extensions: ["iso"] }, { name: "All files", extensions: ["*"] }];
  const backupFilter = [{ name: "Try Linux backup", extensions: ["qcow2"] }];
  const slow = async (title: string, work: () => Promise<string>, tell: "dialog" | "toast" = "dialog") => {
    setWorking(title);
    try {
      const message = await work();
      setWorking(null);
      if (tell === "dialog") ask(title.replace(/…$/, ""), message, null); else toast(message, { duration: 7000 });
      const fresh = await settings(s.id).catch(() => null);
      if (fresh) { setS(fresh); setReread(n => n + 1); }
    } catch (e) {
      setWorking(null);
      problem({ title: "Couldn't finish", text: String(e) });
    }
  };
  const today = new Date().toISOString().slice(0, 10).replace(/-/g, "");
  const rec = {
    backup: async () => {
      const dest = await pickSave({ title: "Back up " + who, defaultPath: `${s.id}-${today}.qcow2`, filters: backupFilter });
      if (dest) slow(`Backing up ${who}`, () => backupSystem(s.id, dest));
    },
    restore: async () => {
      const src = await pick({ title: "Restore a backup as a new system", filters: backupFilter });
      if (typeof src === "string") slow(`Restoring a copy of ${who}`, () => restoreBackup(s.id, src));
    },
    clone: () => slow(`Cloning ${who}`, () => cloneSystem(s.id)),
    move: async () => {
      const parent = await pick({ title: "Move all Try Linux systems and downloads to:", directory: true });
      if (typeof parent !== "string") return;
      try {
        const plan = await moveStorage(parent, false);
        if (await ask("Move storage?", plan + "\n\nEvery system stays shut down while it moves.", "Move"))
          slow("Moving storage", () => moveStorage(parent, true));
      } catch (e) { problem({ title: "Can't move storage", text: String(e) }); }
    },
    clean: async () => {
      const c = await cleanList();
      if (c.files.length === 0) return ask("Nothing to clean up", "Installers that a system still needs are kept.", null);
      const list = c.files.slice(0, 8).join("\n") + (c.files.length > 8 ? `\n… and ${c.files.length - 8} more` : "");
      if (await ask(`Delete ${c.files.length} downloaded ${c.files.length === 1 ? "file" : "files"}?`,
        `${size(c.total)} in all:\n${list}\n\nYou can download them again later.`, "Delete", true))
        toast(await cleanRun(), { duration: 7000 });
    },
    reset: async () => {
      let iso = await installerOf(s.id);
      if (!iso) {
        const chosen = await pick({ title: "Choose the installer to start over with", filters: isoFilter });
        if (typeof chosen !== "string") return;
        iso = chosen;
      }
      if (await ask(`Reset ${who}?`, "This erases everything installed in it, including its snapshots, and starts over with the installer.\n\nBack it up first if you might need anything.", "Reset", true))
        slow(`Resetting ${who}`, () => resetSystem(s.id, iso!), "toast");
    },
    boot: async () => {
      const iso = await pick({ title: `Boot ${who} from an ISO`, filters: isoFilter });
      if (typeof iso !== "string") return;
      try { await bootIso(s.id, iso); onBack(false); } catch (e) { problem({ title: "Couldn't start it", text: String(e) }); }
    },
    remove: async () => {
      const attached = s.disks.slice(1).some(d => !d.toLowerCase().startsWith(s.dir.toLowerCase() + "\\"));
      if (!await ask(`Delete ${who}?`, `This permanently removes its virtual ${s.disks.length > 1 ? "disks" : "disk"} and everything installed in it.\n\n` +
        (attached ? "Disks added from other folders and downloaded ISOs are kept." : "Downloaded ISOs are kept."), "Delete", true)) return;
      try { toast(await deleteSystem(s.id)); onBack(false); } catch (e) { problem({ title: "Couldn't delete it", text: String(e) }); }
    },
  };

  // ---- The desktop shortcut: made (or updated) at once, as on Windows.
  const [hasShortcut, setHasShortcut] = useState(s.has_shortcut);
  const [making, setMaking] = useState(false);
  const makeShortcut = async () => {
    setMaking(true);
    try {
      const name = await addShortcut(s.id, f.shortcut_direct);
      setHasShortcut(true);
      toast(`"${name}" is on your desktop.`);
    } catch (e) { problem({ title: "Couldn't create the shortcut", text: String(e) }); }
    setMaking(false);
  };

  // ---- Renaming: the pencil edits the name in place; Enter or clicking away keeps it, Esc goes back.
  const [renaming, setRenaming] = useState(false);
  const nameBox = useRef<HTMLInputElement>(null);
  useEffect(() => { if (renaming) nameBox.current?.select(); }, [renaming]);
  const endRename = (keep: boolean) => {
    if (!keep || !nameBox.current?.value.trim()) { setRenaming(false); return; }
    set("name", nameBox.current.value.trim());
    setRenaming(false);
  };

  const [saving, setSaving] = useState(false);
  const save = async () => {
    setSaving(true);
    try {
      await saveSettings(s.id, f);
      toast("Saved " + f.name + "'s settings.");
      onBack(true);
    } catch (e) {
      const [where, text] = Array.isArray(e) ? e as [string, string] : ["", String(e)];
      if (where) setTab(where);
      problem({ title: "Couldn't save", text });
      setSaving(false);
    }
  };

  const forwardCount = f.forwards.filter(l => l.trim()).length;
  const rendering = ["Automatic", "GPU", "CPU"][f.render];

  return (
    <Tabs.Root value={tab} onValueChange={v => setTab(String(v))} className="settings-root">
      <Header
        left={<button className="btn icon" data-back title="Back (Alt+Left)" aria-label="Back" onClick={() => onBack(false)}><Icon name="arrow-left" /></button>}
        middle={
          <Tabs.List className="tabs">
            {TABS.map(([value, label, icon]) => (
              <Tabs.Tab key={value} value={value} className="tab"><Icon name={icon} size={16} />{label}</Tabs.Tab>
            ))}
            <Tabs.Indicator className="tab-indicator" />
          </Tabs.List>
        }
        right={<button className="btn primary save" disabled={saving} onClick={save}>Save</button>} />

      <main className="page">
        <div className="column settings">
          <div className="settings-title">
            {renaming
              ? <input ref={nameBox} className="rename" defaultValue={f.name} spellCheck={false}
                  onKeyDown={e => { if (e.key === "Enter") endRename(true); if (e.key === "Escape") endRename(false); }}
                  onBlur={() => endRename(true)} />
              : <>
                  <h1>{f.name}</h1>
                  <button className="pencil" title="Rename" aria-label="Rename" onClick={() => setRenaming(true)}><Icon name="pencil" size={16} /></button>
                </>}
          </div>
          <div className="settings-sub">{s.os.toUpperCase()}  ·  TRY LINUX v{version}{s.running ? "  ·  RUNNING" : ""}</div>

          <SectionsContext.Provider value={sections}>
            {/* ---------------- General */}
            <Tabs.Panel value="general" className="tab-panel">
              <Section title="Info" summary={f.description.trim().split("\n")[0].trim() || "No description"}>
                <Row label="Description" top>
                  <textarea className="text-box" rows={3} value={f.description} onChange={e => set("description", e.target.value)} />
                </Row>
                <Row label="Operating system"><span className="value-text">{s.os}</span></Row>
                {distro?.home && <Row label="Home page"><span className="link" onClick={() => openPage(distro.home!)}>{distro.home}</span></Row>}
                <Row label="Location"><span className="value-text" title={s.dir}>{s.dir}</span></Row>
              </Section>

              <Section title="Display and startup" summary={(f.fullscreen ? "Fullscreen" : "In a window") + (f.sign_in ? "  ·  Starts when you sign in" : "")}>
                <Check label="Open fullscreen" checked={f.fullscreen} onChange={v => set("fullscreen", v)} />
                <Row label="Fullscreen display" inset>
                  <Choice width={300} value={f.fs_display || "primary"} onChange={v => set("fs_display", v === "primary" ? "" : v)}
                    options={[{ value: "primary", label: "Primary display" }, ...monitors.filter(m => m.name).map(m => ({
                      value: m.name!, label: `${m.name!.replace(/^\\\\\.\\/, "")} (${m.size.width} x ${m.size.height})`,
                    }))]} />
                </Row>
                {s.shortcuts && <Check label={`${win ? "Desktop shortcut" : "Shortcut in your apps"} starts ${who} directly`} checked={f.shortcut_direct} onChange={v => set("shortcut_direct", v)} />}
                <Check label={`Start ${who} when I ${mac ? "log in" : win ? "sign in to Windows" : "sign in"}`} checked={f.sign_in} onChange={v => set("sign_in", v)} />
                {s.shortcuts && <div className="buttons">
                  <button className="btn small" disabled={making} onClick={makeShortcut}>
                    {win ? (hasShortcut ? "Update desktop shortcut" : "Add desktop shortcut") : (hasShortcut ? "Update the shortcut in your apps" : "Add to your apps")}
                  </button>
                </div>}
                <Note>{s.shortcuts ? (f.shortcut_direct ? `The shortcut starts ${who} and keeps Try Linux in the tray.` : "The shortcut opens Try Linux.") + " " : ""}Starting at sign-in applies when you Save.</Note>
              </Section>
            </Tabs.Panel>

            {/* ---------------- Hardware */}
            <Tabs.Panel value="hardware" className="tab-panel">
              <Section title="Graphics" summary={`${f.displays} ${f.displays === 1 ? "display" : "displays"}  ·  ${rendering} rendering${f.venus ? `  ·  Vulkan, ${f.gpu_mem} GiB` : ""}`}>
                <Row label="Guest displays">
                  <NumberBox value={f.displays} min={1} max={4} onChange={v => set("displays", v)} />
                  <span className="hint-text">1 to 4; each opens its own window</span>
                </Row>
                <Row label="Rendering">
                  <Radios value={String(f.render)} options={[["0", "Automatic"], ["1", "GPU"], ["2", "CPU"]]} onChange={v => set("render", Number(v))} />
                </Row>
                <Note>Automatic tries the GPU and switches to CPU rendering if the GPU path fails to start. GPU retries every launch.</Note>
                <Check label="Vulkan acceleration (Venus)" checked={f.venus} onChange={v => set("venus", v)} />
                <Row label="GPU memory (GiB)">
                  <NumberBox value={f.gpu_mem} min={1} max={s.max_gpu_mem} onChange={v => set("gpu_mem", v)} />
                  <span className="hint-text">up to {s.max_gpu_mem} GiB{s.gpu_name ? ` (${s.gpu_name})` : `, shared with ${pc}`}</span>
                </Row>
                <Note>GPU shares {pc}'s graphics through OpenGL / Vulkan. Full GPU passthrough and NVIDIA CUDA / OptiX are not available in this runtime. Last successful boot: {s.last_render || "not started yet"}.</Note>
              </Section>

              <Section title="Resources" summary={`${PROFILES[f.profile]}  ·  ${shown[0]} cores  ·  ${shown[1]} GiB`}>
                <Row label="Resource profile"><Segmented value={f.profile} options={PROFILES} onChange={pickProfile} /></Row>
                <Row label="Memory (GiB)">
                  <NumberBox value={shown[1]} min={1} max={s.max_ram} disabled={!manual} onChange={v => set("ram", v)} />
                  <span className="hint-text">up to {s.max_ram} of {s.total_ram} GiB</span>
                </Row>
                <Row label="CPU cores">
                  <NumberBox value={shown[0]} min={coreTop >= 2 ? 2 : 1} max={coreTop} step={coreTop >= 2 ? 2 : 1} disabled={!manual} onChange={v => set("cores", v)} />
                  <span className="hint-text">up to {coreTop} of {s.threads}</span>
                </Row>
              </Section>

              <Section title="Disks" summary={`${s.disks.length} ${s.disks.length === 1 ? "disk" : "disks"}  ·  Disk ${disk + 1}: ${report?.actual != null ? size(report.actual) + (report.size != null ? " of " + size(report.size) : "") : "…"}`}>
                <Row label="Disk">
                  <Choice width={110} value={String(disk)} onChange={v => setDisk(Number(v))}
                    options={s.disks.map((_, i) => ({ value: String(i), label: "Disk " + (i + 1) }))} />
                </Row>
                <Note>Linux sees each disk as its own drive. {who} starts from Disk 1.</Note>

                <SubHeading>Capacity</SubHeading>
                <Row label="Current size"><span className="value-text">{!report ? "Checking…" : report.error ?? (report.actual != null ? size(report.actual) : "")}</span></Row>
                <Row label="System free"><span className="value-text">{report?.free != null && !report.error ? `${size(report.free)} on drive ${report.drive}` : ""}</span></Row>
                <Row label="Maximum size"><span className="value-text">{report?.size != null && !report.error ? size(report.size) : ""}</span></Row>

                <SubHeading>Disk information</SubHeading>
                <Note>{disk === 0 && s.disk_full
                  ? "Preallocated: its full maximum size was taken on the drive when it was made."
                  : "Dynamic: the file takes space on the drive only as Linux stores data, up to the maximum size."}</Note>
                <Note>Stored in {fileName(s.disks[disk])}{disk === 0 ? `, the disk ${who} starts from` : ""}{report?.snapshots ? `, with ${report.snapshots} ${report.snapshots === 1 ? "snapshot" : "snapshots"}` : ""}.</Note>
                <Row label="Location"><input className="text-box" readOnly value={s.disks[disk]} /></Row>

                <SubHeading>Disk Utilities</SubHeading>
                <Check label="Reclaim free space when Linux deletes files" checked={f.reclaim} onChange={v => set("reclaim", v)} />
                <Note inset>Linux tells the disk which space it no longer uses (TRIM), so new files reuse it instead of growing the file. Applies to every disk, from the next start.</Note>
                <Action text="Shrink or expand this disk's maximum size." label="Resize" disabled={!report || !!report.error} onClick={() => setTool("resize")} />
                <Action text={`Give unused space in this disk's file back to ${pc}.`} label="Compact" disabled={!report || !!report.error} onClick={compact} />
                <Action text={`Add another disk to ${who}.`} label="Add disk" onClick={() => setTool("add")} />
                <Action text={`Take this disk out of ${who}.`} label="Remove" disabled={disk === 0} onClick={remove} />
              </Section>
            </Tabs.Panel>

            {/* ---------------- Devices (sections in alphabetical order) */}
            <Tabs.Panel value="devices" className="tab-panel">
              <Section title="Camera and USB" summary={win ? "Not available in WINQ-EMU" : "Not available yet"}>
                <Note>{win ? "WINQ-EMU doesn't include camera or USB passthrough, so Windows cameras and USB devices can't be attached." : `Cameras and USB devices on ${pc} can't be attached yet.`} Use the shared folder to move files.</Note>
              </Section>

              <Section title="CD/DVD drive" summary={cd ? fileName(cd) : "Empty"}>
                <Row label="Disc">
                  <input className="text-box grow" readOnly value={cd || "Empty"} title={cd} />
                  <button className="btn small" onClick={insert}>Insert</button>
                  <button className="btn small" disabled={!cd} onClick={() => changeDisc("")}>Eject</button>
                </Row>
                <Note>Put an ISO in the drive, such as drivers or a rescue disk. The disc changes right away while {who} runs. It still starts from Disk 1; to start from a disc, use Recovery.</Note>
              </Section>

              <Section title="Microphone and sound" summary={!f.sound ? "Sound off" : f.mic ? "Sound and microphone on" : "Sound on, microphone off"}>
                <Check label="Enable sound" checked={f.sound} onChange={v => set("sound", v)} />
                <Check label="Allow microphone access (next start)" checked={f.mic} disabled={!f.sound} onChange={v => set("mic", v)} />
                <Note>Turning microphone access off keeps sound playback enabled.</Note>
                <Row label="Sound output"><Choice width={220} value="default" disabled options={[{ value: "default", label: win ? "Windows default" : "System default" }]} /></Row>
                <Row label="Microphone"><Choice width={220} value="default" disabled options={[{ value: "default", label: win ? "Windows default" : "System default" }]} /></Row>
                <Note>{win ? "This graphics engine doesn't support separate audio choices. Windows defaults are used." : `The sound devices chosen on ${pc} are used.`}</Note>
                {win && <div className="buttons">
                  <button className="btn small" onClick={() => openPage("ms-settings:sound")}>Windows sound devices</button>
                  <button className="btn small" onClick={() => openPage("ms-settings:privacy-microphone")}>Microphone privacy</button>
                </div>}
                {win && <Note>Choose Windows playback and recording defaults before launching. If a choice isn't picked up, restart {who}.</Note>}
              </Section>

              <Section title="Network" summary={f.net_type === 2 ? "No network"
                : `${f.net_type === 1 ? "Port forwards only" : "Shared (NAT)"}  ·  ${forwardCount === 0 ? "No port forwards" : forwardCount + (forwardCount === 1 ? " port forward" : " port forwards")}`}>
                <Row label="Network type">
                  <Choice width={240} value={String(f.net_type)} onChange={v => set("net_type", Number(v))}
                    options={NET_TYPES.map((label, i) => ({ value: String(i), label }))} />
                </Row>
                <Note>{NET_NOTES[f.net_type]}</Note>
                <Row label="Port forwards" top>
                  <textarea className="text-box mono" rows={4} spellCheck={false} disabled={f.net_type === 2}
                    value={f.forwards.join("\n")} onChange={e => set("forwards", e.target.value.split("\n"))} />
                </Row>
                <Note>One per line. tcp:HOST:GUEST is reachable from this PC only; lan tcp:HOST:GUEST also accepts other devices on your network (Windows Firewall asks the first time). SSH uses the rule for guest port 22.</Note>
              </Section>

              <Section title="Shared folder" summary={!f.shared.trim() ? "Not shared"
                : fileName(f.shared.trim().replace(/[\\/]+$/, "")) + (f.shared_write ? "  ·  Read and write" : "  ·  Read-only")}>
                <Row label="Shared folder">
                  <input className="text-box grow" value={f.shared} spellCheck={false} placeholder="Not shared" onChange={e => set("shared", e.target.value)} />
                  <button className="btn small" onClick={browse}>Browse</button>
                </Row>
                <Check label="Allow Linux to read and change this folder" checked={f.shared_write} onChange={v => set("shared_write", v)} />
                <Note inset>Off shares it read-only. In Linux: <code>sudo mount -t 9p -o trans=virtio host /mnt/host</code></Note>
              </Section>
            </Tabs.Panel>

            {/* ---------------- Recovery */}
            <Tabs.Panel value="recovery" className="tab-panel">
              <Section title="Backup and restore" summary="Back up, or restore a copy">
                <Action text={`Save a copy of ${who}, its disks and its settings.`} label="Back up" onClick={rec.backup} />
                <Action text="Restore a backup as a separate copy; the original stays." label="Restore" onClick={rec.restore} />
                <Note>Shut {who} down before backing it up.</Note>
              </Section>
              <Section title="Clone" summary="Copy it as a new system">
                <Action text={`Copy ${who}, its disks and its settings, as a new system.`} label="Clone" onClick={rec.clone} />
                <Note>Shut {who} down before cloning it. Snapshots aren't copied.</Note>
              </Section>
              <Section title="Snapshots" summary="Points in time for Disk 1">
                <Action text="Save Disk 1 as it is now, or go back to a saved point." label="Snapshots" onClick={() => setSnapsOpen(true)} />
              </Section>
              <Section title="Storage" summary="Move storage, or clean up downloads">
                <Action text="Move all systems and downloads to another folder." label="Move" onClick={rec.move} />
                <Action text="Delete installers that no system still needs." label="Clean" onClick={rec.clean} />
              </Section>
              <Section title="Repair" summary="Reset, or boot an ISO">
                <Action text={`Erase ${who} and run its installer again.`} label="Reset" onClick={rec.reset} />
                <Action text={`Start ${who} from an ISO, such as a rescue disk.`} label="Boot ISO" onClick={rec.boot} />
              </Section>
              <Section title="Delete" summary={"Remove " + who}>
                <Action text={`Remove ${who} and everything installed in it.`} label="Delete" danger onClick={rec.remove} />
              </Section>
            </Tabs.Panel>
          </SectionsContext.Provider>
          <ResizeDialog open={tool === "resize"} name={diskName} report={report} onDone={resized} />
          <AddDiskDialog open={tool === "add"} onDone={added} />
          <WorkingDialog open={tool === "compact"} title={`Compacting ${diskName} of ${who}`} />
          <WorkingDialog open={working !== null} title={working ?? ""} />
          <SnapshotsDialog open={snapsOpen} id={s.id} name={who} onClose={() => { setSnapsOpen(false); setReread(n => n + 1); }} />
        </div>
      </main>
    </Tabs.Root>
  );
}
