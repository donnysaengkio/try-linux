"""The main menu (the ☰ button) and what's in it: Check for Updates, Preferences, Keyboard Shortcuts,
About Try Linux and Quit, each with its keyboard shortcut where it has one."""
import threading

from gi.repository import Adw, Gio, Gtk

from .. import HOME_PAGE, VERSION
from . import widgets as W

# Every keyboard shortcut, as (action, accelerators, what it does). The Keyboard Shortcuts window lists them.
SHORTCUTS = [
    ("General", [
        ("app.create", ["<Ctrl>n"], "Create a virtual machine"),
        ("app.find", ["<Ctrl>f"], "Search distributions (on Choose a Linux OS)"),
        ("app.check-updates", ["<Ctrl>u"], "Check for updates"),
        ("app.preferences", ["<Ctrl>comma"], "Preferences"),
        ("app.shortcuts", ["<Ctrl>question"], "Keyboard shortcuts"),
        ("app.quit", ["<Ctrl>q"], "Quit Try Linux"),
    ]),
    ("Navigation", [
        (None, ["<Alt>Left"], "Back"),
        (None, ["Escape"], "Close a dialog"),
    ]),
]


def model():
    """The menu: Check for Updates; Preferences, Keyboard Shortcuts and About; then Quit."""
    menu = Gio.Menu()
    top = Gio.Menu()
    top.append("Check for Updates", "app.check-updates")
    menu.append_section(None, top)
    middle = Gio.Menu()
    middle.append("Preferences", "app.preferences")
    middle.append("Keyboard Shortcuts", "app.shortcuts")
    middle.append("About Try Linux", "app.about")
    menu.append_section(None, middle)
    bottom = Gio.Menu()
    bottom.append("Quit Try Linux", "app.quit")
    menu.append_section(None, bottom)
    return menu


def install(app):
    """The menu's actions and every keyboard shortcut, on the application."""
    def add(name, fn):
        a = Gio.SimpleAction.new(name, None)
        a.connect("activate", lambda *_: fn())
        app.add_action(a)

    add("create", lambda: create(app))
    add("find", lambda: find(app))
    add("check-updates", lambda: threading.Thread(target=app.check_updates, args=(True,), daemon=True).start())
    add("preferences", lambda: preferences(app))
    add("shortcuts", lambda: shortcuts(app))
    add("about", lambda: about(app))
    add("quit", lambda: quit_(app))
    for _, items in SHORTCUTS:
        for action, accels, _ in items:
            if action:
                app.set_accels_for_action(action, accels)


def create(app):
    page = app.nav.get_visible_page()
    if getattr(page, "chooser", None):
        return
    from .choose import ChoosePage
    app.nav.push(ChoosePage(app).page)


def find(app):
    chooser = getattr(app.nav.get_visible_page(), "chooser", None)
    if chooser:
        chooser.search.grab_focus()


def quit_(app):
    # Asks first while systems are running (and shuts them down), as closing the window does.
    if not app.on_close():
        app.quit()


# ---- Preferences: Try Linux as a whole (each system's own settings are in its Settings)


def preferences(app):
    s = app.settings
    d = Adw.PreferencesDialog(title="Preferences")
    page = Adw.PreferencesPage(title="General", icon_name="preferences-system-symbolic")

    look = Adw.PreferencesGroup(title="Appearance")
    theme = Adw.ComboRow(title="Theme", model=Gtk.StringList.new(["Dark", "Light"]),
                         subtitle="Dark: greys with lavender. Light: white with purple.")
    theme.set_selected(1 if s.theme == "light" else 0)
    follow = Adw.SwitchRow(title="Follow system", active=s.follow_system,
                           subtitle="Light while your desktop uses a light style, Dark otherwise")
    theme.set_sensitive(not s.follow_system)

    def changed(*_):
        s.theme = "light" if theme.get_selected() == 1 else "dark"
        s.follow_system = follow.get_active()
        theme.set_sensitive(not s.follow_system)
        s.save()
        app.apply_theme()

    theme.connect("notify::selected", changed)
    follow.connect("notify::active", changed)
    look.add(theme)
    look.add(follow)

    updates = Adw.PreferencesGroup(title="Updates")
    auto = Adw.SwitchRow(title="Check for updates automatically", active=s.check_updates,
                         subtitle="When Try Linux starts; the home screen shows Update when there's a new version")

    def auto_changed(*_):
        s.check_updates = auto.get_active()
        s.save()

    auto.connect("notify::active", auto_changed)
    now = Adw.ActionRow(title="Check for updates now", subtitle="You have Try Linux %s" % VERSION)
    now.add_suffix(W.button("Check For Updates", lambda: app.activate_action("check-updates", None)))
    updates.add(auto)
    updates.add(now)

    page.add(look)
    page.add(updates)
    d.add(page)
    d.present(app.window)
    return d


# ---- About Try Linux, with what it runs on


def about(app):
    h = app.host
    kvm = "available" if h.kvm else ("no access to /dev/kvm" if h.kvm_exists else "not available")
    qemu = "QEMU %s" % h.version if h.version else "QEMU not installed"
    a = Adw.AboutDialog(application_name="Try Linux", version=VERSION, developer_name="donnysaengkio",
                        website=HOME_PAGE, issue_url="https://github.com/donnysaengkio/try-linux/issues",
                        license_type=Gtk.License.MIT_X11, application_icon="io.github.donnysaengkio.TryLinux",
                        comments="Try a Linux desktop in a virtual machine, from the official installer, checked against "
                                 "its published checksum.\n\n%s  ·  KVM %s  ·  %s" % (qemu, kvm, h.arch))
    a.set_debug_info("Try Linux %s\nArchitecture: %s\nQEMU: %s (%s)\nqemu-img: %s\nKVM: %s\n3D acceleration: %s\n"
                     "UEFI firmware: %s\nGTK %d.%d.%d, libadwaita %d.%d.%d\n" % (
                         VERSION, h.arch, h.version or "not installed", h.qemu or "-", h.img or "-", kvm,
                         "available" if h.can_gl else "not available", (h.firmware or ("-",))[0],
                         Gtk.get_major_version(), Gtk.get_minor_version(), Gtk.get_micro_version(),
                         Adw.get_major_version(), Adw.get_minor_version(), Adw.get_micro_version()))
    a.set_debug_info_filename("try-linux-info.txt")
    a.present(app.window)
    return a


# ---- Keyboard Shortcuts


def shortcuts(app):
    import html
    groups = ""
    for title, items in SHORTCUTS:
        rows = "".join(
            '<child><object class="GtkShortcutsShortcut"><property name="title">%s</property>'
            '<property name="accelerator">%s</property></object></child>' % (html.escape(what), html.escape(" ".join(accels)))
            for _, accels, what in items)
        groups += ('<child><object class="GtkShortcutsGroup"><property name="title">%s</property>%s</object></child>'
                   % (html.escape(title), rows))
    ui = ('<interface><object class="GtkShortcutsWindow" id="window"><property name="modal">1</property>'
          '<child><object class="GtkShortcutsSection"><property name="section-name">main</property>'
          '<property name="max-height">12</property>%s</object></child></object></interface>' % groups)
    window = Gtk.Builder.new_from_string(ui, -1).get_object("window")
    window.set_transient_for(app.window)
    window.present()
    return window
