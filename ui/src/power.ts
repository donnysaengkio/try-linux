// The power commands, and the ones that ask first (from the Power menu or the tray), as on
// the Windows edition: Shut down and Restart ask Linux; Force stop and Reset lose unsaved work.
import { ask, choose, problem } from "./Ask";
import { markInstalled, power, start, type PowerAction, type Vm } from "./tauri";

const CONFIRM: Partial<Record<PowerAction, (name: string) => [string, string, string, boolean]>> = {
  shutdown: n => [`Shut down ${n}?`, "Linux shuts down cleanly. Its desktop may ask first; GNOME goes ahead on its own after 60 seconds.", "Shut down", false],
  restart: n => [`Restart ${n}?`, "Linux shuts down cleanly, then starts again with any settings you changed. Its desktop may ask first; GNOME goes ahead on its own after 60 seconds.", "Restart", false],
  force: n => [`Force ${n} to stop?`, "This turns it off immediately, like unplugging a PC. Unsaved work in Linux is lost.", "Force stop", true],
  reset: n => [`Reset ${n}?`, "This restarts it immediately, like a PC's reset button. Unsaved work in Linux is lost.", "Reset", true],
};

// Runs a command (or a Rust-side call) and shows what went wrong, if anything.
export const call = (p: Promise<unknown>) => p.catch(problem);

export async function act(vm: Pick<Vm, "id" | "name">, action: PowerAction) {
  const q = CONFIRM[action];
  if (q && !(await ask(...q(vm.name)))) return;
  call(power(vm.id, action));
}

export interface PowerItem { label: string; action: PowerAction | "console"; danger?: boolean }

// What the Power menu offers for a system in this state; null marks a line between groups.
export function powerItems(vm: Vm): (PowerItem | null)[] {
  if (vm.state === "held") return [{ label: "Power on", action: "power-on" }, { label: "Close console", action: "close-console" }];
  return [
    { label: "Show console", action: "console" },
    null,
    { label: "Shut down", action: "shutdown" },
    { label: "Restart", action: "restart" },
    vm.state === "paused" ? { label: "Resume", action: "resume" } : { label: "Pause", action: "pause" },
    null,
    { label: "Force stop", action: "force", danger: true },
    { label: "Reset", action: "reset", danger: true },
  ];
}

// Start (or Install). A system still waiting to install whose disk already holds several GB was most likely
// installed while Try Linux was closed (so nothing marked it installed): ask whether to start it from its disk.
export async function startSystem(vm: Vm) {
  if (vm.maybe_installed) {
    const a = await choose(`Is ${vm.name} already installed?`,
      `Linux has already written ${vm.disk} to its disk, so the install probably finished while Try Linux was closed.\n\n` +
      "Start it from its disk, or run the installer again (which can erase what's installed).",
      "Start from disk", "Run installer");
    if (a === null) return;
    if (a === "main") {
      try { await markInstalled(vm.id); } catch (e) { return problem(e); }
    }
  }
  call(start(vm.id));
}
