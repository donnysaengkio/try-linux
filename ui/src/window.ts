// Linux: the window rounds its own corners (style.css, ".round"), except when it fills the screen or is snapped
// to a side, where square corners meet the screen's edges. Windows and macOS round windows themselves.
import { currentMonitor, getCurrentWindow } from "@tauri-apps/api/window";

const LINUX = navigator.userAgent.includes("Linux") && !navigator.userAgent.includes("Android");

export function roundCorners() {
  if (!LINUX) return;
  const root = document.documentElement;
  root.classList.add("round");
  const w = getCurrentWindow();
  let pending = 0;
  const update = async () => {
    try {
      const [max, full, pos, size, mon] = await Promise.all([
        w.isMaximized(), w.isFullscreen(), w.outerPosition(), w.outerSize(), currentMonitor()]);
      let square = max || full;
      if (!square && mon) {
        // Snapped (tiled): the window spans the whole height or width of the screen's work area.
        const a = mon.workArea, near = (x: number, y: number) => Math.abs(x - y) <= 4;
        square = (near(pos.y, a.position.y) && near(pos.y + size.height, a.position.y + a.size.height))
          || (near(pos.x, a.position.x) && near(pos.x + size.width, a.position.x + a.size.width));
      }
      root.classList.toggle("square", square);
    } catch { /* keep the corners as they are */ }
  };
  const soon = () => { clearTimeout(pending); pending = window.setTimeout(update, 80); };
  update();
  w.onResized(soon);
  w.onMoved(soon);
}
