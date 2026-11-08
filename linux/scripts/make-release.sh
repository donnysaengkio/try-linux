#!/bin/sh
# Builds build/TryLinux-<version>-preview-linux.tar.gz (one download for x86_64 and ARM64: it's Python)
# and build/SHA256SUMS.txt.
set -e
cd "$(dirname "$0")/.."
version=$(python3 -c 'import trylinux; print(trylinux.VERSION)')
name="TryLinux-$version-preview-linux"
rm -rf build/stage && mkdir -p "build/stage/$name"
cp -r trylinux data try-linux install.sh README.md LICENSE "build/stage/$name/"
find "build/stage/$name" -name __pycache__ -prune -exec rm -rf {} +
chmod +x "build/stage/$name/try-linux" "build/stage/$name/install.sh"
tar -C build/stage --owner=0 --group=0 -czf "build/$name.tar.gz" "$name"
(cd build && sha256sum "$name.tar.gz" > SHA256SUMS.txt && cat SHA256SUMS.txt)
