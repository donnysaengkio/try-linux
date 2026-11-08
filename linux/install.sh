#!/bin/sh
# Installs Try Linux for you (no sudo): the app in ~/.local/share/try-linux, the try-linux command in
# ~/.local/bin, and Try Linux in your apps menu.   ./install.sh --uninstall removes them again (your
# systems in ~/.local/share/TryLinux are kept).
set -e
here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
app="$data/try-linux"
bin="$HOME/.local/bin"
id=io.github.donnysaengkio.TryLinux

if [ "$1" = "--uninstall" ]; then
    rm -rf "$app" "$bin/try-linux" "$data/applications/$id.desktop" "$data/icons/hicolor/scalable/apps/$id.svg"
    echo "Try Linux is uninstalled. Your systems are still in $data/TryLinux; delete that folder to remove them."
    exit 0
fi

mkdir -p "$app" "$bin" "$data/applications" "$data/icons/hicolor/scalable/apps"
rm -rf "$app/trylinux"
cp -r "$here/trylinux" "$app/"
cp "$here/try-linux" "$here/README.md" "$here/LICENSE" "$app/"
chmod +x "$app/try-linux"
touch "$app/.installed"            # lets Try Linux update itself here
ln -sf "$app/try-linux" "$bin/try-linux"
cp "$here/data/$id.svg" "$data/icons/hicolor/scalable/apps/$id.svg"
sed "s|^Exec=.*|Exec=$bin/try-linux|" "$here/data/$id.desktop" > "$data/applications/$id.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q "$data/icons/hicolor" 2>/dev/null || true

echo "Try Linux is installed. Open it from your apps menu, or run: try-linux"
case ":$PATH:" in *":$bin:"*) ;; *) echo "(Add $bin to your PATH to run try-linux from a terminal.)" ;; esac

# What else it needs, checked now so the first start doesn't surprise you.
"$app/try-linux" --help >/dev/null 2>&1 || true
python3 -c 'import gi; gi.require_version("Gtk","4.0"); gi.require_version("Adw","1")' 2>/dev/null ||
    echo "Also install GTK 4 and libadwaita for Python: see Requirements in README.md."
arch=$(uname -m); [ "$arch" = arm64 ] && arch=aarch64
command -v "qemu-system-$arch" >/dev/null 2>&1 && command -v qemu-img >/dev/null 2>&1 ||
    echo "Also install QEMU: see Requirements in README.md."
