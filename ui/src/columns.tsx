// Virtual Machines' columns: what each one shows (a figure, and a line under it saying what it is), how wide it
// is, and what it sorts by. Columns menu shows or hides them; the ones marked `agent` come from QEMU's guest agent
// in Linux (qemu-guest-agent), the rest from Try Linux and QEMU.
import type { Vm } from "./tauri";

const MiB = 2 ** 20, GiB = 2 ** 30;
const MAC = navigator.userAgent.includes("Macintosh");

export const memory = (bytes: number) => bytes >= GiB ? (bytes / GiB).toFixed(1) + " GB" : Math.round(bytes / MiB) + " MB";
export function rate(perSecond: number) {
  if (perSecond < MiB) return (perSecond < 1024 ? 0 : Math.round(perSecond / 1024)) + " KB/s";
  const mb = perSecond / MiB;
  return (mb < 10 ? mb.toFixed(1) : Math.round(mb)) + " MB/s";
}
const total = (bytes: number) => bytes >= GiB ? (bytes / GiB).toFixed(1) + " GB" : bytes >= MiB ? Math.round(bytes / MiB) + " MB" : Math.round(bytes / 1024) + " KB";
function duration(secs: number) {
  const m = Math.floor(secs / 60), h = Math.floor(m / 60), d = Math.floor(h / 24);
  return d >= 2 ? `${d} days` : h ? `${h} h ${m % 60} min` : m ? `${m} min` : "< 1 min";
}
// "2026-11-12 22:29" as "Nov 12" (or "Today"), and its time or year.
function when(text: string): [string, string] {
  const t = new Date(text.replace(" ", "T"));
  if (!text || isNaN(+t)) return ["–", ""];
  const today = new Date(), yesterday = new Date(Date.now() - 864e5);
  const same = (a: Date, b: Date) => a.toDateString() === b.toDateString();
  const day = same(t, today) ? "Today" : same(t, yesterday) ? "Yesterday" : t.toLocaleDateString(undefined, { month: "short", day: "numeric" });
  return [day, t.getFullYear() !== today.getFullYear() ? String(t.getFullYear()) : t.toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" })];
}
function diskBytes(s: string) {
  const m = /([\d.]+)\s*([KMGT]?)B/i.exec(s);
  return m ? parseFloat(m[1]) * 1024 ** " KMGT".indexOf(m[2].toUpperCase() || " ") : 0;
}

export const on = (vm: Vm) => vm.state === "running" || vm.state === "paused" || vm.state === "elsewhere";
const live = (vm: Vm) => on(vm) && vm.cpu !== null;
const NONE: Cell = { value: "–", none: true };
// A column the guest agent fills: its figure, or why there's none.
const agent = (vm: Vm, value: string | null | undefined, sub: string): Cell =>
  value ? { value, sub } : vm.agent === false ? { value: "–", sub: "no agent", none: true, tip: AGENT_TIP } : NONE;
export const AGENT_TIP = "Install qemu-guest-agent in Linux (and start it) to see this.";

export interface Cell { value: string; sub?: string; color?: "on" | "accent" | "dim"; none?: boolean; tip?: string }
export interface Column {
  title: string;              // in capitals, as the titles are shown
  name: string;               // in the Columns menu
  width: number;
  group: "Activity" | "Linux" | "Settings";
  agent?: boolean;            // needs the guest agent
  cell: (vm: Vm) => Cell;
  key: (vm: Vm) => number | string;
  mac?: false;                // not on the Mac
}

const STATE_RANK: Record<string, number> = { running: 0, paused: 1, suspended: 2, held: 3, elsewhere: 4, off: 5 };

export const COLUMNS: Record<string, Column> = {
  status: {
    title: "STATUS", name: "Status", width: 86, group: "Activity",
    cell: vm => {
      const [first, rest] = vm.status === "Ready to install" ? ["Ready", "to install"] : vm.status.split(", ");
      return { value: first, sub: rest ?? (vm.uptime != null ? "for " + duration(vm.uptime) : vm.maybe_installed ? "may be installed" : undefined),
        color: on(vm) ? "on" : vm.state === "suspended" ? "accent" : "dim" };
    },
    key: vm => STATE_RANK[vm.state] ?? 6,
  },
  cpu: {
    title: "CPU", name: "CPU", width: 70, group: "Activity",
    cell: vm => live(vm) ? { value: Math.round(vm.cpu!) + "%", sub: `of ${vm.cores} ${vm.cores === 1 ? "core" : "cores"}` }
      : { value: `${vm.cores} ${vm.cores === 1 ? "core" : "cores"}`, sub: "assigned" },
    key: vm => live(vm) ? 1000 + vm.cpu! : vm.cores,
  },
  mem: {
    title: "MEM", name: "Memory", width: 70, group: "Activity",
    // What Linux is using once it reports it; until then, what QEMU holds on this computer.
    cell: vm => live(vm) ? { value: memory(vm.guest_mem ?? vm.mem ?? 0), sub: `of ${vm.ram_gb} GB` } : { value: vm.ram_gb + " GB", sub: "assigned" },
    key: vm => live(vm) ? 1e13 + (vm.guest_mem ?? vm.mem ?? 0) : vm.ram_gb * GiB,
  },
  disk: {
    title: "DISK", name: "Disk", width: 74, group: "Activity",
    cell: vm => live(vm) && vm.disk_rate !== null ? { value: rate(vm.disk_rate), sub: vm.disk } : { value: vm.disk, sub: "on disk" },
    key: vm => diskBytes(vm.disk),
  },
  net: {
    title: "NET", name: "Network traffic", width: 74, group: "Activity", agent: true, mac: false,
    cell: vm => vm.net_total != null ? { value: vm.net_rate != null ? rate(vm.net_rate) : "…", sub: total(vm.net_total) + " total" } : agent(vm, null, ""),
    key: vm => vm.net_rate ?? -1,
  },
  ip: {
    title: "IP", name: "IP address", width: 98, group: "Linux", agent: !MAC,
    cell: vm => agent(vm, vm.ip, vm.net_mode === "NAT" ? "shared (NAT)" : vm.net_mode.toLowerCase()),
    key: vm => (vm.ip ?? "~").split(".").map(n => n.padStart(3, "0")).join("."),
  },
  uptime: {
    title: "UPTIME", name: "Uptime", width: 80, group: "Linux",
    cell: vm => vm.uptime != null ? { value: duration(vm.uptime), sub: "running" } : NONE,
    key: vm => vm.uptime ?? -1,
  },
  host: {
    title: "HOSTNAME", name: "Hostname", width: 92, group: "Linux", agent: true, mac: false,
    cell: vm => agent(vm, vm.host, "hostname"),
    key: vm => vm.host ?? "~",
  },
  linux: {
    title: "LINUX", name: "Linux version", width: 120, group: "Linux", agent: true, mac: false,
    cell: vm => agent(vm, vm.linux, vm.kernel ? "kernel " + vm.kernel : ""),
    key: vm => vm.linux ?? "~",
  },
  snapshots: {
    title: "SNAPSHOTS", name: "Snapshots", width: 82, group: "Settings", mac: false,
    cell: vm => vm.snapshots == null ? NONE : { value: String(vm.snapshots), sub: vm.snapshots === 1 ? "snapshot" : "snapshots" },
    key: vm => vm.snapshots ?? -1,
  },
  firmware: {
    title: "FIRMWARE", name: "Firmware", width: 78, group: "Settings",
    cell: vm => ({ value: vm.firmware, sub: "startup" }),
    key: vm => vm.firmware,
  },
  graphics: {
    title: "GRAPHICS", name: "Graphics", width: 84, group: "Settings",
    cell: vm => ({ value: vm.graphics, sub: "rendering" }),
    key: vm => vm.graphics,
  },
  netmode: {
    title: "NET MODE", name: "Network mode", width: 96, group: "Settings",
    cell: vm => ({ value: vm.net_mode, sub: vm.net_mode === "NAT" ? "internet" : vm.net_mode === "None" ? "offline" : "no internet" }),
    key: vm => vm.net_mode,
  },
  created: {
    title: "CREATED", name: "Created", width: 80, group: "Settings",
    cell: vm => { const [a, b] = when(vm.created); return { value: a, sub: b, none: a === "–" }; },
    key: vm => vm.created || "~",
  },
  lastrun: {
    title: "LAST RUN", name: "Last run", width: 80, group: "Settings",
    cell: vm => { const [a, b] = when(vm.last_run); return { value: a, sub: b, none: a === "–" }; },
    key: vm => vm.last_run || "",
  },
};

export const DEFAULT_COLUMNS = ["status", "cpu", "mem", "disk", "ip", "net"].filter(c => COLUMNS[c].mac !== false || !MAC);
export const AVAILABLE = Object.keys(COLUMNS).filter(c => COLUMNS[c].mac !== false || !MAC);
