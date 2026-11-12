import { getCurrentWindow } from "@tauri-apps/api/window";
import type { ReactNode } from "react";
import { Icon } from "./Icon";

// The window has no title bar: this header bar moves it (drag) and maximizes it (double-click).
export function Header({ left, middle, right }: { left?: ReactNode; middle?: ReactNode; right?: ReactNode }) {
  return (
    <header className="headerbar" data-tauri-drag-region>
      <div className="side" data-tauri-drag-region>{left}</div>
      {middle && <div className="middle" data-tauri-drag-region>{middle}</div>}
      <div className="side right" data-tauri-drag-region>
        {right}
        <button className="btn icon" title="Close" aria-label="Close" onClick={() => getCurrentWindow().close()}>
          <Icon name="x" />
        </button>
      </div>
    </header>
  );
}
