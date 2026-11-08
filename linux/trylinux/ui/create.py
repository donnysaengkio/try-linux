"""Create: Use Your Own Installer, then General, Resources and a Summary, then downloading and installing."""
import os
import threading
from dataclasses import dataclass, field

from gi.repository import Adw, Gtk

from .. import catalog, config, download, paths, qemu
from . import widgets as W


@dataclass
class InstallPlan:
    distro: object = None      # a catalog.Distro
    local_iso: str = ""        # an .iso file on this PC
    url: str = ""              # a link to one
    disk: str = ""             # an existing virtual disk to import
    disk_info: dict = field(default_factory=dict)
    label: str = ""
    name: str = ""
    description: str = ""
    location: str = ""         # "" is the storage folder
    profile: int = 0
    ram_gb: int = 4
    cores: int = 2
    disk_gb: int = 64
    disk_full: bool = False

    @property
    def importing(self):
        return bool(self.disk)

    def installer_text(self):
        if self.distro:
            return "%s, downloaded from %s and checked" % (self.distro.name, self.distro.base.split("/")[2])
        if self.local_iso:
            return paths.display(self.local_iso)
        if self.url:
            return self.url
        return paths.display(self.disk)


def _sizes(gb):
    return "%d GiB" % gb


# ---- Use Your Own Installer


class OwnInstallerPage:
    def __init__(self, app, last=None):
        self.app = app
        self.plan = last or InstallPlan()
        group = Adw.PreferencesGroup(title="Use Your Own Installer",
                                     description="Import a virtual disk that already has Linux on it, or install from an ISO file or a link. "
                                                 "Choosing one clears the others.")
        self.disk_row = Adw.ActionRow(title="An Existing Virtual Disk",
                                      subtitle="qcow2, vmdk, vhd, vhdx, vdi or raw. It's copied, so the original isn't changed.")
        self.disk_row.add_suffix(W.button("Choose A Disk File", self.choose_disk))
        self.iso_row = Adw.ActionRow(title="An ISO File On This PC", subtitle="A Linux installer you've downloaded")
        self.iso_row.add_suffix(W.button("Choose An ISO File", self.choose_iso))
        self.url_row = Adw.EntryRow(title="URL Link (https://...)")
        self.url_row.connect("changed", lambda *_: self.on_url())
        for r in (self.disk_row, self.iso_row, self.url_row):
            group.add(r)
        self.error = Gtk.Label(xalign=0, wrap=True, visible=False)
        self.error.add_css_class("error")
        scroller, _ = W.column(W.heading("Use Your Own Installer"), group, self.error)
        self.page = W.page("Use Your Own Installer", scroller, bottom=W.bottom_bar(None, W.button("Next", self.next, primary=True, pill=True)))
        self.show()

    def show(self):
        p = self.plan
        self.disk_row.set_subtitle(paths.display(p.disk) + self._disk_text() if p.disk else
                                   "qcow2, vmdk, vhd, vhdx, vdi or raw. It's copied, so the original isn't changed.")
        self.iso_row.set_subtitle(paths.display(p.local_iso) if p.local_iso else "A Linux installer you've downloaded")
        if self.url_row.get_text() != p.url:
            self.url_row.set_text(p.url)

    def _disk_text(self):
        i = self.plan.disk_info
        bits = []
        if i.get("format"):
            bits.append(i["format"])
        if i.get("virtual-size"):
            bits.append("%d GiB" % (i["virtual-size"] >> 30))
        v = i.get("vmx", {})
        if v:
            bits.append("settings from VMware: %s GiB, %s cores" % (v.get("ram_gb", "?"), v.get("cores", "?")))
        return " (" + ", ".join(bits) + ")" if bits else ""

    def choose_disk(self):
        def chosen(path):
            info = qemu.info(self.app.host, path)
            if not info:
                self.fail("That file isn't a virtual disk QEMU can read.")
                return
            info["vmx"] = qemu.read_vmx(path)
            self.plan = InstallPlan(disk=path, disk_info=info, label=info["vmx"].get("name") or os.path.splitext(os.path.basename(path))[0])
            self.error.set_visible(False)
            self.show()

        W.open_file(self.app.window, "Choose a virtual disk", chosen, "Virtual disks", ["*." + t for t in qemu.DISK_TYPES])

    def choose_iso(self):
        def chosen(path):
            self.plan = InstallPlan(local_iso=path, label=os.path.splitext(os.path.basename(path))[0])
            self.error.set_visible(False)
            self.show()

        W.open_file(self.app.window, "Choose a Linux installer", chosen, "Disk images (*.iso)", ["*.iso"])

    def on_url(self):
        t = self.url_row.get_text().strip()
        if t and t != self.plan.url:
            self.plan = InstallPlan(url=t)
            self.show()

    def fail(self, text):
        self.error.set_label(text)
        self.error.set_visible(True)

    def next(self):
        p = self.plan
        if p.url:
            if not p.url.startswith(("https://", "http://")):
                self.fail("That isn't a web address. It should start with https://")
                return
            p.label = os.path.splitext(catalog.last_segment(p.url))[0] or "Linux"
        if not (p.disk or p.local_iso or p.url):
            self.fail("Choose a disk file or an ISO file, or paste a link.")
            return
        if p.disk:
            v = p.disk_info.get("vmx", {})
            p.description = v.get("description", "")
            if v.get("ram_gb") or v.get("cores"):
                p.profile = 2
                p.ram_gb = min(config.max_ram_gb(), v.get("ram_gb", p.ram_gb))
                p.cores = min(config.max_cores(), v.get("cores", p.cores))
            p.disk_gb = max(1, int(p.disk_info.get("virtual-size", 0)) >> 30)
        self.app.nav.push(GeneralPage(self.app, p).page)


