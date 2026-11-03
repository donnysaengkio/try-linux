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

## License

Try Linux for Mac is released under the [MIT License](LICENSE).
