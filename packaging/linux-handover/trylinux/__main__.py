"""Moves Try Linux 1.0.x to Try Linux 2: downloads the AppImage for this processor from the release, checks it
against the release's SHA256SUMS.txt, puts it beside this folder (in ~/.local/share/try-linux) and starts it.
Try Linux 2 then takes over: your apps menu, the try-linux command, your systems' shortcuts and sign-in entries
point at the AppImage, and 1.0.x's Python files go. Your systems, downloads and settings are the same files.

While it downloads, a small window shows the progress (GTK 4, which 1.0.x needed; without it, it just works).
Once the AppImage is in place, starting this again starts the AppImage straight away."""
import hashlib
import os
import platform
import sys
import threading
import urllib.request

from . import REPO, TAG_PREFIX, VERSION

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))     # ~/.local/share/try-linux
APPIMAGE = os.path.join(HERE, "TryLinux.AppImage")
# TRYLINUX_HANDOVER_BASE: another place with the release's files, for testing before it's published.
BASE = os.environ.get("TRYLINUX_HANDOVER_BASE") or "https://github.com/%s/releases/download/%s%s/" % (REPO, TAG_PREFIX, VERSION)
PAGE = "https://github.com/%s/releases/tag/%s%s" % (REPO, TAG_PREFIX, VERSION)


def arch():
    m = platform.machine().lower()
    return {"amd64": "x86_64", "arm64": "aarch64"}.get(m, m)


def fetch(url, timeout=30):
    return urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "TryLinux/1.0"}), timeout=timeout)


def download(progress):
    """Downloads and checks the AppImage into place. progress(fraction) as it goes."""
    name = "TryLinux-%s-%s.AppImage" % (VERSION, arch())
    with fetch(BASE + "SHA256SUMS.txt") as r:
        sums = r.read().decode()
    want = next((l.split()[0].lower() for l in sums.splitlines() if l.strip().endswith(name)), None)
    if not want:
        raise RuntimeError("Try Linux 2 isn't available for this processor (%s) yet." % arch())
    part = APPIMAGE + ".part"
    h = hashlib.sha256()
    with fetch(BASE + name, timeout=60) as r, open(part, "wb") as f:
        total, done = int(r.headers.get("Content-Length") or 0), 0
        while True:
            b = r.read(1 << 16)
            if not b:
                break
            f.write(b)
            h.update(b)
            done += len(b)
            if total:
                progress(done / total)
    if h.hexdigest() != want:
        os.remove(part)
        raise RuntimeError("The download doesn't match its published checksum, so it wasn't used. Try again later.")
    os.chmod(part, 0o755)
    os.replace(part, APPIMAGE)


def start():
    os.execv(APPIMAGE, [APPIMAGE] + sys.argv[1:])


def with_window():
    import gi
    gi.require_version("Gtk", "4.0")
    from gi.repository import GLib, Gtk

    app = Gtk.Application(application_id="io.github.donnysaengkio.TryLinux.Update")
    state = {"error": None}

    def activate(app):
        win = Gtk.ApplicationWindow(application=app, title="Updating Try Linux", default_width=420, resizable=False)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12, margin_top=24, margin_bottom=24, margin_start=24, margin_end=24)
        label = Gtk.Label(label="Downloading Try Linux %s…" % VERSION, xalign=0, wrap=True)
        bar = Gtk.ProgressBar()
        box.append(label)
        box.append(bar)
        win.set_child(box)
        win.present()

        def work():
            try:
                download(lambda f: GLib.idle_add(bar.set_fraction, f))
            except Exception as e:
                state["error"] = str(e)
            GLib.idle_add(finished)

        def finished():
            if state["error"]:
                label.set_text("Couldn't update Try Linux: %s\n\nDownload Try Linux %s from %s" % (state["error"], VERSION, PAGE))
                label.set_selectable(True)
                bar.set_visible(False)
            else:
                app.quit()

        threading.Thread(target=work, daemon=True).start()

    app.connect("activate", activate)
    app.run([])
    if state["error"]:
        return 1
    start()


def main():
    if os.access(APPIMAGE, os.X_OK):
        start()
    try:
        return with_window()
    except (ImportError, ValueError):
        pass
    try:
        download(lambda f: None)
    except Exception as e:
        print("Couldn't update Try Linux: %s\nDownload Try Linux %s from %s" % (e, VERSION, PAGE), file=sys.stderr)
        return 1
    start()
