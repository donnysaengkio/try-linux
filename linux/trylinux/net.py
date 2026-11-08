"""HTTP helpers: https downloads with Try Linux's user agent."""
import urllib.error
import urllib.request

from . import VERSION

USER_AGENT = "TryLinux-Linux/" + VERSION + " (+https://github.com/donnysaengkio/try-linux)"
TIMEOUT = 30


class NetError(Exception):
    pass


def request(url, method="GET", headers=None):
    h = {"User-Agent": USER_AGENT}
    h.update(headers or {})
    return urllib.request.Request(url, method=method, headers=h)


def open_url(url, method="GET", headers=None, timeout=TIMEOUT):
    try:
        return urllib.request.urlopen(request(url, method, headers), timeout=timeout)
    except urllib.error.HTTPError as e:
        raise NetError("%s returned HTTP %d." % (urllib.request.urlparse(url).hostname or "The server", e.code)) from e
    except (urllib.error.URLError, OSError) as e:
        reason = getattr(e, "reason", e)
        raise NetError("Couldn't reach %s (%s)." % (urllib.request.urlparse(url).hostname or url, reason)) from e


def get_text(url, limit=4_000_000):
    with open_url(url) as r:
        return r.read(limit).decode("utf-8", "replace")


def head_size(url):
    """The download's size in bytes, or -1 when the server doesn't say."""
    try:
        with open_url(url, "HEAD") as r:
            n = r.headers.get("Content-Length")
            return int(n) if n and n.isdigit() else -1
    except NetError:
        return -1