# ---- General


class GeneralPage:
    def __init__(self, app, plan):
        self.app, self.plan = app, plan
        taken = {s.display_name for s in app.systems}
        if not plan.name:
            plan.name = config.unique_name(plan.label or "Linux", taken)
        g = Adw.PreferencesGroup()
        self.name = Adw.EntryRow(title="Name")
        self.name.set_text(plan.name)
        g.add(self.name)

        dg = Adw.PreferencesGroup(title="Description", description="(Optional): Shown in Settings > General > Info.")
        self.desc = Gtk.TextView(wrap_mode=Gtk.WrapMode.WORD_CHAR, top_margin=8, bottom_margin=8, left_margin=10, right_margin=10)
        self.desc.get_buffer().set_text(plan.description)
        frame = Gtk.Frame(child=Gtk.ScrolledWindow(child=self.desc, min_content_height=76, hscrollbar_policy=Gtk.PolicyType.NEVER))
        frame.add_css_class("card")
        dg.add(frame)

        lg = Adw.PreferencesGroup(title="Location")
        self.location = plan.location or app.settings.vms_dir
        self.loc_row = Adw.ActionRow(title="Folder", subtitle=paths.display(self.location))
        self.browse = W.button("Browse", self.choose)
        self.loc_row.add_suffix(self.browse)
        self.own = Adw.SwitchRow(title="Choose a different location", active=bool(plan.location))
        self.own.connect("notify::active", lambda *_: self.lock())
        lg.add(self.loc_row)
        lg.add(self.own)
        self.lock()
        scroller, _ = W.column(W.heading("General"), g, dg, lg)
        self.page = W.page("General", scroller, bottom=W.bottom_bar(None, W.button("Next", self.next, primary=True, pill=True)))

    def lock(self):
        # The default is the storage folder; a different one is used only while the switch is on.
        on = self.own.get_active()
        self.browse.set_sensitive(on)
        self.loc_row.set_subtitle(paths.display(self.location if on else self.app.settings.vms_dir) +
                                  ("" if on else "  (where Try Linux keeps its systems)"))

    def choose(self):
        def chosen(folder):
            self.location = folder
            self.lock()

        W.open_folder(self.app.window, "Choose where to keep this system", chosen)

    def next(self):
        name = self.name.get_text().strip()
        if not name:
            W.tell(self.app.window, "Name this system", "Give it a name to tell it apart from the others.")
            return
        b = self.desc.get_buffer()
        self.plan.name = name
        self.plan.description = b.get_text(b.get_start_iter(), b.get_end_iter(), False).strip()
        self.plan.location = self.location if self.own.get_active() else ""
        self.app.nav.push(ResourcesPage(self.app, self.plan).page)


# ---- Resources


