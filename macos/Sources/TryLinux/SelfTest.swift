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

        // Render the screens as a typical Mac would show them, not the small CI machine and its scratch folder.
        VMConfig.previewHost = (cores: 10, ramGB: 16)
        Paths.displayRoot = "~/Library/Application Support/TryLinux"
        c.sharedFolder = "~/Documents"
        c.cores = 5
        var ubuntu = VMConfig(id: "ubuntu", displayName: "Ubuntu Desktop")
        ubuntu.distroId = "ubuntu"
        ubuntu.profile = 1
        let model = AppModel(preview: [c, ubuntu])
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
        snap("choose", ChooseView())
        snap("setup", SetupView(plan: InstallPlan(distro: Distro.catalog[0], label: "Fedora Workstation")))
        model.progress = ProgressState(status: "Downloading Fedora-Workstation-Live-45-1.4.aarch64.iso", value: 0.42,
                                       detail: "1.17 GB of 2.78 GB  ·  24.8 MB/s  ·  2 min left", cancellable: true)
        ProgressScreen.fixedTip = 0
        snap("progress", ProgressScreen())
        for (i, name) in ["settings-general", "settings-devices", "settings-advanced", "settings-recovery"].enumerated() {
            // General is captured taller so it shows everything without scrolling.
            snap(name, SettingsView(config: c, tab: i), height: i == 0 ? 860 : 700)
        }
        Icons.writeIconset(to: dir.appendingPathComponent("AppIcon.iconset"))

        try? (lines.joined(separator: "\n") + "\n").write(to: dir.appendingPathComponent("results.txt"), atomically: true, encoding: .utf8)
        print(lines.joined(separator: "\n"))
        try? FileManager.default.removeItem(at: storage)
    }
}
