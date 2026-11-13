// Virtual Machines: rows of systems with live CPU, memory and disk activity for running ones. Click a column title
// to sort by it; drag a row to arrange them yourself. Systems can go into named groups (New Group in the main
// menu, Ctrl+G or Ctrl+K); once there's a group, systems in none are under Unassigned. How it's arranged is kept
// with the preferences (app.rs, layout), and the tray menu follows it.
import { ContextMenu } from "@base-ui/react/context-menu";
import { Menu } from "@base-ui/react/menu";
import { Fragment, useEffect, useRef, useState } from "react";
import { ask, askText, problem } from "./Ask";
import { Commands } from "./Commands";
import { Header } from "./Header";
import { Icon } from "./Icon";
import { Logo } from "./Logo";
import { MainMenu, setNewGroup } from "./MainMenu";
import { act, call, powerItems, startSystem } from "./power";
import { layout as loadLayout, onSystemsChanged, setLayout as saveLayout, showConsole, systems, type Vm } from "./tauri";

const MiB = 2 ** 20, GiB = 2 ** 30;

// Live figures as plain text, with the unit that suits their size.
const memory = (bytes: number) => bytes >= GiB ? (bytes / GiB).toFixed(1) + " GB" : Math.round(bytes / MiB) + " MB";
function rate(perSecond: number) {
  if (perSecond < MiB) return (perSecond < 1024 ? 0 : Math.round(perSecond / 1024)) + " KB/s";
  const mb = perSecond / MiB;
  return (mb < 10 ? mb.toFixed(1) : Math.round(mb)) + " MB/s";
}

// ---- how the page is arranged

type Col = "name" | "status" | "cpu" | "mem" | "disk";
interface Layout {
  order: string[];                 // system ids, as arranged by dragging; others come after, A to Z
  groups: string[];                // group names
  member: Record<string, string>;  // system id -> its group
  folded: string[];                // groups folded away ("" for Unassigned)
  sort: { col: Col; dir: 1 | -1 } | null;  // a column to sort by, or null for the arranged order
}
const EMPTY: Layout = { order: [], groups: [], member: {}, folded: [], sort: null };
const COLS: Col[] = ["name", "status", "cpu", "mem", "disk"];

function parseLayout(text: string): Layout {
  try {
    const j = JSON.parse(text);
    const strings = (v: unknown) => Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : [];
    const member: Record<string, string> = {};
    if (j.member && typeof j.member === "object") for (const [k, v] of Object.entries(j.member)) if (typeof v === "string") member[k] = v;
    const sort = j.sort && COLS.includes(j.sort.col) ? { col: j.sort.col as Col, dir: (j.sort.dir === -1 ? -1 : 1) as 1 | -1 } : null;
    return { order: strings(j.order), groups: strings(j.groups), member, folded: strings(j.folded), sort };
  } catch { return EMPTY; }
}

const on = (vm: Vm) => vm.state === "running" || vm.state === "paused" || vm.state === "elsewhere";
const live = (vm: Vm) => on(vm) && vm.cpu !== null;
const STATE_RANK: Record<string, number> = { running: 0, paused: 1, held: 2, elsewhere: 3, off: 4 };
function diskBytes(s: string) {
  const m = /([\d.]+)\s*([KMGT]?)B/i.exec(s);
  return m ? parseFloat(m[1]) * 1024 ** " KMGT".indexOf(m[2].toUpperCase() || " ") : 0;
}
// What each column sorts by: the figure it shows (live ones above the others for CPU and MEM).
const KEY: Record<Col, (vm: Vm) => number | string> = {
  name: vm => vm.name.toLowerCase(),
  status: vm => STATE_RANK[vm.state] ?? 5,
  cpu: vm => live(vm) ? 1000 + vm.cpu! : vm.cores,
  mem: vm => live(vm) ? 1e13 + (vm.guest_mem ?? vm.mem ?? 0) : vm.ram_gb * GiB,
  disk: vm => diskBytes(vm.disk),
};

function arrange(vms: Vm[], l: Layout) {
  const byName = (a: Vm, b: Vm) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" });
  if (l.sort) {
    const { col, dir } = l.sort, f = KEY[col];
    return [...vms].sort((a, b) => {
      const x = f(a), y = f(b);
      return (x < y ? -1 : x > y ? 1 : 0) * dir || byName(a, b);
    });
  }
  const at = (vm: Vm) => { const i = l.order.indexOf(vm.id); return i < 0 ? Infinity : i; };
  return [...vms].sort((a, b) => at(a) - at(b) || byName(a, b));
}

