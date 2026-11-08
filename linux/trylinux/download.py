"""Downloads an installer, checking it against the distribution's published SHA-256 or SHA-512 checksum.

A partly downloaded file (.part) is resumed when the server supports it."""
import hashlib
import os
import time
import urllib.error
import urllib.request

from . import net, paths


class Cancelled(Exception):
    pass


class ChecksumError(Exception):
    pass


def hasher(sha):
    return hashlib.sha512() if sha and len(sha) == 128 else hashlib.sha256()


def file_hash(path, sha_hint=None, cancel=None, progress=None):
    h = hasher(sha_hint)
    total, done = os.path.getsize(path), 0
    with open(path, "rb") as f:
        while True:
            if cancel and cancel():
                raise Cancelled()
            b = f.read(4 << 20)
            if not b:
                break
            h.update(b)
            done += len(b)
            if progress:
                progress(done, total, 0)
    return h.hexdigest()


def fetch(resolved, folder=None, progress=None, cancel=None, verifying=None):
    """Downloads `resolved` (a catalog.ResolvedISO) into `folder` and returns the file's path. An ISO that's
    already there with the right checksum is used as it is. `progress(done, total, bytes_per_second)`."""
    folder = paths.ensure(folder or paths.ISOS)
    dest = os.path.join(folder, resolved.file_name)
    if os.path.exists(dest) and resolved.sha:
        if verifying:
            verifying()
        if file_hash(dest, resolved.sha, cancel, progress) == resolved.sha:
            return dest
    part = dest + ".part"
    h = hasher(resolved.sha)
    have = os.path.getsize(part) if os.path.exists(part) else 0
    if have:
        with open(part, "rb") as f:
            while True:
                b = f.read(4 << 20)
                if not b:
                    break
                h.update(b)
    headers = {"Range": "bytes=%d-" % have} if have else {}
    try:
        r = urllib.request.urlopen(net.request(resolved.url, headers=headers), timeout=net.TIMEOUT)
    except urllib.error.HTTPError as e:
        if e.code == 416 and have:   # the part is already complete
            r = None
        else:
            raise net.NetError("%s returned HTTP %d." % (urllib.request.urlparse(resolved.url).hostname, e.code))
    except (urllib.error.URLError, OSError) as e:
        raise net.NetError("Couldn't download the installer (%s)." % getattr(e, "reason", e))
    if r is not None:
        with r:
            if have and r.status != 206:   # the server started over
                have, h = 0, hasher(resolved.sha)
            length = r.headers.get("Content-Length")
            total = have + int(length) if length and length.isdigit() else resolved.size
            done = have
            last, mark_t, mark_d, speed = 0, time.time(), done, 0
            with open(part, "ab" if have else "wb") as f:
                while True:
                    if cancel and cancel():
                        raise Cancelled()
                    b = r.read(1 << 20)
                    if not b:
                        break
                    f.write(b)
                    h.update(b)
                    done += len(b)
                    t = time.time()
                    if t - mark_t >= 1:
                        speed = (done - mark_d) / (t - mark_t)
                        mark_t, mark_d = t, done
                    if progress and t - last >= 0.1:
                        last = t
                        progress(done, total, speed)
            if total > 0 and done < total:
                raise net.NetError("The download stopped before it finished. Try again to continue it.")
    if verifying:
        verifying()
    if resolved.sha:
        got = h.hexdigest()
        if got != resolved.sha:
            os.remove(part)
            raise ChecksumError("The download doesn't match the official checksum, so it wasn't used. Try again.")
    os.replace(part, dest)
    return dest


def human(n):
    n = float(n)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or unit == "TB":
            return ("%.0f %s" if unit in ("B", "KB") else "%.1f %s") % (n, unit)
        n /= 1024
