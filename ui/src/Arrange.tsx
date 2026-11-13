// Settings' sections in the order you arrange them: drag a section's title up or down, or right-click it for Move
// Up, Move Down and Sort A to Z. One order for each tab, the same for every system, kept with the preferences
// (app.rs, section_order). Sections you haven't placed (new ones) come after, as written.
import { Children, createContext, isValidElement, useContext, useEffect, useState, type ReactElement, type ReactNode } from "react";
import { sectionOrder, setSectionOrder } from "./tauri";

export type Order = Record<string, string[]>;

// What a section's right-click menu can do (Form.tsx's Section asks for it).
export interface Arranging { up?: () => void; down?: () => void; sort?: () => void }
export const ArrangeContext = createContext<(title: string) => Arranging>(() => ({}));

// The saved order, and a way to change it (for the Settings page).
export function useSectionOrder(): [Order, (o: Order) => void] {
  const [order, setOrder] = useState<Order>({});
  useEffect(() => {
    sectionOrder().then(t => { try { setOrder(JSON.parse(t)); } catch { /* the usual order */ } }).catch(() => {});
  }, []);
  const change = (o: Order) => { setOrder(o); setSectionOrder(JSON.stringify(o)).catch(() => {}); };
  return [order, change];
}

const titleOf = (e: ReactElement) => (e.props as { title?: string }).title ?? "";

export function Arranged({ tab, order, onOrder, children }: { tab: string; order: Order; onOrder: (o: Order) => void; children: ReactNode }) {
  const items = Children.toArray(children).filter(isValidElement) as ReactElement[];
  const saved = order[tab] ?? [];
  const rank = (t: string) => { const i = saved.indexOf(t); return i < 0 ? Infinity : i; };
  const arranged = items.map((e, i) => ({ e, i, t: titleOf(e) })).sort((a, b) => rank(a.t) - rank(b.t) || a.i - b.i);
  const titles = arranged.map(a => a.t);
  const [drag, setDrag] = useState<{ from: string; to: string | null; after: boolean } | null>(null);

  const place = (next: string[]) => onOrder({ ...order, [tab]: next });
  const move = (t: string, by: number) => {
    const i = titles.indexOf(t), j = i + by;
    if (i < 0 || j < 0 || j >= titles.length) return;
    const next = [...titles];
    [next[i], next[j]] = [next[j], next[i]];
    place(next);
  };
  const sort = () => place([...titles].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: "base" })));
  const sorted = titles.every((t, i) => i === 0 || titles[i - 1].localeCompare(t, undefined, { sensitivity: "base" }) <= 0);

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
    return { up: i > 0 ? () => move(t, -1) : undefined, down: i < titles.length - 1 ? () => move(t, 1) : undefined, sort: sorted ? undefined : sort };
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