const groupOf = (l: Layout, id: string) => l.groups.includes(l.member[id]) ? l.member[id] : "";

// ---- a row

function Stat({ children, sub }: { children: React.ReactNode; sub?: string }) {
  return <div className="stat"><div className="value">{children}</div>{sub && <div className="sub">{sub}</div>}</div>;
}

function PowerMenu({ vm }: { vm: Vm }) {
  const elsewhere = vm.state === "elsewhere";
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
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}

interface RowProps {
  vm: Vm; layout: Layout; drop: string; dragging: boolean;
  onSettings: (id: string) => void; onGrab: (e: React.PointerEvent, id: string) => void;
  onMove: (id: string, group: string) => void; onNewGroup: (id: string) => void;
}

function VmRow({ vm, layout, drop, dragging, onSettings, onGrab, onMove, onNewGroup }: RowProps) {
  const cores = vm.cores + (vm.cores === 1 ? " core" : " cores"), ram = vm.ram_gb + " GB";
  const running = on(vm), showLive = live(vm);
  const startIt = () => startSystem(vm);
  const mine = groupOf(layout, vm.id);
  const groups = [...layout.groups].sort((a, b) => a.localeCompare(b));
  return (
    <ContextMenu.Root>
      <ContextMenu.Trigger className={"vm-row" + (drop ? " drop-" + drop : "") + (dragging ? " dragging" : "")} data-row={vm.id}
        onPointerDown={e => onGrab(e, vm.id)}
        onDoubleClick={e => { if (vm.state === "off" && !(e.target as HTMLElement).closest("button")) startIt(); }}>
        <div className="vm-name">
          <div className="title" title={vm.name}>{vm.name}</div>
          <div className="os">{vm.os}</div>
        </div>
        <div className={"status" + (running ? " on" : "")}>{vm.status}</div>
        <div className="divider" />
        {showLive ? <Stat sub={"of " + cores}>{Math.round(vm.cpu!) + "%"}</Stat> : <Stat>{cores}</Stat>}
        {/* What Linux is using once it reports it; until then, what QEMU holds on this PC. */}
        {showLive ? <Stat sub={"of " + ram}>{memory(vm.guest_mem ?? vm.mem ?? 0)}</Stat> : <Stat>{ram}</Stat>}
        {showLive && vm.disk_rate !== null ? <Stat sub={vm.disk}>{rate(vm.disk_rate)}</Stat> : <Stat>{vm.disk}</Stat>}
        <div className="actions">
          <button className="btn small icon" title={running ? "Show console" : "Open console"} aria-label="Console"
            disabled={vm.state === "elsewhere"} onClick={() => call(showConsole(vm.id))}>
            <Icon name="terminal" />
          </button>
          {vm.state === "off"
            ? <button className="btn small primary go" onClick={startIt}>{vm.status === "Ready to install" ? "Install" : "Start"}</button>
            : <PowerMenu vm={vm} />}
          <button className="btn small icon" title="Settings" aria-label="Settings" onClick={() => onSettings(vm.id)}>
            <Icon name="settings" />
          </button>
        </div>
      </ContextMenu.Trigger>
      <ContextMenu.Portal>
        <ContextMenu.Positioner className="menu-positioner">
          <ContextMenu.Popup className="menu">
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

// ---- the page

type Drop = { id: string; where: "before" | "after" } | { group: string } | null;

export function Home({ first, onCreate, onSettings }: { first: Vm[]; onCreate: () => void; onSettings: (id: string) => void }) {
  const [vms, setVms] = useState(first);
  const [lay, setLay] = useState<Layout>(EMPTY);
  const layRef = useRef(lay);
  const [drag, setDrag] = useState<{ id: string; drop: Drop } | null>(null);

  // Running systems' activity, every two seconds while this page is open; at once when one starts or stops.
  useEffect(() => {
    let alive = true;
    const refresh = () => systems().then(v => alive && setVms(v)).catch(() => {});
    const timer = setInterval(refresh, 2000);
    const unlisten = onSystemsChanged(refresh);
    loadLayout().then(t => { if (alive) { layRef.current = parseLayout(t); setLay(layRef.current); } }).catch(() => {});
    return () => { alive = false; clearInterval(timer); unlisten.then(f => f()); };
  }, []);

  // A change to the arrangement: shown, and kept.
  const update = (f: (l: Layout) => Layout) => {
    const next = f(layRef.current);
    layRef.current = next;
    setLay(next);
    saveLayout(JSON.stringify(next)).catch(problem);
  };
  const shown = arrange(vms, lay);
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
      member: Object.fromEntries(Object.entries(l.member).map(([k, v]) => [k, v === g ? name : v])),
    }));
  };
  const removeGroup = async (g: string) => {
    if (!(await ask(`Remove ${g}?`, "Its systems move to Unassigned. No system is deleted.", "Remove"))) return;
    update(l => ({
      ...l, groups: l.groups.filter(x => x !== g), folded: l.folded.filter(x => x !== g),
      member: Object.fromEntries(Object.entries(l.member).filter(([, v]) => v !== g)),
    }));
  };
  const moveTo = (id: string, g: string) => update(l => {
    const member = { ...l.member };
    if (g) member[id] = g; else delete member[id];
    return { ...l, member };
  });
  const fold = (g: string) => update(l => ({ ...l, folded: l.folded.includes(g) ? l.folded.filter(x => x !== g) : [...l.folded, g] }));
  // Column titles: sort by it, then the other way, then back to the arranged order.
  const sortBy = (col: Col) => update(l => ({
    ...l, sort: l.sort?.col !== col ? { col, dir: 1 } : l.sort.dir === 1 ? { col, dir: -1 } : null,
  }));

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

  const rowsOf = (list: Vm[]) => list.map(vm => {
    const d = drag?.drop;
    const where = d && "id" in d && d.id === vm.id ? d.where : "";
    return <VmRow key={vm.id} vm={vm} layout={lay} drop={where} dragging={drag?.id === vm.id} onSettings={onSettings}
      onGrab={grab} onMove={moveTo} onNewGroup={id => newGroup(id)} />;
  });
  const into = (g: string) => drag?.drop && "group" in drag.drop && drag.drop.group === g ? " drop-into" : "";

  const title = (col: Col, label: string, cls: string) => {
    const s = lay.sort?.col === col ? lay.sort.dir : 0;
    return (
      <button className={"col-label sort " + cls + (s ? " on" : "")} onClick={() => sortBy(col)}
        aria-sort={s === 1 ? "ascending" : s === -1 ? "descending" : "none"}
        title={s === -1 ? "Back to your own order" : "Sort by " + label.toLowerCase()}>
        {label}{s !== 0 && <span className={"sort-arrow" + (s === 1 ? " up" : "")}><Icon name="chevron-down" /></span>}
      </button>
    );
  };

  const groups = [...lay.groups].sort((a, b) => a.localeCompare(b));
  return (
    <Fragment>
      <Header
        left={<button className="btn primary" onClick={onCreate}>Create</button>}
        right={<MainMenu />} />
      <main className="page">
        <div className="column wide home">
          <Logo />
          <div className="vm-head">
            {title("name", "NAME", "name")}
            {title("status", "STATUS", "status")}
            <span className="divider" style={{ visibility: "hidden" }} />
            {title("cpu", "CPU", "stat")}
            {title("mem", "MEM", "stat")}
            {title("disk", "DISK", "stat")}
            <span className="actions" />
          </div>
          {groups.length === 0
            ? <div className="card">{rowsOf(shown)}</div>
            : [...groups, ""].map(g => {
                const members = shown.filter(v => groupOf(lay, v.id) === g);
                const folded = lay.folded.includes(g);
                return (
                  <Fragment key={g || "-"}>
                    <ContextMenu.Root disabled={!g}>
                      <ContextMenu.Trigger render={<button />} className={"vm-group" + into(g)} data-group={g}
                        aria-expanded={!folded} onClick={() => fold(g)}>
                        <span className={"fold" + (folded ? " folded" : "")}><Icon name="chevron-down" /></span>
                        <Icon name="folder" />
                        <span className="name">{g || "Unassigned"}</span>
                        <span className="count">{members.length}</span>
                      </ContextMenu.Trigger>
                      <ContextMenu.Portal>
                        <ContextMenu.Positioner className="menu-positioner">
                          <ContextMenu.Popup className="menu">
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
        </div>
      </main>
      <Commands vms={shown} onCreate={onCreate} onNewGroup={() => newGroup()} />
    </Fragment>
  );
}

