// The Disk Utilities' dialogs (Settings > Hardware > Disks): the new size for Resize, what to add for Add disk,
// and the progress while a disk is compacted; and Recovery's Snapshots and Log.
import { Dialog } from "@base-ui/react/dialog";
import { listen } from "@tauri-apps/api/event";
import { open as pick } from "@tauri-apps/plugin-dialog";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { Note, NumberBox } from "./Form";
import { Icon } from "./Icon";
import { ask, problem } from "./Ask";
import { listSnapshots, openLog, snapshotAction, systemLog, type DiskReport, type Progress, type Snapshot } from "./tauri";

const GiB = 2 ** 30;
const size = (b: number) => b >= GiB ? (b / GiB).toFixed(1) + " GB" : (b / 2 ** 20).toFixed(b < 10 * 2 ** 20 ? 1 : 0) + " MB";

function Box({ open, title, onClose, children, closable = true, wide = false }:
  { open: boolean; title: string; onClose: () => void; children: ReactNode; closable?: boolean; wide?: boolean }) {
  return (
    <Dialog.Root open={open} onOpenChange={o => { if (!o && closable) onClose(); }}>
      <Dialog.Portal>
        <Dialog.Backdrop className="modal-back" />
        <Dialog.Viewport className="modal-viewport">
          <Dialog.Popup className={"modal app-window" + (wide ? " wide" : "")}>
            <div className="app-window-head">
              <Dialog.Title className="modal-title">{title}</Dialog.Title>
              {closable && <Dialog.Close className="btn icon small" aria-label="Close"><Icon name="x" /></Dialog.Close>}
            </div>
            {children}
          </Dialog.Popup>
        </Dialog.Viewport>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

// Resize: the new maximum size, never below what Linux has written plus a little room. Resolves the size, or
// null when cancelled; shrinking is confirmed by the page.
export function ResizeDialog({ open, name, report, onDone }:
  { open: boolean; name: string; report: DiskReport | null; onDone: (gb: number | null) => void }) {
  const current = Math.ceil((report?.size ?? 0) / GiB);
  const min = Math.max(1, Math.ceil((report?.actual ?? 0) / GiB) + 1);
  const [gb, setGb] = useState(current);
  useEffect(() => { if (open) setGb(current); }, [open, current]);
  const note = gb > current
    ? `Expands the disk by ${gb - current} GB. It takes no space until Linux uses it. Linux may need to expand its partition to use the new space (with GNOME Disks or GParted).`
    : gb < current
      ? `Shrinking cuts ${current - gb} GB off the end of the disk. Shrink Linux's partition to fit first (with GParted from a live ISO, for example), or Linux may not start. Back it up in Settings > Recovery.`
      : "Choose a larger size to expand the disk, or a smaller one to shrink it.";
  return (
    <Box open={open} title={"Resize " + name} onClose={() => onDone(null)}>
      <p className="modal-text">Current size {size(report?.actual ?? 0)}, maximum size {size(report?.size ?? 0)}.</p>
      <div className="pref-heading">New maximum size (GB)</div>
      <NumberBox value={gb} min={min} max={4096} onChange={setGb} />
      <Note><span className={gb < current ? "warning" : ""}>{note}</span></Note>
      <div className="modal-buttons">
        <button className="btn" onClick={() => onDone(null)}>Cancel</button>
        <button className={"btn " + (gb < current ? "danger" : "primary")} disabled={gb === current} onClick={() => onDone(gb)}>Resize</button>
      </div>
    </Box>
  );
}

// Add disk: a new empty disk of a size, or an existing .qcow2 file. Resolves what was chosen, or null.
export function AddDiskDialog({ open, onDone }: { open: boolean; onDone: (choice: { gb?: number; file?: string } | null) => void }) {
  const [gb, setGb] = useState(64);
  const [file, setFile] = useState<string | null>(null);
  useEffect(() => { if (open) { setGb(64); setFile(null); } }, [open]);
  const choose = async () => {
    const f = await pick({ title: "Choose a disk to add", filters: [{ name: "QEMU disks", extensions: ["qcow2"] }] });
    if (typeof f === "string") setFile(f);
  };
  return (
    <Box open={open} title="Add a disk" onClose={() => onDone(null)}>
      <div className="pref-heading">A new empty disk</div>
      <div className={"row" + (file ? " faded" : "")} onFocus={() => setFile(null)}>
        <NumberBox value={gb} min={1} max={4096} step={16} onChange={v => { setGb(v); setFile(null); }} />
        <span className="hint-text">GB maximum size, takes no space until Linux uses it</span>
      </div>
      <div className="or-line"><span>OR</span></div>
      <div className="pref-heading">An existing disk file</div>
      <div className="own-pick">
        <button className="btn small" onClick={choose}>Choose a disk file…</button>
        <span className={file ? "chosen" : "none"} title={file ?? undefined}>{file ? file.slice(Math.max(file.lastIndexOf("\\"), file.lastIndexOf("/")) + 1) : "No file chosen"}</span>
      </div>
      <Note>Try Linux uses .qcow2 disks; the file stays where it is.</Note>
      <div className="modal-buttons">
        <button className="btn" onClick={() => onDone(null)}>Cancel</button>
        <button className="btn primary" onClick={() => onDone(file ? { file } : { gb })}>{file ? "Add this disk" : "Add new disk"}</button>
      </div>
    </Box>
  );
}

// While a disk is being compacted: what it's doing and how far along, with no way to stop halfway.
export function WorkingDialog({ open, title }: { open: boolean; title: string }) {
  const [p, setP] = useState<Progress>({ status: "Starting", value: -1, detail: "" });
  useEffect(() => {
    if (!open) return;
    setP({ status: "Starting", value: -1, detail: "This can take a few minutes for large disks" });
    const off = listen<Progress>("tool-progress", e => setP(e.payload));
    return () => { off.then(f => f()); };
  }, [open]);
  return (
    <Box open={open} title={title} onClose={() => {}} closable={false}>
      <div className="progress-status">{p.status}</div>
      <div className={"bar wide" + (p.value < 0 ? " busy" : "")}>
        <div className="bar-fill" style={p.value >= 0 ? { width: `${Math.round(p.value * 1000) / 10}%` } : undefined} />
      </div>
      <div className="progress-detail">{p.detail}</div>
    </Box>
  );
}

// Snapshots: Disk 1 as it was at points in time, kept inside its file. Take one, go back to one, or delete one.
export function SnapshotsDialog({ open, id, name, onClose }: { open: boolean; id: string; name: string; onClose: () => void }) {
  const [snaps, setSnaps] = useState<Snapshot[] | null>(null);
  const [chosen, setChosen] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const reload = () => listSnapshots(id).then(s => { setSnaps(s); setError(null); setChosen(c => s.some(x => x.name === c) ? c : null); })
    .catch(e => { setSnaps([]); setError(String(e)); });
  useEffect(() => { if (open) { setSnaps(null); setChosen(null); reload(); } }, [open]);
  const act = async (action: "take" | "revert" | "delete") => {
    if (action !== "take" && !(await ask(action === "revert" ? "Revert to this snapshot?" : "Delete this snapshot?",
      `${chosen}\n\n${action === "revert" ? `Changes made to ${name} since then are lost.` : "It can't be restored afterwards."}`,
      action === "revert" ? "Revert" : "Delete", true))) return;
    setBusy(true);
    try { await snapshotAction(id, action, chosen ?? ""); } catch (e) { problem({ title: "Snapshots", text: String(e) }); }
    setBusy(false);
    reload();
  };
  return (
    <Box open={open} title={"Snapshots  ·  " + name} onClose={onClose}>
      <Note>A snapshot saves the whole of Disk 1 as it is now, inside the disk file. Reverting throws away changes made since.</Note>
      <div className="box list snaps" role="listbox" aria-label="Snapshots">
        {snaps === null ? <div className="empty">Reading…</div>
          : error ? <div className="empty">Couldn't read snapshots<br />{error}</div>
          : snaps.length === 0 ? <div className="empty">No snapshots yet<br />Take a snapshot to save the disk as it is now.</div>
          : snaps.map(s => (
              <div key={s.name} role="option" aria-selected={s.name === chosen} className={"option small" + (s.name === chosen ? " selected" : "")}
                onClick={() => setChosen(s.name)}>
                <div className="text"><div className="title">{s.name}</div><div className="about">{s.when}</div></div>
              </div>))}
      </div>
      <div className="modal-buttons spread">
        <button className="btn primary" disabled={busy} onClick={() => act("take")}>Take Snapshot</button>
        <button className="btn" disabled={busy || !chosen} onClick={() => act("revert")}>Revert</button>
        <button className="btn danger" disabled={busy || !chosen} onClick={() => act("delete")}>Delete</button>
        <button className="btn" onClick={onClose}>Close</button>
      </div>
    </Box>
  );
}

// A system's log (log.rs): its starts and stops and why, the Power menu's commands, and what QEMU reported while
// it ran. The newest is at the bottom, where it opens.
export function LogDialog({ open, id, name, onClose }: { open: boolean; id: string; name: string; onClose: () => void }) {
  const [text, setText] = useState<string | null>(null);
  const view = useRef<HTMLPreElement>(null);
  const read = () => systemLog(id).then(setText).catch(e => setText("Couldn't read the log.\n\n" + e));
  useEffect(() => { if (open) { setText(null); read(); } }, [open]);
  useEffect(() => { if (view.current) view.current.scrollTop = view.current.scrollHeight; }, [text]);
  return (
    <Box open={open} title={"Log  ·  " + name} onClose={onClose} wide>
      <Note>When {name} started and stopped and why, the Power menu's commands, and any warnings while it ran.</Note>
      <pre ref={view} className="box log-text">
        {text === null ? "Reading…" : text || `Nothing yet. The log starts the next time ${name} runs.`}
      </pre>
      <div className="modal-buttons spread">
        <button className="btn" onClick={read}>Refresh</button>
        <button className="btn" disabled={!text} onClick={() => openLog(id).catch(problem)}>Open Log File</button>
        <button className="btn" onClick={onClose}>Close</button>
      </div>
    </Box>
  );
}
