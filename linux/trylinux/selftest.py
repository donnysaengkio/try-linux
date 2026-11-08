"""The CI self-test: logic checks (catalog, disks, QEMU's command line) and a screenshot of each screen, in
both themes. It runs in a scratch folder (see __main__), never with your own systems.

Results go to results.txt in the self-test folder, one line a check: "ok", "FAIL" or "note"."""
import os
import subprocess
import time
import traceback

from . import VERSION, catalog, config, paths, qemu

LOG = []


def note(kind, name, detail=""):
    line = "%s\t%s\t%s" % (kind, name, detail)
    LOG.append(line)
    print(line, flush=True)


def check(name, fn):
    try:
        r = fn()
        note("ok", name, "" if r is None else str(r))
    except Exception as e:
        note("FAIL", name, "%s: %s" % (type(e).__name__, e))
        traceback.print_exc()


# ---- logic


def logic(app, out):
    host = app.host
    note("note", "version", "%s on %s, Python %s" % (VERSION, host.arch, __import__("sys").version.split()[0]))
    note("note", "host", "qemu=%s %s img=%s kvm=%s gl=%s window=%s firmware=%s" % (
        host.qemu, host.version, host.img, host.kvm, host.can_gl, host.window, host.firmware))
    check("missing", lambda: host.missing() or "nothing")

    src = os.environ.get("TRYLINUX_CATALOG")
    if src:
        check("catalog parse", lambda: "%d entries (%d website-only)" % (len(catalog.refresh(src)), sum(d.website_only for d in catalog.cached())))
    if os.environ.get("TRYLINUX_RESOLVE") == "1":
        for d in catalog.builtin():
            def r(d=d):
                x = catalog.resolve(d)
                assert x.sha, "no checksum"
                return "%s %d MB" % (x.file_name, x.size >> 20)
            check("resolve " + d.id, r)

    check("forwards", lambda: [str(f) for f in config.parse_forwards(["tcp:2222:22", "udp:5000:5000", "bad"])[0]])

    # Disks: create, resize, snapshots, compact, import.
    folder = paths.ensure(os.path.join(out, "disks"))
    d = os.path.join(folder, "disk.qcow2")
    check("create disk", lambda: (qemu.create_disk(host, d, 8), qemu.capacity(host, d) >> 30)[1])
    check("preallocated disk", lambda: (qemu.create_disk(host, os.path.join(folder, "full.qcow2"), 1, True),
                                        qemu.used(os.path.join(folder, "full.qcow2")) >> 20)[1])
    check("resize grow", lambda: (qemu.resize(host, d, 12), qemu.capacity(host, d) >> 30)[1])
    check("resize shrink", lambda: (qemu.resize(host, d, 10), qemu.capacity(host, d) >> 30)[1])
    check("snapshot take", lambda: (qemu.snapshot(host, d, "take", "first"), qemu.snapshots(host, d))[1])
    check("snapshot revert", lambda: qemu.snapshot(host, d, "revert", "first"))
    check("snapshot delete", lambda: (qemu.snapshot(host, d, "delete", "first"), qemu.snapshots(host, d))[1])
    check("compact", lambda: (qemu.compact(host, d), qemu.capacity(host, d) >> 30)[1])
    vmdk = os.path.join(folder, "Fedora-disk1.vmdk")
    with open(os.path.join(folder, "Fedora.vmx"), "w") as f:
        f.write('displayName = "Fedora 44 Dev"\nannotation = "Dev box"\nmemsize = "8192"\nnumvcpus = "4"\n')
    check("import vmdk", lambda: (subprocess.run([host.img, "create", "-f", "vmdk", vmdk, "2G"], check=True, capture_output=True),
                                  qemu.convert(host, vmdk, os.path.join(folder, "imported.qcow2")),
                                  qemu.info(host, os.path.join(folder, "imported.qcow2"))["format"], qemu.read_vmx(vmdk))[2:])

    # QEMU's command line for a typical system, and QEMU accepting it (checked without starting Linux).
    c = config.VMConfig(id="argtest", display_name="Arg test", forwards=["tcp:2222:22"], shared_folder=folder, cd="")
    c.dir = paths.ensure(os.path.join(out, "argtest"))
    qemu.create_disk(host, c.disk, 4)
    qemu.prepare_firmware(host, c)
    a = qemu.args(host, c, installer=False, qmp=os.path.join(c.dir, "q.sock"), display="none")
    note("note", "args", " ".join(a))

    if host.qemu:
        def accepts():
            p = subprocess.Popen([host.qemu] + a + ["-S"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            time.sleep(4)
            alive = p.poll() is None
            p.kill()
            err = p.stderr.read()
            assert alive, err.strip()[-400:]
            return "QEMU started the machine"
        check("qemu accepts args", accepts)
        gl = qemu.args(host, c, installer=True, gl=True)
        note("note", "args with window", " ".join(gl))


# ---- boot test (no GTK): an ISO boots to a login prompt on the serial console


def boot_test(iso, log, timeout=600):
    host = qemu.Host()
    out = os.path.dirname(os.path.abspath(log))
    c = config.VMConfig(id="boottest", display_name="Boot test", sound=False, forwards=[], iso=iso, profile=2, ram_gb=2, cores=2)
    c.dir = paths.ensure(os.path.join(out, "boottest"))
    if os.path.exists(c.disk):
        os.remove(c.disk)
    qemu.create_disk(host, c.disk, 4)
    qemu.prepare_firmware(host, c)
    serial = os.path.join(out, "serial.txt")
    a = qemu.args(host, c, installer=True, display="none", serial=serial, gl=False)
    print("kvm=%s accel=%s\n%s" % (host.kvm, "kvm" if host.kvm else "tcg", " ".join(a)), flush=True)
    started = time.time()
    p = subprocess.Popen([host.qemu] + a, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    result = "timeout"
    while time.time() - started < timeout:
        if p.poll() is not None:
            result = "QEMU exited (%s)" % p.returncode
            break
        try:
            with open(serial, errors="replace") as f:
                text = f.read()
        except OSError:
            text = ""
        if "login:" in text:
            result = "login prompt"
            break
        time.sleep(2)
    took = time.time() - started
    if p.poll() is None:
        p.kill()
    with open(log, "w") as f:
        f.write("%s after %.0f s (kvm=%s)\n" % (result, took, host.kvm))
    print("%s after %.0f s" % (result, took), flush=True)
    return 0 if result == "login prompt" else 1


# ---- screenshots


def snap(app, name, out, window=None):
    from gi.repository import Graphene, Gtk
    w = window or app.window
    width, height = w.get_width(), w.get_height()
    paintable = Gtk.WidgetPaintable.new(w)
    s = Gtk.Snapshot()
    paintable.snapshot(s, width, height)
    node = s.to_node()
    if node is None:
        note("FAIL", "screenshot " + name, "nothing drawn")
        return
    tex = w.get_renderer().render_texture(node, Graphene.Rect().init(0, 0, width, height))
    tex.save_to_png(os.path.join(out, name + ".png"))
    note("ok", "screenshot " + name)


def fake_systems(app):
    """Two systems for the screenshots (disks of a few MB; nothing is installed)."""
    host = app.host
    for sid, name, distro, desc, profile in (("fedora", "Fedora Workstation", "fedora", "Dev box with Docker and VS Code.", 0),
                                             ("ubuntu", "Ubuntu Desktop", "ubuntu", "", 2)):
        c = config.VMConfig(id=sid, display_name=name, distro_id=distro, description=desc, profile=profile, ram_gb=6, cores=4,
                            iso=os.path.join(paths.ISOS, "%s-%s.iso" % ({"fedora": "Fedora-Workstation-Live-45-1.4", "ubuntu": "ubuntu-26.04-desktop"}[sid],
                                                                         "x86_64" if host.arch == "x86_64" else "aarch64")),
                            created=config.now(), last_boot=config.now())
        c.dir = paths.ensure(os.path.join(app.settings.vms_dir, sid))
        if host.img and not os.path.exists(c.disk):
            qemu.create_disk(host, c.disk, 64)
        if sid == "fedora" and host.img:
            qemu.create_disk(host, os.path.join(c.dir, "disk2.qcow2"), 32)
            c.extra_disks = ["disk2.qcow2"]
        c.save()
    app.reload()


def run(app):
    from gi.repository import GLib
    from .ui import choose, create, settings
    out = app.selftest
    steps = []

    def step(fn, wait=700):
        steps.append((fn, wait))

    def shot(name):
        step(lambda: snap(app, name, out), 300)

    def push(make):
        step(lambda: app.nav.push(make()), 900)

    def home():
        step(lambda: app.nav.pop_to_tag("home"), 700)

    # As right after installing: no saved copy of the lists, and Choose a Linux OS as the first screen. Search
    # has to find more than the recommended distributions once the lists arrive.
    fresh = {}
    src = os.environ.get("TRYLINUX_CATALOG")

    def first_screen():
        catalog._loaded = None
        for name, _ in catalog.catalog_files():
            for f in (os.path.join(paths.CATALOG, name), os.path.join(paths.CATALOG, name + ".etag")):
                if os.path.exists(f):
                    os.remove(f)
        page = choose.ChoosePage(app, root=True)
        fresh["page"], fresh["before"] = page, page.store.get_n_items()
        page.search.set_text("KDE")
        import threading
        threading.Thread(target=page.refresh_catalog, args=(src,), daemon=True).start()

    def first_screen_search():
        page = fresh["page"]
        page.on_search()
        found = page.sel.get_n_items()
        assert page.store.get_n_items() > fresh["before"], "the list didn't get the catalog's entries"
        assert found > 1, "searching KDE found %d" % found
        return "%d listed (from %d), KDE finds %d" % (page.store.get_n_items(), fresh["before"], found)

    if src:
        step(first_screen, 2500)
        step(lambda: check("first screen search", first_screen_search), 100)
    step(lambda: logic(app, out), 100)
    step(lambda: fake_systems(app), 500)
    for theme in ("dark", "light"):
        step(lambda t=theme: (setattr(app.settings, "theme", t), app.apply_theme()), 500)
        home()
        shot("home-" + theme)
        push(lambda: choose.ChoosePage(app).page)
        shot("choose-" + theme)
        if theme == "dark":
            def search(q):
                def go():
                    page = app.nav.get_visible_page()
                    entry = _find(page, __import__("gi.repository.Gtk", fromlist=["Gtk"]).SearchEntry)
                    entry.set_text(q)
                return go
            step(search("KDE"), 1200)
            shot("choose-search")
            step(search("manjaro"), 1200)
            shot("choose-website-only")
            step(lambda: _chooser(app).next(), 800)
            shot("website-only-prompt")
            step(lambda: _close_dialogs(app), 500)

            def own_chosen():
                # Choosing Use Your Own Installer clears the search result's selection (it stayed highlighted).
                page = _chooser(app)
                page.search.set_text("")
                page.own.select_row(page.own.get_row_at_index(0))
                assert page.sel.get_selected_item() is None, "a search result is still selected"
                assert page.chosen() == "own"
                return "only Use Your Own Installer selected"
            step(lambda: check("own installer chosen", own_chosen), 600)
            shot("choose-own-selected")
            push(lambda: create.OwnInstallerPage(app).page)
            shot("own-installer")
            plan = create.InstallPlan(distro=catalog.builtin()[0], label=catalog.builtin()[0].name)
            push(lambda: create.GeneralPage(app, plan).page)
            shot("create-general")
            push(lambda: create.ResourcesPage(app, plan).page)
            shot("create-resources")
            push(lambda: create.SummaryPage(app, plan).page)
            shot("create-summary")
        home()
        for tab, opened, name in (("general", {"Info", "Display and startup"}, "settings-general"),
                                  ("hardware", {"Resources", "Disks"}, "settings-hardware"),
                                  ("devices", {"Network", "CD/DVD drive"}, "settings-devices"),
                                  ("recovery", {"Snapshots", "Storage"}, "settings-recovery")):
            if theme == "light" and tab != "general":
                continue
            def open_settings(tab=tab, opened=opened):
                settings.SettingsPage.opened = set(opened)
                c = next(s for s in app.systems if s.id == "fedora")
                p = settings.SettingsPage(app, c)
                p.stack.set_visible_child_name(tab)
                return p.page
            push(open_settings)
            shot(name + ("-light" if theme == "light" else ""))
            if tab == "hardware" and theme == "dark":
                # Switching between disks shows the chosen disk's details, once (they used to pile up).
                def switch_disks():
                    page = app.nav.get_visible_page()
                    sp = _settings_of(page)
                    for i in (1, 0, 1):
                        sp.disk_index = -1
                        sp.pick_disk(i)
                        sp.fill_disks()
                    labels = []
                    _labels(sp.disks, labels)
                    n = sum(1 for t in labels if t == "Capacity")
                    assert n == 1, "Capacity shown %d times" % n
                    assert "disk2.qcow2" in " ".join(labels), "Disk 2's details aren't shown"
                    return "Disk 2 shown, headings once"
                step(lambda: check("switch disks", switch_disks), 500)
                shot("settings-disk-2")
            home()
    step(lambda: (setattr(app.settings, "theme", "dark"), app.apply_theme()), 300)

    # The main menu's windows: Preferences, About and Keyboard Shortcuts, and the shortcuts themselves.
    from .ui import menu
    shown = {}

    def accels():
        missing = [a for _, items in menu.SHORTCUTS for a, keys, _ in items
                   if a and (not app.lookup_action(a.split(".", 1)[1]) or app.get_accels_for_action(a) != keys)]
        assert not missing, "not set up: " + ", ".join(missing)
        return "; ".join("%s %s" % (a, app.get_accels_for_action(a)[0]) for _, items in menu.SHORTCUTS for a, _, _ in items if a)
    step(lambda: check("keyboard shortcuts", accels), 100)
    home()
    step(lambda: shown.__setitem__("d", menu.preferences(app)), 900)
    shot("preferences")
    step(lambda: shown["d"].close(), 500)
    step(lambda: shown.__setitem__("d", menu.about(app)), 900)
    shot("about")
    step(lambda: shown["d"].close(), 500)
    step(lambda: shown.__setitem__("w", menu.shortcuts(app)), 1200)
    step(lambda: snap(app, "keyboard-shortcuts", out, shown["w"]), 300)
    step(lambda: shown["w"].close(), 300)

    def reopened_closed():
        # Settings opened again (after leaving it with sections open) starts with every section closed.
        from gi.repository import Adw
        c = next(s for s in app.systems if s.id == "fedora")
        p = settings.SettingsPage(app, c)
        open_ = []

        def walk(w):
            if isinstance(w, Adw.ExpanderRow) and w.get_expanded():
                open_.append(w.get_title())
            ch = w.get_first_child()
            while ch:
                walk(ch)
                ch = ch.get_next_sibling()
        for name in ("general", "hardware", "devices", "recovery"):
            walk(p.stack.get_child_by_name(name))
        assert not open_, "open: " + ", ".join(open_)
        return "every section closed"
    step(lambda: check("settings reopened", reopened_closed), 300)

    def finish():
        with open(os.path.join(out, "results.txt"), "w") as f:
            f.write("\n".join(LOG) + "\n")
        fails = [l for l in LOG if l.startswith("FAIL")]
        print("%d checks, %d failed" % (len(LOG), len(fails)), flush=True)
        app.quit()

    step(finish, 100)

    def next_step(i=0):
        if i >= len(steps):
            return False
        fn, wait = steps[i]
        try:
            fn()
        except Exception as e:
            note("FAIL", "step %d" % i, "%s: %s" % (type(e).__name__, e))
            traceback.print_exc()
        GLib.timeout_add(wait, next_step, i + 1)
        return False

    GLib.timeout_add(1500, next_step, 0)


def _find(widget, cls):
    if isinstance(widget, cls):
        return widget
    c = widget.get_first_child()
    while c:
        r = _find(c, cls)
        if r:
            return r
        c = c.get_next_sibling()
    return None


def _labels(widget, out):
    from gi.repository import Gtk
    if isinstance(widget, Gtk.Label) and widget.get_mapped():
        out.append(widget.get_label())
    c = widget.get_first_child()
    while c:
        _labels(c, out)
        c = c.get_next_sibling()


def _settings_of(page):
    return page.settings


def _chooser(app):
    # The ChoosePage object for the visible page (kept on the page for the self-test).
    return app.nav.get_visible_page().chooser


def _close_dialogs(app):
    d = app.window.get_visible_dialog() if hasattr(app.window, "get_visible_dialog") else None
    if d:
        d.force_close() if hasattr(d, "force_close") else d.close()
