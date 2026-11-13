// Questions and problems: `ask()` shows one dialog at a time (Base UI's AlertDialog) and resolves true when
// its main action is chosen; `choose()` offers a second action too. <AskHost /> sits once in the app and draws them.
import { AlertDialog } from "@base-ui/react/alert-dialog";
import { useEffect, useRef, useState } from "react";
import type { Problem } from "./tauri";

type Answer = "main" | "alt" | null;
interface Question { title: string; text: string; action: string | null; alt: string | null; danger: boolean; done: (a: Answer) => void;
  input?: string; check?: (v: string) => string | null; typed?: (v: string) => void }
let show: (q: Question) => void = () => {};

export function ask(title: string, text: string, action: string | null, danger = false) {
  return new Promise<boolean>(done => show({ title, text, action, alt: null, danger, done: a => done(a === "main") }));
}

// Two actions and Cancel: resolves "main", "alt", or null for Cancel.
export function choose(title: string, text: string, action: string, alt: string) {
  return new Promise<Answer>(done => show({ title, text, action, alt, danger: false, done }));
}

// A name to type: resolves the text (trimmed), or null for Cancel. `check` returns why a name won't do, or null.
export function askText(title: string, text: string, action: string, initial = "", check?: (v: string) => string | null) {
  return new Promise<string | null>(done => {
    let value = initial;
    show({ title, text, action, alt: null, danger: false, input: initial, check, typed: v => { value = v; },
      done: a => done(a === "main" ? value.trim() : null) });
  });
}

export const problem = (p: Problem | unknown) => {
  const q = (p && typeof p === "object" ? p : {}) as Problem;
  return ask(q.title || "Something went wrong", q.text || String(p), null);
};

export function AskHost() {
  const [q, setQ] = useState<Question | null>(null);
  const [open, setOpen] = useState(false);
  const main = useRef<HTMLButtonElement>(null);
  const field = useRef<HTMLInputElement>(null);
  const answered = useRef(false);
  const [value, setValue] = useState("");

  useEffect(() => {
    show = next => { answered.current = false; setQ(next); setValue(next.input ?? ""); setOpen(true); };
    return () => { show = () => {}; };
  }, []);

  // A name to type: the main action waits for one that will do ("" for an empty name, which needs no message).
  const why = q?.input !== undefined ? (value.trim() === "" ? "" : q.check?.(value.trim()) ?? null) : null;
  const close = (a: Answer) => {
    if (a === "main" && why !== null) return;
    if (q && !answered.current) { answered.current = true; q.done(a); }
    setOpen(false);
  };

  return (
    <AlertDialog.Root open={open} onOpenChange={o => { if (!o) close(null); }}>
      <AlertDialog.Portal>
        <AlertDialog.Backdrop className="modal-back" />
        <AlertDialog.Viewport className="modal-viewport">
          <AlertDialog.Popup className="modal" initialFocus={q?.input !== undefined ? field : main}>
            <AlertDialog.Title className="modal-title">{q?.title}</AlertDialog.Title>
            <AlertDialog.Description className="modal-text">{q?.text}</AlertDialog.Description>
            {q?.input !== undefined && <>
              <input ref={field} className="text-box modal-field" value={value} maxLength={40} spellCheck={false}
                aria-label={q.title} onFocus={e => e.target.select()}
                onChange={e => { setValue(e.target.value); q.typed?.(e.target.value); }}
                onKeyDown={e => { if (e.key === "Enter") { e.preventDefault(); close("main"); } }} />
              {why && <div className="field-error">{why}</div>}
            </>}
            <div className="modal-buttons">
              {q?.action && <button className="btn" onClick={() => close(null)}>Cancel</button>}
              {q?.alt && <button className="btn" onClick={() => close("alt")}>{q.alt}</button>}
              <button ref={main} className={"btn " + (q?.danger ? "danger" : "primary")} disabled={why !== null} onClick={() => close("main")}>
                {q?.action || "OK"}
              </button>
            </div>
          </AlertDialog.Popup>
        </AlertDialog.Viewport>
      </AlertDialog.Portal>
    </AlertDialog.Root>
  );
}
