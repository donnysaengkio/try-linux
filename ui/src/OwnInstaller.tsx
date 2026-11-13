// Use Your Own Installer: three cards, like Choose a Linux OS's rows: an existing virtual machine (.ova) or disk to
// import, an ISO file on this PC, or a URL link to one. Choosing a card opens its button or address box; choosing
// one thing clears the others.
// Choosing one clears the others; `last` is what was chosen before, when coming Back from General.
import { open as pick } from "@tauri-apps/plugin-dialog";
import { Fragment, useEffect, useRef, useState, type ReactNode } from "react";
import type { Source } from "./Create";
import { Header } from "./Header";
import { Icon, type IconName } from "./Icon";
import { Logo } from "./Logo";
import { readDisk } from "./tauri";
import "./settings.css";

const fileName = (p: string) => p.slice(Math.max(p.lastIndexOf("\\"), p.lastIndexOf("/")) + 1);
const DISKS = ["ova", "qcow2", "vmdk", "vhd", "vhdx", "vdi", "img", "raw"];

type Kind = "disk" | "iso" | "url";

// One choice: its icon, title and what it's for; selected, what to choose with.
function Card({ kind, chosen, icon, title, about, onPick, children }:
  { kind: Kind; chosen: Kind | null; icon: IconName; title: string; about: ReactNode; onPick: (k: Kind) => void; children: ReactNode }) {
  const on = chosen === kind;
  return (
    <div className={"own-card" + (on ? " on" : "")} role="radio" aria-checked={on} tabIndex={0}
      onClick={() => onPick(kind)} onKeyDown={e => { if ((e.key === "Enter" || e.key === " ") && e.target === e.currentTarget) { e.preventDefault(); onPick(kind); } }}>
      <span className="own-icon"><Icon name={icon} /></span>
      <div className="own-text">
        <div className="own-title">{title}</div>
        <div className="own-about">{about}</div>
        {on && <div className="own-pick" onClick={e => e.stopPropagation()}>{children}</div>}
      </div>
      {!on && <span className="own-chev"><Icon name="chevron-right" /></span>}
    </div>
  );
}

export function OwnInstaller({ last, onBack, onNext }: { last?: Source; onBack: () => void; onNext: (s: Source) => void }) {
  const [disk, setDisk] = useState(last?.kind === "disk" ? last : null);
  const [iso, setIso] = useState(last?.kind === "iso" ? last.path : null);
  const [url, setUrl] = useState(last?.kind === "url" ? last.url : "");
  const [urlProblem, setUrlProblem] = useState(false);
  const [reading, setReading] = useState(false);
  // The card that's open: the one already chosen, coming Back.
  const [card, setCard] = useState<Kind | null>(last && last.kind !== "distro" ? last.kind : null);
  const urlBox = useRef<HTMLInputElement>(null);
  useEffect(() => { if (card === "url") urlBox.current?.focus(); }, [card]);

  const chooseDisk = async () => {
    const path = await pick({ title: "Choose a virtual machine or disk to import",
      filters: [{ name: "Virtual machines and disks", extensions: DISKS }, { name: "All files", extensions: ["*"] }] });
    if (typeof path !== "string") return;
    setIso(null); setUrl(""); setReading(true);
    // Its format and size, and any settings saved beside it, fill in the next steps.
    try { setDisk({ kind: "disk", path, info: await readDisk(path) }); } finally { setReading(false); }
  };
  const chooseIso = async () => {
    const path = await pick({ title: "Choose a Linux installer",
      filters: [{ name: "Disc images", extensions: ["iso"] }, { name: "All files", extensions: ["*"] }] });
    if (typeof path !== "string") return;
    setIso(path); setDisk(null); setUrl("");
  };

  // Next takes what's chosen on the open card.
  const ready = card === "disk" ? !!disk : card === "iso" ? !!iso : card === "url" ? !!url.trim() : false;
  const next = () => {
    if (card === "disk" && disk) return onNext(disk);
    if (card === "iso" && iso) return onNext({ kind: "iso", path: iso });
    if (card !== "url") return;
    const u = url.trim();
    if (!/^https?:\/\/[^\s/]+\.[^\s]+$/i.test(u)) return setUrlProblem(true);
    onNext({ kind: "url", url: u });
  };

  const info = disk?.info;
  const diskNote = !disk ? ""
    : [info!.format, info!.size ? (info!.size / 2 ** 30).toFixed(1).replace(/\.0$/, "") + " GiB" : "",
       info!.from ? `settings from ${info!.from}${info!.ram_gb ? `: ${info!.ram_gb} GiB, ${info!.cores} ${info!.cores === 1 ? "core" : "cores"}` : ""}`
         : "no saved settings found beside it"].filter(Boolean).join("  ·  ");

  return (
    <Fragment>
      <Header />
      <main className="page">
        <div className="column create own">
          <Logo />
          <h1 className="center">Use Your Own Installer</h1>
          <div className="subtitle">A system that's already installed, or an installer Try Linux doesn't list.</div>

          <div className="own-cards" role="radiogroup" aria-label="Use your own installer">
            <Card kind="disk" chosen={card} icon="file-input" title="An Existing Virtual Machine Or Disk" onPick={setCard}
              about={disk ? diskNote : "From VirtualBox, VMware or Hyper-V: .ova, .vmdk, .vhdx, .vdi, .qcow2 or raw. It's copied; the original isn't changed."}>
              <button className="btn small" disabled={reading} onClick={chooseDisk}>Choose A File</button>
              <span className={disk ? "chosen" : "none"} title={disk?.path}>{reading ? "Reading…" : disk ? fileName(disk.path) : "Nothing chosen"}</span>
            </Card>
            <Card kind="iso" chosen={card} icon="disc" title="An ISO File On This PC" onPick={setCard} about="An installer you've downloaded.">
              <button className="btn small" onClick={chooseIso}>Choose An ISO File</button>
              <span className={iso ? "chosen" : "none"} title={iso ?? undefined}>{iso ? fileName(iso) : "No file chosen"}</span>
            </Card>
            <Card kind="url" chosen={card} icon="globe" title="URL Link" onPick={setCard}
              about={urlProblem ? <span className="bad">That isn't a web address. It should start with https://</span> : "The web address of an .iso file. Try Linux downloads it."}>
              <input ref={urlBox} className="text-box" value={url} spellCheck={false} placeholder="https://... (address of an .iso file)"
                onChange={e => { setUrl(e.target.value); setUrlProblem(false); if (e.target.value) { setDisk(null); setIso(null); } }}
                onKeyDown={e => { if (e.key === "Enter") next(); }} />
            </Card>
          </div>

          <div className="nav">
            <button className="btn" data-back onClick={onBack}>Back</button>
            <button className="btn primary" disabled={!ready} onClick={next}>Next</button>
          </div>
        </div>
      </main>
    </Fragment>
  );
}
