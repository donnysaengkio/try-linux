"""The distributions Try Linux offers, and how it finds and checks their installers.

The built-in list depends on the PC: x86_64 PCs get the same five as Try Linux for Windows, ARM64 PCs the
same four as Try Linux for Mac. Search adds the lists kept in the try-linux repository: catalog/distros.json
(x86_64 installers, and distributions Try Linux can't download itself, which you get from their website) and,
on ARM64, catalog/distros-aarch64.json (ARM64 installers) in place of the x86_64 ones."""
import json
import os
import re
import urllib.parse
from dataclasses import dataclass, field

from . import net, paths

CATALOG_BASE = "https://raw.githubusercontent.com/donnysaengkio/try-linux/main/catalog/"


def catalog_files(arch=None):
    """The lists to read, with whether each one's downloads are for this PC."""
    if (arch or paths.arch()) == "aarch64":
        return [("distros-aarch64.json", True), ("distros.json", False)]
    return [("distros.json", True)]


@dataclass
class Distro:
    id: str
    name: str
    about: str
    base: str = None             # an official mirror folder; None: download from the home page by hand
    # Each step is added to `base`. A step starting with ~ is a regex matched against the folder's links;
    # the newest match wins.
    steps: list = field(default_factory=list)
    checksum: str = None         # the checksum file beside the ISO ({iso} is its name; a leading ~ is a regex)
    home: str = None             # the distribution's home page (https)
    builtin: bool = False

    @property
    def website_only(self):
        return self.base is None


X86_64 = [
    Distro("fedora", "Fedora Workstation",
           "A community distribution sponsored by Red Hat that ships new GNOME, kernel and Mesa releases soon after they come out",
           "https://mirrors.kernel.org/fedora/releases/",
           [r"~^\d+/$", "Workstation/x86_64/iso/", r"~^Fedora-Workstation-Live-.*\.iso$"], "~CHECKSUM$",
           "https://fedoraproject.org/", True),
    Distro("ubuntu", "Ubuntu Desktop",
           "The most widely used desktop Linux, made by Canonical, with long-term support releases and a large software catalog",
           "https://releases.ubuntu.com/",
           [r"~^\d\d\.\d\d(\.\d+)?/$", r"~^ubuntu-.*-desktop-amd64\.iso$"], "SHA256SUMS",
           "https://ubuntu.com/", True),
    Distro("debian", "Debian (GNOME)",
           "One of the oldest community distributions, known for stability and the base of Ubuntu, Mint and many others",
           "https://cdimage.debian.org/debian-cd/current-live/amd64/iso-hybrid/",
           [r"~^debian-live-.*-amd64-gnome\.iso$"], "SHA256SUMS",
           "https://www.debian.org/", True),
    Distro("mint", "Linux Mint (Cinnamon)",
           "A beginner-friendly distribution based on Ubuntu, with the Windows-like Cinnamon desktop and media codecs available at install",
           "https://mirrors.edge.kernel.org/linuxmint/stable/",
           [r"~^\d+(\.\d+)?/$", r"~^linuxmint-.*-cinnamon-64bit\.iso$"], "sha256sum.txt",
           "https://linuxmint.com/", True),
    Distro("zorin", "Zorin OS Core",
           "A polished distribution based on Ubuntu, designed to feel familiar to people moving from Windows",
           "https://mirrors.edge.kernel.org/zorinos-isos/",
           [r"~^\d+/$", r"~^Zorin-OS-(?<v>\d+(\.\d+)*)-Core-64-bit(-r\d+)?\.iso$"], "SHA256SUMS.txt",
           "https://zorin.com/os/", True),
]

AARCH64 = [
    Distro("fedora", "Fedora Workstation",
           "A community distribution sponsored by Red Hat that ships new GNOME, kernel and Mesa releases soon after they come out",
           "https://mirrors.kernel.org/fedora/releases/",
           [r"~^\d+/$", "Workstation/aarch64/iso/", r"~^Fedora-Workstation-Live-.*\.aarch64\.iso$"], "~CHECKSUM$",
           "https://fedoraproject.org/", True),
    Distro("ubuntu", "Ubuntu Desktop",
           "The most widely used desktop Linux, made by Canonical, with long-term support releases and a large software catalog",
           "https://cdimage.ubuntu.com/releases/",
           [r"~^\d\d\.\d\d(\.\d+)?/$", "release/", r"~^ubuntu-.*-desktop-arm64\.iso$"], "SHA256SUMS",
           "https://ubuntu.com/", True),
    Distro("debian", "Debian",
           "One of the oldest community distributions, known for stability and the base of Ubuntu, Mint and many others",
           "https://cdimage.debian.org/debian-cd/current/arm64/iso-cd/",
           [r"~^debian-.*-arm64-netinst\.iso$"], "SHA256SUMS",
           "https://www.debian.org/", True),
    Distro("opensuse", "openSUSE Tumbleweed",
           "A rolling-release distribution from the openSUSE community that always has the newest KDE Plasma and kernel",
           "https://download.opensuse.org/ports/aarch64/tumbleweed/iso/",
           ["openSUSE-Tumbleweed-KDE-Live-aarch64-Current.iso"], "{iso}.sha256",
           "https://www.opensuse.org/", True),
]


