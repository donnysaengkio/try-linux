// Settings' sections in the order chosen with the sort button beside Search (Ascending, A to Z; Descending, Z to
// A; or Default, the usual order), or as arranged by hand: drag a section's title, or right-click it for Move Up
// and Move Down. The same for every system, kept with the preferences
// (app.rs, section_order). Sections you haven't placed (new ones) come after, as written.
import { Menu } from "@base-ui/react/menu";
import { Children, createContext, isValidElement, useContext, useEffect, useState, type ReactElement, type ReactNode } from "react";
import { Icon } from "./Icon";
import { sectionOrder, setSectionOrder } from "./tauri";

export type Mode = "custom" | "asc" | "desc";
export interface Arrangement {
  order: Record<string, string[]>;  // each tab's sections, as you arranged them
  mode: Mode;                       // "custom": as arranged by hand (or the default order, when nothing is)
}
const EMPTY: Arrangement = { order: {}, mode: "custom" };

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
        setA({ order, mode: j.mode === "asc" || j.mode === "desc" ? j.mode : "custom" });
      } catch { /* the usual order */ }
    }).catch(() => {});
  }, []);
  const change = (next: Arrangement) => { setA(next); setSectionOrder(JSON.stringify(next)).catch(() => {}); };
  return [a, change];
}

// After a drag, the click that ends it (if the computer sends one, straight away) shouldn't also open or close what
// was dragged; a later click is left alone.
export function swallowClick() {
  const stop = (ev: MouseEvent) => { ev.stopPropagation(); ev.preventDefault(); };
  addEventListener("click", stop, { capture: true, once: true });
  setTimeout(() => removeEventListener("click", stop, { capture: true }), 0);
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
      swallowClick();
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

// The sort button beside Search: Ascending, Descending, or Default (the usual order). Sections arranged by hand
// (dragging, or Move Up and Move Down) stay that way, and the button says "Sort" until one of these is chosen.
export function SortMenu({ arrangement, onChange, compact }: { arrangement: Arrangement; onChange: (a: Arrangement) => void; compact?: boolean }) {
  const mode = arrangement.mode;
  const isDefault = mode === "custom" && Object.values(arrangement.order).every(o => !o?.length);
  const label = mode === "asc" ? "Ascending" : mode === "desc" ? "Descending" : isDefault ? "Default" : "Sort";
  const item = (text: string, on: boolean, pick: () => void, icon?: "arrow-up" | "arrow-down") => (
    <Menu.Item className="menu-item with-keys" onClick={pick}>
      <span className="tick-row"><span className="sort-icon">{icon && <Icon name={icon} />}</span>{text}</span>
      {on && <span className="menu-keys">✓</span>}
    </Menu.Item>
  );
  return (
    <Menu.Root>
      {compact
        ? <Menu.Trigger className="tab-tool" title={`Sort: ${label}`} aria-label={`Sort the sections: ${label}`}>
            <Icon name={mode === "asc" ? "arrow-up" : mode === "desc" ? "arrow-down" : "arrow-down-up"} />
          </Menu.Trigger>
        : <Menu.Trigger className="btn small columns-btn sort-btn" title="Sort the sections">
            <Icon name={mode === "asc" ? "arrow-up" : mode === "desc" ? "arrow-down" : "arrow-down-up"} />
            <span className="sort-label">{label}</span>
            <span className="chev"><Icon name="chevron-down" /></span>
          </Menu.Trigger>}
      <Menu.Portal>
        <Menu.Positioner className="menu-positioner" sideOffset={4} align="end">
          <Menu.Popup className="menu sort-menu">
            {item("Ascending", mode === "asc", () => onChange({ ...arrangement, mode: "asc" }), "arrow-up")}
            {item("Descending", mode === "desc", () => onChange({ ...arrangement, mode: "desc" }), "arrow-down")}
            <Menu.Separator className="menu-line" />
            {item("Default", isDefault, () => onChange(EMPTY))}
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}
