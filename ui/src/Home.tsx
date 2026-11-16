// Virtual Machines: a row for each system, with the columns chosen in Columns (columns.tsx): live CPU, memory,
// disk and network activity for running ones, and more from QEMU and the guest agent. Click a column title to sort
// by it, drag one to move it; drag a row to arrange the systems yourself. The filter narrows the list by name or
// state. Systems can go into named groups (New Group in the main menu, Ctrl+G or Ctrl+K); once there's a group,
// systems in none are under Unassigned. How it's arranged is kept with the preferences (app.rs, layout), and the
// tray menu follows it.
import { ContextMenu } from "@base-ui/react/context-menu";
import { Menu } from "@base-ui/react/menu";
import { Fragment, useEffect, useRef, useState } from "react";
import { toast } from "sonner";
import { ask, askText, problem } from "./Ask";
import { swallowClick } from "./Arrange";
import { AVAILABLE, COLUMNS, DEFAULT_COLUMNS, on, type Cell } from "./columns";
import { Commands } from "./Commands";
import { SnapshotsDialog, WorkingDialog } from "./DiskTools";
import { Header } from "./Header";
import { Icon } from "./Icon";
import { Logo } from "./Logo";
import { MainMenu, setNewGroup } from "./MainMenu";
import { act, call, powerItems, startSystem } from "./power";
import { cloneSystem, deleteSystem, layout as loadLayout, onSystemsChanged, power, renameSystem, reveal, screenshot, setLayout as saveLayout, showConsole, systems, type PowerAction, type Vm } from "./tauri";

// ---- how the page is arranged

interface Layout {
  order: string[];                 // system ids, as arranged by dragging; others come after, A to Z
  groups: string[];                // group names
  member: Record<string, string>;  // system id -> its group
  folded: string[];                // groups folded away ("" for Unassigned)
  sort: { col: string; dir: 1 | -1 } | null;  // "name" or a column to sort by, or null for the arranged order
  cols: string[] | null;           // the columns shown, in order (null: the usual ones)
  gorder: string[];                // groups as arranged by hand (empty: the default, A to Z)
  gsort: "custom" | "asc" | "desc";  // the groups' order: as arranged, or A to Z, or Z to A
}
const EMPTY: Layout = { order: [], groups: [], member: {}, folded: [], sort: null, cols: null, gorder: [], gsort: "custom" };

function parseLayout(text: string): Layout {
  try {
    const j = JSON.parse(text);
    const strings = (v: unknown) => Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : [];
    const member: Record<string, string> = {};
    if (j.member && typeof j.member === "object") for (const [k, v] of Object.entries(j.member)) if (typeof v === "string") member[k] = v;
    const sort = j.sort && (j.sort.col === "name" || AVAILABLE.includes(j.sort.col)) ? { col: j.sort.col as string, dir: (j.sort.dir === -1 ? -1 : 1) as 1 | -1 } : null;
    const cols = Array.isArray(j.cols) ? strings(j.cols).filter(c => AVAILABLE.includes(c)) : null;
    const gsort = j.gsort === "asc" || j.gsort === "desc" ? j.gsort : "custom";
    return { order: strings(j.order), groups: strings(j.groups), member, folded: strings(j.folded), sort, cols, gorder: strings(j.gorder), gsort };
  } catch { return EMPTY; }
}

function arrange(vms: Vm[], l: Layout) {
  const byName = (a: Vm, b: Vm) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" });
  if (l.sort) {
    const { col, dir } = l.sort;
    const f = col === "name" ? (vm: Vm) => vm.name.toLowerCase() : COLUMNS[col].key;
    return [...vms].sort((a, b) => {
      const x = f(a), y = f(b);
      return (x < y ? -1 : x > y ? 1 : 0) * dir || byName(a, b);
    });
  }
  const at = (vm: Vm) => { const i = l.order.indexOf(vm.id); return i < 0 ? Infinity : i; };
  return [...vms].sort((a, b) => at(a) - at(b) || byName(a, b));
}

// The groups in the order shown (Unassigned comes after them all): A to Z, Z to A, or as arranged by hand (any not
// placed yet come after, A to Z).
function groupOrder(l: Layout) {
  const az = [...l.groups].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: "base" }));
  if (l.gsort === "asc") return az;
  if (l.gsort === "desc") return az.reverse();
  const at = (g: string) => { const i = l.gorder.indexOf(g); return i < 0 ? Infinity : i; };
  return az.sort((a, b) => at(a) - at(b));
}
const groupsByHand = (l: Layout) => l.gsort === "custom" && l.gorder.length > 0;