def builtin(arch=None):
    return AARCH64 if (arch or paths.arch()) == "aarch64" else X86_64


# ---- the list from the repository

_loaded = None


def _str(d, k):
    v = d.get(k)
    return v if isinstance(v, str) else None


def parse(text, arch=None, downloads=None, taken=None):
    """The entries of a distros list that this PC can use, skipping any whose id is taken (the built-in ones
    included). `downloads` says whether its downloads are for this PC (by default: x86_64 PCs, for
    distros.json); its website-only entries are always kept."""
    arch = arch or paths.arch()
    downloads = (arch == "x86_64") if downloads is None else downloads
    root = json.loads(text)
    if not isinstance(root, dict) or root.get("version") != 2 or not isinstance(root.get("distros"), list):
        raise ValueError("not a version 2 distribution list")
    taken = set(taken or ()) | {d.id for d in builtin(arch)}
    out = []
    for d in root["distros"]:
        if not isinstance(d, dict):
            continue
        did, name = _str(d, "id"), _str(d, "name")
        if not did or not name or not re.match(r"^[a-z0-9-]{1,40}$", did) or did in taken:
            continue
        home = _str(d, "home")
        home = home if home and home.startswith("https://") else None
        base, steps = _str(d, "base"), d.get("steps")
        if base is None and steps is None:
            if home:
                out.append(Distro(did, name, _str(d, "about") or "", home=home))
                taken.add(did)
            continue
        if not downloads or not base or not base.startswith("https://") or not isinstance(steps, list):
            continue
        if not all(isinstance(s, str) for s in steps):
            continue
        out.append(Distro(did, name, _str(d, "about") or "", base, list(steps), _str(d, "checksum"), home))
        taken.add(did)
    return out


def _cache_file(name):
    return os.path.join(paths.CATALOG, name)


def _combine(texts, arch=None):
    """The lists' entries together, in order; a later list doesn't repeat an earlier one's ids."""
    out, taken = [], set()
    for (name, downloads), text in zip(catalog_files(arch), texts):
        if not text:
            continue
        try:
            entries = parse(text, arch, downloads, taken)
        except ValueError:
            continue
        out += entries
        taken |= {d.id for d in entries}
    return out


def cached():
    """The lists as last downloaded (empty until the first download)."""
    global _loaded
    if _loaded is None:
        texts = []
        for name, _ in catalog_files():
            try:
                with open(_cache_file(name), encoding="utf-8") as f:
                    texts.append(f.read())
            except OSError:
                texts.append("")
        _loaded = _combine(texts)
    return _loaded


def _fetch(name, source):
    """One list's text: from `source` (a folder or a file in it, for tests), or downloaded again only when it
    has changed (its ETag); the cached copy otherwise, and offline."""
    import urllib.error
    import urllib.request
    if source and not source.startswith("http"):
        folder = source.replace("file://", "")
        folder = folder if os.path.isdir(folder) else os.path.dirname(folder)
        try:
            with open(os.path.join(folder, name), encoding="utf-8") as f:
                return f.read()
        except OSError:
            return ""
    cache, etag_file = _cache_file(name), _cache_file(name + ".etag")
    old = ""
    if os.path.exists(cache):
        with open(cache, encoding="utf-8") as f:
            old = f.read()
    headers = {}
    saved = _saved_etag(etag_file, old)
    if saved:
        headers["If-None-Match"] = saved
    try:
        with urllib.request.urlopen(net.request((source or CATALOG_BASE) + name, headers=headers), timeout=net.TIMEOUT) as r:
            text = r.read().decode("utf-8")
            etag = r.headers.get("ETag")
        parse(text, downloads=False)   # a list, not an error page
    except Exception:
        return old
    with open(cache, "w", encoding="utf-8") as f:
        f.write(text)
    if etag:
        # With a fingerprint of the list it came with, so it's only ever sent for that list.
        with open(etag_file, "w", encoding="utf-8") as f:
            f.write(etag + "\n" + _fingerprint(text))
    return text


def _fingerprint(text):
    import hashlib
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _saved_etag(etag_file, cached):
    """The saved ETag, if it belongs to the cached list. One saved without its list (a newer ETag beside an
    older list) would get "not modified" for a list this computer never had, so it's dropped and the whole
    list is downloaded again."""
    if not cached or not os.path.exists(etag_file):
        return None
    try:
        with open(etag_file, encoding="utf-8") as f:
            lines = f.read().split("\n")
    except OSError:
        return None
    if len(lines) < 2 or lines[1].strip() != _fingerprint(cached):
        return None
    return lines[0].strip() or None


