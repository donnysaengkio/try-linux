// The command menu (Ctrl+K): type a few letters of a system or a command, then Enter. Built with cmdk inside
// a Base UI dialog.
import { Dialog } from "@base-ui/react/dialog";
import { Command } from "cmdk";
import { useEffect, useState } from "react";
import { checkForUpdatesNow, openPreferences, openShortcuts } from "./MainMenu";
import { act, call, powerItems } from "./power";
import { showConsole, start, type Vm } from "./tauri";

export function Commands({ vms, onCreate }: { vms: Vm[]; onCreate: () => void }) {
  const [open, setOpen] = useState(false);

  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); setOpen(o => !o); }
    };
    addEventListener("keydown", key);
    return () => removeEventListener("keydown", key);
  }, []);

  const run = (f: () => unknown) => { setOpen(false); f(); };

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Portal>
        <Dialog.Backdrop className="modal-back" />
        <Dialog.Viewport className="modal-viewport top">
          <Dialog.Popup className="commands" aria-label="Command menu">
            <Command loop>
              <Command.Input className="commands-input" placeholder="Start, stop or open a system…" autoFocus />
              <Command.List className="commands-list">
                <Command.Empty className="commands-empty">Nothing matches.</Command.Empty>
                <Command.Group heading="Try Linux">
                  <Command.Item className="commands-item" onSelect={() => run(onCreate)}>Create a system</Command.Item>
                  <Command.Item className="commands-item" onSelect={() => run(openPreferences)}>Preferences</Command.Item>
                  <Command.Item className="commands-item" onSelect={() => run(openShortcuts)}>Keyboard shortcuts</Command.Item>
                  <Command.Item className="commands-item" onSelect={() => run(checkForUpdatesNow)}>Check for updates</Command.Item>
                </Command.Group>
                {vms.filter(vm => vm.state !== "elsewhere").map(vm => (
                  <Command.Group key={vm.id} heading={vm.name}>
                    {vm.state === "off"
                      ? <>
                          <Command.Item className="commands-item" value={`${vm.name} start`} onSelect={() => run(() => call(start(vm.id)))}>
                            {vm.status === "Ready to install" ? "Install" : "Start"} <span className="commands-of">{vm.name}</span>
                          </Command.Item>
                          <Command.Item className="commands-item" value={`${vm.name} open console`} onSelect={() => run(() => call(showConsole(vm.id)))}>
                            Open console <span className="commands-of">{vm.name}</span>
                          </Command.Item>
                        </>
                      : powerItems(vm).filter(i => i !== null).map(i => (
                          <Command.Item key={i!.action} className={"commands-item" + (i!.danger ? " danger" : "")}
                            value={`${vm.name} ${i!.label}`}
                            onSelect={() => run(() => i!.action === "console" ? call(showConsole(vm.id)) : act(vm, i!.action))}>
                            {i!.label} <span className="commands-of">{vm.name}</span>
                          </Command.Item>
                        ))}
                  </Command.Group>
                ))}
              </Command.List>
            </Command>
          </Dialog.Popup>
        </Dialog.Viewport>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