const groupOf = (l: Layout, id: string) => l.groups.includes(l.member[id]) ? l.member[id] : "";

// The filter's states.
type Show = "all" | "running" | "suspended" | "off";
const SHOWS: [Show, string][] = [["all", "All"], ["running", "Running"], ["suspended", "Suspended"], ["off", "Off"]];
const shows = (vm: Vm, s: Show) => s === "all" || (s === "running" ? on(vm) || vm.state === "held" : vm.state === s);

// The grid both the titles and the rows use: the name, the chosen columns, then the buttons.
const ACTIONS = 146, NAME = 130, GAP = 8;
const grid = (cols: string[]) => `minmax(${NAME}px, 1fr) ${cols.map(c => COLUMNS[c].width + "px").join(" ")} ${ACTIONS}px`;
const tableWidth = (cols: string[]) => NAME + cols.reduce((n, c) => n + COLUMNS[c].width, 0) + ACTIONS + GAP * (cols.length + 1) + 34;

// ---- a row

function CellView({ cell }: { cell: Cell }) {
  return (
    <div className="cell" title={cell.tip}>
      <div className={"value" + (cell.none ? " none" : "") + (cell.color ? " " + cell.color : "")}>{cell.value}</div>
      <div className="sub">{cell.sub || " "}</div>
    </div>
  );
}

const MAC = navigator.userAgent.includes("Macintosh");
// Send Keys: what the computer would otherwise keep for itself.
const KEYS: [string, string, string?][] = [["ctrl-alt-delete", "Ctrl+Alt+Del"], ["ctrl-alt-f1", "Ctrl+Alt+F1", "desktop"],
  ["ctrl-alt-f2", "Ctrl+Alt+F2", "text screen"], ["ctrl-alt-f3", "Ctrl+Alt+F3"], ["ctrl-alt-f4", "Ctrl+Alt+F4"], ["ctrl-alt-f5", "Ctrl+Alt+F5"],
  ["ctrl-alt-f6", "Ctrl+Alt+F6"]];
async function takeScreenshot(vm: Vm) {
  try {
    const path = await screenshot(vm.id);
    toast(`Saved a screenshot of ${vm.name} in Pictures.`, { action: { label: "Show", onClick: () => { reveal(path).catch(problem); } }, duration: 7000 });
  } catch (e) { problem({ title: "Couldn't take a screenshot", text: String(e) }); }
}