def refresh(source=None):
    """Checks the lists in the repository (each downloaded again only when it has changed) and returns the
    combined list. `source` is a folder (or a file in it) to read them from instead."""
    global _loaded
    paths.ensure(paths.CATALOG)
    _loaded = _combine([_fetch(name, source) for name, _ in catalog_files()])
    return _loaded


def all_distros():
    return builtin() + cached()


def find(did):
    for d in all_distros():
        if d.id == did:
            return d
    return None


# ---- finding the newest installer on a mirror

class ResolveError(Exception):
    pass


@dataclass
class ResolvedISO:
    url: str
    file_name: str
    sha: str = None       # SHA-256 (64 hex digits) or SHA-512 (128)
    size: int = -1


def py_pattern(p):
    """.NET-style named groups (?<v>...) as Python writes them (?P<v>...)."""
    return re.sub(r"\(\?<(?=[A-Za-z_])", "(?P<", p)


def last_segment(url):
    path = urllib.parse.urlparse(url).path
    return urllib.parse.unquote(path[path.rfind("/") + 1:])


def safe_name(name):
    return re.sub(r'[/\\:*?"<>|\x00-\x1f]', "_", name).strip() or "installer.iso"


def compare_versions(a, b):
    # Architecture tags are not version numbers.
    strip = lambda s: re.sub(r"(x86_|amd)64|aarch64|arm64|64-?bit", "", s, flags=re.I)
    na = [int(x) for x in re.findall(r"\d+", strip(a))]
    nb = [int(x) for x in re.findall(r"\d+", strip(b))]
    for x, y in zip(na, nb):
        if x != y:
            return -1 if x < y else 1
    if len(na) != len(nb):
        return -1 if len(na) < len(nb) else 1
    return (a > b) - (a < b)


def matches(url, pattern, get=net.get_text):
    """The folder's links that match `pattern`, newest first."""
    html = get(url)
    re_ = re.compile(py_pattern(pattern))
    found = set()
    for h in re.findall(r'href="([^"?#]+)"', html, re.I):
        is_dir = h.endswith("/")
        seg = h.rstrip("/")
        seg = seg[seg.rfind("/") + 1:]
        if not seg or seg in ("..", "."):
            continue
        if is_dir:
            seg += "/"
        if re_.search(urllib.parse.unquote(seg)):
            found.add(seg)

    def version(s):
        m = re_.search(urllib.parse.unquote(s))
        if m and "v" in re_.groupindex and m.group("v"):
            return m.group("v")
        return s

    import functools

    def cmp(a, b):
        c = compare_versions(version(a), version(b))
        return c if c else compare_versions(a, b)

    return sorted(found, key=functools.cmp_to_key(cmp), reverse=True)


def walk(url, steps, get=net.get_text):
    if not steps:
        return url
    step, rest = steps[0], steps[1:]
    if not step.startswith("~"):
        return walk(url + step, rest, get)
    # Newest first; fall back to an older entry when the newest isn't fully published yet.
    for m in matches(url, step[1:], get)[:3]:
        try:
            found = walk(url + m, rest, get)
            if found:
                return found
        except net.NetError:
            pass
    return None


HEX = re.compile(r"\b([0-9a-fA-F]{128}|[0-9a-fA-F]{64})\b")


def find_hash(iso_url, spec, file_name, get=net.get_text):
    folder = iso_url[:iso_url.rfind("/") + 1]
    if spec.startswith("~"):
        names = matches(folder, spec[1:], get)
        if not names:
            return None
        name = names[0]
    else:
        name = spec.replace("{iso}", iso_url[len(folder):])
    text = get(folder + name)
    for line in text.splitlines():
        if file_name in line:
            m = HEX.search(line)
            if m:
                return m.group(1).lower()
    # A checksum file for a single image (e.g. openSUSE's *-Current.iso.sha256).
    found = {m.lower() for m in HEX.findall(text)}
    return found.pop() if len(found) == 1 else None


def resolve(d, get=net.get_text, head=net.head_size):
    if d.website_only:
        raise ResolveError("Try Linux can't download %s itself." % d.name)
    url = walk(d.base, list(d.steps), get)
    if not url:
        raise ResolveError("Couldn't find the current %s download. The mirror may be updating; try again later." % d.name)
    r = ResolvedISO(url, last_segment(url))
    r.size = head(url)
    if d.checksum:
        try:
            r.sha = find_hash(url, d.checksum, r.file_name, get)
        except net.NetError:
            pass
    return r


def from_url(url, head=net.head_size):
    name = last_segment(url)
    if not name.lower().endswith(".iso"):
        name += ".iso"
    return ResolvedISO(url, safe_name(name), None, head(url))
