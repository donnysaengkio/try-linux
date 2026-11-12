// Questions and problems: `ask()` shows one dialog at a time (Base UI's AlertDialog) and resolves true when
// its main action is chosen; `choose()` offers a second action too. <AskHost /> sits once in the app and draws them.
import { AlertDialog } from "@base-ui/react/alert-dialog";
import { useEffect, useRef, useState } from "react";
import type { Problem } from "./tauri";

type Answer = "main" | "alt" | null;
interface Question { title: string; text: string; action: string | null; alt: string | null; danger: boolean; done: (a: Answer) => void }
let show: (q: Question) => void = () => {};

export function ask(title: string, text: string, action: string | null, danger = false) {
  return new Promise<boolean>(done => show({ title, text, action, alt: null, danger, done: a => done(a === "main") }));
}

// Two actions and Cancel: resolves "main", "alt", or null for Cancel.
export function choose(title: string, text: string, action: string, alt: string) {
  return new Promise<Answer>(done => show({ title, text, action, alt, danger: false, done }));
}

export const problem = (p: Problem | unknown) => {
  const q = (p && typeof p === "object" ? p : {}) as Problem;
  return ask(q.title || "Something went wrong", q.text || String(p), null);
};

export function AskHost() {
  const [q, setQ] = useState<Question | null>(null);
  const [open, setOpen] = useState(false);
  const main = useRef<HTMLButtonElement>(null);
  const answered = useRef(false);

  useEffect(() => {
    show = next => { answered.current = false; setQ(next); setOpen(true); };
    return () => { show = () => {}; };
  }, []);

  const close = (a: Answer) => {
    if (q && !answered.current) { answered.current = true; q.done(a); }
    setOpen(false);
  };

  return (
    <AlertDialog.Root open={open} onOpenChange={o => { if (!o) close(null); }}>
      <AlertDialog.Portal>
        <AlertDialog.Backdrop className="modal-back" />
        <AlertDialog.Viewport className="modal-viewport">
          <AlertDialog.Popup className="modal" initialFocus={main}>
            <AlertDialog.Title className="modal-title">{q?.title}</AlertDialog.Title>
            <AlertDialog.Description className="modal-text">{q?.text}</AlertDialog.Description>
            <div className="modal-buttons">
              {q?.action && <button className="btn" onClick={() => close(null)}>Cancel</button>}
              {q?.alt && <button className="btn" onClick={() => close("alt")}>{q.alt}</button>}
              <button ref={main} className={"btn " + (q?.danger ? "danger" : "primary")} onClick={() => close("main")}>
                {q?.action || "OK"}
              </button>
            </div>
          </AlertDialog.Popup>
        </AlertDialog.Viewport>
      </AlertDialog.Portal>
    </AlertDialog.Root>
  );
}
