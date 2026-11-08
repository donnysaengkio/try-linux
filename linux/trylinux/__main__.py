"""try-linux [--start <system id>]
try-linux --self-test <folder>        checks and screenshots for CI (uses a scratch folder, not your systems)
try-linux --boot-test <iso> <log>     boots an ISO headless and waits for Linux to reach a login prompt
try-linux --resolve-test              finds the current installer of each built-in distribution"""
import os
import sys


def main():
    args = sys.argv[1:]
    if "--help" in args or "-h" in args:
        print(__doc__)
        return 0
    selftest = None
    if "--self-test" in args:
        selftest = os.path.abspath(args[args.index("--self-test") + 1])
        os.makedirs(selftest, exist_ok=True)
        # Everything the self-test makes stays in its folder.
        os.environ["TRYLINUX_DATA"] = os.path.join(selftest, "data")
        os.environ["TRYLINUX_CONFIG"] = os.path.join(selftest, "config")
    if "--resolve-test" in args:
        from . import catalog
        for d in catalog.builtin():
            try:
                r = catalog.resolve(d)
                print("%s\t%s\t%d MB\t%s" % (d.id, r.url, r.size >> 20, r.sha or "NO CHECKSUM"))
            except Exception as e:
                print("%s\tERROR\t%s" % (d.id, e))
        return 0
    if "--boot-test" in args:
        from . import selftest as st
        i = args.index("--boot-test")
        return st.boot_test(args[i + 1], args[i + 2])

    try:
        import gi
        gi.require_version("Gtk", "4.0")
        gi.require_version("Adw", "1")
        from gi.repository import Adw
    except (ImportError, ValueError) as e:
        print("Try Linux needs GTK 4 and libadwaita for Python (PyGObject): %s\n"
              "  Ubuntu/Debian: sudo apt install python3-gi gir1.2-gtk-4.0 gir1.2-adw-1\n"
              "  Fedora:        sudo dnf install python3-gobject gtk4 libadwaita\n"
              "  Arch:          sudo pacman -S python-gobject gtk4 libadwaita" % e, file=sys.stderr)
        return 1
    if (Adw.get_major_version(), Adw.get_minor_version()) < (1, 5):
        print("Try Linux needs libadwaita 1.5 or later (this PC has %d.%d): Ubuntu 24.04, Debian 13, Fedora 40 or newer."
              % (Adw.get_major_version(), Adw.get_minor_version()), file=sys.stderr)
        return 1
    from .ui.app import App
    passthrough = [a for a in args if a != "--self-test" and a != selftest]
    return App(selftest=selftest).run([sys.argv[0]] + passthrough)


if __name__ == "__main__":
    sys.exit(main())