function PowerMenu({ vm }: { vm: Vm }) {
  const elsewhere = vm.state === "elsewhere";
  // Keys and screenshots go through QEMU, so they're for a system that's on (not on the Mac, whose framework has neither).
  const extras = !MAC && (vm.state === "running" || vm.state === "paused");
  return (
    <Menu.Root>
      <Menu.Trigger className="btn small go" disabled={elsewhere}
        title={elsewhere ? "Started by another copy of Try Linux" : undefined}>
        Power <span className="chev"><Icon name="chevron-down" /></span>
      </Menu.Trigger>
      <Menu.Portal>
        <Menu.Positioner className="menu-positioner" sideOffset={4} align="start">
          <Menu.Popup className="menu">
            {powerItems(vm).map((item, i) => item === null
              ? <Menu.Separator key={i} className="menu-line" />
              : <Menu.Item key={item.action} className={"menu-item" + (item.danger ? " danger" : "")}
                  onClick={() => item.action === "console" ? call(showConsole(vm.id)) : act(vm, item.action)}>
                  {item.label}
                </Menu.Item>)}
            {extras && <>
              <Menu.Separator className="menu-line" />
              <Menu.SubmenuRoot>
                <Menu.SubmenuTrigger className="menu-item with-keys">
                  <span>Send Keys</span><span className="chev side"><Icon name="chevron-right" /></span>
                </Menu.SubmenuTrigger>
                <Menu.Portal>
                  <Menu.Positioner className="menu-positioner" sideOffset={6} alignOffset={-4}>
                    <Menu.Popup className="menu">
                      {KEYS.map(([k, label, note], i) => (
                        <Fragment key={k}>
                          <Menu.Item className="menu-item with-keys" onClick={() => call(power(vm.id, ("keys:" + k) as PowerAction))}>
                            <span>{label}</span>{note && <span className="menu-keys">{note}</span>}
                          </Menu.Item>
                          {i === 0 && <Menu.Separator className="menu-line" />}
                        </Fragment>))}
                    </Menu.Popup>
                  </Menu.Positioner>
                </Menu.Portal>
              </Menu.SubmenuRoot>
              <Menu.Item className="menu-item" onClick={() => takeScreenshot(vm)}>Take Screenshot</Menu.Item>
            </>}
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}

// The gear: what's done to a system as a whole, then its Settings.
function SystemMenu({ vm, tools, onSettings }: { vm: Vm; tools: Tools; onSettings: (id: string) => void }) {
  return (
    <Menu.Root>
      <Menu.Trigger className="btn small icon" title="Snapshots, clone, delete and settings" aria-label="System menu">
        <Icon name="settings" />
      </Menu.Trigger>
      <Menu.Portal>
        <Menu.Positioner className="menu-positioner" sideOffset={4} align="end">
          <Menu.Popup className="menu">
            <Menu.Item className="menu-item" onClick={() => tools.snapshots(vm)}>Snapshots…</Menu.Item>
            <Menu.Item className="menu-item" onClick={() => tools.clone(vm)}>Clone…</Menu.Item>
            <Menu.Item className="menu-item danger" onClick={() => tools.remove(vm)}>Delete…</Menu.Item>
            <Menu.Separator className="menu-line" />
            <Menu.Item className="menu-item" onClick={() => onSettings(vm.id)}>Settings</Menu.Item>
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}

interface Tools { snapshots: (vm: Vm) => void; clone: (vm: Vm) => void; remove: (vm: Vm) => void; rename: (vm: Vm) => void }

interface RowProps {
  vm: Vm; layout: Layout; cols: string[]; drop: string; dragging: boolean; tools: Tools; match: string;
  onSettings: (id: string) => void; onGrab: (e: React.PointerEvent, id: string) => void;
  onMove: (id: string, group: string) => void; onNewGroup: (id: string) => void;
}

// The name, with what the filter matched marked.
function Marked({ text, match }: { text: string; match: string }) {
  const i = match ? text.toLowerCase().indexOf(match.toLowerCase()) : -1;
  if (i < 0) return <>{text}</>;
  return <>{text.slice(0, i)}<mark>{text.slice(i, i + match.length)}</mark>{text.slice(i + match.length)}</>;
}

function VmRow({ vm, layout, cols, drop, dragging, tools, match, onSettings, onGrab, onMove, onNewGroup }: RowProps) {
  const startIt = () => startSystem(vm);
  const mine = groupOf(layout, vm.id);
  const groups = [...layout.groups].sort((a, b) => a.localeCompare(b));
  return (
    <ContextMenu.Root>
      <ContextMenu.Trigger className={"vm-row" + (drop ? " drop-" + drop : "") + (dragging ? " dragging" : "")} data-row={vm.id}
        style={{ gridTemplateColumns: grid(cols) }}
        onPointerDown={e => onGrab(e, vm.id)}
        onDoubleClick={e => { if (vm.state === "off" && !(e.target as HTMLElement).closest("button")) startIt(); }}>
        <div className="vm-name">
          <div className="title" title={vm.name}><Marked text={vm.name} match={match} /></div>
          <div className="os"><Marked text={vm.os} match={match} /></div>
        </div>
        {cols.map(c => <CellView key={c} cell={COLUMNS[c].cell(vm)} />)}
        <div className="actions">
          <button className="btn small icon" title={on(vm) ? "Show console" : "Open console"} aria-label="Console"
            disabled={vm.state === "elsewhere"} onClick={() => call(showConsole(vm.id))}>
            <Icon name="terminal" />
          </button>
          {vm.state === "off" || vm.state === "suspended"
            ? <button className="btn small primary go" onClick={startIt}>
                {vm.state === "suspended" ? "Resume" : vm.status === "Ready to install" ? "Install" : "Start"}
              </button>
            : <PowerMenu vm={vm} />}
          <SystemMenu vm={vm} tools={tools} onSettings={onSettings} />
        </div>
      </ContextMenu.Trigger>
      <ContextMenu.Portal>
        <ContextMenu.Positioner className="menu-positioner">
          <ContextMenu.Popup className="menu">
            <ContextMenu.Item className="menu-item" onClick={() => tools.rename(vm)}>Rename…</ContextMenu.Item>
            <ContextMenu.Separator className="menu-line" />
            {groups.length > 0 && <>
              <div className="menu-heading">Move {vm.name} to</div>
              {[...groups, ""].map(g => (
                <ContextMenu.Item key={g || "-"} className="menu-item with-keys" disabled={g === mine} onClick={() => onMove(vm.id, g)}>
                  <span>{g || "Unassigned"}</span>{g === mine && <span className="menu-keys">Here</span>}
                </ContextMenu.Item>))}
              <ContextMenu.Separator className="menu-line" />
            </>}
            <ContextMenu.Item className="menu-item" onClick={() => onNewGroup(vm.id)}>
              {groups.length ? "New Group…" : "Move to a New Group…"}
            </ContextMenu.Item>
          </ContextMenu.Popup>
        </ContextMenu.Positioner>
      </ContextMenu.Portal>
    </ContextMenu.Root>
  );
}

// Groups: New Group, collapsing every group or expanding them all, and the groups' order: Ascending, Descending,
// or Default (A to Z). Arranged by hand (dragging their titles), none of these is ticked.
function GroupsMenu({ any, layout, onNew, onFold, onSort }:
  { any: boolean; layout: Layout; onNew: () => void; onFold: (fold: boolean) => void; onSort: (s: "asc" | "desc" | "default") => void }) {
  const isDefault = layout.gsort === "custom" && !groupsByHand(layout);
  const label = layout.gsort === "asc" ? "Ascending" : layout.gsort === "desc" ? "Descending" : "";
  const sortItem = (k: "asc" | "desc" | "default", text: string, on: boolean, icon?: "arrow-up" | "arrow-down") => (
    <Menu.Item className="menu-item with-keys" disabled={!any} onClick={() => onSort(k)}>
      <span className="tick-row"><span className="sort-icon">{icon && <Icon name={icon} />}</span>{text}</span>
      {on && any && <span className="menu-keys">✓</span>}
    </Menu.Item>
  );
  return (
    <Menu.Root>
      <Menu.Trigger className="btn small columns-btn"><Icon name="folders" />Groups{label && <span className="menu-keys">· {label}</span>}
        <span className="chev"><Icon name="chevron-down" /></span></Menu.Trigger>
      <Menu.Portal>
        <Menu.Positioner className="menu-positioner" sideOffset={4} align="end">
          <Menu.Popup className="menu">
            <Menu.Item className="menu-item with-keys" onClick={onNew}><span>New Group…</span><kbd className="menu-keys">Ctrl+G</kbd></Menu.Item>
            <Menu.Separator className="menu-line" />
            <Menu.Item className="menu-item" disabled={!any} onClick={() => onFold(true)}>Collapse</Menu.Item>
            <Menu.Item className="menu-item" disabled={!any} onClick={() => onFold(false)}>Expand</Menu.Item>
            <Menu.Separator className="menu-line" />
            {sortItem("asc", "Ascending", layout.gsort === "asc", "arrow-up")}
            {sortItem("desc", "Descending", layout.gsort === "desc", "arrow-down")}
            {sortItem("default", "Default", isDefault)}
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}

// Columns: show or hide each one, in one list; the menu stays open while ticking.
function ColumnsMenu({ cols, onChange }: { cols: string[]; onChange: (cols: string[] | null) => void }) {
  return (
    <Menu.Root>
      <Menu.Trigger className="btn small columns-btn">Columns <span className="chev"><Icon name="chevron-down" /></span></Menu.Trigger>
      <Menu.Portal>
        <Menu.Positioner className="menu-positioner" sideOffset={4} align="end">
          <Menu.Popup className="menu columns-menu">
            {AVAILABLE.map(c => {
              const shown = cols.includes(c);
              return (
                <Menu.Item key={c} className="menu-item with-keys" closeOnClick={false}
                  onClick={() => onChange(shown ? cols.filter(x => x !== c) : [...cols, c])}>
                  <span className="tick-row"><span className={"tick" + (shown ? " on" : "")}>{shown ? "✓" : ""}</span>{COLUMNS[c].name}</span>
                </Menu.Item>
              );
            })}
            <Menu.Separator className="menu-line" />
            <Menu.Item className="menu-item" onClick={() => onChange(null)}>Default</Menu.Item>
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}

// ---- the page

type Drop = { id: string; where: "before" | "after" } | { group: string } | null;

export function Home({ first, onCreate, onSettings }: { first: Vm[]; onCreate: () => void; onSettings: (id: string) => void }) {
  const [vms, setVms] = useState(first);
  const [lay, setLay] = useState<Layout>(EMPTY);
  const layRef = useRef(lay);
  const [drag, setDrag] = useState<{ id: string; drop: Drop } | null>(null);
  const [colDrag, setColDrag] = useState<{ col: string; to: string | null; after: boolean } | null>(null);
  const [snaps, setSnaps] = useState<Vm | null>(null);
  const [working, setWorking] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const [show, setShow] = useState<Show>("all");
  const filterBox = useRef<HTMLInputElement>(null);

  // Running systems' activity, every two seconds while this page is open; at once when one starts or stops.
  useEffect(() => {
    let alive = true;
    const refresh = () => systems().then(v => alive && setVms(v)).catch(() => {});
    const timer = setInterval(refresh, 2000);
    const unlisten = onSystemsChanged(refresh);
    loadLayout().then(t => { if (alive) { layRef.current = parseLayout(t); setLay(layRef.current); } }).catch(() => {});
    // Ctrl+F: the filter.
    const key = (e: KeyboardEvent) => {
      if (e.ctrlKey && !e.altKey && e.key.toLowerCase() === "f") { e.preventDefault(); filterBox.current?.focus(); filterBox.current?.select(); }
    };
    addEventListener("keydown", key);
    return () => { alive = false; clearInterval(timer); unlisten.then(f => f()); removeEventListener("keydown", key); };
  }, []);

  // A change to the arrangement: shown, and kept.
  const update = (f: (l: Layout) => Layout) => {
    const next = f(layRef.current);
    layRef.current = next;
    setLay(next);
    saveLayout(JSON.stringify(next)).catch(problem);
  };
  const cols = lay.cols ?? DEFAULT_COLUMNS;
  const filtering = filter.trim() !== "" || show !== "all";
  // A system matches by its name, what it runs, or its group's name (so typing a group's name shows its systems).
  const matches = (vm: Vm) => shows(vm, show)
    && [vm.name, vm.os, groupOf(lay, vm.id) || (lay.groups.length ? "Unassigned" : "")].some(t => t.toLowerCase().includes(filter.trim().toLowerCase()));
  const shown = arrange(vms, lay).filter(matches);
  const allIds = () => arrange(vms, layRef.current).map(v => v.id);

  // Group names: not empty, not "Unassigned", and not one already used (ignoring case).
  const check = (current: string) => (n: string) =>
    n.toLowerCase() === "unassigned" ? "That name is kept for systems in no group."
      : layRef.current.groups.some(g => g !== current && g.toLowerCase() === n.toLowerCase()) ? "There's already a group with that name." : null;
  const newGroup = async (moving?: string) => {
    const name = await askText("New Group", moving
      ? "Name the group for " + (vms.find(v => v.id === moving)?.name ?? "this system") + "."
      : "Name the group. Then drag systems into it, or right-click a system to move it.", "Create", "", check(""));
    if (!name) return;
    update(l => ({ ...l, groups: [...l.groups, name], member: moving ? { ...l.member, [moving]: name } : l.member }));
  };
  useEffect(() => { setNewGroup(() => newGroup()); return () => setNewGroup(null); });
  const renameGroup = async (g: string) => {
    const name = await askText("Rename Group", `A new name for ${g}.`, "Rename", g, check(g));
    if (!name || name === g) return;
    update(l => ({
      ...l, groups: l.groups.map(x => x === g ? name : x), folded: l.folded.map(x => x === g ? name : x),
      gorder: l.gorder.map(x => x === g ? name : x),
      member: Object.fromEntries(Object.entries(l.member).map(([k, v]) => [k, v === g ? name : v])),
    }));
  };
  const removeGroup = async (g: string) => {
    if (!(await ask(`Remove ${g}?`, "Its systems move to Unassigned. No system is deleted.", "Remove"))) return;
    update(l => ({
      ...l, groups: l.groups.filter(x => x !== g), folded: l.folded.filter(x => x !== g), gorder: l.gorder.filter(x => x !== g),
      member: Object.fromEntries(Object.entries(l.member).filter(([, v]) => v !== g)),
    }));
  };
  const placeGroups = (next: string[]) => update(l => ({ ...l, gsort: "custom", gorder: next }));
  const moveGroup = (g: string, by: number) => {
    const o = groupOrder(layRef.current), i = o.indexOf(g), j = i + by;
    if (i < 0 || j < 0 || j >= o.length) return;
    [o[i], o[j]] = [o[j], o[i]];
    placeGroups(o);
  };
  // Dragging a group's title (after a few pixels, so a click still collapses or expands it) moves the group.
  const [groupDrag, setGroupDrag] = useState<{ from: string; to: string | null; after: boolean } | null>(null);
  const grabGroup = (e: React.PointerEvent, g: string) => {
    if (e.button !== 0 || !g) return;
    const y0 = e.clientY;
    let started = false, to: string | null = null, after = false;
    const over = (ev: PointerEvent) => {
      if (!started && Math.abs(ev.clientY - y0) < 6) return;
      if (!started) { started = true; document.body.classList.add("dragging-rows"); }
      const el = (document.elementFromPoint(ev.clientX, ev.clientY) as HTMLElement | null)?.closest<HTMLElement>("[data-group-title]");
      if (el && el.dataset.groupTitle) {
        const r = el.getBoundingClientRect();
        to = el.dataset.groupTitle;
        after = ev.clientY > r.top + r.height / 2;
      }
      setGroupDrag({ from: g, to, after });
    };
    const end = () => {
      removeEventListener("pointermove", over);
      removeEventListener("pointerup", end);
      removeEventListener("pointercancel", end);
      document.body.classList.remove("dragging-rows");
      setGroupDrag(null);
      if (!started) return;
      swallowClick();
      if (!to || to === g) return;
      const o = groupOrder(layRef.current).filter(x => x !== g);
      o.splice(o.indexOf(to) + (after ? 1 : 0), 0, g);
      placeGroups(o);
    };
    addEventListener("pointermove", over);
    addEventListener("pointerup", end);
    addEventListener("pointercancel", end);
  };
  const moveTo = (id: string, g: string) => update(l => {
    const member = { ...l.member };
    if (g) member[id] = g; else delete member[id];
    return { ...l, member };
  });
  // The gear menu's tools, and Rename from the right-click menu.
  const tools: Tools = {
    snapshots: vm => setSnaps(vm),
    clone: async vm => {
      if (on(vm)) return ask(`Shut ${vm.name} down first`, "A system is cloned while it's shut down.", null);
      if (!(await ask(`Clone ${vm.name}?`, `This copies ${vm.name}, its disks and its settings, as a new system. Snapshots aren't copied.`, "Clone"))) return;
      setWorking(`Cloning ${vm.name}`);
      try { const msg = await cloneSystem(vm.id); setWorking(null); ask(`Cloned ${vm.name}`, msg, null); }
      catch (e) { setWorking(null); problem({ title: "Couldn't clone it", text: String(e) }); }
    },
    remove: async vm => {
      if (on(vm)) return ask(`Shut ${vm.name} down first`, "A system is deleted while it's shut down.", null);
      if (!(await ask(`Delete ${vm.name}?`, "This permanently removes its virtual disks and everything installed in it.\n\nDownloaded ISOs, and disks added from other folders, are kept.", "Delete", true))) return;
      try { toast(await deleteSystem(vm.id)); } catch (e) { problem({ title: "Couldn't delete it", text: String(e) }); }
    },
    rename: async vm => {
      const name = await askText("Rename", `A new name for ${vm.name}.`, "Rename", vm.name);
      if (name && name !== vm.name) renameSystem(vm.id, name).catch(e => problem({ title: "Couldn't rename it", text: String(e) }));
    },
  };
  const fold = (g: string) => update(l => ({ ...l, folded: l.folded.includes(g) ? l.folded.filter(x => x !== g) : [...l.folded, g] }));
  // Column titles: sort by it, then the other way, then back to the arranged order.
  const sortBy = (col: string) => update(l => ({
    ...l, sort: l.sort?.col !== col ? { col, dir: 1 } : l.sort.dir === 1 ? { col, dir: -1 } : null,
  }));
  const setCols = (next: string[] | null) => update(l => ({ ...l, cols: next }));

  // Dragging a row (after moving a few pixels, so clicks and double-clicks stay clicks): over another row it goes
  // before or after it, in that row's group; over a group's title or empty box, to the end of that group.
  const grab = (e: React.PointerEvent, id: string) => {
    if (e.button !== 0 || (e.target as HTMLElement).closest("button")) return;
    const x0 = e.clientX, y0 = e.clientY;
    let started = false, drop: Drop = null;
    const target = (x: number, y: number): Drop => {
      const el = document.elementFromPoint(x, y) as HTMLElement | null;
      const row = el?.closest<HTMLElement>("[data-row]");
      if (row) {
        if (row.dataset.row === id) return null;
        const r = row.getBoundingClientRect();
        return { id: row.dataset.row!, where: y < r.top + r.height / 2 ? "before" : "after" };
      }
      const g = el?.closest<HTMLElement>("[data-group]");
      return g ? { group: g.dataset.group! } : null;
    };
    const move = (ev: PointerEvent) => {
      if (!started && Math.hypot(ev.clientX - x0, ev.clientY - y0) < 5) return;
      if (!started) { started = true; document.body.classList.add("dragging-rows"); }
      drop = target(ev.clientX, ev.clientY);
      setDrag({ id, drop });
    };
    const end = () => {
      removeEventListener("pointermove", move);
      removeEventListener("pointerup", end);
      removeEventListener("pointercancel", end);
      document.body.classList.remove("dragging-rows");
      setDrag(null);
      if (!started || !drop) return;
      const d = drop;
      update(l => {
        const order = allIds().filter(x => x !== id);
        const member = { ...l.member };
        let g: string;
        if ("group" in d) {
          g = d.group;
          order.push(id);
        } else {
          g = groupOf(l, d.id);
          order.splice(order.indexOf(d.id) + (d.where === "after" ? 1 : 0), 0, id);
        }
        if (g) member[id] = g; else delete member[id];
        return { ...l, order, member, sort: null };
      });
    };
    addEventListener("pointermove", move);
    addEventListener("pointerup", end);
    addEventListener("pointercancel", end);
  };

  // Dragging a column title sideways moves the column; a click without moving sorts by it.
  const grabColumn = (e: React.PointerEvent, col: string) => {
    if (e.button !== 0) return;
    const x0 = e.clientX;
    let started = false, to: string | null = null, after = false;
    const move = (ev: PointerEvent) => {
      if (!started && Math.abs(ev.clientX - x0) < 6) return;
      if (!started) { started = true; document.body.classList.add("dragging-cols"); }
      const el = (document.elementFromPoint(ev.clientX, ev.clientY) as HTMLElement | null)?.closest<HTMLElement>("[data-col]");
      if (el) {
        const r = el.getBoundingClientRect();
        to = el.dataset.col!;
        after = ev.clientX > r.left + r.width / 2;
      }
      setColDrag({ col, to, after });
    };
    const end = () => {
      removeEventListener("pointermove", move);
      removeEventListener("pointerup", end);
      removeEventListener("pointercancel", end);
      document.body.classList.remove("dragging-cols");
      setColDrag(null);
      if (!started) return sortBy(col);
      if (!to || to === col) return;
      const rest = cols.filter(c => c !== col);
      rest.splice(rest.indexOf(to) + (after ? 1 : 0), 0, col);
      setCols(rest);
    };
    addEventListener("pointermove", move);
    addEventListener("pointerup", end);
    addEventListener("pointercancel", end);
  };

  const rowsOf = (list: Vm[]) => list.map(vm => {
    const d = drag?.drop;
    const where = d && "id" in d && d.id === vm.id ? d.where : "";
    return <VmRow key={vm.id} vm={vm} layout={lay} cols={cols} drop={where} dragging={drag?.id === vm.id} tools={tools}
      match={filter.trim()} onSettings={onSettings} onGrab={grab} onMove={moveTo} onNewGroup={id => newGroup(id)} />;
  });
  const into = (g: string) => drag?.drop && "group" in drag.drop && drag.drop.group === g ? " drop-into" : "";

  const arrow = (col: string) => {
    const s = lay.sort?.col === col ? lay.sort.dir : 0;
    return s !== 0 && <span className={"sort-arrow" + (s === 1 ? " up" : "")}><Icon name="chevron-down" /></span>;
  };
  const sortTitle = (col: string, label: string) =>
    lay.sort?.col === col && lay.sort.dir === -1 ? "Back to your own order" : "Sort by " + label.toLowerCase();

  const groups = groupOrder(lay);
  const empty = (
    <div className="card empty-group">
      No systems match.&nbsp;<button className="link-button" onClick={() => { setFilter(""); setShow("all"); }}>Clear the filter</button>
    </div>
  );
  return (
    <Fragment>
      <Header
        left={<button className="btn primary" onClick={onCreate}>Create</button>}
        right={<MainMenu />} />
      <main className="page">
        <div className="column wide home">
          <Logo />
          <div className="vm-tools">
            <div className="filter">
              <span className="filter-icon"><Icon name="search" /></span>
              <input ref={filterBox} className="text-box filter-box" placeholder="Filter by name or group  (Ctrl+F)" value={filter} spellCheck={false}
                onChange={e => setFilter(e.target.value)} onKeyDown={e => { if (e.key === "Escape") setFilter(""); }} aria-label="Filter by name or group" />
            </div>
            <div className="segmented small" role="radiogroup" aria-label="Show">
              {SHOWS.map(([k, label]) => (
                <button key={k} role="radio" aria-checked={show === k} className={show === k ? "on" : ""} onClick={() => setShow(k)}>{label}</button>
              ))}
            </div>
            <GroupsMenu any={lay.groups.length > 0} layout={lay} onNew={() => newGroup()}
              onFold={fold => update(l => ({ ...l, folded: fold ? [...l.groups, ""] : [] }))}
              onSort={k => update(l => k === "default" ? { ...l, gsort: "custom", gorder: [] } : { ...l, gsort: k })} />
            <ColumnsMenu cols={cols} onChange={setCols} />
          </div>
          <div className="vm-table" style={{ minWidth: tableWidth(cols) }}>
            <div className="vm-head" style={{ gridTemplateColumns: grid(cols) }}>
              <button className={"col-label sort" + (lay.sort?.col === "name" ? " on" : "")} onClick={() => sortBy("name")} title={sortTitle("name", "name")}>
                NAME{arrow("name")}
              </button>
              {cols.map(c => (
                <button key={c} data-col={c} title={sortTitle(c, COLUMNS[c].name) + ". Drag to move it."}
                  className={"col-label sort" + (lay.sort?.col === c ? " on" : "") + (colDrag?.col === c ? " moving" : "")
                    + (colDrag?.to === c && colDrag.col !== c ? (colDrag.after ? " drop-after" : " drop-before") : "")}
                  onPointerDown={e => grabColumn(e, c)}
                  onKeyDown={e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); sortBy(c); } }}>
                  {COLUMNS[c].title}{arrow(c)}
                </button>
              ))}
              <span />
            </div>
            {groups.length === 0
              ? shown.length ? <div className="card">{rowsOf(shown)}</div> : vms.length ? empty : null
              : <>
                  {[...groups, ""].map(g => {
                    const all = vms.filter(v => groupOf(lay, v.id) === g);
                    const members = shown.filter(v => groupOf(lay, v.id) === g);
                    if (filtering && members.length === 0) return null;
                    // While filtering, folded groups open, so no match is hidden.
                    const folded = lay.folded.includes(g) && !filtering;
                    return (
                      <Fragment key={g || "-"}>
                        <ContextMenu.Root disabled={!g}>
                          <ContextMenu.Trigger render={<button />} data-group={g} data-group-title={g || undefined}
                            className={"vm-group" + into(g) + (groupDrag?.from === g ? " moving" : "")
                              + (groupDrag?.to === g && groupDrag.from !== g ? (groupDrag.after ? " drop-after" : " drop-before") : "")}
                            aria-expanded={!folded} onClick={() => fold(g)} onPointerDown={e => grabGroup(e, g)}>
                            <span className={"fold" + (folded ? " folded" : "")}><Icon name="chevron-down" /></span>
                            <Icon name="folder" />
                            <span className="name"><Marked text={g || "Unassigned"} match={filter.trim()} /></span>
                            <span className="count">{filtering ? `${members.length} of ${all.length}` : all.length}</span>
                          </ContextMenu.Trigger>
                          <ContextMenu.Portal>
                            <ContextMenu.Positioner className="menu-positioner">
                              <ContextMenu.Popup className="menu">
                                <ContextMenu.Item className="menu-item" disabled={groups.indexOf(g) <= 0} onClick={() => moveGroup(g, -1)}>Move Up</ContextMenu.Item>
                                <ContextMenu.Item className="menu-item" disabled={groups.indexOf(g) >= groups.length - 1} onClick={() => moveGroup(g, 1)}>Move Down</ContextMenu.Item>
                                <ContextMenu.Separator className="menu-line" />
                                <ContextMenu.Item className="menu-item" onClick={() => renameGroup(g)}>Rename Group…</ContextMenu.Item>
                                <ContextMenu.Item className="menu-item danger" onClick={() => removeGroup(g)}>Remove Group</ContextMenu.Item>
                              </ContextMenu.Popup>
                            </ContextMenu.Positioner>
                          </ContextMenu.Portal>
                        </ContextMenu.Root>
                        {!folded && (members.length
                          ? <div className={"card" + into(g)} data-group={g}>{rowsOf(members)}</div>
                          : <div className={"card empty-group" + into(g)} data-group={g}>
                              {g ? "Drag systems here, or right-click one to move it." : "Every system is in a group."}
                            </div>)}
                      </Fragment>
                    );
                  })}
                  {filtering && shown.length === 0 && empty}
                </>}
          </div>
        </div>
      </main>
      <Commands vms={shown} onCreate={onCreate} onNewGroup={() => newGroup()} />
      <SnapshotsDialog open={snaps !== null} id={snaps?.id ?? ""} name={snaps?.name ?? ""} onClose={() => setSnaps(null)} />
      <WorkingDialog open={working !== null} title={working ?? ""} />
    </Fragment>
  );
}
