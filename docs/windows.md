# Try Linux for Windows

Try a Linux desktop on Windows without repartitioning your drive. Pick a distribution, and Try Linux downloads
the official installer, checks it against the distribution's published SHA-256 or SHA-512 checksum, creates a
virtual disk and starts the installer in a window, with GPU-accelerated graphics.

Try Linux runs your systems with [WINQ-EMU](https://github.com/cmspam/winq-emu), a QEMU build for Windows that
uses the Windows Hypervisor Platform and passes OpenGL / Vulkan graphics through to Linux (VirGL and Venus).

Version 2 is a new app that replaces Try Linux 1.x. It keeps your systems, downloads and settings, and 1.x moves to
it on its own: see [Updating from 1.x](#updating-from-1x).

On a Linux PC? See [Try Linux for Linux](linux.md), the same app as an AppImage.

![Virtual Machines: Fedora Workstation, Linux Mint and Ubuntu Desktop, each with its status, CPU cores, memory and disk use, and Console, Start and Settings buttons](images/virtual-machines.png)

## Screenshots

| | |
|---|---|
| ![Choose a Linux OS: the recommended distributions and a search box](images/choose-os.png) | ![Create, Resources: Balanced, Maximum or Manual, memory, CPU cores and disk size](images/create-resources.png) |
| **Choose a Linux OS:** five recommended distributions, and a search of hundreds more | **Create:** name and location, then memory, cores and disk size, then a summary |
| ![Use Your Own Installer: an existing virtual disk, an ISO file on this PC, or a URL](images/own-installer.png) | ![Settings, Hardware: Graphics, Resources and Disks](images/settings-hardware.png) |
| **Use Your Own Installer:** an existing virtual disk, an ISO file or a link | **Settings, Hardware:** displays, rendering, GPU memory, resources, disks |
| ![Settings, Devices: Camera and USB, CD/DVD drive, microphone and sound, network](images/settings-devices.png) | ![Settings, Recovery: backup and restore, clone, snapshots, storage, repair, delete](images/settings-recovery.png) |
| **Settings, Devices:** CD/DVD drive, sound and microphone, network and port forwards, shared folder | **Settings, Recovery:** back up, clone, snapshots, move storage, repair, delete |
| ![Virtual Machines in the Light theme](images/theme-light.png) | |
| **Light theme**, or Dark, or following Windows | |

## Requirements

- 64-bit Windows 10 or 11 (ARM64 isn't supported)
- Hardware virtualization turned on in your PC's BIOS/UEFI
- About 20 GB of free space per system
- Internet access to download Linux and WINQ-EMU

## Install

1. Download `TryLinux-<version>-Setup.exe` from
   [the latest release](https://github.com/donnysaengkio/try-linux/releases/latest).
2. Run it. The installer isn't code-signed, so Windows may show **"Windows protected your PC"**: click
   **More info**, then **Run anyway**.

   <img src="images/smartscreen.png" alt="Windows SmartScreen: Windows protected your PC, with Run anyway and Don't run buttons" width="420">

3. Try Linux installs for you alone, in `%LOCALAPPDATA%\Try Linux`, with a **Try Linux** entry in the Start menu.
   It doesn't need administrator permission.

You can check the download against the SHA-256 checksum in the release's `SHA256SUMS.txt`:

```powershell
Get-FileHash .\TryLinux-2.0.0-Setup.exe -Algorithm SHA256
```

**The first time you create a system**, Try Linux checks what your PC still needs and offers to set it up:

1. **Hardware virtualization.** If it's off, Try Linux says how to turn it on (see
   [Troubleshooting](#troubleshooting)). It can still download and prepare the system meanwhile, and the system
   waits, **Ready to install**.
2. **Windows Hypervisor Platform.** Try Linux turns it on; Windows asks for permission, then needs one restart.
3. **WINQ-EMU** (about 55 MB), downloaded from its GitHub releases and installed. It isn't bundled with Try Linux.

## Updates

Each time it opens, Try Linux checks for a new version of itself and of WINQ-EMU, and shows a notice with an
**Update** button when there is one. **Check For Updates** in the main menu (☰, or Ctrl+U) checks right away;
**Preferences** turns the automatic check off.

- **Try Linux updates** download, then Try Linux closes, installs the new version and opens again. Systems that
  are running keep running. Try Linux only installs updates signed with its own key, so a changed or damaged
  download is refused.
- **WINQ-EMU updates** download WINQ-EMU's new installer and run it; Windows asks for permission, because it
  installs to `C:\WINQ-EMU`. Shut down running systems first. WINQ-EMU is alpha software, so its release notes are
  worth a look.

## Updating from 1.x

Try Linux 1.x offers 2.0 as an update like any other: click **Update**. It downloads the 2.0 installer, checks it
against the release's SHA-256 checksum and runs it. You can also run `TryLinux-<version>-Setup.exe` yourself.

The first time 2.0 starts, it takes over from 1.x:

- Your systems, downloads and preferences carry over as they are (they're the same files, in
  `%LOCALAPPDATA%\TryLinux` and `%APPDATA%\TryLinux`).
- Desktop shortcuts and "start at sign-in" entries made by 1.x now open 2.0.
- 1.x's Start menu entry, its entry in Installed apps and its program folder (`%LOCALAPPDATA%\Programs\TryLinux`)
  are removed. If 1.x is still open, this waits until it's closed.

A system that 1.x started keeps running; 2.0 shows it as running and can control it once 1.x has closed.

## Using it

Click **Create** and choose a Linux OS (or **Use Your Own Installer**), then:

1. **General:** the system's name, an optional description, and where it's kept.
2. **Resources:** Balanced, Maximum or Manual memory and CPU cores, and the disk size, with the free space on that
   drive. **Allocate the full size now** takes the disk's whole size straight away, instead of as Linux stores
   data.
3. **Summary:** everything you chose. **Install** downloads and checks the installer, creates the disk and opens
   the system's window (an imported disk is copied and started).

Install Linux onto the virtual disk as you would on a PC; your Windows drive is never touched.

**Virtual Machines** lists each system with its status (**Running**, **Paused**, **Powered off** or **Ready to
install**), and while it runs, its CPU use, the memory Linux is using and its disk activity.

- **Start** starts a system in its own window; **Console** (`>_`) brings a running system's window to the front.
- **Closing a system's window** keeps it running: Console brings it back.
- **Closing Try Linux** while systems run keeps it in the tray. Right-click the tray icon to show a system's
  window, use its power commands, or open Try Linux.
- **Power** replaces Start while a system runs: **Shut down** and **Restart** ask Linux to shut down cleanly,
  **Pause** freezes it, and **Force stop** and **Reset** act at once, losing unsaved work, so they ask first.
- **Several systems can run at once**, each with the memory and cores it was given.
- If Try Linux is closed while a system installs, the system keeps going; Try Linux picks it up again the next
  time it opens, and when an install finished meanwhile, Start asks whether to start from the disk.

**Settings** (the gear button) has four tabs of sections; click one to open it. Save applies the changes.

- **General:** the name and description, fullscreen and which display, starting at Windows sign-in, and a desktop
  shortcut.
- **Hardware:** guest displays, GPU or CPU rendering, Vulkan and GPU memory; memory and CPU cores; and the disks,
  with **Disk Utilities** to resize, compact, add and remove disks.
- **Devices:** a CD/DVD drive for ISOs such as drivers or rescue disks (changeable while the system runs), sound
  and microphone, network type and port forwards (`tcp:2223:22` for this PC, `lan tcp:8080:80` for your network),
  and a shared Windows folder.
- **Recovery:** back up and restore, clone, snapshots, move the storage folder, clean up downloads, reset or boot
  an ISO, and delete.

To use the shared folder in Linux:

```sh
sudo mkdir -p /mnt/host
sudo mount -t 9p -o trans=virtio,version=9p2000.L host /mnt/host
```

**Keyboard shortcuts:** Ctrl+N Create, Ctrl+F search distributions, Ctrl+U Check For Updates, Ctrl+comma
Preferences, Ctrl+? all shortcuts, Ctrl+Q quit, Alt+← back, Esc close a dialog or menu. In a system's window,
**Ctrl+Alt+G** releases the mouse and keyboard and **Ctrl+Alt+F** toggles fullscreen.

## Uninstall

Quit Try Linux (☰ > Quit Try Linux), then use **Windows Settings > Apps > Installed apps > Try Linux >
Uninstall**. Your systems and downloads stay in `%LOCALAPPDATA%\TryLinux`; delete that folder to remove them too.
WINQ-EMU is uninstalled separately, from Installed apps.

## Troubleshooting

**Check first:** open Task Manager (Ctrl+Shift+Esc), go to **Performance > CPU**, and look for **Virtualization**.
It should say **Enabled**.

<img src="images/troubleshoot-task-manager.png" alt="Task Manager, Performance, CPU: Virtualization: Enabled" width="560">

**"Hardware virtualization is turned off in your PC's BIOS/UEFI settings"**

1. Open **Settings > System > Recovery**, and next to **Advanced startup** click **Restart now**.
2. Choose **Troubleshoot > Advanced options > UEFI Firmware Settings > Restart** (or press Del, F2, F10 or Esc
   while the PC starts).
3. Turn on **Intel Virtualization Technology (VT-x)** or **AMD SVM Mode**. It's often under Advanced, CPU
   Configuration or Security.
4. Save, exit, and open Try Linux again. A system that was waiting shows **Install** on Virtual Machines.

**"Turn on Windows Hypervisor Platform?"** Click **Turn On**; Windows asks for permission, then needs one restart.
Or do it yourself: press Win+R, type `optionalfeatures`, tick **Windows Hypervisor Platform**, click **OK**, and
restart.

<img src="images/troubleshoot-windows-features.png" alt="Turn Windows features on or off, with Windows Hypervisor Platform outlined" width="415">

**"Windows Hypervisor Platform is turned on but isn't running yet"** Restart Windows. If the message comes back,
virtualization is probably off in the BIOS/UEFI; follow the steps above.

**"This PC's processor doesn't support hardware virtualization"** Try Linux can't run on this PC.

## Limitations

- No camera or USB passthrough, and no choice of sound device (Windows' defaults are used). WINQ-EMU doesn't
  support these.
- No clipboard sharing or drag-and-drop between Windows and Linux. Use the shared folder, or SSH
  (`ssh -p 2223 you@localhost`).
- Chromium-based browsers can show black video in Linux; Firefox works best.
- WINQ-EMU is alpha software.

## Credits

- [WINQ-EMU](https://github.com/cmspam/winq-emu) by cmspam: QEMU (GPL-2.0), virglrenderer (MIT), launcher (MIT).
  Downloaded by Try Linux, not redistributed.
- Built with [Tauri](https://tauri.app), [React](https://react.dev), [Base UI](https://base-ui.com) and
  [Sonner](https://sonner.emilkowal.ski); icons from [Lucide](https://lucide.dev).
- Linux distributions are downloaded from their official mirrors and carry their own licenses.
- Fonts under the SIL Open Font License 1.1 (see `ui/public/fonts`): Prompt by Cadson Demak, Work Sans by Wei
  Huang and Noto Sans by the Noto Project Authors.

Try Linux is released under the [MIT License](../LICENSE).
