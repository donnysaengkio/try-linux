"""The application: its window, the systems and the ones running, and the screens' navigation."""
import os
import threading
import time

from gi.repository import Adw, Gio, GLib

from .. import APP_ID, VERSION, catalog, config, qemu, runner, update
from . import widgets as W


class App(Adw.Application):
    def __init__(self, selftest=None):
        flags = Gio.ApplicationFlags.HANDLES_COMMAND_LINE
        if selftest:
            flags |= Gio.ApplicationFlags.NON_UNIQUE
        super().__init__(application_id=APP_ID + (".SelfTest" if selftest else ""), flags=flags)
        self.selftest = selftest
        self.settings = config.AppSettings.load()
        self.host = qemu.Host()
        self.running = {}          # system id -> runner.RunningVM
        self.systems = []
        self.release = None        # a newer release, when there is one
        self.window = None
        self.nav = None
        self.toasts = None
        self.home = None
        self.listeners = []        # screens to refresh when systems start, stop or change
        power = Gio.SimpleAction.new("power", GLib.VariantType.new("s"))
        power.connect("activate", lambda _a, v: self.power(*v.get_string().split("|", 1)))
        self.add_action(power)
        from . import menu
        menu.install(self)   # the main menu's actions and the keyboard shortcuts

    # ---- start-up

    def do_command_line(self, cmdline):
        args = cmdline.get_arguments()[1:]
        self.activate()
        if "--start" in args:
            i = args.index("--start")
            if i + 1 < len(args):
                c = next((s for s in self.systems if s.id == args[i + 1]), None)
                if c:
                    self.start(c)
        return 0

    def do_activate(self):
        if self.window:
            self.window.present()
            return
        W.apply_theme(self.settings.theme, self.settings.follow_system)
        Adw.StyleManager.get_default().connect("notify::dark", lambda *_: self.follow_theme())
        self.window = Adw.ApplicationWindow(application=self, title="Try Linux", default_width=980, default_height=760)
        self.window.set_size_request(640, 520)
        self.nav = Adw.NavigationView()
        self.toasts = Adw.ToastOverlay(child=self.nav)
        self.window.set_content(self.toasts)
        self.window.connect("close-request", self.on_close)
        self.reload()
        from . import choose, home
        self.home = home.HomePage(self)
        self.nav.push(self.home.page)
        if not self.systems:
            self.nav.push(choose.ChoosePage(self, root=True).page)
        self.window.present()
        if not self.selftest:
            threading.Thread(target=catalog.refresh, daemon=True).start()
            if self.settings.check_updates:
                threading.Thread(target=self.check_updates, daemon=True).start()
        else:
            from .. import selftest
            selftest.run(self)

    def follow_theme(self):
        if self.settings.follow_system:
            W.apply_theme(self.settings.theme, True)
            self.redraw()

    def apply_theme(self):
        W.apply_theme(self.settings.theme, self.settings.follow_system)
        self.redraw()

    def redraw(self):
        if self.window:
            self.window.queue_draw()
            for l in list(self.listeners):
                if hasattr(l, "redraw"):
                    l.redraw()

    def on_close(self, *_):
        live = [self.running[k] for k in self.running if self.running[k].alive]
        if not live or self.selftest:
            return False
        names = ", ".join(v.cfg.display_name for v in live)
        W.ask(self.window, "Quit Try Linux?",
              "%s %s still running. Quitting shuts %s down." % (names, "is" if len(live) == 1 else "are", "it" if len(live) == 1 else "them"),
              "Shut Down and Quit", destructive=True, on_yes=self.quit_all)
        return True

    def quit_all(self):
        for v in list(self.running.values()):
            try:
                v.shut_down()
            except Exception:
                v.force_stop()

        def wait(deadline=time.time() + 60):
            if any(v.alive for v in self.running.values()) and time.time() < deadline:
                return True
            for v in self.running.values():
                if v.alive:
                    v.force_stop()
            self.quit()
            return False

        GLib.timeout_add(500, wait)

    # ---- systems

    def reload(self):
        self.systems = config.systems(self.settings)
        self.changed()

    def changed(self):
        for l in list(self.listeners):
            l.refresh()

    def toast(self, text):
        if self.toasts:
            self.toasts.add_toast(Adw.Toast.new(text))

    def os_label(self, c):
        d = catalog.find(c.distro_id)
        name = os.path.splitext(os.path.basename(c.iso))[0] if c.iso else ""
        if not d:
            return name or ("Custom ISO" if c.pending_install else "Imported disk")
        import re
        cleaned = re.sub(r"(x86_64|amd64|aarch64|arm64|64-?bit)", "", name)
        m = re.search(r"(?<![A-Za-z0-9])[0-9]+(\.[0-9]+)?", cleaned)
        return d.name + (" " + m.group(0) if m else "")

    def status(self, c):
        v = self.running.get(c.id)
        if not v:
            return "Installing" if c.pending_install else ""
        if v.paused:
            return "Paused"
        return "Installing" if v.installer else "Running"

    # ---- running

    def start(self, c, installer=None, iso=None, gl=None):
        """Starts a system: from its installer while it's being installed, otherwise from its disk.
        `iso` starts it once from another ISO (Recovery > Repair)."""
        if c.id in self.running:
            return
        problems = self.host.missing()
        blocking = [p for p in problems if "isn't installed" in p[0]]
        if blocking:
            W.tell(self.window, "Can't start %s" % c.display_name, "\n\n".join("%s\n%s" % p for p in blocking))
            return
        if installer is None:
            installer = c.pending_install and os.path.exists(c.iso)
        if iso:
            c.iso, installer = iso, True
        try:
            v = runner.RunningVM(self.host, c, installer,
                                 changed=lambda vm: W.ui(self.changed),
                                 ended=lambda vm, code: W.ui(self.ended, vm, code), gl=gl)
        except Exception as e:
            W.tell(self.window, "Can't start %s" % c.display_name, str(e))
            return
        v.repair = bool(iso)
        self.running[c.id] = v
        self.changed()

    def ended(self, v, code):
        c = v.cfg
        self.running.pop(c.id, None)
        fresh = config.VMConfig.load(c.dir) or c
        if code == 0 and v.reset:
            # Linux restarted: start it again, from its disk once it has been installed.
            if v.installer and not getattr(v, "repair", False):
                fresh.pending_install = False
            fresh.last_boot = config.now()
            fresh.save()
            self.reload()
            self.start(fresh, installer=False)
            return
        if code == 0 or getattr(v, "forced", False):
            if time.time() - v.started > 20:
                fresh.last_boot = config.now()
                fresh.save()
        elif v.gl and runner.gl_failed(v.err) and time.time() - v.started < 30:
            # 3D acceleration couldn't start on this PC: start it again without, once.
            self.toast("3D acceleration couldn't start, so %s runs without it. Turn it off in Settings > Hardware > Graphics." % c.display_name)
            self.reload()
            self.start(fresh, installer=v.installer, gl=False)
            return
        else:
            W.tell(self.window, "%s stopped" % c.display_name, runner.friendly_error(v.err, c))
        self.reload()

    def power(self, what, sid):
        v = self.running.get(sid)
        if not v:
            return
        name = v.cfg.display_name
        try:
            if what == "shutdown":
                v.shut_down()
                self.toast("Asked %s to shut down" % name)
            elif what == "restart":
                v.restart()
            elif what == "pause":
                v.resume() if v.paused else v.pause()
            elif what == "force":
                def force():
                    v.forced = True
                    v.force_stop()
                W.ask(self.window, "Force %s to stop?" % name,
                      "This turns it off immediately, like unplugging a PC. Unsaved work in Linux is lost.",
                      "Force Stop", destructive=True, on_yes=force)
        except Exception as e:
            W.tell(self.window, name, str(e))
        self.changed()

    # ---- updates

    def check_updates(self, tell_if_none=False):
        try:
            r = update.latest()
        except Exception as e:
            if tell_if_none:
                W.ui(W.tell, self.window, "Couldn't check for updates", str(e))
            return
        if update.newer(r):
            self.release = r
            W.ui(self.changed)
        elif tell_if_none:
            W.ui(W.tell, self.window, "Try Linux is up to date", "You have the newest version, %s." % VERSION)

    def install_update(self):
        r = self.release
        if not r:
            return
        if not update.can_install():
            W.open_uri(self.window, r.url)
            return

        def go():
            def work():
                try:
                    update.install(r)
                except Exception as e:
                    W.ui(W.tell, self.window, "Couldn't update Try Linux", str(e))
                    return
                W.ui(W.ask, self.window, "Try Linux %s is installed" % r.version,
                     "Open the new version now? Running systems keep running.", "Open", "Later", False, update.relaunch)

            threading.Thread(target=work, daemon=True).start()

        W.ask(self.window, "Update to Try Linux %s?" % r.version,
              "Try Linux downloads the update, checks it against its published checksum and puts it in place.",
              "Update", extra=("notes", "What's New", lambda: W.open_uri(self.window, r.url)), on_yes=go)
