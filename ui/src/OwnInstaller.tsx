// Use Your Own Installer: an existing virtual disk to import, an ISO file on this PC, or a URL link to one.
// Choosing one clears the others; `last` is what was chosen before, when coming Back from General.
import { open as pick } from "@tauri-apps/plugin-dialog";
import { Fragment, useState } from "react";
import type { Source } from "./Create";
import { Header } from "./Header";
import { Logo } from "./Logo";
import { readDisk } from "./tauri";
import "./settings.css";

const fileName = (p: string) => p.slice(Math.max(p.lastIndexOf("\\"), p.lastIndexOf("/")) + 1);
const DISKS = ["qcow2", "vmdk", "vhd", "vhdx", "vdi", "img", "raw"];

function OrLine() {
  return <div className="or-line"><span>OR</span></div>;
}

export function OwnInstaller({ last, onBack, onNext }: { last?: Source; onBack: () => void; onNext: (s: Source) => void }) {
  const [disk, setDisk] = useState(last?.kind === "disk" ? last : null);
  const [iso, setIso] = useState(last?.kind === "iso" ? last.path : null);
  const [url, setUrl] = useState(last?.kind === "url" ? last.url : "");
  const [urlProblem, setUrlProblem] = useState(false);
  const [reading, setReading] = useState(false);

  const chooseDisk = async () => {
    const path = await pick({ title: "Choose a virtual disk to import",
      filters: [{ name: "Virtual disks", extensions: DISKS }, { name: "All files", extensions: ["*"] }] });
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

  const next = () => {
    if (disk) return onNext(disk);
    if (iso) return onNext({ kind: "iso", path: iso });
    const u = url.trim();
    if (!/^https?:\/\/[^\s/]+\.[^\s]+$/i.test(u)) return setUrlProblem(true);
    onNext({ kind: "url", url: u });
  };

  const info = disk?.info;
  const diskNote = !disk ? "qcow2, vmdk, vhd, vhdx, vdi or raw. It's copied; the original isn't changed."
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

          <div className="own-option">
            <div className="own-heading">An Existing Virtual Disk</div>
            <div className="own-pick">
              <button className="btn small" disabled={reading} onClick={chooseDisk}>Choose A Disk File</button>
              <span className={disk ? "chosen" : "none"}>{reading ? "Reading…" : disk ? fileName(disk.path) : "No disk chosen"}</span>
            </div>
            <div className="note">{diskNote}</div>
          </div>
          <OrLine />
          <div className="own-option">
            <div className="own-heading">An ISO File On This PC</div>
            <div className="own-pick">
              <button className="btn small" onClick={chooseIso}>Choose An ISO File</button>
              <span className={iso ? "chosen" : "none"} title={iso ?? undefined}>{iso ? fileName(iso) : "No file chosen"}</span>
            </div>
          </div>
          <OrLine />
          <div className="own-option">
            <div className="own-heading">URL Link</div>
            <input className="text-box" value={url} spellCheck={false} placeholder="https://... (address of an .iso file)"
              onChange={e => { setUrl(e.target.value); setUrlProblem(false); if (e.target.value) { setDisk(null); setIso(null); } }}
              onKeyDown={e => { if (e.key === "Enter") next(); }} />
            <div className={"note" + (urlProblem ? " bad" : "")}>
              {urlProblem ? "That isn't a web address. It should start with https://" : "Paste the address of an .iso file."}
            </div>
          </div>

          <div className="nav">
            <button className="btn" data-back onClick={onBack}>Back</button>
            <button className="btn primary" disabled={!disk && !iso && !url.trim()} onClick={next}>Next</button>
          </div>
        </div>
      </main>
    </Fragment>
  );
}
