// Virtual Machines: one rounded card of rows, with live CPU, memory and disk activity for running systems.
import { Menu } from "@base-ui/react/menu";
import { Fragment, useEffect, useState } from "react";
import { toast } from "sonner";
import { Header } from "./Header";
import { Icon } from "./Icon";
import { Logo } from "./Logo";
import { MainMenu } from "./MainMenu";
import { act, call, powerItems, startSystem } from "./power";
import { onSystemsChanged, showConsole, start, systems, type Vm } from "./tauri";

const MiB = 2 ** 20, GiB = 2 ** 30;

// Live figures as plain text, with the unit that suits their size.
const memory = (bytes: number) => bytes >= GiB ? (bytes / GiB).toFixed(1) + " GB" : Math.round(bytes / MiB) + " MB";
function rate(perSecond: number) {
  if (perSecond < MiB) return (perSecond < 1024 ? 0 : Math.round(perSecond / 1024)) + " KB/s";
  const mb = perSecond / MiB;
  return (mb < 10 ? mb.toFixed(1) : Math.round(mb)) + " MB/s";
}

function Stat({ children, sub }: { children: React.ReactNode; sub?: string }) {
  return <div className="stat"><div className="value">{children}</div>{sub && <div className="sub">{sub}</div>}</div>;
}

function PowerMenu({ vm }: { vm: Vm }) {
  const elsewhere = vm.state === "elsewhere";
  return (
    <Menu.Root>
      <Menu.Trigger className="btn small go" disabled={elsewhere}
        title={elsewhere ? "Started by another copy of Try Linux" : undefined}>
        Power <span className="chev"><Icon name="chevron-down" /></span>
      </Menu.Trigger>
      <Menu.Portal>
        <Menu.Positioner className="menu-positioner" sideOffset={4} align="start">
          <Menu.Popup className="menu">
            {powerItems(vm).map((item, i) => item === null
              ? <Menu.Separator key={i} className="menu-line" />
              : <Menu.Item key={item.action} className={"menu-item" + (item.danger ? " danger" : "")}
                  onClick={() => item.action === "console" ? call(showConsole(vm.id)) : act(vm, item.action)}>
                  {item.label}
                </Menu.Item>)}
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}

function VmRow({ vm, onSettings }: { vm: Vm; onSettings: (id: string) => void }) {
  const cores = vm.cores + (vm.cores === 1 ? " core" : " cores"), ram = vm.ram_gb + " GB";
  const on = vm.state === "running" || vm.state === "paused" || vm.state === "elsewhere";
  const live = on && vm.cpu !== null;
  const startIt = () => startSystem(vm);
  return (
    <div className="vm-row" onDoubleClick={e => {
      if (vm.state === "off" && !(e.target as HTMLElement).closest("button")) startIt();
    }}>
      <div className="vm-name">
        <div className="title" title={vm.name}>{vm.name}</div>
        <div className="os">{vm.os}</div>
      </div>
      <div className={"status" + (on ? " on" : "")}>{vm.status}</div>
      <div className="divider" />
      {live ? <Stat sub={"of " + cores}>{Math.round(vm.cpu!) + "%"}</Stat> : <Stat>{cores}</Stat>}
      {/* What Linux is using once it reports it; until then, what QEMU holds on this PC. */}
      {live ? <Stat sub={"of " + ram}>{memory(vm.guest_mem ?? vm.mem ?? 0)}</Stat> : <Stat>{ram}</Stat>}
      {live && vm.disk_rate !== null ? <Stat sub={vm.disk}>{rate(vm.disk_rate)}</Stat> : <Stat>{vm.disk}</Stat>}
      <div className="actions">
        <button className="btn small icon" title={on ? "Show console" : "Open console"} aria-label="Console"
          disabled={vm.state === "elsewhere"} onClick={() => call(showConsole(vm.id))}>
          <Icon name="terminal" />
        </button>
        {vm.state === "off"
          ? <button className="btn small primary go" onClick={startIt}>{vm.status === "Ready to install" ? "Install" : "Start"}</button>
          : <PowerMenu vm={vm} />}
        <button className="btn small icon" title="Settings" aria-label="Settings"
          onClick={() => onSettings(vm.id)}>
          <Icon name="settings" />
        </button>
      </div>
    </div>
  );
}

export function Home({ first, onCreate, onSettings }: { first: Vm[]; onCreate: () => void; onSettings: (id: string) => void }) {
  const [vms, setVms] = useState(first);

  // Running systems' activity, every two seconds while this page is open; at once when one starts or stops.
  useEffect(() => {
    let alive = true;
    const refresh = () => systems().then(v => alive && setVms(v)).catch(() => {});
    const timer = setInterval(refresh, 2000);
    const unlisten = onSystemsChanged(refresh);
    return () => { alive = false; clearInterval(timer); unlisten.then(f => f()); };
  }, []);

  return (
    <Fragment>
      <Header
        left={<button className="btn primary" onClick={onCreate}>Create</button>}
        right={<MainMenu />} />
      <main className="page">
        <div className="column wide home">
          <Logo />
          <div className="vm-head">
            <h1>Virtual Machines</h1>
            <span className="col-label status">STATUS</span>
            <span className="divider" style={{ visibility: "hidden" }} />
            <span className="col-label stat">CPU</span>
            <span className="col-label stat">MEM</span>
            <span className="col-label stat">DISK</span>
            <span className="actions" />
          </div>
          <div className="card">{vms.map(vm => <VmRow key={vm.id} vm={vm} onSettings={onSettings} />)}</div>
        </div>
      </main>
    </Fragment>
  );
}
