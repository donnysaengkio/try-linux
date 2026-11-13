// Settings' sections in the order chosen with the sort button beside Search: your own order (arranged by
// dragging a section's title, or right-clicking it for Move Up and Move Down; you can rename it), Ascending (A to
// Z), Descending (Z to A), or Default (the usual order). The same for every system, kept with the preferences
// (app.rs, section_order). Sections you haven't placed (new ones) come after, as written.
import { Menu } from "@base-ui/react/menu";
import { Children, createContext, isValidElement, useContext, useEffect, useState, type ReactElement, type ReactNode } from "react";
import { askText } from "./Ask";
import { Icon } from "./Icon";
import { sectionOrder, setSectionOrder } from "./tauri";

export type Mode = "custom" | "asc" | "desc";
export interface Arrangement {
  order: Record<string, string[]>;  // each tab's sections, as you arranged them
  mode: Mode;
  name: string;                     // what your own order is called ("" for "Your order")
}
const EMPTY: Arrangement = { order: {}, mode: "custom", name: "" };
export const customName = (a: Arrangement) => a.name || "Your order";

// What a section's right-click menu can do (Form.tsx's Section asks for it).
export interface Arranging { up?: () => void; down?: () => void }
export const ArrangeContext = createContext<(title: string) => Arranging>(() => ({}));

// The saved arrangement, and a way to change it (for the Settings page).
export function useArrangement(): [Arrangement, (a: Arrangement) => void] {
  const [a, setA] = useState<Arrangement>(EMPTY);
  useEffect(() => {
    sectionOrder().then(t => {
      try {
        const j = JSON.parse(t);
        const order = j.order && typeof j.order === "object" ? j.order : {};
        setA({ order, mode: j.mode === "asc" || j.mode === "desc" ? j.mode : "custom", name: typeof j.name === "string" ? j.name : "" });
      } catch { /* the usual order */ }
    }).catch(() => {});
  }, []);
  const change = (next: Arrangement) => { setA(next); setSectionOrder(JSON.stringify(next)).catch(() => {}); };
  return [a, change];
}

const titleOf = (e: ReactElement) => (e.props as { title?: string }).title ?? "";
const byName = (x: string, y: string) => x.localeCompare(y, undefined, { sensitivity: "base" });

export function Arranged({ tab, arrangement, onChange, children }:
  { tab: string; arrangement: Arrangement; onChange: (a: Arrangement) => void; children: ReactNode }) {
  const items = Children.toArray(children).filter(isValidElement) as ReactElement[];
  const saved = arrangement.order[tab] ?? [];
  const rank = (t: string) => { const i = saved.indexOf(t); return i < 0 ? Infinity : i; };
  const mine = items.map((e, i) => ({ e, i, t: titleOf(e) })).sort((a, b) => rank(a.t) - rank(b.t) || a.i - b.i);
  const arranged = arrangement.mode === "custom" ? mine
    : [...mine].sort((a, b) => byName(a.t, b.t) * (arrangement.mode === "asc" ? 1 : -1));
  const titles = arranged.map(a => a.t);
  const [drag, setDrag] = useState<{ from: string; to: string | null; after: boolean } | null>(null);

  // Moving a section makes the shown order your own.
  const place = (next: string[]) => onChange({ ...arrangement, mode: "custom", order: { ...arrangement.order, [tab]: next } });
  const move = (t: string, by: number) => {
    const i = titles.indexOf(t), j = i + by;
    if (i < 0 || j < 0 || j >= titles.length) return;
    const next = [...titles];
    [next[i], next[j]] = [next[j], next[i]];
    place(next);
  };

  // Dragging a section's title bar (after a few pixels, so a click still opens and closes it).
  const grab = (e: React.PointerEvent) => {
    const bar = (e.target as HTMLElement).closest(".section-bar");
    const item = (e.target as HTMLElement).closest<HTMLElement>("[data-section]");
    if (e.button !== 0 || !bar || !item) return;
    const from = item.dataset.section!, y0 = e.clientY;
    let started = false, to: string | null = null, after = false;
    const over = (ev: PointerEvent) => {
      if (!started && Math.abs(ev.clientY - y0) < 6) return;
      if (!started) { started = true; document.body.classList.add("dragging-rows"); }
      const el = (document.elementFromPoint(ev.clientX, ev.clientY) as HTMLElement | null)?.closest<HTMLElement>("[data-section]");
      if (el) {
        const r = el.getBoundingClientRect();
        to = el.dataset.section!;
        after = ev.clientY > r.top + Math.min(r.height, 44) / 2;
      }
      setDrag({ from, to, after });
    };
    const end = () => {
      removeEventListener("pointermove", over);
      removeEventListener("pointerup", end);
      removeEventListener("pointercancel", end);
      document.body.classList.remove("dragging-rows");
      setDrag(null);
      if (!started) return;
      // The click that ends a drag doesn't open or close the section.
      addEventListener("click", ev => { ev.stopPropagation(); ev.preventDefault(); }, { capture: true, once: true });
      if (!to || to === from) return;
      const next = titles.filter(t => t !== from);
      next.splice(next.indexOf(to) + (after ? 1 : 0), 0, from);
      place(next);
    };
    addEventListener("pointermove", over);
    addEventListener("pointerup", end);
    addEventListener("pointercancel", end);
  };

  const menu = (t: string): Arranging => {
    const i = titles.indexOf(t);
    return { up: i > 0 ? () => move(t, -1) : undefined, down: i < titles.length - 1 ? () => move(t, 1) : undefined };
  };

  return (
    <ArrangeContext.Provider value={menu}>
      {arranged.map(({ e, t }) => (
        <div key={t} data-section={t} onPointerDown={grab}
          className={"arranged" + (drag?.from === t ? " moving" : "") + (drag?.to === t && drag.from !== t ? (drag.after ? " drop-after" : " drop-before") : "")}>
          {e}
        </div>
      ))}
    </ArrangeContext.Provider>
  );
}

