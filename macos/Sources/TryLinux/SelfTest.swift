// --self-test <dir>: checks the download catalog and VM configuration and renders every screen to PNGs.
// Used by CI, which can build and inspect the app but has no display to drive it.
import AppKit
import SwiftUI
import Virtualization

enum SelfTest {
    static func start(outputDirectory dir: URL) {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        Task { @MainActor in
            await run(dir)
            exit(0)
        }
        app.run()
    }

    @MainActor
    static func run(_ dir: URL) async {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        var lines: [String] = []
        lines.append("version=\(AppInfo.version) macOS=\(ProcessInfo.processInfo.operatingSystemVersionString)")
        lines.append("virtualization supported=\(VZVirtualMachine.isSupported) maxCPU=\(VZVirtualMachineConfiguration.maximumAllowedCPUCount) maxMemGB=\(VZVirtualMachineConfiguration.maximumAllowedMemorySize >> 30) rosetta=\(VZLinuxRosettaDirectoryShare.availability.rawValue)")

        for d in Distro.catalog {
            do {
                let r = try await Resolver.resolve(d)
                lines.append("resolve \(d.id)\t\(r.url.absoluteString)\t\(r.size)\t\(r.sha256 ?? "no-checksum")")
            } catch {
                lines.append("resolve \(d.id)\tERROR\t\(error.localizedDescription)")
            }
        }

        // Build and validate a full VM configuration against a throwaway storage folder.
        let storage = dir.appendingPathComponent("storage", isDirectory: true)
        UserDefaults.standard.set(storage.path, forKey: "storage")
        var c = VMConfig(id: "fedora", displayName: "Fedora Workstation")
        c.distroId = "fedora"
        c.sharedFolder = dir.path
        c.mic = true
        // Each check runs on its own: CI machines have no virtualization, so validating fails there,
        // but the disk and backup work below doesn't need it.
        let iso = storage.appendingPathComponent("test.iso")
        do {
            try Disk.create(c.diskURL, gb: 64)
            FileManager.default.createFile(atPath: iso.path, contents: Data(count: 1 << 20))
            c.iso = iso.path
            c.pendingInstall = true
            c.lastBoot = "2026-11-03 09:12"
            try c.save()
        } catch {
            lines.append("setup\tERROR\t\(error.localizedDescription)")
        }
        do {
            _ = try VMBuilder.configuration(c, installer: true)
            lines.append("vm configuration\tvalid (disk capacity \(Disk.capacity(c.diskURL) >> 30) GB, used \(Disk.used(c.diskURL)) bytes)")
        } catch {
            lines.append("vm configuration\tERROR\t\(error.localizedDescription)")
        }
        do {
            try Snapshots.take(c)
            lines.append("snapshot\t\(Snapshots.list(c).count) taken")
        } catch {
            lines.append("snapshot\tERROR\t\(error.localizedDescription)")
        }

        // A second disk, a disc in the CD/DVD drive and no network, as an installed system starts.
        var more = c
        let name = more.newDiskName()
        let disk2 = more.dir.appendingPathComponent(name)
        more.extraDisks = [name]
        more.cd = iso.path
        more.netType = 1
        do {
            try Disk.create(disk2, gb: 32)
            lines.append("add disk\t\(name), \(Disk.capacity(disk2) >> 30) GB; disks: \(more.allDisks.map(\.lastPathComponent).joined(separator: ", "))")
            try Disk.resize(disk2, gb: 48)
            let grown = Disk.capacity(disk2) >> 30
            try Disk.resize(disk2, gb: 40)
            lines.append("resize\tDisk 2 grew to \(grown) GB, then shrank to \(Disk.capacity(disk2) >> 30) GB")
        } catch {
            lines.append("add disk / resize\tERROR\t\(error.localizedDescription)")
        }
        do {
            let conf = try VMBuilder.configuration(more, installer: false)
            lines.append("vm configuration (2 disks, CD, no network)\tvalid: \(conf.storageDevices.count) storage devices, \(conf.networkDevices.count) network devices")
        } catch {
            lines.append("vm configuration (2 disks, CD, no network)\tERROR\t\(error.localizedDescription)")
        }
        do {
            more.displayName = "Backup test"
            try more.save()
            let backups = storage.appendingPathComponent("backups", isDirectory: true)
            try FileManager.default.createDirectory(at: backups, withIntermediateDirectories: true)
            let saved = try Backups.backup(more, into: backups)
            let restored = try Backups.restore(from: saved)
            let present = restored.allDisks.allSatisfy { FileManager.default.fileExists(atPath: $0.path) }
            let sizes = restored.allDisks.map { "\(Disk.capacity($0) >> 30) GB" }.joined(separator: " + ")
            lines.append("backup and restore\t\(restored.allDisks.count) disks restored (\(sizes)), all present: \(present), cd=\(restored.cd == iso.path), netType=\(restored.netType)")
            try? FileManager.default.removeItem(at: restored.dir)
        } catch {
            lines.append("backup and restore\tERROR\t\(error.localizedDescription)")
        }
        try? FileManager.default.removeItem(at: disk2)
        try? c.save()

        // Updates: version comparison, the real release list, and installing / updating a copy of this app.
        for (a, b) in [("1.0.1-preview", "1.0.0"), ("1.0.0", "1.0.0"), ("1.0.0-preview", "1.0.0"), ("0.9.9", "1.0.0"), ("1.1", "1.0.9")] {
            lines.append("newer(\(a), \(b)) = \(Updater.isNewer(a, than: b))")
        }
        do {
            let r = try await Updater.fetch()
            lines.append("latest release\t\(r.version)\tzip=\(r.zip?.lastPathComponent ?? "none")\tsums=\(r.sums?.lastPathComponent ?? "none")")
        } catch {
            lines.append("latest release\tERROR\t\(error.localizedDescription)")
        }
        let work = dir.appendingPathComponent("install-test", isDirectory: true)
        try? FileManager.default.removeItem(at: work)
        do {
            let installed = try Installer.install(from: Bundle.main.bundleURL, to: work.appendingPathComponent("Applications", isDirectory: true))
            let signed = Installer.run("/usr/bin/codesign", ["--verify", "--deep", installed.path]) == 0
            lines.append("install\t\(installed.lastPathComponent) version=\(Installer.version(of: installed) ?? "?") signed=\(signed)")
            let build = Bundle.main.bundleURL.deletingLastPathComponent()
            if let zip = (try? FileManager.default.contentsOfDirectory(at: build, includingPropertiesForKeys: nil))?.first(where: { $0.lastPathComponent.hasSuffix("-macOS.zip") }) {
                let hash = try Hashing.sha256(zip, cancelled: { false }, report: { _ in })
                let copy = work.appendingPathComponent(zip.lastPathComponent)
                try FileManager.default.copyItem(at: zip, to: copy)
                do {
                    _ = try Updater.unpack(copy, sums: String(repeating: "0", count: 64) + "  " + zip.lastPathComponent + "\n", into: work)
                    lines.append("update wrong checksum\tACCEPTED (bad)")
                } catch {
                    lines.append("update wrong checksum\trefused: \(error.localizedDescription)")
                }
                try? FileManager.default.removeItem(at: copy)
                try FileManager.default.copyItem(at: zip, to: copy)
                let app = try Updater.unpack(copy, sums: hash + "  " + zip.lastPathComponent + "\n", into: work)
                try Updater.apply(app, to: installed)
                let ok = Installer.run("/usr/bin/codesign", ["--verify", "--deep", installed.path]) == 0
                lines.append("update\tapplied version=\(Installer.version(of: installed) ?? "?") signed=\(ok)")
            } else {
                lines.append("update\tskipped (no zip next to the app)")
            }
        } catch {
            lines.append("install/update\tERROR\t\(error.localizedDescription)")
        }
        try? FileManager.default.removeItem(at: work)

        // Render the screens as a typical Mac would show them, not the small CI machine and its scratch folder.
        VMConfig.previewHost = (cores: 10, ramGB: 16)
        Paths.displayRoot = "~/Library/Application Support/TryLinux"
        c.sharedFolder = "~/Documents"
        c.cores = 5
        // Systems renamed by the user, with the OS and version taken from their installers' file names.
        c.displayName = "Dev box"
        c.iso = "/Users/you/Library/Application Support/TryLinux/isos/Fedora-Workstation-Live-45-1.4.aarch64.iso"
        var ubuntu = VMConfig(id: "ubuntu", displayName: "Ubuntu test")
        ubuntu.distroId = "ubuntu"
        ubuntu.iso = "/Users/you/Library/Application Support/TryLinux/isos/ubuntu-26.04-desktop-arm64.iso"
        ubuntu.profile = 1
        let model = AppModel(preview: [c, ubuntu])
        model.previewDisk = ["fedora": "16.5 GB", "ubuntu": "9.2 GB"]
        lines.append("os labels\t\(model.osLabel(c)) / \(model.osLabel(ubuntu))")
        func snap<V: View>(_ name: String, _ view: V, height: CGFloat = 700) {
            let root = view.environmentObject(model).frame(width: 980, height: height).background(Theme.bg).preferredColorScheme(.dark)
            let renderer = ImageRenderer(content: root)
            renderer.scale = 2
            if let img = renderer.nsImage, let tiff = img.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
               let png = rep.representation(using: .png, properties: [:]) {
                try? png.write(to: dir.appendingPathComponent(name + ".png"))
            }
            // AppKit-backed controls (text fields, scroll views) only draw in a real window, so capture that too.
            let host = NSHostingView(rootView: root)
            let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 980, height: height), styleMask: [.titled], backing: .buffered, defer: false)
            win.appearance = NSAppearance(named: .darkAqua)
            win.contentView = host
            win.orderFrontRegardless()
            host.layoutSubtreeIfNeeded()
            RunLoop.main.run(until: Date().addingTimeInterval(0.6))
            if let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) {
                host.cacheDisplay(in: host.bounds, to: rep)
                try? rep.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent(name + "-window.png"))
            }
            win.orderOut(nil)
        }
        snap("home", HomeView())
        // The update notification, in the notification area above the page.
        model.updateAvailable = "1.0.5"
        snap("home-update", VStack(spacing: 0) { NoticeArea(); HomeView() })
        model.updateAvailable = nil
        // One notification of each kind.
        model.notices = [
            Notice(id: "a", kind: .info, text: "Try Linux 1.0.5 is available. You have \(AppInfo.version).", actionTitle: "Update", action: {}),
            Notice(id: "b", kind: .success, text: "Dev box was backed up to ~/Backups."),
            Notice(id: "c", kind: .warning, text: "Ubuntu test couldn't grow its disk, so it keeps its current size."),
            Notice(id: "d", kind: .error, text: "Couldn't save settings: the disk is full."),
        ]
        snap("notices", VStack(spacing: 0) { NoticeArea(); HomeView() }, height: 860)
        model.notices = []
        // Fedora shown as running (Power in place of Start) without starting it.
        model.previewStatus = ["fedora": "Running"]
        snap("home-running", HomeView())
        model.previewStatus = ["fedora": "Running, window closed"]
        snap("home-window-closed", HomeView())
        model.previewStatus = [:]
        snap("choose", ChooseView())
        snap("setup", SetupView(plan: InstallPlan(distro: Distro.catalog[0], label: "Fedora Workstation")))
        model.progress = ProgressState(status: "Downloading Fedora-Workstation-Live-45-1.4.aarch64.iso", value: 0.42,
                                       detail: "1.17 GB of 2.78 GB  ·  24.8 MB/s  ·  2 min left", cancellable: true)
        ProgressScreen.fixedTip = 0
        snap("progress", ProgressScreen())
        // Each tab with a section or two open, and the others closed with their summaries.
        let tabs: [(String, Int, Set<String>)] = [
            ("settings-general", 0, ["Display and startup", "Shared folder"]),
            ("settings-hardware", 1, ["Resources"]),
            ("settings-disks", 1, ["Disks"]),
            ("settings-devices", 2, ["CD/DVD drive", "Network"]),
            ("settings-recovery", 3, ["Backup and restore", "Storage", "Repair"]),
            ("settings-about", 0, ["About"]),
            ("settings-closed", 2, []),
        ]
        c.cd = "/Users/you/Downloads/virtio-drivers.iso"
        for (name, tab, open) in tabs {
            SettingsView.opened = open
            snap(name, SettingsView(config: c, tab: tab), height: name == "settings-disks" ? 900 : 760)
        }
        SettingsView.opened = []
        c.cd = ""

        func dialog<V: View>(_ name: String, _ view: V) {
            let root = view.preferredColorScheme(.dark)
            let host = NSHostingView(rootView: root)
            let size = host.fittingSize
            let win = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.titled], backing: .buffered, defer: false)
            win.appearance = NSAppearance(named: .darkAqua)
            win.contentView = host
            win.orderFrontRegardless()
            host.layoutSubtreeIfNeeded()
            RunLoop.main.run(until: Date().addingTimeInterval(0.4))
            if let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) {
                host.cacheDisplay(in: host.bounds, to: rep)
                try? rep.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent(name + ".png"))
            }
            win.orderOut(nil)
        }
        let apps = "/Applications"
        dialog("install-prompt", DialogView(title: "Install Try Linux?", text: Installer.installText, path: apps,
            after: "Run opens it from here without installing; you can install it later from Settings > General > About.\n\nTo uninstall, quit Try Linux and drag it from Applications to the Trash.",
            primary: "Install", secondary: "Run", danger: false, pick: { _ in }))
        dialog("installed", DialogView(title: "Try Linux is installed", text: "Try Linux is now available in your Applications folder.", path: nil, after: nil,
            primary: "Open", secondary: nil, danger: false, pick: { _ in }))
        dialog("quit-prompt", DialogView(title: "Quit Try Linux?", text: "Try Linux will close without installing. You'll be asked again the next time you open it.",
            path: nil, after: nil, primary: "Quit", secondary: "Back", danger: false, pick: { _ in }))
        dialog("update-prompt", DialogView(title: "Update to Try Linux 1.0.5?",
            text: "You have version \(AppInfo.version). Try Linux downloads the update from GitHub, checks it against its published checksum and restarts.",
            path: nil, after: "Your Linux systems aren't affected.", primary: "Update", secondary: "What's new", danger: false, pick: { _ in }))
        dialog("update-when-quit", DialogView(title: "Update when you quit?",
            text: "Linux runs inside Try Linux, so restarting now would stop your running systems. Try Linux can download the update now and install it the next time it quits.",
            path: nil, after: nil, primary: "Update when I quit", secondary: "Cancel", danger: false, pick: { _ in }))
        // Power menu and Recovery confirmations, and Console's question for a fullscreen system.
        let sys = c.displayName
        dialog("confirm-shut-down", DialogView(title: "Shut down \(sys)?",
            text: "Linux shuts down cleanly. Its desktop may ask first; GNOME goes ahead on its own after 60 seconds.",
            path: nil, after: nil, primary: "Shut down", secondary: "Cancel", danger: false, pick: { _ in }))
        dialog("confirm-force-stop", DialogView(title: "Force \(sys) to stop?",
            text: "This turns it off immediately, like unplugging a Mac. Unsaved work in Linux is lost.",
            path: nil, after: nil, primary: "Force stop", secondary: "Cancel", danger: true, pick: { _ in }))
        dialog("confirm-delete", DialogView(title: "Delete \(sys)?",
            text: "This permanently removes its virtual disk, snapshots and everything installed in it.",
            path: nil, after: "Downloaded ISOs are kept.", primary: "Delete", secondary: "Cancel", danger: true, pick: { _ in }))
        dialog("confirm-clean-up", DialogView(title: "Delete 2 downloaded files?", text: "4.3 GB in all:",
            path: "Fedora-Workstation-Live-45-1.4.aarch64.iso\nubuntu-26.04-desktop-arm64.iso",
            after: "You can download them again later.", primary: "Delete", secondary: "Cancel", danger: true, pick: { _ in }))
        dialog("console-ask", DialogView(title: "\(sys) isn't running", text: "Would you like to power it on?",
            path: nil, after: nil, primary: "Power on", secondary: "Cancel", danger: false, pick: { _ in }))
        dialog("own-installer", OwnInstallerView(done: { _ in }))
        dialog("resize-disk", DiskSizeView(name: "Disk 1", capacity: 64 << 30, used: 17 << 30, done: { _ in }))
        dialog("add-disk", AddDiskView(done: { _ in }))
        Icons.writeIconset(to: dir.appendingPathComponent("AppIcon.iconset"))

        try? (lines.joined(separator: "\n") + "\n").write(to: dir.appendingPathComponent("results.txt"), atomically: true, encoding: .utf8)
        print(lines.joined(separator: "\n"))
        try? FileManager.default.removeItem(at: storage)
    }
}
