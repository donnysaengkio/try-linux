// Settings for one system: General, Devices, Advanced and Recovery tabs.
import SwiftUI
import Virtualization

struct SettingsView: View {
    @EnvironmentObject var model: AppModel
    @State private var c: VMConfig
    @State private var tab: Int
    @State private var openAtLogin = AppModel.openAtLogin
    @State private var rosettaState = VZLinuxRosettaDirectoryShare.availability
    @State private var showSnapshots = false
    @State private var checkUpdates = Updater.enabled
    @State private var checking = false

    static let resolutions = [(1280, 800), (1920, 1200), (2560, 1600)]

    init(config: VMConfig, tab: Int = 0) {
        _c = State(initialValue: config)
        _tab = State(initialValue: tab)
    }

    private var isRunning: Bool { model.running[c.id] != nil }

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 0) {
                header
                TabStrip(tabs: ["General", "Devices", "Advanced", "Recovery"], selection: $tab).padding(.top, 14)
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        switch tab {
                        case 1: devices
                        case 2: advanced
                        case 3: recovery
                        default: general
                        }
                    }
                    .padding(.trailing, 14)
                    .padding(.bottom, 10)
                }
                .frame(maxHeight: .infinity)
                footer
            }
            .frame(width: 640)
            .padding(.vertical, 16)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.bg)
        .sheet(isPresented: $showSnapshots) { SnapshotsSheet(config: c).environmentObject(model) }
    }

    private var header: some View {
        HStack(spacing: 14) {
            Image(nsImage: Icons.image(size: 48)).resizable().frame(width: 48, height: 48)
            VStack(alignment: .leading, spacing: 4) {
                Text(c.displayName).font(Theme.mono(19, .bold)).foregroundColor(Theme.text)
                Text([(Distro.find(c.distroId)?.name ?? "Custom ISO").uppercased(), "TRY LINUX FOR MAC v\(AppInfo.version)", isRunning ? "RUNNING" : ""]
                    .filter { !$0.isEmpty }.joined(separator: "  ·  "))
                    .font(Theme.mono(11.5)).foregroundColor(Theme.muted)
            }
            Spacer()
        }
    }

    // MARK: General

    @ViewBuilder private var general: some View {
        SectionTitle(text: "Display and startup")
        CheckRow(title: "Open fullscreen", on: $c.fullscreen)
        FormRow(label: "Resolution") {
            Segmented(options: Self.resolutions.map { "\($0.0)×\($0.1)" }, selection: resolutionIndex).frame(width: 400, height: 30)
        }
        Note(text: "On macOS 14 and later, the Linux display also follows the window size.")
        CheckRow(title: "Open Try Linux when I log in", on: $openAtLogin)

        SectionTitle(text: "Resources")
        ResourcePicker(profile: $c.profile, ramGB: $c.ramGB, cores: $c.cores)
        Note(text: "Applies next boot; no live resizing.")

        SectionTitle(text: "Storage and sharing")
        FormRow(label: "Disk capacity (GB)") {
            NumberField(value: $c.diskGB, range: 0...4096, step: 8)
            Text("0 keeps the current size").font(Theme.mono(11.5)).foregroundColor(Theme.muted)
        }
        Note(text: "Increasing grows the disk next launch; lowering never shrinks it. Linux may need to expand its partition to use the new space.")
        Note(text: "Current capacity: \(gb(Disk.capacity(c.diskURL))) (\(gb(Disk.used(c.diskURL))) used). Free on this Mac: \(gb(UInt64(max(0, Disk.freeSpace(c.dir))))).")
        FormRow(label: "Location") {
            Text(Paths.display(c.dir)).font(Theme.mono(11.5)).foregroundColor(Theme.muted).lineLimit(1).truncationMode(.middle)
        }
        FormRow(label: "Shared folder") {
            TextInput(text: $c.sharedFolder).frame(width: 300)
            Button("Browse…") {
                if let url = Ask.folder("Choose a Mac folder to share with \(c.displayName)") { c.sharedFolder = url.path }
            }
            .buttonStyle(FlatButton()).frame(width: 100, height: 28)
        }
        FormRow(label: "") { CheckRow(title: "Allow Linux to read and change this folder", on: $c.sharedWrite) }
        FormRow(label: "") { Note(text: "Off shares it read-only. In Linux: sudo mount -t virtiofs host /mnt/host") }
    }

    private var resolutionIndex: Binding<Int> {
        Binding(
            get: { Self.resolutions.firstIndex { $0.0 == c.width && $0.1 == c.height } ?? 1 },
            set: { i in c.width = Self.resolutions[i].0; c.height = Self.resolutions[i].1 })
    }

    // MARK: Devices

    @ViewBuilder private var devices: some View {
        SectionTitle(text: "Sound and microphone")
        CheckRow(title: "Enable sound", on: $c.sound)
        CheckRow(title: "Allow microphone access (next start)", on: $c.mic).disabled(!c.sound)
        Note(text: "macOS asks for permission the first time Linux uses the microphone. Turning it off keeps sound playback.")
        HStack(spacing: 10) {
            Button("Sound settings…") { open("x-apple.systempreferences:com.apple.Sound-Settings.extension") }
                .buttonStyle(FlatButton()).frame(width: 190, height: 30)
            Button("Microphone privacy…") { open("x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") }
                .buttonStyle(FlatButton()).frame(width: 210, height: 30)
        }
        Note(text: "Linux uses the Mac's current sound output and input.")

        SectionTitle(text: "Clipboard")
        CheckRow(title: "Share the clipboard between macOS and Linux", on: $c.clipboard)
        Note(text: "Needs spice-vdagent in Linux. Fedora and Ubuntu include it; on Debian and openSUSE, install the spice-vdagent package.")

        SectionTitle(text: "Camera and USB")
        Note(text: "Apple Virtualization doesn't pass Mac cameras or USB devices to Linux. Use the shared folder to move files.")
    }

    // MARK: Advanced

    @ViewBuilder private var advanced: some View {
        SectionTitle(text: "Rosetta")
        CheckRow(title: "Run x86_64 Linux apps with Rosetta (next start)", on: $c.rosetta).disabled(rosettaState == .notSupported)
        HStack(spacing: 10) {
            Note(text: rosettaText)
            if rosettaState == .notInstalled {
                Button("Install Rosetta…") { installRosetta() }.buttonStyle(FlatButton()).frame(width: 170, height: 30)
            }
        }
        Note(text: "In Linux: sudo mkdir -p /media/rosetta && sudo mount -t virtiofs rosetta /media/rosetta, then register it with binfmt (see the README).")

        SectionTitle(text: "Graphics")
        Note(text: "Apple Virtualization gives Linux a 2D virtio-gpu display. 3D / Vulkan acceleration isn't available for Linux guests.")
        Note(text: "Last successful boot: \(c.lastBoot.isEmpty ? "not started yet" : c.lastBoot).")

        SectionTitle(text: "Network")
        Note(text: "Linux shares your Mac's connection through NAT. Apple Virtualization has no port forwarding; to reach Linux from the Mac (for example over SSH), use its IP address. Run ip addr in Linux to see it.")

        SectionTitle(text: "About")
        Note(text: "Try Linux for Mac \(AppInfo.version)\nApple Virtualization on macOS \(ProcessInfo.processInfo.operatingSystemVersionString)\nStorage: \(Paths.display(Paths.root))")
        HStack(spacing: 10) {
            Button("Open storage folder") {
                try? FileManager.default.createDirectory(at: Paths.root, withIntermediateDirectories: true)
                NSWorkspace.shared.open(Paths.root)
            }
            .buttonStyle(FlatButton()).frame(width: 200, height: 30)
            if !Installer.isInApplications {
                Button("Install…") { installFromSettings() }.buttonStyle(FlatButton()).frame(width: 120, height: 30)
            }
        }
        CheckRow(title: "Check for updates automatically (once a day)", on: $checkUpdates)
            .onChange(of: checkUpdates) { Updater.enabled = $0 }
        Button(checking ? "Checking…" : model.updateAvailable.map { "Update to \($0)" } ?? "Check for updates") { checkNow() }
            .buttonStyle(FlatButton()).frame(width: 200, height: 30).disabled(checking || model.stagedUpdate != nil)
    }

    private func checkNow() {
        if model.updateAvailable != nil { Task { await model.startUpdate() }; return }
        checking = true
        Task { @MainActor in
            do {
                let found = try await Updater.check(force: true)
                checking = false
                if let found {
                    model.updateAvailable = found
                    await model.startUpdate()
                } else {
                    ThemedDialog.show("Try Linux is up to date", "You have the latest version, \(AppInfo.version).", primary: "OK")
                }
            } catch {
                checking = false
                ThemedDialog.show("Couldn't check for updates", error.localizedDescription, primary: "OK")
            }
        }
    }

    private func installFromSettings() {
        guard ThemedDialog.show("Install Try Linux?", Installer.installText, path: Paths.display(Installer.destinationFolder),
                                after: "To uninstall, quit Try Linux and drag it from Applications to the Trash.",
                                primary: "Install", secondary: "Cancel") == .primary else { return }
        do {
            let app = try Installer.install()
            try? FileManager.default.trashItem(at: Installer.originalURL, resultingItemURL: nil)
            if ThemedDialog.show("Try Linux is installed", "Try Linux is now available in your Applications folder.", primary: "Open") == .primary {
                Installer.relaunch(app)
            }
        } catch {
            ThemedDialog.show("Couldn't install Try Linux", error.localizedDescription, primary: "OK")
        }
    }

    private var rosettaText: String {
        switch rosettaState {
        case .installed: return "Rosetta is installed on this Mac."
        case .notInstalled: return "Rosetta isn't installed on this Mac yet."
        default: return "Rosetta isn't available on this Mac."
        }
    }

    private func installRosetta() {
        VZLinuxRosettaDirectoryShare.installRosetta { error in
            DispatchQueue.main.async {
                rosettaState = VZLinuxRosettaDirectoryShare.availability
                if let error { model.show("Rosetta wasn't installed: \(error.localizedDescription)") }
            }
        }
    }

    // MARK: Recovery

    @ViewBuilder private var recovery: some View {
        SectionTitle(text: "Backup and recovery")
        HStack(spacing: 10) {
            toolButton("Back up…") { backup() }
            toolButton("Restore…") { restore() }
            toolButton("Snapshots…") { if stopped() { showSnapshots = true } }
            toolButton("Reset…") { reset() }
        }
        Note(text: "Shut \(c.displayName) down first. Backups include its settings. Restore creates a separate copy, so the original stays as it is.")
        HStack(spacing: 10) {
            toolButton("Boot an ISO…") { bootISO() }
            toolButton("Clean up…") { cleanUp() }
            toolButton("Delete…", danger: true) { delete() }
        }
        Note(text: "Clean up removes downloaded installers that no system still needs.")
    }

    private func toolButton(_ title: String, danger: Bool = false, _ action: @escaping () -> Void) -> some View {
        Button(title, action: action).buttonStyle(FlatButton(danger: danger)).frame(width: 148, height: 30)
    }

    private func stopped() -> Bool {
        if !isRunning { return true }
        model.show("Shut \(c.displayName) down first, from its window or the Try Linux menu bar icon.")
        return false
    }

    private func backup() {
        guard stopped(), let folder = Ask.folder("Back up \(c.displayName) to…", prompt: "Back Up") else { return }
        let cfg = c
        model.runTool("Backing up \(cfg.displayName)", for: cfg.id) {
            let dest = try Backups.backup(cfg, into: folder)
            return "Backed up \(cfg.displayName) to\n\(dest.path)"
        }
    }

    private func restore() {
        guard let folder = Ask.folder("Choose a Try Linux backup (.trylinux folder)", prompt: "Restore") else { return }
        let id = c.id
        model.runTool("Restoring a copy", for: id) {
            let restored = try Backups.restore(from: folder)
            return "Restored as \"\(restored.displayName)\"."
        }
    }

    private func reset() {
        guard stopped() else { return }
        var iso = URL(fileURLWithPath: c.iso)
        if !FileManager.default.fileExists(atPath: iso.path) {
            guard let picked = Ask.file("Choose the installer to start over with") else { return }
            iso = picked
        }
        guard Ask.confirm("Reset \(c.displayName)?", "This erases everything installed in it, including its snapshots, and starts over with the installer. Back it up first if you might need anything.", "Reset", destructive: true) else { return }
        let cfg = c, isoPath = iso.path
        model.runTool("Resetting \(cfg.displayName)", for: cfg.id) {
            let size = max(16, Int(Disk.capacity(cfg.diskURL) >> 30), cfg.diskGB)
            try FileManager.default.removeItem(at: cfg.diskURL)
            try? FileManager.default.removeItem(at: cfg.efiURL)
            try? FileManager.default.removeItem(at: cfg.snapshotsDir)
            try Disk.create(cfg.diskURL, gb: size)
            var fresh = VMConfig.load(cfg.dir) ?? cfg
            fresh.iso = isoPath
            fresh.pendingInstall = true
            try fresh.save()
            return "\(cfg.displayName) was reset. Click Launch to run the installer."
        }
    }

    private func bootISO() {
        guard stopped(), let iso = Ask.file("Boot \(c.displayName) from an ISO") else { return }
        c.iso = iso.path
        c.pendingInstall = true
        if save() { model.boot(c) }
    }

    private func cleanUp() {
        let needed = Set(model.systems.filter(\.pendingInstall).map(\.iso))
        let files = ((try? FileManager.default.contentsOfDirectory(at: Paths.isos, includingPropertiesForKeys: [.fileSizeKey])) ?? [])
            .filter { !needed.contains($0.path) }
        guard !files.isEmpty else { model.show("Nothing to clean up. Installers that a system still needs are kept."); return }
        let total = files.reduce(Int64(0)) { $0 + Int64((try? $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
        let names = files.prefix(8).map(\.lastPathComponent).joined(separator: "\n") + (files.count > 8 ? "\n…" : "")
        guard Ask.confirm("Delete \(files.count) downloaded file(s), \(formatBytes(total))?", names + "\n\nYou can download them again later.", "Delete") else { return }
        var failed = 0
        for f in files { do { try FileManager.default.removeItem(at: f) } catch { failed += 1 } }
        model.show("Freed \(formatBytes(total))." + (failed > 0 ? " \(failed) file(s) couldn't be deleted." : ""))
    }

    private func delete() {
        guard stopped(), Ask.confirm("Delete \(c.displayName)?", "This permanently removes its virtual disk, snapshots and everything installed in it. Downloaded ISOs are kept.", "Delete", destructive: true) else { return }
        do { try FileManager.default.removeItem(at: c.dir) } catch { model.show("Couldn't delete it completely: \(error.localizedDescription)") }
        model.goHome()
    }

    // MARK: Footer

    private var footer: some View {
        VStack(alignment: .leading, spacing: 10) {
            Note(text: isRunning ? "\(c.displayName) is running. Changes apply the next time it starts." : "Your files persist between sessions. Choose your settings, then launch.")
            HStack(spacing: 10) {
                Button("Back") { model.goHome() }.buttonStyle(FlatButton()).frame(width: 120, height: 42)
                Spacer()
                Button("Save") { if save() { model.goHome() } }.buttonStyle(FlatButton()).frame(width: 120, height: 42)
                Button(isRunning ? "Show \(short)" : "Launch \(short)") { if save() { model.boot(c) } }
                    .buttonStyle(FlatButton(primary: true)).frame(width: 190, height: 42)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(.top, 12)
    }

    private var short: String {
        let first = c.displayName.split(separator: " ").first.map(String.init) ?? c.displayName
        return String(first.prefix(14))
    }

    private func save() -> Bool {
        if !c.sharedFolder.isEmpty {
            var isDir: ObjCBool = false
            guard FileManager.default.fileExists(atPath: c.sharedFolder, isDirectory: &isDir), isDir.boolValue else {
                tab = 0
                model.show("The shared folder doesn't exist.")
                return false
            }
        }
        do { try c.save() } catch { model.show("Couldn't save settings: \(error.localizedDescription)"); return false }
        if openAtLogin != AppModel.openAtLogin { AppModel.openAtLogin = openAtLogin }
        model.reload()
        return true
    }

    private func gb(_ bytes: UInt64) -> String { String(format: "%.1f GB", Double(bytes) / 1_073_741_824) }

    private func open(_ s: String) { if let u = URL(string: s) { NSWorkspace.shared.open(u) } }
}

struct SnapshotsSheet: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let config: VMConfig
    @State private var items: [Snapshots.Item] = []
    @State private var selected: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Snapshots  ·  \(config.displayName)").font(Theme.mono(15, .bold)).foregroundColor(Theme.text)
            Note(text: "A snapshot saves the whole disk as it is now. It's instant and only uses space as the disk changes. Reverting throws away changes made since.")
            VStack(spacing: 4) {
                if items.isEmpty {
                    Note(text: "No snapshots yet.").padding(10)
                }
                ForEach(items) { s in
                    Text(s.id).font(Theme.mono(13)).foregroundColor(Theme.text)
                        .frame(maxWidth: .infinity, alignment: .leading).padding(8)
                        .background(s.id == selected ? Theme.cardSelected : Theme.card)
                        .overlay(Rectangle().stroke(s.id == selected ? Theme.accent : Theme.border, lineWidth: 1))
                        .contentShape(Rectangle())
                        .onTapGesture { selected = s.id }
                }
                Spacer(minLength: 0)
            }
            .frame(height: 220, alignment: .top)
            HStack(spacing: 10) {
                Button("Take snapshot") { act { try Snapshots.take(config) } }.buttonStyle(FlatButton(primary: true)).frame(width: 160, height: 34)
                Button("Revert") {
                    if let s = current, Ask.confirm("Revert to \(s.id)?", "Changes made to \(config.displayName) since then are lost.", "Revert", destructive: true) {
                        act { try Snapshots.revert(config, to: s) }
                    }
                }
                .buttonStyle(FlatButton()).frame(width: 100, height: 34).disabled(current == nil)
                Button("Delete") {
                    if let s = current, Ask.confirm("Delete the snapshot \(s.id)?", "", "Delete", destructive: true) {
                        act { try Snapshots.delete(config, s) }
                    }
                }
                .buttonStyle(FlatButton(danger: true)).frame(width: 100, height: 34).disabled(current == nil)
                Spacer()
                Button("Close") { dismiss() }.buttonStyle(FlatButton()).frame(width: 100, height: 34)
            }
        }
        .padding(20)
        .frame(width: 560)
        .background(Theme.bg)
        .onAppear { items = Snapshots.list(config) }
    }

    private var current: Snapshots.Item? { items.first { $0.id == selected } }

    private func act(_ work: () throws -> Void) {
        do { try work() } catch { model.show(error.localizedDescription) }
        items = Snapshots.list(config)
        if !items.contains(where: { $0.id == selected }) { selected = nil }
    }
}
