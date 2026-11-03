# Try Linux for Mac

Try a Linux desktop on your Mac without repartitioning your drive. Pick a
distribution, and Try Linux downloads the official ARM64 installer, checks it
against the distribution's published SHA-256 checksum, creates a virtual disk and
boots it in a window.

Try Linux for Mac uses Apple's built-in Virtualization framework, so there's
nothing else to install. It's the Mac edition of
[Try Linux for Windows](https://github.com/donnysaengkio/try-linux).

> **Preview:** this first version is being tested. Please report problems in
> [Issues](../../issues).

## Distributions

| Distribution | About |
|---|---|
| [Fedora&nbsp;Workstation](https://fedoraproject.org/workstation/) | A community distribution sponsored by Red Hat that ships new GNOME, kernel and Mesa releases soon after they come out |
| [Ubuntu&nbsp;Desktop](https://ubuntu.com/desktop) | The most widely used desktop Linux, made by Canonical, with long-term support releases and a large software catalog |
| [Debian](https://www.debian.org/) | One of the oldest community distributions, known for stability and the base of Ubuntu, Mint and many others |
| [openSUSE&nbsp;Tumbleweed](https://www.opensuse.org/#Tumbleweed) | A rolling-release distribution from the openSUSE community that always has the newest KDE Plasma and kernel |

Macs with Apple silicon need ARM64 (aarch64) Linux. Linux Mint and Zorin OS don't
publish ARM64 installers, so they aren't offered here. You can also install from any
ARM64 `.iso` file or download link.

## Requirements

- A Mac with Apple silicon (M1 or later)
- macOS 13 Ventura or later
- About 20 GB of free space per system

## Install

1. Download `TryLinux-<version>-macOS.zip` from [Releases](../../releases) and open it.
2. Move **Try Linux.app** to your Applications folder and open it.
3. The app isn't notarized by Apple, so macOS blocks it the first time. Open
   **System Settings > Privacy & Security**, scroll down, click **Open Anyway** next
   to "Try Linux", and confirm.

You can check the download against the SHA-256 hash published with each release:

```sh
shasum -a 256 TryLinux-*-macOS.zip
```

## Using it

- **Choose a Linux**, then **Next** and **Install**. Try Linux downloads and verifies
  the installer, creates a virtual disk and opens the system in its own window.
  Install Linux onto the virtual disk; your Mac's drive is never touched.
- Running systems are in the **Try Linux menu bar icon**: show the window, open the
  shared folder, shut down cleanly or force stop.
- Quitting Try Linux shuts running systems down, so save your work in Linux first.
- **Settings** (per system):
  - **General:** fullscreen, resolution, open at login, resource profile
    (Balanced / Maximum / Manual), disk capacity, shared Mac folder (read-only unless
    allowed).
  - **Devices:** sound, microphone and clipboard sharing.
  - **Advanced:** Rosetta for x86_64 Linux apps, graphics and network details.
  - **Recovery:** back up, restore as a copy, snapshots, reset, boot an ISO, clean
    up downloads, delete.

To use the shared folder in Linux:

```sh
sudo mkdir -p /mnt/host
sudo mount -t virtiofs host /mnt/host
```

Clipboard sharing needs `spice-vdagent` in Linux. Fedora and Ubuntu include it; on
Debian and openSUSE, install the `spice-vdagent` package.

### Rosetta

With Rosetta turned on in Settings > Advanced, ARM64 Linux can run x86_64 Linux apps.
In Linux:

```sh
sudo mkdir -p /media/rosetta
sudo mount -t virtiofs rosetta /media/rosetta
sudo /usr/sbin/update-binfmts --install rosetta /media/rosetta/rosetta \
  --magic "\x7fELF\x02\x01\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x02\x00\x3e\x00" \
  --mask "\xff\xff\xff\xff\xff\xfe\xfe\x00\xff\xff\xff\xff\xff\xff\xff\xff\xfe\xff\xff\xff" \
  --credentials yes --preserve no --fix-binary yes
```

## Limitations

- Graphics are 2D (virtio-gpu). 3D / Vulkan acceleration isn't available for Linux
  guests in Apple Virtualization.
- No port forwarding. To reach Linux from the Mac (for example over SSH), use its IP
  address; run `ip addr` in Linux to see it.
- No camera or USB passthrough.

## Building from source

Needs Xcode or the Swift toolchain on a Mac with Apple silicon:

```sh
bash scripts/build-app.sh
```

This produces `build/Try Linux.app` and a zip of it. GitHub Actions builds every push
and runs `--self-test`, which checks the download catalog and renders every screen.

## License

Try Linux for Mac is released under the [MIT License](LICENSE).
