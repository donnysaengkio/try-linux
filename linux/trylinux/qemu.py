"""QEMU on this PC: what's installed, the command line for a system, and the disk tools (qemu-img)."""
import json
import os
import re
import shutil
import subprocess

from . import paths

# ---- what's installed


class Host:
    """QEMU, KVM and firmware on this PC, found once."""

    def __init__(self, arch=None):
        self.arch = arch or paths.arch()
        self.qemu = self._find(["qemu-system-" + self.arch] + (["/usr/libexec/qemu-kvm"] if self.arch == "x86_64" else []))
        self.img = self._find(["qemu-img"])
        self.kvm = os.access("/dev/kvm", os.R_OK | os.W_OK)
        self.kvm_exists = os.path.exists("/dev/kvm")
        self._devices = self._displays = self._audio = None
        self.version = ""
        if self.qemu:
            out = self._run([self.qemu, "--version"])
            m = re.search(r"version (\d+\.\d+(\.\d+)?)", out)
            self.version = m.group(1) if m else ""
        self.firmware = self._firmware() if self.arch == "aarch64" else None

    @staticmethod
    def _find(names):
        for n in names:
            p = n if os.path.isabs(n) and os.access(n, os.X_OK) else shutil.which(n)
            if p:
                return p
        return None

    @staticmethod
    def _run(cmd, timeout=15):
        try:
            return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout).stdout
        except (OSError, subprocess.SubprocessError):
            return ""

    def _help(self, what):
        if not self.qemu:
            return ""
        return self._run([self.qemu, "-" + what, "help"])

    @property
    def devices(self):
        if self._devices is None:
            self._devices = set(re.findall(r'name "([^"]+)"', self._help("device")))
        return self._devices

    @property
    def displays(self):
        if self._displays is None:
            self._displays = set(l.strip() for l in self._help("display").splitlines()[1:] if l.strip())
        return self._displays

    @property
    def audio(self):
        if self._audio is None:
            self._audio = set(l.strip() for l in self._help("audiodev").splitlines()[1:] if l.strip())
        return self._audio

    @property
    def gpu_device(self):
        return "virtio-vga" if self.arch == "x86_64" else "virtio-gpu-pci"

    @property
    def gl_device(self):
        return self.gpu_device.replace("virtio-vga", "virtio-vga-gl").replace("virtio-gpu-pci", "virtio-gpu-gl-pci")

    @property
    def can_gl(self):
        """3D acceleration (VirGL): QEMU's GL graphics device and a window that can show it."""
        return self.gl_device in self.devices and ("gtk" in self.displays or "sdl" in self.displays)

    @property
    def window(self):
        return "gtk" if "gtk" in self.displays or not self.displays else ("sdl" if "sdl" in self.displays else "")

    def _firmware(self):
        """UEFI firmware for ARM64 systems (code, and a template for its variables)."""
        pairs = [
            ("/usr/share/AAVMF/AAVMF_CODE.fd", "/usr/share/AAVMF/AAVMF_VARS.fd"),
            ("/usr/share/qemu-efi-aarch64/QEMU_EFI.fd", None),
            ("/usr/share/edk2/aarch64/QEMU_EFI-silent-pflash.raw", "/usr/share/edk2/aarch64/vars-template-pflash.raw"),
            ("/usr/share/edk2/aarch64/QEMU_EFI-pflash.raw", "/usr/share/edk2/aarch64/vars-template-pflash.raw"),
            ("/usr/share/edk2/aarch64/QEMU_CODE.fd", "/usr/share/edk2/aarch64/QEMU_VARS.fd"),
            ("/usr/share/qemu/edk2-aarch64-code.fd", "/usr/share/qemu/edk2-arm-vars.fd"),
            ("/usr/share/edk2-armvirt/aarch64/QEMU_CODE.fd", "/usr/share/edk2-armvirt/aarch64/QEMU_VARS.fd"),
        ]
        for code, vars_ in pairs:
            if os.path.exists(code):
                return code, vars_ if vars_ and os.path.exists(vars_) else None
        return None

    def missing(self):
        """What's missing before systems can run, as (problem, how to fix it) pairs."""
        out = []
        pkg = install_hint(self.arch)
        if not self.qemu or not self.img:
            out.append(("QEMU isn't installed.", pkg))
        if self.arch == "aarch64" and not self.firmware:
            out.append(("The UEFI firmware for ARM64 systems isn't installed.", pkg))
        if not self.kvm_exists:
            out.append(("KVM isn't available, so systems would run very slowly.",
                        "Turn on virtualization (Intel VT-x / AMD-V) in your PC's firmware settings."
                        if self.arch == "x86_64" else "This PC's kernel doesn't offer KVM."))
        elif not self.kvm:
            out.append(("You don't have access to KVM (/dev/kvm), so systems would run very slowly.",
                        "Add yourself to the kvm group (sudo usermod -aG kvm $USER), then sign out and back in."))
        return out


