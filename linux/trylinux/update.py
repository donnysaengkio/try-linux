"""Updates: the newest release on GitHub, and installing it over a copy that install.sh put in place."""
import hashlib
import json
import os
import re
import shutil
import sys
import tarfile
import tempfile

from . import REPO, VERSION, net

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # holds trylinux/ and try-linux
MARKER = os.path.join(APP_DIR, ".installed")                          # written by install.sh


def version_key(v):
    return [int(x) for x in re.findall(r"\d+", v)[:3]]


class Release:
    def __init__(self, data):
        self.tag = data["tag_name"]
        self.version = re.sub(r"^v|-preview$", "", self.tag)
        self.url = data.get("html_url", "")
        self.notes = data.get("body", "")
        self.assets = {a["name"]: a["browser_download_url"] for a in data.get("assets", [])}

    @property
    def archive(self):
        return next((n for n in self.assets if n.endswith("-linux.tar.gz")), None)


def latest(api="https://api.github.com/repos/%s/releases?per_page=10" % REPO):
    """The newest release (previews included), or None."""
    for r in json.loads(net.get_text(api)):
        if not r.get("draft"):
            return Release(r)
    return None


def newer(release):
    return release is not None and version_key(release.version) > version_key(VERSION)


def can_install():
    """Whether this copy can update itself: one install.sh put in place, in a folder you can write to."""
    return os.path.exists(MARKER) and os.access(APP_DIR, os.W_OK)


def install(release, progress=None):
    """Downloads the release, checks it against its SHA256SUMS.txt, and puts it in place of this copy."""
    name = release.archive
    if not name or "SHA256SUMS.txt" not in release.assets:
        raise RuntimeError("This release has no Linux download.")
    sums = net.get_text(release.assets["SHA256SUMS.txt"])
    want = next((l.split()[0].lower() for l in sums.splitlines() if l.strip().endswith(name)), None)
    if not want:
        raise RuntimeError("The release's checksum list doesn't include %s." % name)
    tmp = tempfile.mkdtemp(prefix="trylinux-update-")
    try:
        path = os.path.join(tmp, name)
        h = hashlib.sha256()
        with net.open_url(release.assets[name], timeout=60) as r, open(path, "wb") as f:
            total = int(r.headers.get("Content-Length") or 0)
            done = 0
            while True:
                b = r.read(1 << 16)
                if not b:
                    break
                f.write(b)
                h.update(b)
                done += len(b)
                if progress and total:
                    progress(done / total)
        if h.hexdigest() != want:
            raise RuntimeError("The downloaded update doesn't match its published checksum, so it wasn't used. Try again later.")
        out = os.path.join(tmp, "x")
        with tarfile.open(path) as t:
            for m in t.getmembers():
                if m.name.startswith("/") or ".." in m.name.split("/") or m.issym() or m.islnk():
                    raise RuntimeError("The update contains an unexpected file.")
            t.extractall(out)
        roots = os.listdir(out)
        src = os.path.join(out, roots[0]) if len(roots) == 1 else out
        if not os.path.isdir(os.path.join(src, "trylinux")):
            raise RuntimeError("The update isn't laid out as expected.")
        old = os.path.join(APP_DIR, "trylinux")
        backup = old + ".old"
        shutil.rmtree(backup, ignore_errors=True)
        os.rename(old, backup)
        try:
            shutil.copytree(os.path.join(src, "trylinux"), old)
        except Exception:
            shutil.rmtree(old, ignore_errors=True)
            os.rename(backup, old)
            raise
        for f in ("try-linux", "README.md", "LICENSE"):
            if os.path.exists(os.path.join(src, f)):
                shutil.copy2(os.path.join(src, f), os.path.join(APP_DIR, f))
        shutil.rmtree(backup, ignore_errors=True)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def relaunch():
    launcher = os.path.join(APP_DIR, "try-linux")
    os.execv(sys.executable, [sys.executable, launcher] if os.path.exists(launcher) else [sys.executable, "-m", "trylinux"])
