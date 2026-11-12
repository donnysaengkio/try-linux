#!/bin/bash
# Builds Try Linux for Linux and the files for a GitHub release, in prototype/dist-linux:
#   TryLinux-<version>-<arch>.AppImage       the app (x86_64, aarch64): one file that runs on any distribution and
#                                            updates itself
#   TryLinux-<version>-linux.tar.gz          what Try Linux 1.0.x updates to: a stand-in (linux-handover/) that
#                                            downloads the AppImage for its processor and starts it
#   SHA256SUMS.txt                           checksums of all of the above (1.0.x and the stand-in check them)
#   latest-linux.json                        what the app's updater reads: the version and each signed AppImage
#
#   scripts/release-linux.sh ["notes"]                     builds this machine's AppImage (x86_64 or aarch64)
#   scripts/release-linux.sh --add FILE ARCH ["notes"]     adds an AppImage built elsewhere (the prototype
#                                                          workflow's aarch64 artifact), signing it here
# Every AppImage needs an update signature, made with the private key in TAURI_KEY_FILE (default
# ~/.tauri/try-linux-updates.key; never put it in the repository): the app only installs updates signed with it.
#
# Then: a release tagged linux-v<version> with both AppImages, the .tar.gz and SHA256SUMS.txt, and
# latest-linux.json uploaded to the "tauri-updates" release, which is where installed copies look for updates.
# Publish both AppImages together: 1.0.x on either processor updates to the stand-in, which needs its AppImage.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)              # prototype/
key="${TAURI_KEY_FILE:-$HOME/.tauri/try-linux-updates.key}"
[ -f "$key" ] || { echo "The update signing key isn't at $key." >&2; exit 1; }
version=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$root/src-tauri/tauri.conf.json")
tag="linux-v$version"
repo="https://github.com/donnysaengkio/try-linux/releases/download"
out="$root/dist-linux"
tauri="$root/ui/node_modules/.bin/tauri"
mkdir -p "$out"

if [ "${1:-}" = "--add" ]; then
    file=$2 arch=$3 notes="${4:-}"
    [ -x "$tauri" ] || (cd "$root/ui" && npm ci --no-audit --no-fund)
    name="TryLinux-$version-$arch.AppImage"
    cp "$file" "$out/$name"
    chmod 755 "$out/$name"
    rm -f "$out/$name.sig"
    TAURI_SIGNING_PRIVATE_KEY="$(cat "$key")" TAURI_SIGNING_PRIVATE_KEY_PASSWORD="" "$tauri" signer sign "$out/$name" > /dev/null
else
    notes="${1:-}"
    arch=$(uname -m); [ "$arch" = arm64 ] && arch=aarch64
    (cd "$root/ui" && npm ci --no-audit --no-fund && npm run build)
    (cd "$root" && TAURI_SIGNING_PRIVATE_KEY="$(cat "$key")" TAURI_SIGNING_PRIVATE_KEY_PASSWORD="" "$tauri" build)
    built=$(ls "$root"/src-tauri/target/release/bundle/appimage/*_"$version"_*.AppImage | head -1)
    name="TryLinux-$version-$arch.AppImage"
    cp "$built" "$out/$name"
    cp "$built.sig" "$out/$name.sig"
fi
[ -s "$out/$name.sig" ] || { echo "No update signature for $name." >&2; exit 1; }

# The stand-in for 1.0.x, with this version in it.
stage=$(mktemp -d)
mkdir -p "$stage/try-linux"
cp -r "$root/linux-handover/." "$stage/try-linux/"
sed -i "s/^VERSION = .*/VERSION = \"$version\"/" "$stage/try-linux/trylinux/__init__.py"
find "$stage" -name __pycache__ -prune -exec rm -rf {} +
chmod 755 "$stage/try-linux/try-linux"
tar -czf "$out/TryLinux-$version-linux.tar.gz" -C "$stage" try-linux
rm -rf "$stage"

# Checksums of every file here, and the updater's feed from every AppImage so far.
(cd "$out" && sha256sum TryLinux-"$version"-*.AppImage TryLinux-"$version"-linux.tar.gz > SHA256SUMS.txt)
python3 - "$out" "$version" "$repo/$tag" "$notes" <<'PY'
import glob, json, os, sys, time
out, version, base, notes = sys.argv[1:5]
platforms = {}
for sig in sorted(glob.glob(os.path.join(out, "TryLinux-%s-*.AppImage.sig" % version))):
    name = os.path.basename(sig)[:-4]
    arch = name[len("TryLinux-%s-" % version):-len(".AppImage")]
    platforms["linux-" + arch] = {"signature": open(sig).read().strip(), "url": "%s/%s" % (base, name)}
feed = {"version": version, "notes": notes, "pub_date": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "platforms": platforms}
json.dump(feed, open(os.path.join(out, "latest-linux.json"), "w"), indent=2)
print("updater feed:", ", ".join(sorted(platforms)))
PY
ls -la "$out"
echo "Release tag: $tag"
