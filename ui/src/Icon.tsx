// The shared Lucide icons (the repository's icons/ folder, the same files the Windows, macOS and Linux
// editions use), built into the page.
const files = import.meta.glob("../../icons/*.svg", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const ICONS: Record<string, string> = {};
for (const [path, svg] of Object.entries(files)) {
  const name = path.split("/").pop()!.replace(/\.svg$/, "");
  // Only the <svg> tag's own size and class go (its shapes keep theirs); CSS sizes it.
  ICONS[name] = svg.replace(/<svg[^>]*>/, tag => tag.replace(/\s(width|height|class)="[^"]*"/g, "").replace("<svg", '<svg class="icon" aria-hidden="true"'));
}

export type IconName =
  | "arrow-down" | "arrow-down-up" | "arrow-left" | "arrow-up" | "chevron-down" | "chevron-right" | "disc" | "file-input" | "folder" | "folders" | "globe" | "menu" | "search" | "monitor" | "pencil"
  | "settings" | "terminal" | "undo-2" | "usb" | "wrench" | "x";

export function Icon({ name, size }: { name: IconName; size?: number }) {
  return <span className="icon-box" style={size ? { width: size, height: size } : undefined}
    dangerouslySetInnerHTML={{ __html: ICONS[name] ?? "" }} />;
}
