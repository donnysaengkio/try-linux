# Try Linux

Try a Linux desktop without repartitioning your drive. Pick a distribution, and Try Linux
downloads the official installer, checks it against the distribution's published SHA-256 or
SHA-512 checksum, creates a virtual disk and boots it in a window.

![Choose a Linux OS](windows/docs/images/choose-os.png)

## Editions

| Edition | Runs on | Virtual machines | Download |
|---|---|---|---|
| [**Try&nbsp;Linux&nbsp;for&nbsp;Windows**](windows/README.md) | Windows&nbsp;10,&nbsp;11 | QEMU,&nbsp;Hyper-V&nbsp;platform | [Latest](https://github.com/donnysaengkio/try-linux/releases/latest) |
| [**Try&nbsp;Linux&nbsp;for&nbsp;Mac**](macos/README.md)&nbsp;(preview) | Apple&nbsp;silicon | Apple&nbsp;Virtualization | [`macos-v`&nbsp;releases](https://github.com/donnysaengkio/try-linux/releases?q=macos-v&expanded=true) |
| [**Try&nbsp;Linux&nbsp;for&nbsp;Linux**](linux/README.md)&nbsp;(preview) | x86_64,&nbsp;ARM64 | QEMU&nbsp;with&nbsp;KVM | [`linux-v`&nbsp;releases](https://github.com/donnysaengkio/try-linux/releases?q=linux-v&expanded=true) |

The Windows edition runs QEMU through [WINQ-EMU](https://github.com/cmspam/winq-emu), with
OpenGL and Vulkan graphics; the Linux edition needs GTK 4 and libadwaita, and uses VirGL for 3D.

Each edition updates itself from its own releases here: Windows releases are tagged `v1.2.3`,
Mac releases `macos-v1.2.3-preview` and Linux releases `linux-v1.2.3-preview`.

## The distribution catalog

[`catalog/distros.json`](catalog/distros.json) is shared by the editions: the distributions
Try Linux downloads and checks (with each one's mirror, how to find its newest installer and
its checksum file), and over 400 more from DistroWatch that you download from their website.
The apps check it when you open Choose a Linux OS, so new entries appear without a new
release. Its address is part of the released apps, so it stays at `catalog/distros.json` on
`main`.

## Repository layout

```
catalog/   the shared distribution list
windows/   Try Linux for Windows (C# WinForms, built with build.cmd)
macos/     Try Linux for Mac (SwiftUI, built with scripts/build-app.sh)
linux/     Try Linux for Linux (Python, GTK 4 and libadwaita)
```

Each edition has its own CI workflow, run when its folder (or the catalog) changes. The Mac
and Linux editions were separate repositories (try-linux-macos and try-linux-linux) until
November 2026; their full history is here, under `macos/` and `linux/`.

## License

MIT. See [LICENSE](LICENSE).