// For Section: its right-click menu's actions.
export const useArranging = (title: string) => useContext(ArrangeContext)(title);

// The sort button beside Search: the order shown, and a menu to choose another, rename your own, or go back to
// the default.
export function SortMenu({ arrangement, onChange }: { arrangement: Arrangement; onChange: (a: Arrangement) => void }) {
  const mode = arrangement.mode;
  // Nothing arranged yet: the sections are in their default order.
  const isDefault = mode === "custom" && Object.values(arrangement.order).every(o => !o?.length);
  const label = mode === "asc" ? "Ascending" : mode === "desc" ? "Descending" : isDefault ? "Default" : customName(arrangement);
  const rename = async () => {
    const name = await askText(`Rename “${customName(arrangement)}”`, "A name for the order you arrange by dragging sections.", "Rename", customName(arrangement));
    if (name) onChange({ ...arrangement, name: name === "Your order" ? "" : name });
  };
  const item = (m: Mode, text: string, icon?: "arrow-up" | "arrow-down") => (
    <Menu.Item className="menu-item with-keys" onClick={() => onChange({ ...arrangement, mode: m })}>
      <span className="tick-row">{icon ? <span className="sort-icon"><Icon name={icon} /></span> : <span className="sort-icon" />}{text}</span>
      {mode === m && !(m === "custom" && isDefault) && <span className="menu-keys">✓</span>}
    </Menu.Item>
  );
  return (
    <Menu.Root>
      <Menu.Trigger className="btn small columns-btn sort-btn" title="Sort the sections">
        <Icon name={mode === "asc" ? "arrow-up" : mode === "desc" ? "arrow-down" : "arrow-down-up"} />
        <span className="sort-label">{label}</span>
        <span className="chev"><Icon name="chevron-down" /></span>
      </Menu.Trigger>
      <Menu.Portal>
        <Menu.Positioner className="menu-positioner" sideOffset={4} align="end">
          <Menu.Popup className="menu sort-menu">
            {item("custom", customName(arrangement))}
            {item("asc", "Ascending", "arrow-up")}
            {item("desc", "Descending", "arrow-down")}
            <Menu.Separator className="menu-line" />
            <Menu.Item className="menu-item" onClick={rename}>
              <span className="tick-row"><span className="sort-icon" />Rename “{customName(arrangement)}”…</span>
            </Menu.Item>
            <Menu.Item className="menu-item with-keys" onClick={() => onChange({ ...EMPTY, name: arrangement.name })}>
              <span className="tick-row"><span className="sort-icon" />Default</span>{isDefault && <span className="menu-keys">✓</span>}
            </Menu.Item>
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}
