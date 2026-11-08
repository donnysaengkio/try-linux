"""Where Try Linux keeps things, following the XDG base directories.

TRYLINUX_DATA and TRYLINUX_CONFIG override the folders (the self-test uses a scratch folder)."""
import os
import platform

HOME = os.path.expanduser("~")


def _xdg(var, default):
    v = os.environ.get(var, "")
    return v if os.path.isabs(v) else os.path.join(HOME, default)


DATA = os.environ.get("TRYLINUX_DATA") or os.path.join(_xdg("XDG_DATA_HOME", ".local/share"), "TryLinux")
CONFIG = os.environ.get("TRYLINUX_CONFIG") or os.path.join(_xdg("XDG_CONFIG_HOME", ".config"), "TryLinux")
APPS = os.path.join(_xdg("XDG_DATA_HOME", ".local/share"), "applications")
AUTOSTART = os.path.join(_xdg("XDG_CONFIG_HOME", ".config"), "autostart")

ISOS = os.path.join(DATA, "isos")
CATALOG = os.path.join(DATA, "catalog")
DEFAULT_VMS = os.path.join(DATA, "vms")


def arch():
    """The host's architecture as distributions name it: x86_64 or aarch64."""
    m = platform.machine().lower()
    if m in ("amd64", "x86_64", "x64"):
        return "x86_64"
    if m in ("arm64", "aarch64", "armv8l"):
        return "aarch64"
    return m


def display(path):
    """A path as the app shows it, with the home folder written as ~."""
    if path == HOME:
        return "~"
    if path.startswith(HOME + os.sep):
        return "~" + path[len(HOME):]
    return path


def ensure(path):
    os.makedirs(path, exist_ok=True)
    return path
