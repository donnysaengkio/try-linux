"""Virtual Machines: every system, whether it's running, its CPU, memory and disk, and its commands."""
from gi.repository import Adw, Gio, Gtk

from .. import qemu
from . import widgets as W


class HomePage:
    def __init__(self, app):
        self.app = app
        create = W.button("Create", lambda: self.create(), primary=True)
        menu = Gio.Menu()
        menu.append("Check for Updates", "app.check-updates")
        menu.append("About Try Linux", "app.about")
        more = Gtk.MenuButton(icon_name="open-menu-symbolic", menu_model=menu, tooltip_text="Menu")
        for name, fn in (("check-updates", lambda *_: __import__("threading").Thread(
                target=app.check_updates, args=(True,), daemon=True).start()),
                         ("about", lambda *_: self.about())):
            a = Gio.SimpleAction.new(name, None)
            a.connect("activate", fn)
            app.add_action(a)

        self.notices = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        self.list = Gtk.ListBox(selection_mode=Gtk.SelectionMode.NONE)
        self.list.add_css_class("boxed-list")
        self.list.connect("row-activated", lambda _l, row: self.open(row.cfg))
        self.empty = Adw.StatusPage(title="No virtual machines yet", description="Create one to try a Linux distribution.",
                                    icon_name="computer-symbolic")
        title = Gtk.Label(label="Virtual Machines", xalign=0)
        title.add_css_class("title-3")
        scroller, self.box = W.column(W.logo(7), self.notices, title, self.list, self.empty, spacing=14)
        self.page = W.page("Try Linux", scroller, tag="home", header_start=[create], header_end=[more])
        app.listeners.append(self)
        self.refresh()

    def create(self):
        from .choose import ChoosePage
        self.app.nav.push(ChoosePage(self.app).page)

    def about(self):
        from .. import VERSION
        a = Adw.AboutDialog(application_name="Try Linux", version=VERSION, developer_name="donnysaengkio",
                            website="https://github.com/donnysaengkio/try-linux-linux",
                            issue_url="https://github.com/donnysaengkio/try-linux-linux/issues",
                            license_type=Gtk.License.MIT_X11, application_icon="io.github.donnysaengkio.TryLinux",
                            comments="Try a Linux desktop in a virtual machine (QEMU/KVM), from the official installer, checked against its published checksum.")
        a.present(self.app.window)

    def open(self, c):
        from .settings import SettingsPage
        self.app.nav.push(SettingsPage(self.app, c).page)

    # ---- notices above the list

    def _notices(self):
        c = self.notices.get_first_child()
        while c:
            n = c.get_next_sibling()
            self.notices.remove(c)
            c = n
        for problem, fix in self.app.host.missing():
            b = Adw.Banner(title=problem, button_label="How to Fix", revealed=True)
            b.connect("button-clicked", lambda _b, p=problem, f=fix: W.tell(self.app.window, p, f))
            self.notices.append(b)
        r = self.app.release
        if r:
            b = Adw.Banner(title="Try Linux %s is available. You have %s." % (r.version, __import__("trylinux").VERSION),
                           button_label="Update", revealed=True)
            b.connect("button-clicked", lambda *_: self.app.install_update())
            self.notices.append(b)

    # ---- the list

    def refresh(self):
        self._notices()
        while (row := self.list.get_row_at_index(0)) is not None:
            self.list.remove(row)
        for c in self.app.systems:
            self.list.append(self._row(c))
        self.list.set_visible(bool(self.app.systems))
        self.empty.set_visible(not self.app.systems)

    def _row(self, c):
        status = self.app.status(c)
        sub = self.app.os_label(c)
        row = Adw.ActionRow(title=c.display_name, subtitle=sub, activatable=True)
        row.cfg = c
        cores, ram = c.resources()
        used = qemu.used(c.disk)
        stats = Gtk.Box(spacing=18, valign=Gtk.Align.CENTER)
        st = Gtk.Label(label=status, width_chars=10, xalign=0)
        st.add_css_class("status-on" if c.id in self.app.running else "status-off")
        st.add_css_class("caption")
        stats.append(st)
        for text in ("%d cores" % cores, "%d GiB" % ram, "%.1f GB" % (used / 1e9)):
            l = Gtk.Label(label=text, width_chars=8, xalign=0)
            l.add_css_class("stat")
            l.add_css_class("dim-label")
            stats.append(l)
        row.add_suffix(stats)
        v = self.app.running.get(c.id)
        if v:
            menu = Gio.Menu()
            for label, what in (("Shut Down", "shutdown"), ("Restart", "restart"),
                                ("Resume" if v.paused else "Pause", "pause"), ("Force Stop", "force")):
                menu.append(label, "app.power('%s|%s')" % (what, c.id))
            power = Gtk.MenuButton(label="Power", menu_model=menu, valign=Gtk.Align.CENTER)
            row.add_suffix(power)
        else:
            start = Gtk.Button(label="Install" if c.pending_install else "Start", valign=Gtk.Align.CENTER)
            start.add_css_class("suggested-action")
            start.connect("clicked", lambda *_: self.app.start(c))
            row.add_suffix(start)
        gear = Gtk.Button(icon_name="emblem-system-symbolic", valign=Gtk.Align.CENTER, tooltip_text="Settings")
        gear.add_css_class("flat")
        gear.connect("clicked", lambda *_: self.open(c))
        row.add_suffix(gear)
        return row
