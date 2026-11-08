"""Settings for one system: General, Hardware, Devices and Recovery. Each tab's sections open and close
(more than one can be open). Disk changes take effect at once; the rest is kept with Save."""
import os
import shutil
import threading

from gi.repository import Adw, Gtk

from .. import VERSION, catalog, config, paths, qemu
from . import widgets as W
from .create import ResourcePicker


def _value_row(title, value=""):
    """A label and its value on one line, the values lined up in a column (as in the Windows and Mac
    editions). The value can be selected and copied."""
    box = Gtk.Box(spacing=12, margin_top=9, margin_bottom=9, margin_start=12, margin_end=12)
    label = Gtk.Label(label=title, xalign=0, valign=Gtk.Align.START)
    label.set_size_request(170, -1)
    val = Gtk.Label(label=value, xalign=0, hexpand=True, wrap=True, selectable=True, valign=Gtk.Align.START)
    box.append(label)
    box.append(val)
    return Gtk.ListBoxRow(child=box, activatable=False, selectable=False)


def _note(text):
    """An explanation under a setting, in smaller, dimmer text."""
    l = Gtk.Label(label=text, xalign=0, wrap=True, margin_top=4, margin_bottom=8, margin_start=12, margin_end=12)
    l.add_css_class("dim-label")
    l.add_css_class("caption")
    return Gtk.ListBoxRow(child=l, activatable=False, selectable=False)


def _text_box(text, height=76):
    tv = Gtk.TextView(wrap_mode=Gtk.WrapMode.WORD_CHAR, top_margin=8, bottom_margin=8, left_margin=10, right_margin=10)
    tv.get_buffer().set_text(text)
    frame = Gtk.Frame(child=Gtk.ScrolledWindow(child=tv, min_content_height=height, hscrollbar_policy=Gtk.PolicyType.NEVER),
                      margin_top=6, margin_bottom=6, margin_start=12, margin_end=12)
    return tv, frame


def _text(tv):
    b = tv.get_buffer()
    return b.get_text(b.get_start_iter(), b.get_end_iter(), False)


def _subheading(text):
    # A row of its own, so it can be taken out again with the rows under it (a bare label is wrapped in a
    # row by GTK, and removing the label left that row behind).
    l = Gtk.Label(label=text, xalign=0, margin_top=10, margin_bottom=4, margin_start=12)
    l.add_css_class("heading")
    return Gtk.ListBoxRow(child=l, activatable=False, selectable=False)