class ResourcePicker:
    """Balanced, Maximum or Manual, with memory and CPU cores (changeable with Manual only). Shared with Settings."""

    def __init__(self, group, profile, ram_gb, cores, others=(0, 0)):
        self.max_cores = max(1, config.max_cores() - others[0])
        self.max_ram = max(1, config.max_ram_gb() - others[1])
        row = Adw.ActionRow(title="Resource profile")
        box = Gtk.Box(valign=Gtk.Align.CENTER)
        box.add_css_class("linked")
        self.toggles = []
        for i, n in enumerate(config.PROFILES):
            t = Gtk.ToggleButton(label=n)
            if self.toggles:
                t.set_group(self.toggles[0])
            t.connect("toggled", lambda b, i=i: b.get_active() and self.set_profile(i))
            box.append(t)
            self.toggles.append(t)
        row.add_suffix(box)
        self.ram = Adw.SpinRow.new_with_range(1, self.max_ram, 1)
        self.ram.set_title("Memory (GiB)")
        self.ram.set_subtitle("up to %d of %d GiB" % (self.max_ram, config.host_ram_gb()))
        self.cores = Adw.SpinRow.new_with_range(1, self.max_cores, 1)
        self.cores.set_title("CPU cores")
        self.cores.set_subtitle("up to %d of %d" % (self.max_cores, config.host_cores()))
        add = getattr(group, "add_row", None) or group.add   # a preferences group, or a section (expander row)
        for r in (row, self.ram, self.cores):
            add(r)
        self.manual = (ram_gb, cores)
        self.profile = profile
        self.toggles[profile].set_active(True)
        self.set_profile(profile)

    def set_profile(self, i):
        if self.profile == 2 and i != 2:
            self.manual = (int(self.ram.get_value()), int(self.cores.get_value()))
        self.profile = i
        c = config.VMConfig(profile=i, ram_gb=self.manual[0], cores=self.manual[1])
        cores, ram = c.resources()
        self.ram.set_value(min(ram, self.max_ram))
        self.cores.set_value(min(cores, self.max_cores))
        self.ram.set_sensitive(i == 2)
        self.cores.set_sensitive(i == 2)

    def values(self):
        return self.profile, int(self.ram.get_value()), int(self.cores.get_value())


class ResourcesPage:
    def __init__(self, app, plan):
        self.app, self.plan = app, plan
        g = Adw.PreferencesGroup(title="Resources")
        self.res = ResourcePicker(g, plan.profile, plan.ram_gb, plan.cores)
        dg = Adw.PreferencesGroup(title="Disk")
        where = plan.location or app.settings.vms_dir
        free = qemu.free_space(where)
        self.size = Adw.SpinRow.new_with_range(8, 2048, 1)
        self.size.set_title("Disk size (GiB)")
        self.size.set_value(plan.disk_gb)
        self.size.set_subtitle("%s free on the drive" % download.human(free))
        self.full = Adw.SwitchRow(title="Allocate the full size now",
                                  subtitle="Takes the whole size on the drive now, instead of as Linux stores data", active=plan.disk_full)
        self.full.connect("notify::active", lambda *_: self.size.set_sensitive(not self.full.get_active()))
        dg.add(self.size)
        if plan.importing:
            self.size.set_sensitive(False)
            self.size.set_subtitle("The imported disk's size")
        else:
            dg.add(self.full)
            self.size.set_sensitive(not plan.disk_full)
        scroller, _ = W.column(W.heading("Resources"), g, dg)
        self.page = W.page("Resources", scroller, bottom=W.bottom_bar(None, W.button("Next", self.next, primary=True, pill=True)))

    def next(self):
        p = self.plan
        p.profile, p.ram_gb, p.cores = self.res.values()
        if not p.importing:
            p.disk_gb = int(self.size.get_value())
            p.disk_full = self.full.get_active()
            need = p.disk_gb << 30 if p.disk_full else 0
            if need and need > qemu.free_space(p.location or self.app.settings.vms_dir):
                W.tell(self.app.window, "Not enough free space", "Allocating %d GiB now needs that much free space on the drive." % p.disk_gb)
                return
        self.app.nav.push(SummaryPage(self.app, p).page)


# ---- Summary


class SummaryPage:
    def __init__(self, app, plan):
        self.app, self.plan = app, plan
        g = Adw.PreferencesGroup(title="Summary")
        cores, ram = config.VMConfig(profile=plan.profile, ram_gb=plan.ram_gb, cores=plan.cores).resources()
        rows = [("Name", plan.name), ("Description", plan.description or "None"),
                ("Location", paths.display(plan.location or app.settings.vms_dir)),
                ("Disk to import" if plan.importing else "Installer", plan.installer_text()),
                ("Memory", "%d GiB" % ram), ("CPU cores", str(cores)),
                ("Disk size", "%d GiB (%s)" % (plan.disk_gb, "Preallocated" if plan.disk_full else "Dynamic"))]
        for t, v in rows:
            r = Adw.ActionRow(title=t, subtitle=v, subtitle_selectable=True)
            r.add_css_class("property")
            g.add(r)
        scroller, _ = W.column(W.heading("Summary"), g)
        label = "Import" if plan.importing else "Install"
        self.page = W.page("Summary", scroller, bottom=W.bottom_bar(None, W.button(label, self.go, primary=True, pill=True)))

    def go(self):
        self.app.nav.push(ProgressPage(self.app, self.plan).page)