def install_hint(arch):
    """The packages to install, for the usual distributions."""
    distro = ""
    try:
        with open("/etc/os-release") as f:
            info = dict(l.rstrip().split("=", 1) for l in f if "=" in l)
        distro = (info.get("ID", "") + " " + info.get("ID_LIKE", "")).replace('"', "").lower()
    except OSError:
        pass
    arm = arch == "aarch64"
    if any(x in distro for x in ("fedora", "rhel", "centos")):
        return "sudo dnf install qemu-kvm qemu-img qemu-ui-gtk qemu-ui-opengl qemu-audio-pipewire " + \
               ("qemu-device-display-virtio-gpu-gl edk2-aarch64" if arm else "qemu-device-display-virtio-vga-gl")
    if "arch" in distro:
        return "sudo pacman -S qemu-desktop" + (" edk2-aarch64" if arm else "")
    if "suse" in distro:
        return "sudo zypper install qemu-kvm qemu-tools qemu-ui-gtk qemu-ui-opengl qemu-audio-pipewire" + (" qemu-uefi-aarch64" if arm else "")
    return "sudo apt install qemu-utils qemu-system-gui " + ("qemu-system-arm qemu-efi-aarch64" if arm else "qemu-system-x86")


# ---- the command line


def args(host, cfg, installer=False, qmp=None, gl=None, display=None, serial=None):
    """QEMU's arguments for `cfg`. `installer` starts it from its installer ISO. `display` overrides the
    window (the self-test uses "none"); `serial` sends the serial console to a file."""
    cores, ram = cfg.resources()
    arm = host.arch == "aarch64"
    gl = (cfg.gl and host.can_gl) if gl is None else gl
    accel = "kvm" if host.kvm else "tcg"
    a = ["-name", cfg.display_name or cfg.id]
    if arm:
        a += ["-machine", "virt,accel=%s,gic-version=max" % accel, "-cpu", "host" if host.kvm else "max"]
    else:
        a += ["-machine", "q35,accel=%s" % accel, "-cpu", "host" if host.kvm else "max"]
    a += ["-smp", str(cores), "-m", "%dG" % ram]
    if display is None:
        win = host.window or "gtk"
        a += ["-display", "%s%s,window-close=off" % (win, ",gl=on" if gl else "")]
    else:
        a += ["-display", display]
    a += ["-device", host.gl_device if gl else host.gpu_device]
    if arm and host.firmware:
        a += ["-drive", "if=pflash,format=raw,readonly=on,file=" + host.firmware[0],
              "-drive", "if=pflash,format=raw,file=" + cfg.efivars]
    # The boot disk first; a missing extra disk is left out.
    discard = ",discard=unmap,detect-zeroes=unmap" if cfg.reclaim else ""
    for i, d in enumerate(cfg.all_disks):
        if i == 0 or os.path.exists(d):
            a += ["-drive", "file=%s,format=qcow2,if=none,id=hd%d%s" % (_esc(d), i, discard),
                  "-device", "virtio-blk-pci,drive=hd%d%s" % (i, ",bootindex=1" if i == 0 else "")]
    if cfg.net_type == 2:
        a += ["-nic", "none"]
    else:
        # restrict=on keeps the system off this PC and the internet, but port forwards still reach it.
        net = "user,id=net0" + (",restrict=on" if cfg.net_type == 1 else "") + "".join("," + f.hostfwd() for f in cfg.forward_rules())
        a += ["-netdev", net, "-device", "virtio-net-pci,netdev=net0"]
    a += ["-device", "qemu-xhci", "-device", "usb-tablet"]
    if arm:
        a += ["-device", "usb-kbd"]
    # A CD/DVD drive whose disc can be changed while the system runs (QMP id "cd"). The installer is in it
    # (and boots first) while installing; otherwise the hard disk boots first.
    cd = cfg.iso if installer else (cfg.cd if cfg.cd and os.path.exists(cfg.cd) else "")
    a += ["-device", "virtio-scsi-pci,id=scsi0",
          "-drive", "if=none,id=cd0,media=cdrom,readonly=on" + (",format=raw,file=" + _esc(cd) if cd else ""),
          "-device", "scsi-cd,drive=cd0,id=cd" + (",bootindex=0" if installer else "")]
    if cfg.sound:
        backend = "pipewire" if "pipewire" in host.audio else ("pa" if "pa" in host.audio else None)
        if backend:
            a += ["-audiodev", "%s,id=snd0" % backend, "-device", "intel-hda",
                  "-device", "%s,audiodev=snd0" % ("hda-duplex" if cfg.mic else "hda-output")]
    if cfg.shared_folder and os.path.isdir(cfg.shared_folder):
        a += ["-virtfs", "local,path=%s,mount_tag=host,security_model=mapped-xattr,id=fs0%s"
              % (_esc(cfg.shared_folder), "" if cfg.shared_write else ",readonly=on")]
    if cfg.fullscreen and display is None:
        a += ["-full-screen"]
    if qmp:
        a += ["-qmp", "unix:%s,server=on,wait=off" % qmp]
    if serial:
        a += ["-serial", "file:" + serial]
    # Restarting Linux ends QEMU (a "guest-reset" shutdown) and Try Linux starts it again, without the
    # installer once Linux has been installed.
    a += ["-action", "reboot=shutdown"]
    return a


