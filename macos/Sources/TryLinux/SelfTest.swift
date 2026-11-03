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
                if d.isImage {
                    let r = try await Resolver.resolveImage(d)
                    for p in r.parts { lines.append("resolve \(d.id) \(r.tag)\t\(p.url.absoluteString)\t\(p.size)\t\(p.sha256 ?? "no-checksum")") }
                } else {
                    let r = try await Resolver.resolve(d)
                    lines.append("resolve \(d.id)\t\(r.url.absoluteString)\t\(r.size)\t\(r.sha256 ?? "no-checksum")")
                }
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
        do {
            try Disk.create(c.diskURL, gb: 64)
            let iso = storage.appendingPathComponent("test.iso")
            FileManager.default.createFile(atPath: iso.path, contents: Data(count: 1 << 20))
            c.iso = iso.path
            c.pendingInstall = true
            c.lastBoot = "2026-11-03 09:12"
            try c.save()
            _ = try VMBuilder.configuration(c, installer: true)
            lines.append("vm configuration\tvalid (disk capacity \(Disk.capacity(c.diskURL) >> 30) GB, used \(Disk.used(c.diskURL)) bytes)")
            try Snapshots.take(c)
            lines.append("snapshot\t\(Snapshots.list(c).count) taken")
        } catch {
            lines.append("vm configuration\tERROR\t\(error.localizedDescription)")
        }

        var ubuntu = VMConfig(id: "ubuntu", displayName: "Ubuntu Desktop")
        ubuntu.distroId = "ubuntu"
        ubuntu.profile = 1
        var omarchy = VMConfig(id: "omarchy", displayName: "Omarchy")
        omarchy.distroId = "omarchy"
        let model = AppModel(preview: [c, omarchy, ubuntu])

        // --with-image: download, verify and unpack the real Omarchy image, then check the disk it produces.
        if CommandLine.arguments.contains("--with-image"), let d = Distro.find("omarchy") {
            let started = Date()
            do {
                let parts = try await model.downloadImage(d)
                lines.append("image download\t\(parts.count) parts verified in \(Int(Date().timeIntervalSince(started)))s")
                let disk = storage.appendingPathComponent("omarchy-disk.img")
                let t = Date()
                try await model.unpack(parts, to: disk)
                let h = try FileHandle(forReadingFrom: disk)
                try h.seek(toOffset: 512)
                let sig = String(decoding: try h.read(upToCount: 8) ?? Data(), as: UTF8.self)
                try h.close()
                lines.append("image unpack\t\(Int(Date().timeIntervalSince(t)))s capacity \(Disk.capacity(disk) >> 20) MB used \(Disk.used(disk) >> 20) MB gpt=\(sig == "EFI PART")")
                try Disk.grow(disk, gb: 64)
                lines.append("image grow\tcapacity \(Disk.capacity(disk) >> 30) GB used \(Disk.used(disk) >> 20) MB")
                try? FileManager.default.removeItem(at: disk)
            } catch {
                lines.append("image\tERROR\t\(error.localizedDescription)")
            }
        }
        func snap<V: View>(_ name: String, _ view: V) {
            let root = view.environmentObject(model).frame(width: 980, height: 700).background(Theme.bg).preferredColorScheme(.dark)
            let renderer = ImageRenderer(content: root)
            renderer.scale = 2
            if let img = renderer.nsImage, let tiff = img.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
               let png = rep.representation(using: .png, properties: [:]) {
                try? png.write(to: dir.appendingPathComponent(name + ".png"))
            }
            // AppKit-backed controls (text fields, scroll views) only draw in a real window, so capture that too.
            let host = NSHostingView(rootView: root)
            let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 980, height: 700), styleMask: [.titled], backing: .buffered, defer: false)
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
        snap("choose", ChooseView())
        snap("setup", SetupView(plan: InstallPlan(distro: Distro.catalog[0], label: "Fedora Workstation")))
        model.progress = ProgressState(status: "Downloading Fedora-Workstation-Live-44-1.7.aarch64.iso", value: 0.42,
                                       detail: "1.12 GB of 2.66 GB  ·  24.8 MB/s  ·  2 min left", cancellable: true)
        snap("progress", ProgressScreen())
        for (i, name) in ["settings-general", "settings-devices", "settings-advanced", "settings-recovery"].enumerated() {
            snap(name, SettingsView(config: c, tab: i))
        }
        Icons.writeIconset(to: dir.appendingPathComponent("AppIcon.iconset"))

        try? (lines.joined(separator: "\n") + "\n").write(to: dir.appendingPathComponent("results.txt"), atomically: true, encoding: .utf8)
        print(lines.joined(separator: "\n"))
        try? FileManager.default.removeItem(at: storage)
    }
}