# ---- downloading, checking and making the system


class ProgressPage:
    def __init__(self, app, plan):
        self.app, self.plan = app, plan
        self.cancelled = False
        self.title = Gtk.Label(label="Getting ready", wrap=True)
        self.title.add_css_class("title-3")
        self.bar = Gtk.ProgressBar(show_text=False)
        self.detail = Gtk.Label(label="", wrap=True)
        self.detail.add_css_class("dim-label")
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14, valign=Gtk.Align.CENTER, vexpand=True)
        for w in (W.logo(7), self.title, self.bar, self.detail):
            box.append(w)
        self.cancel_btn = W.button("Cancel", self.cancel)
        self.page = W.page("Installing", Adw.Clamp(maximum_size=560, child=box), can_pop=False,
                           bottom=W.bottom_bar(self.cancel_btn, None))
        threading.Thread(target=self.work, daemon=True).start()

    def cancel(self):
        self.cancelled = True
        self.cancel_btn.set_sensitive(False)

    def set(self, title=None, fraction=None, detail=None):
        def apply():
            if title is not None:
                self.title.set_label(title)
            if fraction is None:
                self.bar.pulse()
            else:
                self.bar.set_fraction(max(0, min(1, fraction)))
            if detail is not None:
                self.detail.set_label(detail)

        W.ui(apply)

    def work(self):
        p, host = self.plan, self.app.host
        try:
            iso = ""
            if p.distro or p.url:
                label = p.distro.name if p.distro else p.label
                self.set("Finding the latest %s" % label, None, (p.distro.base if p.distro else p.url).split("/")[2])
                r = catalog.resolve(p.distro) if p.distro else catalog.from_url(p.url)
                title = "Downloading %s" % r.file_name

                def prog(done, total, speed):
                    frac = done / total if total > 0 else None
                    d = "%s of %s" % (download.human(done), download.human(total)) if total > 0 else download.human(done)
                    if speed:
                        d += "  ·  %s/s" % download.human(speed)
                    self.set(title, frac, d)

                iso = download.fetch(r, progress=prog, cancel=lambda: self.cancelled,
                                     verifying=lambda: self.set("Checking the download", None,
                                                                "Against the official checksum" if r.sha else "No official checksum to check against"))
            elif p.local_iso:
                iso = p.local_iso
            parent = p.location or self.app.settings.vms_dir
            folder = config.new_folder(parent, p.name)
            c = config.VMConfig(id=os.path.basename(folder), display_name=p.name, description=p.description,
                                distro_id=p.distro.id if p.distro else "", iso=iso, pending_install=bool(iso),
                                profile=p.profile, ram_gb=p.ram_gb, cores=p.cores, disk_gb=p.disk_gb, disk_full=p.disk_full,
                                gl=host.can_gl, created=config.now())
            c.dir = folder
            paths.ensure(folder)
            if p.importing:
                self.set("Importing %s" % os.path.basename(p.disk), 0, "Copying it to a new qcow2 disk; the original isn't changed")
                qemu.convert(host, p.disk, c.disk, lambda f: self.set(None, f, None))
            else:
                self.set("Making the virtual disk", None, "%d GiB, %s" % (p.disk_gb, "preallocated" if p.disk_full else "dynamic"))
                qemu.create_disk(host, c.disk, p.disk_gb, p.disk_full)
            c.save()
            if p.location and folder not in self.app.settings.systems:
                self.app.settings.systems.append(folder)
                self.app.settings.save()
        except download.Cancelled:
            W.ui(self.done, None, None)
            return
        except Exception as e:
            W.ui(self.done, None, str(e))
            return
        W.ui(self.done, c, None)

    def done(self, c, error):
        self.app.reload()
        self.app.nav.pop_to_tag("home")
        if error:
            W.tell(self.app.window, "Couldn't create the system", error)
            return
        if c:
            self.app.start(c)
