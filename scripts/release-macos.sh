#!/bin/bash
# Makes the files for a Mac release of Try Linux, in dist-macos/, from the Build workflow's try-linux-macos-app
# artifact (the app is built and signed ad hoc on GitHub's Mac; nothing here needs a Mac):
#   TryLinux-<version>-macOS.zip        Try Linux.app, zipped: the download, and what Mac 1.x updates to (it checks
#                                       the app's signature and puts it in place of its own)
#   TryLinux-<version>-macOS.app.tar.gz the same app, for 2.x's updater
#   SHA256SUMS.txt                      the zip's checksum (Mac 1.x checks it)
#   latest-macos.json                   what the app's updater reads: the version and the signed .tar.gz
#
#   scripts/release-macos.sh ARTIFACT-FOLDER ["notes"]
# The .tar.gz is signed for updates with the private key in TAURI_KEY_FILE (default ~/.tauri/try-linux-updates.key;
# never put it in the repository): the app only installs updates signed with it.
#
# Then: a release tagged macos-v<version> with the zip and SHA256SUMS.txt (and the .tar.gz), and latest-macos.json
# uploaded to the "tauri-updates" release.
set -euo pipefail
from=$1 notes="${2:-}"
root=$(cd "$(dirname "$0")/.." && pwd)              # the repository
key="${TAURI_KEY_FILE:-$HOME/.tauri/try-linux-updates.key}"
[ -f "$key" ] || { echo "The update signing key isn't at $key." >&2; exit 1; }
version=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$root/src-tauri/tauri.conf.json")
tag="macos-v$version"
repo="https://github.com/donnysaengkio/try-linux/releases/download"
out="$root/dist-macos"
tauri="$root/ui/node_modules/.bin/tauri"
[ -x "$tauri" ] || (cd "$root/ui" && npm ci --no-audit --no-fund)
rm -rf "$out" && mkdir -p "$out"

zip="TryLinux-$version-macOS.zip"
tgz="TryLinux-$version-macOS.app.tar.gz"
cp "$from/TryLinux-macOS.zip" "$out/$zip"
cp "$from/Try Linux.app.tar.gz" "$out/$tgz"
# The app inside must be the one Mac 1.x looks for, with this version.
python3 - "$out/$zip" "$version" <<'PY'
import plistlib, sys, zipfile
z = zipfile.ZipFile(sys.argv[1])
info = plistlib.loads(z.read("Try Linux.app/Contents/Info.plist"))
print("app:", info["CFBundleIdentifier"], info["CFBundleExecutable"], info["CFBundleShortVersionString"])
assert info["CFBundleShortVersionString"] == sys.argv[2], "the app's version isn't " + sys.argv[2]
assert info["CFBundleExecutable"] == "TryLinux" and info["CFBundleIdentifier"] == "io.github.donnysaengkio.trylinux"
PY
TAURI_SIGNING_PRIVATE_KEY="$(cat "$key")" TAURI_SIGNING_PRIVATE_KEY_PASSWORD="" "$tauri" signer sign "$out/$tgz" > /dev/null
[ -s "$out/$tgz.sig" ] || { echo "No update signature for $tgz." >&2; exit 1; }

(cd "$out" && sha256sum "$zip" "$tgz" > SHA256SUMS.txt)
python3 - "$out" "$version" "$repo/$tag/$tgz" "$tgz" "$notes" <<'PY'
import json, os, sys, time
out, version, url, tgz, notes = sys.argv[1:6]
sig = open(os.path.join(out, tgz + ".sig")).read().strip()
feed = {"version": version, "notes": notes, "pub_date": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "platforms": {"darwin-aarch64": {"signature": sig, "url": url}}}
json.dump(feed, open(os.path.join(out, "latest-macos.json"), "w"), indent=2)
PY
ls -la "$out"
echo "Release tag: $tag"
