"""Settings: the app's own (settings.json in the config folder) and each system's (vm.json in its folder)."""
import json
import os
import re
import shutil
import time
from dataclasses import asdict, dataclass, field, fields

from . import paths

# ---- the app


@dataclass
class AppSettings:
    theme: str = "dark"            # dark or light
    follow_system: bool = False
    storage: str = ""              # where new systems go; "" is the default (~/.local/share/TryLinux/vms)
    check_updates: bool = True
    systems: list = field(default_factory=list)   # folders of systems kept outside the storage folder

    @property
    def vms_dir(self):
        return self.storage or paths.DEFAULT_VMS

    @staticmethod
    def file():
        return os.path.join(paths.CONFIG, "settings.json")

    @classmethod
    def load(cls):
        s = cls()
        try:
            with open(cls.file(), encoding="utf-8") as f:
                data = json.load(f)
            for k in (f.name for f in fields(cls)):
                if k in data:
                    setattr(s, k, data[k])
        except (OSError, ValueError):
            pass
        if s.theme not in ("dark", "light"):
            s.theme = "dark"
        return s

    def save(self):
        paths.ensure(paths.CONFIG)
        tmp = self.file() + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(asdict(self), f, indent=1)
        os.replace(tmp, self.file())


# ---- a system

PROFILES = ["Balanced", "Maximum", "Manual"]
NET_TYPES = ["Shared with this PC", "Port forwards only", "No network"]


def host_cores():
    return os.cpu_count() or 2


def host_ram_gb():
    try:
        with open("/proc/meminfo") as f:
            for line in f:
                if line.startswith("MemTotal:"):
                    return max(1, int(line.split()[1]) // (1024 * 1024))
    except OSError:
        pass
    return 8


def max_cores():
    """What a system may use: the PC keeps 2 cores."""
    return max(1, host_cores() - 2)


def max_ram_gb():
    """What a system may use: the PC keeps a quarter of its memory (at least 2 GiB)."""
    total = host_ram_gb()
    return max(1, total - max(2, total // 4))


@dataclass
class Forward:
    proto: str
    host: int
    guest: int

    def hostfwd(self):
        return "hostfwd=%s::%d-:%d" % (self.proto, self.host, self.guest)

    def __str__(self):
        return "%s:%d:%d" % (self.proto, self.host, self.guest)


def parse_forwards(lines):
    """Port forwards written as tcp:2222:22 (or udp:...), one a line. Returns (forwards, problems)."""
    out, bad = [], []
    for raw in lines:
        line = raw.strip()
        if not line:
            continue
        m = re.match(r"^(tcp|udp):(\d{1,5}):(\d{1,5})$", line, re.I)
        if not m or not (0 < int(m.group(2)) < 65536 and 0 < int(m.group(3)) < 65536):
            bad.append(line)
            continue
        out.append(Forward(m.group(1).lower(), int(m.group(2)), int(m.group(3))))
    return out, bad


@dataclass
class VMConfig:
    id: str = ""
    display_name: str = ""
    description: str = ""
    distro_id: str = ""
    iso: str = ""                  # the installer it was made from
    pending_install: bool = False  # starts from the installer until Linux restarts after installing
    profile: int = 0
    ram_gb: int = 4
    cores: int = 2
    disk_gb: int = 64
    disk_full: bool = False        # preallocated: the whole size taken on the drive when it was made
    gl: bool = True                # 3D acceleration (VirGL)
    fullscreen: bool = False
    shared_folder: str = ""
    shared_write: bool = False
    sound: bool = True
    mic: bool = False
    cd: str = ""
    net_type: int = 0
    forwards: list = field(default_factory=lambda: ["tcp:2222:22"])
    extra_disks: list = field(default_factory=list)
    reclaim: bool = True           # pass TRIM through, so deleted files' space is reused
    last_boot: str = ""
    created: str = ""
    dir: str = field(default="", metadata={"skip": True})

    @property
    def disk(self):
        return os.path.join(self.dir, "disk.qcow2")

    @property
    def all_disks(self):
        return [self.disk] + [d if os.path.isabs(d) else os.path.join(self.dir, d) for d in self.extra_disks]

    @property
    def efivars(self):
        return os.path.join(self.dir, "efivars.fd")

    def resources(self):
        """(cores, ram_gb) for the chosen profile, within what the PC can spare."""
        mc, mr = max_cores(), max_ram_gb()
        if self.profile == 0:
            return max(1, min(mc, max(2, min(8, host_cores() // 2)))), max(1, min(mr, max(2, min(8, host_ram_gb() // 4))))
        if self.profile == 1:
            return mc, mr
        return max(1, min(mc, self.cores)), max(1, min(mr, self.ram_gb))

    def forward_rules(self):
        return parse_forwards(self.forwards)[0]

    @staticmethod
    def file_in(folder):
        return os.path.join(folder, "vm.json")

    @classmethod
    def load(cls, folder):
        try:
            with open(cls.file_in(folder), encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError):
            return None
        c = cls()
        for fl in fields(cls):
            if fl.name in data and fl.name != "dir":
                setattr(c, fl.name, data[fl.name])
        c.dir = folder
        if not c.id:
            c.id = os.path.basename(folder.rstrip("/"))
        return c

    def save(self):
        paths.ensure(self.dir)
        data = {k: v for k, v in asdict(self).items() if k != "dir"}
        tmp = self.file_in(self.dir) + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=1)
        os.replace(tmp, self.file_in(self.dir))


def slug(name):
    s = re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")
    return s[:40] or "linux"


def new_folder(parent, name):
    """A new, unused folder for a system called `name`."""
    paths.ensure(parent)
    base = slug(name)
    folder, n = os.path.join(parent, base), 2
    while os.path.exists(folder):
        folder, n = os.path.join(parent, "%s-%d" % (base, n)), n + 1
    return folder


def unique_name(name, taken):
    """`name`, or name 2, name 3... when another system already has it."""
    if name not in taken:
        return name
    n = 2
    while "%s %d" % (name, n) in taken:
        n += 1
    return "%s %d" % (name, n)


def systems(settings):
    """Every system: the ones in the storage folder, then any kept elsewhere."""
    found, seen = [], set()
    folders = []
    if os.path.isdir(settings.vms_dir):
        folders += [os.path.join(settings.vms_dir, n) for n in sorted(os.listdir(settings.vms_dir))]
    folders += settings.systems
    for folder in folders:
        real = os.path.realpath(folder)
        if real in seen:
            continue
        c = VMConfig.load(folder)
        if c:
            seen.add(real)
            found.append(c)
    return found


def now():
    return time.strftime("%Y-%m-%d %H:%M")


def delete_system(c, settings):
    shutil.rmtree(c.dir, ignore_errors=True)
    if c.dir in settings.systems:
        settings.systems.remove(c.dir)
        settings.save()
