// Create, after Choose a Linux OS (or Use Your Own Installer): General (name, description, location),
// Resources (memory, cores, disk), Summary, then the download, check and install, or the import, with its
// progress, as on the Windows edition.
import { open as pick } from "@tauri-apps/plugin-dialog";
import { Fragment, useEffect, useRef, useState } from "react";
import { toast } from "sonner";
import { ask, problem } from "./Ask";
import type { Distro } from "./distros";
import { Check, Note, NumberBox, Row, Segmented } from "./Form";
import { Header } from "./Header";
import { Logo } from "./Logo";
import { cancelCreate, create, createDefaults, enableHypervisor, freeSpace, onCreateProgress, readiness, type Defaults, type DiskSource, type Free, type Plan, type Progress } from "./tauri";
import "./settings.css";

// Where the new system comes from.
export type Source =
  | { kind: "distro"; distro: Distro }
  | { kind: "iso"; path: string }
  | { kind: "url"; url: string }
  | { kind: "disk"; path: string; info: DiskSource };

const fileName = (p: string) => p.slice(Math.max(p.lastIndexOf("\\"), p.lastIndexOf("/")) + 1);
const stem = (p: string) => decodeURIComponent(fileName(p.split(/[?#]/)[0])).replace(/\.[^.]+$/, "");
// What it's called before it has a name: the distribution, or the file's name.
export const sourceLabel = (s: Source) => s.kind === "distro" ? s.distro.name : stem(s.kind === "url" ? s.url : s.path) || "Linux";
const sameSource = (a: Source, b: Source) => a.kind === b.kind &&
  (a.kind === "distro" ? a.distro.id === (b as typeof a).distro.id : a.kind === "url" ? a.url === (b as typeof a).url : a.path === (b as { path: string }).path);

const PROFILES = ["Balanced", "Maximum", "Manual"];
const GiB = 2 ** 30;
const gib = (b: number) => { const g = b / GiB; return g === Math.floor(g) ? String(g) : g.toFixed(1); };
// Tips while it installs, worded for this computer (the web view's user agent names it).
const WIN = navigator.userAgent.includes("Windows"), MAC = navigator.userAgent.includes("Macintosh");
const TIPS = [
  ...(MAC ? [] : [
    `Ctrl+Alt+G releases the mouse and keyboard back to ${WIN ? "Windows" : "your desktop"}.`,
    "Ctrl+Alt+F switches the VM window in and out of fullscreen.",
  ]),
  WIN ? "The installer only sees the virtual disk, a file on your PC. Your Windows drive is never touched."
    : "The installer only sees the virtual disk, a file on this computer. Your own drive is never touched.",
  `${WIN ? "Right-click" : "Click"} the Try Linux ${MAC ? "menu bar" : "tray"} icon to shut a system down cleanly.`,
  "Chromium-based browsers can show black video in the VM. Firefox works best.",
  `Settings can share a ${WIN ? "Windows " : ""}folder with Linux${MAC ? "" : " and add a desktop shortcut"}.`,
];

interface Draft {
  name: string; description: string; ownLocation: boolean; location: string;
  profile: number; ram: number; cores: number; diskGB: number; diskFull: boolean;
}
// The answers so far, kept when going Back to the page before and coming again with the same choice.
let saved: { source: Source; draft: Draft } | null = null;

type Step = "general" | "resources" | "summary" | "progress";

export function Create({ source, onBack, onDone, startStep }:
  { source: Source; onBack: () => void; onDone: () => void; startStep?: "general" | "resources" | "summary" }) {
  const [defaults, setDefaults] = useState<Defaults | null>(null);
  const [d, setD] = useState<Draft | null>(null);
  const [step, setStep] = useState<Step>(startStep ?? "general");
  const [installWinq, setInstallWinq] = useState(false);
  const set = <K extends keyof Draft>(k: K, v: Draft[K]) => setD(o => o && { ...o, [k]: v });
  const label = sourceLabel(source);
  const disk = source.kind === "disk" ? source : null;

  useEffect(() => {
    // A disk's own settings (beside it, from Try Linux, VMware or VirtualBox) fill in what they can.
    createDefaults(disk?.info.name || label).then(x => {
      setDefaults(x);
      if (saved && sameSource(saved.source, source)) return setD(saved.draft);
      const own = disk && (disk.info.ram_gb > 0 || disk.info.cores > 0);
      setD({
        name: x.name, description: disk?.info.description ?? "", ownLocation: false, location: x.location,
        profile: own ? 2 : 0,   // Manual, starting from the disk's own memory and cores
        ram: own && disk.info.ram_gb ? Math.min(disk.info.ram_gb, x.max_ram) : x.balanced[1],
        cores: own && disk.info.cores ? Math.min(disk.info.cores, x.max_cores) : x.balanced[0],
        diskGB: source.kind === "distro" ? source.distro.diskGB ?? 64 : 64, diskFull: false,
      });
    }).catch(problem);
  }, []);
  useEffect(() => { if (d) saved = { source, draft: d }; }, [d]);

  // The drive the system goes on, and its room, for Resources.
  const [free, setFree] = useState<Free | null>(null);
  useEffect(() => { if (d?.location) freeSpace(d.location).then(setFree).catch(() => setFree(null)); }, [d?.location]);

  if (!defaults || !d) return <Header />;

  const where = d.ownLocation ? d.location.trim() : defaults.location;
  const manual = d.profile === 2;
  const shown = manual ? [d.cores, d.ram] : d.profile === 1 ? defaults.maximum : defaults.balanced;
  const coreTop = defaults.max_cores >= 2 ? Math.floor(defaults.max_cores / 2) * 2 : 1;
  const pickProfile = (p: number) => setD(o => o && (p === 2 && o.profile !== 2 ? { ...o, profile: p, cores: shown[0], ram: shown[1] } : { ...o, profile: p }));
  const freeText = free?.bytes != null ? `up to ${Math.floor(free.bytes / GiB)} GiB free on ${free.drive}` : "";

  const nextFromGeneral = () => {
    const name = d.name.trim();
    if (!name || name.length > 60) return problem({ title: "Name it", text: "Give the system a name of up to 60 characters." });
    if (defaults.taken.some(t => t.toLowerCase() === name.toLowerCase()))
      return problem({ title: "That name is taken", text: `You already have a system named "${name}". Choose another name.` });
    if (d.ownLocation) {
      if (!/^([A-Za-z]:\\|\\\\|\/)/.test(where)) return problem({ title: "Choose a folder", text: "Choose a full folder path for the location, or untick \"Choose a different location\"." });
      if (where.includes(",")) return problem({ title: "Choose another folder", text: "QEMU can't use a folder whose path contains a comma. Choose another location." });
    }
    setStep("resources");
  };
  const nextFromResources = () => {
    if (!disk && d.diskFull && free?.bytes != null && free.bytes < (d.diskGB + 1) * GiB)
      return problem({ title: "Not enough room", text: `The drive doesn't have ${d.diskGB} GiB free to allocate now. Choose a smaller size, or untick "Allocate the full size now".` });
    setStep("summary");
  };

  // Before Install: what this PC still needs, as the Windows edition asks it. Linux can't run without
  // hardware virtualization; Windows Hypervisor Platform can be turned on now (one restart); WINQ-EMU is
  // installed first, during setup. Downloading can go ahead while a restart or BIOS setting is still to do.
  const prepare = async () => {
    const r = await readiness();
    if (r.virt === "no-cpu" || r.virt === "mac-unsupported") return problem({ title: "Linux can't run on this computer", text: r.message });
    // Linux: QEMU is installed with the distribution's packages, not by Try Linux.
    if (!r.winq && !r.can_install) return problem({ title: "QEMU isn't installed", text: r.install_message });
    if ((r.virt === "firmware-off" || r.virt === "kvm-missing" || r.virt === "kvm-denied") && !await ask(`Download ${d.name.trim()} anyway?`,
      r.message + "\n\nTry Linux can download and prepare it now, so it's ready after you change the setting.", "Download")) return;
    if (r.virt === "feature-off") {
      if (!await ask("Turn on Windows Hypervisor Platform?",
        "Try Linux needs it, and it's turned off. Windows asks for permission, then needs one restart. Setup can keep downloading in the meantime.", "Turn on")) return;
      try { await enableHypervisor(); } catch (e) { if (e !== "declined") problem({ title: "Couldn't turn it on", text: String(e) }); return; }
    }
    if (!r.winq && !await ask("Install WINQ-EMU?",
      "Try Linux runs systems with WINQ-EMU, a free QEMU build with GPU acceleration for Windows. It isn't installed yet. Download and install it now (about 55 MB)?", "Install")) return;
    setInstallWinq(!r.winq);
    setStep("progress");
  };

  const page = (title: string, body: React.ReactNode, back: () => void, next: () => void, nextLabel = "Next") => (
    <Fragment>
      <Header />
      <main className="page">
        <div className="column create">
          <Logo />
          <h1 className="center">{title}</h1>
          <div className="subtitle">{label}</div>
          <div className="create-body">{body}</div>
          <div className="nav">
            <button className="btn" data-back onClick={back}>Back</button>
            <button className="btn primary" onClick={next}>{nextLabel}</button>
          </div>
        </div>
      </main>
    </Fragment>
  );

  if (step === "general") return page("General", <>
    <Row label="Name"><input className="text-box" value={d.name} autoFocus spellCheck={false} maxLength={60}
      onChange={e => set("name", e.target.value)} onKeyDown={e => { if (e.key === "Enter") nextFromGeneral(); }} /></Row>
    <Row label="Description" top>
      <textarea className="text-box" rows={3} value={d.description} onChange={e => set("description", e.target.value)} />
    </Row>
    <Note><span className="under-field">(Optional): Shown in Settings &gt; General &gt; Info.</span></Note>
    <Row label="Location">
      <input className="text-box grow" value={d.ownLocation ? d.location : defaults.location} readOnly={!d.ownLocation}
        spellCheck={false} onChange={e => set("location", e.target.value)} />
      <button className="btn small" disabled={!d.ownLocation} onClick={async () => {
        const dir = await pick({ title: "Where should Try Linux keep this system?", directory: true });
        if (typeof dir === "string") set("location", dir);
      }}>Browse</button>
    </Row>
    <div className="under-field">
      <Check label="Choose a different location" checked={d.ownLocation}
        onChange={v => setD(o => o && { ...o, ownLocation: v, location: v ? o.location : defaults.location })} />
      <Note>{d.ownLocation ? "Try Linux makes a folder for this system inside it." : "The default: where Try Linux keeps its systems."}</Note>
    </div>
  </>, onBack, nextFromGeneral);

  if (step === "resources") return page("Resources", <>
    <Row label="Resource profile"><Segmented value={d.profile} options={PROFILES} onChange={pickProfile} /></Row>
    <Row label="Memory (GiB)">
      <NumberBox value={shown[1]} min={1} max={defaults.max_ram} disabled={!manual} onChange={v => set("ram", v)} />
      <span className="hint-text">up to {defaults.max_ram} of {defaults.total_ram} GiB</span>
    </Row>
    <Row label="CPU cores">
      <NumberBox value={shown[0]} min={coreTop >= 2 ? 2 : 1} max={coreTop} step={coreTop >= 2 ? 2 : 1} disabled={!manual} onChange={v => set("cores", v)} />
      <span className="hint-text">up to {coreTop} of {defaults.threads}</span>
    </Row>
    {disk ? <>
      <Row label="Disk"><span className="value-text">{fileName(disk.path)}{disk.info.size ? `  ·  ${gib(disk.info.size)} GiB` : ""}</span></Row>
      <Note><span className="under-field">Keeps its own size{freeText ? "  ·  " + freeText : ""}</span></Note>
    </> : <>
      <Row label="Disk size (GiB)">
        <NumberBox value={d.diskGB} min={16} max={2048} step={16} disabled={d.diskFull} onChange={v => set("diskGB", v)} />
        <span className="hint-text">{freeText}</span>
      </Row>
      <div className="under-field">
        <Check label="Allocate the full size now" checked={d.diskFull} onChange={v => set("diskFull", v)} />
        <Note>{d.diskFull ? "The whole size is taken on the drive now; untick to change the size."
          : "Dynamic: the disk takes space only as Linux stores data, up to this size."}</Note>
      </div>
    </>}
  </>, () => setStep("general"), nextFromResources);

  if (step === "summary") {
    const firstLine = d.description.trim().split("\n")[0].trim();
    const installer = source.kind === "distro" ? `Download from ${new URL(source.distro.base!).host}, checksum verified`
      : source.kind === "url" ? "Download " + source.url : source.kind === "iso" ? source.path : "Import " + source.path;
    const rows: [string, string][] = [
      ["Name", d.name.trim()],
      ["Description", !firstLine ? "None" : firstLine + (d.description.trim().includes("\n") ? " …" : "")],
      ["Operating system", source.kind === "distro" ? source.distro.name : disk ? "From the imported disk" : label],
      [disk ? "Disk" : "Installer", installer],
      ["Location", where.replace(/[\\/]+$/, "") + (where.startsWith("/") ? "/" : "\\") + (d.name.trim().toLowerCase().replace(/[^a-z0-9_-]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 32) || "linux")],
      ["Resource profile", PROFILES[d.profile]],
      ["Memory", shown[1] + " GiB"],
      ["CPU cores", String(shown[0])],
      ["Disk size", disk ? (disk.info.size ? gib(disk.info.size) + " GiB" : "As imported") + " (Dynamic)" : `${d.diskGB} GiB (${d.diskFull ? "Preallocated" : "Dynamic"})`],
    ];
    return page("Summary",
      <div className="box summary">
        {rows.map(([k, v]) => <div key={k} className="summary-row"><span className="summary-key">{k}</span><span className="summary-value" title={v}>{v}</span></div>)}
      </div>, () => setStep("resources"), prepare, disk ? "Import" : "Install");
  }

  const plan: Plan = {
    install_winq: installWinq,
    distro: source.kind === "distro"
      ? { id: source.distro.id, name: source.distro.name, base: source.distro.base!, steps: source.distro.steps ?? [], checksum: source.distro.checksum ?? null }
      : null,
    iso: source.kind === "iso" ? source.path : null,
    url: source.kind === "url" ? source.url : null,
    disk: disk ? disk.path : null,
    name: d.name.trim(), description: d.description, location: where,
    profile: d.profile, ram: shown[1], cores: shown[0], disk_gb: d.diskGB, disk_full: !disk && d.diskFull,
  };
  return <Installing title={label} plan={plan} onEnd={ok => { if (ok) { saved = null; onDone(); } else setStep("summary"); }} />;
}

// The download, check and set-up (or the import), with a bar, what it's doing, and tips while it works.
function Installing({ title, plan, onEnd }: { title: string; plan: Plan; onEnd: (ok: boolean) => void }) {
  const [p, setP] = useState<Progress>({ status: "Getting ready", value: -1, detail: "" });
  const [tip, setTip] = useState(() => Math.floor(Math.random() * TIPS.length));
  const [stopping, setStopping] = useState(false);
  const started = useRef(false);

  useEffect(() => {
    const t = setInterval(() => setTip(i => (i + 1) % TIPS.length), 7000);
    const off = onCreateProgress(setP);
    if (!started.current) {
      started.current = true;
      create(plan).then(note => {
        // Ready, but Linux can't run yet (a restart, or a BIOS/UEFI setting): say what's left.
        if (note) ask(plan.disk ? `${plan.name} is imported` : `${plan.name} is ready to install`, note, null);
        else toast(plan.disk ? `${plan.name} is imported and starting.` : `${plan.name}'s installer is starting.`);
        onEnd(true);
      }).catch(e => {
        if (e !== "cancelled") problem({ title: "Couldn't create " + plan.name, text: String(e) });
        onEnd(false);
      });
    }
    return () => { clearInterval(t); off.then(f => f()); };
  }, []);

  const cancel = async () => {
    if (await ask("Stop setting up?", "Anything already downloaded is kept, so next time it picks up where it left off.", "Stop")) {
      setStopping(true);
      setP(o => ({ ...o, status: "Stopping…" }));
      cancelCreate();
    }
  };

  return (
    <Fragment>
      <Header />
      <main className="page">
        <div className="column progress-page">
          <Logo />
          <h1 className="center">{title}</h1>
          <div className="progress-status">{p.status}</div>
          <div className={"bar" + (p.value < 0 ? " busy" : "")}>
            <div className="bar-fill" style={p.value >= 0 ? { width: `${Math.round(p.value * 1000) / 10}%` } : undefined} />
          </div>
          <div className="progress-detail">{p.detail}</div>
          <div className="progress-tip">Tip: {TIPS[tip]}</div>
          <div className="center"><button className="btn" disabled={stopping} onClick={cancel}>Cancel</button></div>
        </div>
      </main>
    </Fragment>
  );
}
