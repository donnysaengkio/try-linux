"""The stand-in that moves Try Linux 1.0.x to Try Linux 2 (an AppImage). 1.0.x updates by downloading the newest
linux-v release's TryLinux-<version>-linux.tar.gz and putting its trylinux/ folder in place of its own, then
starting it again; this is that folder. scripts/release-linux.sh sets VERSION."""

VERSION = "2.0.0"
REPO = "donnysaengkio/try-linux"
TAG_PREFIX = "linux-v"