class SettingsPage:
    opened = set()      # sections left open, remembered while Try Linux runs

    def __init__(self, app, cfg):
        self.app = app
        self.host = app.host
        self.c = config.VMConfig.load(cfg.dir) or cfg
        self.disk_index = 0
        self.stack = Adw.ViewStack()
        for name, title, icon, build in (("general", "General", "preferences-system-symbolic", self.general),
                                         ("hardware", "Hardware", "computer-symbolic", self.hardware),
                                         ("devices", "Devices", "drive-optical-symbolic", self.devices),
                                         ("recovery", "Recovery", "edit-undo-symbolic", self.recovery)):
            pg = Adw.PreferencesPage()
            build(pg)
            p = self.stack.add_titled(pg, name, title)
            p.set_icon_name(icon)
        switcher = Adw.ViewSwitcher(stack=self.stack, policy=Adw.ViewSwitcherPolicy.WIDE)
        save = W.button("Save", self.save, primary=True)
        self.page = W.page(self.c.display_name, self.stack, title_widget=switcher, header_end=[save])
        self.page.settings = self   # for the self-test

    def section(self, pg, title, summary=""):
        g = Adw.PreferencesGroup()
        e = Adw.ExpanderRow(title=title, subtitle=summary, expanded=title in self.opened)
        e.connect("notify::expanded", lambda r, *_: (self.opened.add(title) if r.get_expanded() else self.opened.discard(title)))
        g.add(e)
        pg.add(g)
        return e

    @property
    def running(self):
        return self.c.id in self.app.running

    def stopped(self, doing):
        if self.running:
            W.tell(self.app.window, "Shut down %s first" % self.c.display_name, "It has to be shut down before %s." % doing)
            return False
        return True

    # ---- General

    def general(self, pg):
        c = self.c
        info = self.section(pg, "Info", (c.description.splitlines() or ["No description"])[0])
        self.name = Adw.EntryRow(title="Name")
        self.name.set_text(c.display_name)
        info.add_row(self.name)
        self.desc, frame = _text_box(c.description)
        info.add_row(_subheading("Description"))
        info.add_row(frame)
        info.add_row(_value_row("Operating system", self.app.os_label(c)))
        d = catalog.find(c.distro_id)
        if d and d.home:
            r = Adw.ActionRow(title="Home page")
            r.add_suffix(W.link(d.home, d.home))
            info.add_row(r)
        info.add_row(_value_row("Location", paths.display(c.dir)))

        disp = self.section(pg, "Display and startup", ("Fullscreen" if c.fullscreen else "In a window"))
        self.fullscreen = Adw.SwitchRow(title="Open fullscreen", active=c.fullscreen)
        disp.add_row(self.fullscreen)
        sc = Adw.ActionRow(title="Desktop shortcut", subtitle="Starts %s from your apps menu" % c.display_name)
        sc.add_suffix(W.button("Add Shortcut", self.add_shortcut))
        disp.add_row(sc)
        self.autostart = Adw.SwitchRow(title="Start %s when I sign in" % c.display_name, active=os.path.exists(self._autostart_file()))
        disp.add_row(self.autostart)

        shared = self.section(pg, "Shared folder", paths.display(c.shared_folder) if c.shared_folder else "None")
        self.shared = c.shared_folder
        self.shared_row = Adw.ActionRow(title="Shared folder", subtitle=paths.display(self.shared) if self.shared else "None")
        self.shared_row.add_suffix(W.button("Browse", self.choose_shared))
        self.shared_row.add_suffix(W.button("Clear", lambda: self.set_shared("")))
        shared.add_row(self.shared_row)
        self.shared_write = Adw.SwitchRow(title="Allow Linux to read and change this folder", active=c.shared_write,
                                          subtitle="Off shares it read-only. In Linux: sudo mount -t 9p -o trans=virtio host /mnt")
        shared.add_row(self.shared_write)

        s = self.app.settings
        look = self.section(pg, "Appearance", ("Follow system" if s.follow_system else s.theme.title()))
        self.theme = Adw.ComboRow(title="Theme", model=Gtk.StringList.new(["Dark", "Light"]))
        self.theme.set_selected(1 if s.theme == "light" else 0)
        self.follow = Adw.SwitchRow(title="Follow system", active=s.follow_system,
                                    subtitle="Light while your desktop uses a light style, Dark otherwise. The theme changes when you save.")
        look.add_row(self.theme)
        look.add_row(self.follow)

        about = self.section(pg, "About", "Try Linux %s" % VERSION)
        about.add_row(_value_row("Try Linux", VERSION))
        about.add_row(_value_row("QEMU", self.host.version or "Not installed"))
        about.add_row(_value_row("KVM", "Available" if self.host.kvm else ("No access to /dev/kvm" if self.host.kvm_exists else "Not available")))
        self.check_updates = Adw.SwitchRow(title="Check for updates automatically", active=s.check_updates)
        about.add_row(self.check_updates)
        chk = Adw.ActionRow(title="Check for updates now")
        chk.add_suffix(W.button("Check For Updates", lambda: threading.Thread(target=self.app.check_updates, args=(True,), daemon=True).start()))
        about.add_row(chk)

    def _autostart_file(self):
        return os.path.join(paths.AUTOSTART, "trylinux-%s.desktop" % self.c.id)

    def _desktop_entry(self):
        exe = shutil.which("try-linux") or os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "try-linux")
        return ("[Desktop Entry]\nType=Application\nName=%s\nComment=Start %s in Try Linux\nExec=%s --start %s\n"
                "Icon=io.github.donnysaengkio.TryLinux\nCategories=System;Emulator;\n" % (self.c.display_name, self.c.display_name, exe, self.c.id))

    def add_shortcut(self):
        paths.ensure(paths.APPS)
        path = os.path.join(paths.APPS, "trylinux-%s.desktop" % self.c.id)
        with open(path, "w") as f:
            f.write(self._desktop_entry())
        self.app.toast("Added %s to your apps" % self.c.display_name)

    def choose_shared(self):
        W.open_folder(self.app.window, "Choose a folder to share with %s" % self.c.display_name, self.set_shared)

    def set_shared(self, folder):
        self.shared = folder
        self.shared_row.set_subtitle(paths.display(folder) if folder else "None")

    # ---- Hardware

    def hardware(self, pg):
        c, h = self.c, self.host
        gfx = self.section(pg, "Graphics", "3D acceleration (VirGL)" if c.gl and h.can_gl else "2D")
        self.gl = Adw.SwitchRow(title="3D acceleration (VirGL)", active=c.gl and h.can_gl, sensitive=h.can_gl,
                                subtitle="Linux uses this PC's GPU for OpenGL through virglrenderer" if h.can_gl else
                                "This PC's QEMU doesn't have the 3D graphics device (%s) or a window that can show it" % h.gl_device)
        gfx.add_row(self.gl)
        gfx.add_row(_value_row("Last start", c.last_boot or "Not started yet"))

        cores, ram = c.resources()
        res = self.section(pg, "Resources", "%s  ·  %d cores  ·  %d GiB" % (config.PROFILES[c.profile], cores, ram))
        others = [0, 0]
        for v in self.app.running.values():
            if v.cfg.id != c.id:
                oc, orr = v.cfg.resources()
                others[0] += oc
                others[1] += orr
        self.res = ResourcePicker(res, c.profile, c.ram_gb, c.cores, others)

        n = len(c.all_disks)
        self.disks = self.section(pg, "Disks", "%d disk%s  ·  Disk 1: %s of %s" % (
            n, "" if n == 1 else "s", W_size(qemu.used(c.disk)), W_size(qemu.capacity(h, c.disk))))
        self.disk_rows = []
        self.fill_disks()

    def fill_disks(self):
        for r in self.disk_rows:
            self.disks.remove(r)
        self.disk_rows = []
        c, h = self.c, self.host
        add = lambda r: (self.disks.add_row(r), self.disk_rows.append(r))
        disks = c.all_disks
        self.disk_index = min(self.disk_index, len(disks) - 1)
        url = disks[self.disk_index]
        pick = Adw.ComboRow(title="Disk", model=Gtk.StringList.new(["Disk %d" % (i + 1) for i in range(len(disks))]),
                            subtitle="Linux sees each disk as its own drive. %s starts from Disk 1." % c.display_name)
        pick.set_selected(self.disk_index)
        pick.connect("notify::selected", lambda r, *_: self.pick_disk(r.get_selected()))
        add(pick)
        exists = os.path.exists(url)
        add(_subheading("Capacity"))
        add(_value_row("Current size", W_size(qemu.used(url)) if exists else "The file is missing. It may have been moved or deleted."))
        add(_value_row("System free", "%s on the drive" % W_size(qemu.free_space(os.path.dirname(url)))))
        add(_value_row("Maximum size", W_size(qemu.capacity(h, url)) if exists else ""))
        add(_subheading("Disk information"))
        add(_note("Preallocated: its full maximum size was taken on the drive when it was made."
                  if self.disk_index == 0 and c.disk_full else
                  "Dynamic: the file takes space on the drive only as Linux stores data, up to the maximum size."))
        add(_note("Stored in %s%s" % (os.path.basename(url), ", the disk %s starts from." % c.display_name if self.disk_index == 0 else ".")))
        add(_value_row("Location", paths.display(url)))
        # What can be done with the disks, under the disk's details.
        add(_subheading("Disk Utilities"))
        self.reclaim = Adw.SwitchRow(title="Reclaim free space when Linux deletes files", active=c.reclaim,
                                     subtitle="Linux tells the disk which space it no longer uses (TRIM), so new files reuse it "
                                              "instead of growing the file. Applies to every disk, from the next start.")
        add(self.reclaim)
        for title, label, fn, ok in (("Shrink or expand this disk's maximum size.", "Resize", self.resize, exists),
                                     ("Give unused space in this disk's file back to the PC.", "Compact", self.compact, exists),
                                     ("Add another disk to %s." % c.display_name, "Add Disk", self.add_disk, True),
                                     ("Take this disk out of %s." % c.display_name, "Remove", self.remove_disk, self.disk_index > 0)):
            r = Adw.ActionRow(title=title)
            b = W.button(label, fn)
            b.set_valign(Gtk.Align.CENTER)
            b.set_sensitive(ok)
            r.add_suffix(b)
            add(r)

    def pick_disk(self, i):
        if i != self.disk_index:
            self.disk_index = i
            W.ui(self.fill_disks)

    def save_disks(self):
        fresh = config.VMConfig.load(self.c.dir) or self.c
        fresh.extra_disks = self.c.extra_disks
        fresh.disk_gb = self.c.disk_gb
        fresh.save()

    def resize(self):
        if not self.stopped("resizing its disks"):
            return
        url = self.c.all_disks[self.disk_index]
        cap = qemu.capacity(self.host, url)
        d = Adw.AlertDialog(heading="Resize Disk %d" % (self.disk_index + 1),
                            body="Now %d GiB, with %s in use. Shrinking below what Linux uses loses data: shrink its partition in Linux first." % (cap >> 30, W_size(qemu.used(url))))
        spin = Gtk.SpinButton.new_with_range(1, 4096, 1)
        spin.set_value(cap >> 30)
        box = Gtk.Box(spacing=8, halign=Gtk.Align.CENTER)
        box.append(spin)
        box.append(Gtk.Label(label="GiB"))
        d.set_extra_child(box)
        d.add_response("no", "Cancel")
        d.add_response("yes", "Resize")
        d.set_response_appearance("yes", Adw.ResponseAppearance.SUGGESTED)

        def done(_d, r):
            if r != "yes":
                return
            gb = int(spin.get_value())
            try:
                qemu.resize(self.host, url, gb)
                if self.disk_index == 0:
                    self.c.disk_gb = gb
                self.save_disks()
                self.fill_disks()
            except Exception as e:
                W.tell(self.app.window, "Couldn't resize the disk", str(e))

        d.connect("response", done)
        d.present(self.app.window)

    def compact(self):
        if not self.stopped("compacting its disks"):
            return
        url = self.c.all_disks[self.disk_index]
        before = qemu.used(url)

        def work():
            try:
                qemu.compact(self.host, url)
                W.ui(W.tell, self.app.window, "Disk compacted", "%s now, from %s." % (W_size(qemu.used(url)), W_size(before)))
            except Exception as e:
                W.ui(W.tell, self.app.window, "Couldn't compact the disk", str(e))
            W.ui(self.fill_disks)

        self.app.toast("Compacting Disk %d..." % (self.disk_index + 1))
        threading.Thread(target=work, daemon=True).start()

    def add_disk(self):
        if not self.stopped("adding a disk"):
            return

        def new():
            n = len(self.c.all_disks) + 1
            name = "disk%d.qcow2" % n
            while os.path.exists(os.path.join(self.c.dir, name)):
                n += 1
                name = "disk%d.qcow2" % n
            try:
                qemu.create_disk(self.host, os.path.join(self.c.dir, name), 32)
            except Exception as e:
                W.tell(self.app.window, "Couldn't add a disk", str(e))
                return
            self.c.extra_disks.append(name)
            self.save_disks()
            self.disk_index = len(self.c.all_disks) - 1
            self.fill_disks()

        def existing():
            def chosen(path):
                if path in self.c.all_disks:
                    return
                self.c.extra_disks.append(path)
                self.save_disks()
                self.disk_index = len(self.c.all_disks) - 1
                self.fill_disks()

            W.open_file(self.app.window, "Choose a qcow2 disk", chosen, "qcow2 disks", ["*.qcow2"])

        W.ask(self.app.window, "Add a disk to %s?" % self.c.display_name,
              "A new, empty 32 GiB disk (resize it afterwards if you need more), or an existing .qcow2 file.",
              "New Disk", extra=("existing", "Existing File", existing), on_yes=new)

    def remove_disk(self):
        if self.disk_index == 0 or not self.stopped("removing a disk"):
            return
        i = self.disk_index - 1
        entry = self.c.extra_disks[i]
        path = entry if os.path.isabs(entry) else os.path.join(self.c.dir, entry)
        inside = not os.path.isabs(entry)

        def remove(delete):
            self.c.extra_disks.pop(i)
            if delete and inside and os.path.exists(path):
                os.remove(path)
            self.save_disks()
            self.disk_index = 0
            self.fill_disks()

        if inside:
            W.ask(self.app.window, "Remove Disk %d?" % self.disk_index,
                  "Its file is deleted, with everything Linux stored on it.", "Remove and Delete", destructive=True,
                  on_yes=lambda: remove(True))
        else:
            W.ask(self.app.window, "Remove Disk %d?" % self.disk_index,
                  "It's taken out of %s; its file (%s) is kept." % (self.c.display_name, paths.display(path)), "Remove",
                  on_yes=lambda: remove(False))

    # ---- Devices

    def devices(self, pg):
        c = self.c
        snd = self.section(pg, "Microphone and sound", "Sound on" if c.sound else "Sound off")
        self.sound = Adw.SwitchRow(title="Sound", active=c.sound)
        self.mic = Adw.SwitchRow(title="Microphone", active=c.mic, subtitle="Lets Linux use this PC's microphone")
        snd.add_row(self.sound)
        snd.add_row(self.mic)

        cd = self.section(pg, "CD/DVD drive", os.path.basename(c.cd) if c.cd else "Empty")
        self.cd = c.cd
        self.cd_row = Adw.ActionRow(title="Disc", subtitle=paths.display(c.cd) if c.cd else "Empty")
        self.cd_row.add_suffix(W.button("Choose ISO", self.choose_cd))
        self.cd_row.add_suffix(W.button("Eject", lambda: self.set_cd("")))
        cd.add_row(self.cd_row)
        cd.add_row(_note("For ISOs such as drivers or rescue disks. While %s runs, the disc changes at once." % c.display_name))

        net = self.section(pg, "Network", config.NET_TYPES[c.net_type])
        self.net = Adw.ComboRow(title="Network type", model=Gtk.StringList.new(config.NET_TYPES))
        self.net.set_selected(c.net_type)
        net.add_row(self.net)
        net.add_row(_subheading("Port forwards"))
        self.fwd, frame = _text_box("\n".join(c.forwards), 60)
        net.add_row(frame)
        net.add_row(_note("One a line, as tcp:2222:22 (this PC's port, then Linux's). ssh -p 2222 you@localhost reaches Linux's SSH."))

    def choose_cd(self):
        W.open_file(self.app.window, "Choose an ISO for the CD/DVD drive", self.set_cd, "Disk images (*.iso)", ["*.iso"])

    def set_cd(self, iso):
        self.cd = iso
        self.cd_row.set_subtitle(paths.display(iso) if iso else "Empty")
        v = self.app.running.get(self.c.id)
        if v:
            try:
                v.change_cd(iso)
            except Exception as e:
                W.tell(self.app.window, "Couldn't change the disc", str(e))
        fresh = config.VMConfig.load(self.c.dir) or self.c
        fresh.cd = iso
        fresh.save()

    # ---- Recovery

    def recovery(self, pg):
        c = self.c
        self.snaps = self.section(pg, "Snapshots", "Saved states of Disk 1")
        self.snap_rows = []
        self.fill_snapshots()

        bk = self.section(pg, "Backup and restore", "")
        r = Adw.ActionRow(title="Back up", subtitle="Copies %s (its settings and every disk) to a folder you choose." % c.display_name)
        r.add_suffix(W.button("Back Up", self.backup))
        bk.add_row(r)
        r = Adw.ActionRow(title="Restore as a copy", subtitle="Adds a backed-up system as a new one; this one isn't changed.")
        r.add_suffix(W.button("Restore", self.restore))
        bk.add_row(r)

        st = self.section(pg, "Storage", "Downloaded installers")
        isos = [os.path.join(paths.ISOS, n) for n in os.listdir(paths.ISOS)] if os.path.isdir(paths.ISOS) else []
        total = sum(os.path.getsize(p) for p in isos if os.path.isfile(p))
        r = Adw.ActionRow(title="Downloaded installers", subtitle="%d file%s, %s in %s" % (
            len(isos), "" if len(isos) == 1 else "s", W_size(total), paths.display(paths.ISOS)))
        b = W.button("Clean Up", self.clean_up)
        b.set_sensitive(bool(isos))
        r.add_suffix(b)
        st.add_row(r)

        rep = self.section(pg, "Repair", "")
        r = Adw.ActionRow(title="Start from an ISO once", subtitle="Boots a rescue or installer ISO this time only, to repair Linux.")
        r.add_suffix(W.button("Choose ISO", self.repair))
        rep.add_row(r)

        de = self.section(pg, "Delete", "")
        r = Adw.ActionRow(title="Delete %s" % c.display_name, subtitle="Removes its virtual disk and everything installed in it.")
        r.add_suffix(W.button("Delete", self.delete, destructive=True))
        de.add_row(r)

    def fill_snapshots(self):
        for r in self.snap_rows:
            self.snaps.remove(r)
        self.snap_rows = []
        try:
            snaps = qemu.snapshots(self.host, self.c.disk) if os.path.exists(self.c.disk) else []
        except Exception as e:
            snaps = []
            r = _value_row("Couldn't read snapshots", str(e))
            self.snaps.add_row(r)
            self.snap_rows.append(r)
        for name, date in snaps:
            r = Adw.ActionRow(title=name, subtitle=date)
            r.add_suffix(W.button("Revert", lambda n=name: self.snapshot("revert", n)))
            r.add_suffix(W.button("Delete", lambda n=name: self.snapshot("delete", n), destructive=True))
            self.snaps.add_row(r)
            self.snap_rows.append(r)
        r = Adw.ActionRow(title="Take a snapshot", subtitle="Saves the whole disk as it is now, inside the disk file. Reverting throws away changes made since.")
        r.add_suffix(W.button("Take Snapshot", lambda: self.snapshot("take", "snapshot-" + config.now().replace(" ", "-").replace(":", "")), primary=True))
        self.snaps.add_row(r)
        self.snap_rows.append(r)

    def snapshot(self, action, name):
        if not self.stopped({"take": "taking", "revert": "reverting to", "delete": "deleting"}[action] + " a snapshot"):
            return

        def go():
            try:
                qemu.snapshot(self.host, self.c.disk, action, name)
            except Exception as e:
                W.tell(self.app.window, "Snapshots", str(e))
            self.fill_snapshots()

        if action == "take":
            go()
        elif action == "revert":
            W.ask(self.app.window, "Revert to %s?" % name, "Changes made to %s since then are lost." % self.c.display_name,
                  "Revert", destructive=True, on_yes=go)
        else:
            W.ask(self.app.window, "Delete %s?" % name, "It can't be restored afterwards.", "Delete", destructive=True, on_yes=go)

    def backup(self):
        if not self.stopped("backing it up"):
            return

        def chosen(folder):
            dest = config.new_folder(folder, self.c.display_name + " backup")

            def work():
                try:
                    shutil.copytree(self.c.dir, dest, ignore=shutil.ignore_patterns("qmp.sock", "qemu.log"))
                    for i, d in enumerate(self.c.all_disks[1:]):
                        if os.path.isabs(self.c.extra_disks[i]) and os.path.exists(d):
                            shutil.copy2(d, dest)
                    W.ui(W.tell, self.app.window, "Backed up", "%s is in %s." % (self.c.display_name, paths.display(dest)))
                except Exception as e:
                    W.ui(W.tell, self.app.window, "Couldn't back up", str(e))

            self.app.toast("Backing up %s..." % self.c.display_name)
            threading.Thread(target=work, daemon=True).start()

        W.open_folder(self.app.window, "Choose where to put the backup", chosen)

    def restore(self):
        def chosen(folder):
            src = config.VMConfig.load(folder)
            if not src:
                W.tell(self.app.window, "Not a backup", "That folder doesn't have a Try Linux system in it (vm.json).")
                return
            name = config.unique_name(src.display_name, {s.display_name for s in self.app.systems})
            dest = config.new_folder(self.app.settings.vms_dir, name)

            def work():
                try:
                    shutil.copytree(folder, dest)
                    c = config.VMConfig.load(dest)
                    c.id, c.display_name = os.path.basename(dest), name
                    c.extra_disks = [os.path.basename(d) for d in c.extra_disks]
                    c.save()
                    W.ui(self.app.reload)
                    W.ui(W.tell, self.app.window, "Restored", "%s is added to Virtual Machines." % name)
                except Exception as e:
                    W.ui(W.tell, self.app.window, "Couldn't restore", str(e))

            threading.Thread(target=work, daemon=True).start()

        W.open_folder(self.app.window, "Choose a backup folder", chosen)

    def clean_up(self):
        used = {s.iso for s in self.app.systems if s.pending_install}
        files = [os.path.join(paths.ISOS, n) for n in os.listdir(paths.ISOS)] if os.path.isdir(paths.ISOS) else []
        files = [f for f in files if f not in used and os.path.isfile(f)]
        if not files:
            W.tell(self.app.window, "Nothing to clean up", "The downloaded installers are still needed to finish installing.")
            return
        total = sum(os.path.getsize(f) for f in files)

        def go():
            for f in files:
                try:
                    os.remove(f)
                except OSError:
                    pass
            self.app.toast("Deleted %d downloaded file%s" % (len(files), "" if len(files) == 1 else "s"))

        W.ask(self.app.window, "Delete %d downloaded file%s?" % (len(files), "" if len(files) == 1 else "s"),
              "%s in all:\n%s\n\nYou can download them again later." % (W_size(total), "\n".join(os.path.basename(f) for f in files)),
              "Delete", destructive=True, on_yes=go)

    def repair(self):
        if not self.stopped("starting it from an ISO"):
            return
        W.open_file(self.app.window, "Choose a rescue or installer ISO",
                    lambda iso: self.app.start(config.VMConfig.load(self.c.dir) or self.c, iso=iso), "Disk images (*.iso)", ["*.iso"])

    def delete(self):
        if not self.stopped("deleting it"):
            return

        def go():
            config.delete_system(self.c, self.app.settings)
            for p in (os.path.join(paths.APPS, "trylinux-%s.desktop" % self.c.id), self._autostart_file()):
                if os.path.exists(p):
                    os.remove(p)
            self.app.reload()
            self.app.nav.pop_to_tag("home")

        W.ask(self.app.window, "Delete %s?" % self.c.display_name,
              "This permanently removes its virtual disk and everything installed in it. Downloaded ISOs are kept.",
              "Delete", destructive=True, on_yes=go)

    # ---- Save

    def save(self):
        c = config.VMConfig.load(self.c.dir) or self.c
        name = self.name.get_text().strip()
        if not name:
            W.tell(self.app.window, "Name this system", "It needs a name.")
            return
        forwards = [l.strip() for l in _text(self.fwd).splitlines() if l.strip()]
        ok, bad = config.parse_forwards(forwards)
        if bad:
            W.tell(self.app.window, "Check the port forwards", "These aren't written as tcp:2222:22: " + ", ".join(bad))
            return
        c.display_name = name
        c.description = _text(self.desc).strip()
        c.fullscreen = self.fullscreen.get_active()
        c.shared_folder, c.shared_write = self.shared, self.shared_write.get_active()
        c.gl = self.gl.get_active()
        c.profile, c.ram_gb, c.cores = self.res.values()
        c.reclaim = self.reclaim.get_active()
        c.sound, c.mic = self.sound.get_active(), self.mic.get_active()
        c.net_type = self.net.get_selected()
        c.forwards = [str(f) for f in ok]
        c.cd = self.cd
        c.save()
        auto = self._autostart_file()
        if self.autostart.get_active():
            paths.ensure(paths.AUTOSTART)
            with open(auto, "w") as f:
                f.write(self._desktop_entry())
        elif os.path.exists(auto):
            os.remove(auto)
        s = self.app.settings
        s.theme = "light" if self.theme.get_selected() == 1 else "dark"
        s.follow_system = self.follow.get_active()
        s.check_updates = self.check_updates.get_active()
        s.save()
        self.app.apply_theme()
        self.app.reload()
        if self.running:
            self.app.toast("Saved. Most changes take effect the next time %s starts." % name)
        self.app.nav.pop_to_tag("home")


def W_size(n):
    from ..download import human
    return human(n)