def _esc(path):
    """A path inside a -drive/-virtfs option, where a comma is written twice."""
    return path.replace(",", ",,")


def prepare_firmware(host, cfg):
    """An ARM64 system's own copy of the UEFI variables (made the first time it starts)."""
    if host.arch != "aarch64" or not host.firmware or os.path.exists(cfg.efivars):
        return
    code, vars_ = host.firmware
    if vars_:
        shutil.copyfile(vars_, cfg.efivars)
    else:
        with open(cfg.efivars, "wb") as f:
            f.truncate(os.path.getsize(code))


# ---- disks (qemu-img)


class DiskError(Exception):
    pass


def _img(host, *a, timeout=None):
    if not host.img:
        raise DiskError("qemu-img isn't installed.")
    p = subprocess.run([host.img] + list(a), capture_output=True, text=True, timeout=timeout)
    if p.returncode != 0:
        raise DiskError((p.stderr or p.stdout).strip().splitlines()[-1] if (p.stderr or p.stdout).strip() else "qemu-img failed.")
    return p.stdout


def create_disk(host, path, gb, full=False):
    _img(host, "create", "-f", "qcow2", "-o", "preallocation=" + ("falloc" if full else "off"), path, "%dG" % gb)


def info(host, path):
    try:
        return json.loads(_img(host, "info", "--output=json", "-U", path, timeout=30))
    except (DiskError, ValueError, subprocess.SubprocessError):
        return {}


def capacity(host, path):
    return int(info(host, path).get("virtual-size", 0))


def used(path):
    try:
        return os.stat(path).st_blocks * 512
    except OSError:
        return 0


def resize(host, path, gb):
    shrink = gb * (1 << 30) < capacity(host, path)
    _img(host, "resize", *(["--shrink"] if shrink else []), path, "%dG" % gb)


def free_space(folder):
    while folder and not os.path.exists(folder):
        folder = os.path.dirname(folder)
    try:
        return shutil.disk_usage(folder or "/").free
    except OSError:
        return 0


def convert(host, src, dest, progress=None, compress=False):
    """Copies a disk (any format QEMU reads) to a new qcow2 file, reporting progress 0..1."""
    if not host.img:
        raise DiskError("qemu-img isn't installed.")
    cmd = [host.img, "convert", "-p", "-O", "qcow2"] + (["-c"] if compress else []) + [src, dest]
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)
    buf = ""
    while True:
        ch = p.stdout.read(1)
        if not ch:
            break
        if ch in "\r\n":
            m = re.search(r"\((\d+(\.\d+)?)/100%\)", buf)
            if m and progress:
                progress(float(m.group(1)) / 100)
            buf = ""
        else:
            buf += ch
    err = p.stderr.read()
    if p.wait() != 0:
        raise DiskError(err.strip().splitlines()[-1] if err.strip() else "qemu-img convert failed.")


def compact(host, path, progress=None):
    """Gives a disk's unused space back to the PC, by copying it to a new file."""
    tmp = path + ".compact"
    try:
        convert(host, path, tmp, progress)
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)


def snapshots(host, path):
    """The disk's snapshots as (name, date) pairs, oldest first."""
    # ID  TAG  VM SIZE  DATE  VM CLOCK  ICOUNT, e.g. "1  before-update  0 B 2026-11-07 10:00:00 ..."
    out = []
    text = _img(host, "snapshot", "-l", "-U", path)
    for line in text.splitlines():
        m = re.match(r"^\s*\d+\s+(\S+)\s+\S+(\s+\S+)?\s+(\d{4}-\d\d-\d\d \d\d:\d\d)", line)
        if m:
            out.append((m.group(1), m.group(3)))
    return out


def snapshot(host, path, action, name):
    _img(host, "snapshot", {"take": "-c", "revert": "-a", "delete": "-d"}[action], name, path)


# ---- importing a disk from another app


DISK_TYPES = ["qcow2", "vmdk", "vhd", "vhdx", "vdi", "img", "raw"]


def read_vmx(path):
    """Name, description, memory (GiB) and cores from a VMware .vmx file beside the disk, if there is one."""
    folder = os.path.dirname(path)
    out = {}
    try:
        vmx = next((os.path.join(folder, n) for n in os.listdir(folder) if n.lower().endswith(".vmx")), None)
    except OSError:
        vmx = None
    if not vmx:
        return out
    try:
        with open(vmx, encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError:
        return out
    get = lambda k: (re.search(r'^\s*%s\s*=\s*"([^"]*)"' % re.escape(k), text, re.M | re.I) or [None, None])[1]
    if get("displayName"):
        out["name"] = get("displayName")
    if get("annotation"):
        out["description"] = get("annotation").replace("|0A", "\n")
    if (get("memsize") or "").isdigit():
        out["ram_gb"] = max(1, int(get("memsize")) // 1024)
    if (get("numvcpus") or "").isdigit():
        out["cores"] = int(get("numvcpus"))
    out["from"] = "VMware"
    return out
