// Settings for one system: General, Hardware, Devices and Recovery tabs. Each tab is a list of sections
// that open and close; a closed section shows a summary of its settings.
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
    @State private var open: Set<String>
    @State private var diskIndex = 0

    static let resolutions = [(1280, 800), (1920, 1200), (2560, 1600)]
    // Which sections are open, kept while Try Linux runs.
    static var opened: Set<String> = []

    init(config: VMConfig, tab: Int = 0) {
        _c = State(initialValue: config)
        _tab = State(initialValue: tab)
        _open = State(initialValue: Self.opened)
    }

    private var isRunning: Bool { model.running[c.id] != nil }

    private func bar(_ title: String) -> Binding<Bool> {
        Binding(get: { open.contains(title) },
                set: { v in if v { open.insert(title) } else { open.remove(title) }; Self.opened = open })
    }

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 0) {
                header
                TabStrip(tabs: ["General", "Hardware", "Devices", "Recovery"], selection: $tab).padding(.top, 28)
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        switch tab {
                        case 1: hardware
                        case 2: devices
                        case 3: recovery
                        default: general
                        }
                    }
                    .padding(.top, 16)
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

    // Renaming: the pencil after the name edits it in place; Return (or clicking away) saves, Esc cancels.
    @State private var editingName = false
    @State private var nameDraft = ""
    @FocusState private var nameFocused: Bool

    private func finishRename(_ keep: Bool) {
        guard editingName else { return }
        editingName = false
        let n = nameDraft.trimmingCharacters(in: .whitespaces)
        if keep, !n.isEmpty, n != c.displayName {
            c.displayName = n
            model.rename(c, to: n)
        }
    }

    private var header: some View {
        HStack(spacing: 14) {
            Image(nsImage: Icons.image(size: 48)).resizable().frame(width: 48, height: 48)
            VStack(alignment: .leading, spacing: 4) {
                if editingName {
                    TextField("", text: $nameDraft)
                        .textFieldStyle(.plain)
                        .font(Theme.display(21)).foregroundColor(Theme.text)
                        .padding(.horizontal, 6).frame(height: 28)
                        .background(Theme.card).overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
                        .focused($nameFocused)
                        .onSubmit { finishRename(true) }
                        .onExitCommand { finishRename(false) }
                        .onChange(of: nameFocused) { focused in if !focused { finishRename(true) } }
                } else {
                    HStack(spacing: 8) {
                        Text(c.displayName).font(Theme.display(21)).foregroundColor(Theme.text).lineLimit(1)
                        Button {
                            nameDraft = c.displayName
                            editingName = true
                            DispatchQueue.main.async { nameFocused = true }
                        } label: {
                            Image(systemName: "pencil").font(.system(size: 13)).foregroundColor(Theme.muted)
                                .frame(width: 24, height: 24).contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .help("Rename")
                    }
                }
                Text([model.osLabel(c).uppercased(), "TRY LINUX FOR MAC v\(AppInfo.version)", isRunning ? "RUNNING" : ""]
                    .filter { !$0.isEmpty }.joined(separator: "  ·  "))
                    .font(Theme.sans(11.5)).foregroundColor(Theme.muted)
            }
            Spacer()
        }
    }

    // MARK: General

    @ViewBuilder private var general: some View {
        SectionBar(title: "Display and startup",
                   summary: (c.fullscreen ? "Fullscreen" : "In a window") + "  ·  \(c.width)×\(c.height)" + (openAtLogin ? "  ·  Opens when you log in" : ""),
                   open: bar("Display and startup")) {
            CheckRow(title: "Open fullscreen", on: $c.fullscreen)
            FormRow(label: "Resolution") {
                Segmented(options: Self.resolutions.map { "\($0.0)×\($0.1)" }, selection: resolutionIndex).frame(width: 400, height: 30)
            }
            Note(text: "On macOS 14 and later, the Linux display also follows the window size.")
            CheckRow(title: "Open Try Linux when I log in", on: $openAtLogin)
        }

        SectionBar(title: "Shared folder", summary: sharedSummary, open: bar("Shared folder")) {
            FormRow(label: "Shared folder") {
                TextInput(text: $c.sharedFolder)
                Button("Browse") {
                    if let url = Ask.folder("Choose a Mac folder to share with \(c.displayName)") { c.sharedFolder = url.path }
                }
                .buttonStyle(FlatButton()).frame(width: ActionRow.width(["Browse"]), height: 28)
            }
            .padding(.trailing, 12)
            FormRow(label: "") { CheckRow(title: "Allow Linux to read and change this folder", on: $c.sharedWrite) }
            FormRow(label: "") { Note(text: "Off shares it read-only. In Linux: sudo mount -t virtiofs host /mnt/host") }
        }

        SectionBar(title: "About", summary: "Try Linux for Mac \(AppInfo.version)" + (model.updateAvailable.map { "  ·  \($0) available" } ?? ""),
                   open: bar("About")) {
            Note(text: "Try Linux for Mac \(AppInfo.version)\nApple Virtualization on macOS \(ProcessInfo.processInfo.operatingSystemVersionString)\nStorage: \(Paths.display(Paths.root))")
            HStack(spacing: 10) {
                Button("Open Storage Folder") {
                    try? FileManager.default.createDirectory(at: Paths.root, withIntermediateDirectories: true)
                    NSWorkspace.shared.open(Paths.root)
                }
                .buttonStyle(FlatButton()).frame(width: ActionRow.width(["Open Storage Folder"]), height: 30)
                if !Installer.isInApplications {
                    Button("Install") { installFromSettings() }.buttonStyle(FlatButton()).frame(width: ActionRow.width(["Install"]), height: 30)
                }
                Spacer()
            }
            CheckRow(title: "Check for updates automatically (once a day)", on: $checkUpdates)
                .onChange(of: checkUpdates) { Updater.enabled = $0 }
            HStack {
                let title = checking ? "Checking…" : model.updateAvailable.map { "Update To \($0)" } ?? "Check For Updates"
                Button(title) { checkNow() }
                    .buttonStyle(FlatButton()).frame(width: ActionRow.width([title]), height: 30).disabled(checking || model.stagedUpdate != nil)
                Spacer()
            }
        }
    }

    private var sharedSummary: String {
        let f = c.sharedFolder.trimmingCharacters(in: .whitespaces)
        if f.isEmpty { return "Not shared" }
        return URL(fileURLWithPath: f).lastPathComponent + (c.sharedWrite ? "  ·  Read and write" : "  ·  Read-only")
    }

    private var resolutionIndex: Binding<Int> {
        Binding(
            get: { Self.resolutions.firstIndex { $0.0 == c.width && $0.1 == c.height } ?? 1 },
            set: { i in c.width = Self.resolutions[i].0; c.height = Self.resolutions[i].1 })
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

    // MARK: Hardware

    private var hardwareWidth: CGFloat { ActionRow.width(["Resize", "Add disk", "Remove", "Install Rosetta"]) }

    // What other running systems use, so Resources only offers what's left.
    private var others: (cores: Int, ramGB: Int) {
        var cores = 0, ram = 0
        for s in model.running.values where !s.held && s.config.id != c.id {
            let r = s.config.resources()
            cores += r.cores
            ram += r.ramGB
        }
        return (cores: cores, ramGB: ram)
    }

    @ViewBuilder private var hardware: some View {
        SectionBar(title: "Graphics", summary: "2D virtio-gpu display", open: bar("Graphics")) {
            Note(text: "Apple Virtualization gives Linux a 2D virtio-gpu display. 3D / Vulkan acceleration isn't available for Linux guests.")
            Note(text: "Last successful boot: \(c.lastBoot.isEmpty ? "not started yet" : c.lastBoot).")
        }

        SectionBar(title: "Resources", summary: resourcesSummary, open: bar("Resources")) {
            ResourcePicker(profile: $c.profile, ramGB: $c.ramGB, cores: $c.cores, otherCores: others.cores, otherRamGB: others.ramGB)
        }

        SectionBar(title: "Disks", summary: disksSummary, open: bar("Disks")) { disksSection }

        SectionBar(title: "Disk utilities", summary: "Add or remove disks", open: bar("Disk utilities")) {
            ActionRow(text: "Add another disk to \(c.displayName).", button: "Add disk", width: hardwareWidth) { addDisk() }
            ActionRow(text: "Take this disk out of \(c.displayName).", button: "Remove", width: hardwareWidth) { removeDisk() }
                .disabled(diskIndex == 0)
        }

        SectionBar(title: "Rosetta", summary: c.rosetta ? "On" : "Off", open: bar("Rosetta")) {
            CheckRow(title: "Run x86_64 Linux apps with Rosetta (next start)", on: $c.rosetta).disabled(rosettaState == .notSupported)
            if rosettaState == .notInstalled {
                ActionRow(text: rosettaText, button: "Install Rosetta", width: hardwareWidth) { installRosetta() }
            } else {
                Note(text: rosettaText)
            }
            Note(text: "In Linux: sudo mkdir -p /media/rosetta && sudo mount -t virtiofs rosetta /media/rosetta, then register it with binfmt (see the README).")
        }
    }

    private var resourcesSummary: String {
        let r = c.resources()
        return "\(VMConfig.profileNames[min(2, max(0, c.profile))])  ·  \(r.cores) cores  ·  \(r.ramGB) GB"
    }

    private var selectedDisk: URL {
        let all = c.allDisks
        return all[min(diskIndex, all.count - 1)]
    }

    private var disksSummary: String {
        let n = c.allDisks.count
        return "\(n) \(n == 1 ? "disk" : "disks")  ·  Disk 1: \(size(Disk.used(c.diskURL))) of \(size(Disk.capacity(c.diskURL)))"
    }

    @ViewBuilder private var disksSection: some View {
        let url = selectedDisk
        let exists = FileManager.default.fileExists(atPath: url.path)
        FormRow(label: "Disk") {
            DropdownField(options: c.allDisks.indices.map { "Disk \($0 + 1)" }, selection: $diskIndex, width: 100)
        }
        Note(text: "Linux sees each disk as its own drive. \(c.displayName) starts from Disk 1.")

        SubHeading(text: "Capacity")
        FormRow(label: "Current size") { Text(exists ? size(Disk.used(url)) : "The file is missing. It may have been moved or deleted.").font(Theme.sans(13)).foregroundColor(Theme.text) }
        FormRow(label: "System free") { Text(exists ? size(UInt64(max(0, Disk.freeSpace(url.deletingLastPathComponent())))) + " on this Mac" : "").font(Theme.sans(13)).foregroundColor(Theme.text) }
        FormRow(label: "Maximum size") { Text(exists ? size(Disk.capacity(url)) : "").font(Theme.sans(13)).foregroundColor(Theme.text) }

        SubHeading(text: "Disk information")
        Note(text: "Disk space is not preallocated: the file grows as Linux uses it, up to the maximum size.")
        Note(text: "Stored in \(url.lastPathComponent)" + (diskIndex == 0 ? ", the disk \(c.displayName) starts from." : "."))
        // The path field starts right after its label.
        HStack(spacing: 12) {
            Text("Location").font(Theme.sans(13)).foregroundColor(Theme.text).fixedSize()
            Text(Paths.display(url)).font(Theme.sans(12)).foregroundColor(Theme.text).lineLimit(1).truncationMode(.middle)
                .textSelection(.enabled)
                .padding(.horizontal, 8).frame(maxWidth: .infinity, minHeight: 26, alignment: .leading)
                .background(Theme.card).overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
        }
        .padding(.trailing, 12)
        ActionRow(text: "Shrink or expand this disk's maximum size.", button: "Resize", width: hardwareWidth) { resizeDisk() }
            .disabled(!exists)
    }

    private func size(_ bytes: UInt64) -> String {
        let b = Double(bytes)
        return b >= 1_073_741_824 ? String(format: "%.1f GB", b / 1_073_741_824) : String(format: "%.1f MB", b / 1_048_576)
    }

    // Disk changes take effect at once, so they're saved straight away instead of with Save.
    private func saveDisks() {
        var fresh = VMConfig.load(c.dir) ?? c
        fresh.extraDisks = c.extraDisks
        fresh.diskGB = c.diskGB
        try? fresh.save()
    }

    private func resizeDisk() {
        guard stopped("resizing its disks") else { return }
        let url = selectedDisk, name = "Disk \(diskIndex + 1)"
        let capacity = Disk.capacity(url), used = Disk.used(url)
        guard let gb = DiskSizeDialog.ask(name: name, capacity: capacity, used: used) else { return }
        let shrink = UInt64(gb) << 30 < capacity
        if shrink, !Ask.confirm("Shrink \(name) to \(gb) GB?",
                                "Anything Linux stored past \(gb) GB is lost, so shrink its partitions in Linux first (with GNOME Disks or GParted, for example).",
                                "Shrink", after: "Back it up first if you might need anything.", destructive: true) { return }
        do {
            try Disk.resize(url, gb: gb)
            if diskIndex == 0 { c.diskGB = 0; saveDisks() }   // its new size is its own now; nothing to grow at start
            model.show("\(name) is now \(gb) GB." + (shrink ? "" : " Linux may need to expand its partition to use the new space."))
        } catch {
            model.show("Couldn't resize the disk.\n\n\(error.localizedDescription)")
        }
        model.reload()
    }

    private func addDisk() {
        guard let pick = AddDiskDialog.ask() else { return }
        do {
            if let file = pick.file {
                let path = file.standardizedFileURL.path
                if c.allDisks.contains(where: { $0.standardizedFileURL.path == path }) { model.show("That disk is already part of \(c.displayName)."); return }
                if model.systems.contains(where: { s in s.id != c.id && s.allDisks.contains { $0.standardizedFileURL.path == path } }) {
                    model.show("That disk belongs to another system. Two systems can't share a disk.")
                    return
                }
                c.extraDisks.append(path)
            } else {
                let name = c.newDiskName()
                try Disk.create(c.dir.appendingPathComponent(name), gb: pick.gb)
                c.extraDisks.append(name)
            }
            saveDisks()
            diskIndex = c.extraDisks.count
            model.show("Disk \(c.extraDisks.count + 1) was added to \(c.displayName)." + (isRunning ? " Linux sees it the next time \(c.displayName) starts." : "")
                       + (pick.file == nil ? " Partition and format it in Linux (with GNOME Disks, for example) before using it." : ""))
        } catch {
            model.show("Couldn't add the disk.\n\n\(error.localizedDescription)")
        }
    }

    private func removeDisk() {
        guard diskIndex > 0, stopped("removing a disk") else { return }
        let entry = c.extraDisks[diskIndex - 1], url = c.diskFile(entry), name = "Disk \(diskIndex + 1)"
        let ours = !entry.hasPrefix("/")
        guard Ask.confirm("Remove \(name) from \(c.displayName)?",
                          ours ? "Its file goes to the Trash, with everything Linux stored on it." : "The disk file is kept where it is:",
                          "Remove", path: ours ? nil : Paths.display(url),
                          after: ours ? "To get it back, put the file back from the Trash and add it with Add disk." : "You can add it again with Add disk.",
                          destructive: ours) else { return }
        do {
            if ours, FileManager.default.fileExists(atPath: url.path) { try FileManager.default.trashItem(at: url, resultingItemURL: nil) }
            c.extraDisks.remove(at: diskIndex - 1)
            saveDisks()
            diskIndex -= 1
            model.show("\(name) was removed from \(c.displayName).")
        } catch {
            model.show("Couldn't remove the disk.\n\n\(error.localizedDescription)")
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

    // MARK: Devices

    @ViewBuilder private var devices: some View {
        SectionBar(title: "Microphone and sound",
                   summary: !c.sound ? "Sound off" : c.mic ? "Sound and microphone on" : "Sound on, microphone off",
                   open: bar("Microphone and sound")) {
            CheckRow(title: "Enable sound", on: $c.sound)
            CheckRow(title: "Allow microphone access (next start)", on: $c.mic).disabled(!c.sound)
            Note(text: "macOS asks for permission the first time Linux uses the microphone. Turning it off keeps sound playback.")
            HStack(spacing: 10) {
                let w = ActionRow.width(["Sound Settings", "Microphone Privacy"])
                Button("Sound Settings") { open("x-apple.systempreferences:com.apple.Sound-Settings.extension") }
                    .buttonStyle(FlatButton()).frame(width: w, height: 30)
                Button("Microphone Privacy") { open("x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") }
                    .buttonStyle(FlatButton()).frame(width: w, height: 30)
                Spacer()
            }
            Note(text: "Linux uses the Mac's current sound output and input.")
        }

        SectionBar(title: "Clipboard", summary: c.clipboard ? "Shared" : "Not shared", open: bar("Clipboard")) {
            CheckRow(title: "Share the clipboard between macOS and Linux", on: $c.clipboard)
            Note(text: "Needs spice-vdagent in Linux. Fedora and Ubuntu include it; on Debian and openSUSE, install the spice-vdagent package.")
        }

        SectionBar(title: "CD/DVD drive", summary: c.cd.isEmpty ? "Empty" : URL(fileURLWithPath: c.cd).lastPathComponent, open: bar("CD/DVD drive")) {
            let w = ActionRow.width(["Insert", "Eject"])
            FormRow(label: "Disc") {
                Text(c.cd.isEmpty ? "Empty" : Paths.display(URL(fileURLWithPath: c.cd)))
                    .font(Theme.sans(13)).foregroundColor(c.cd.isEmpty ? Theme.muted : Theme.text).lineLimit(1).truncationMode(.middle)
                    .padding(.horizontal, 8).frame(maxWidth: .infinity, minHeight: 26, alignment: .leading)
                    .background(Theme.card).overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
                Button("Insert") { if let iso = Ask.file("Insert a disc into \(c.displayName)") { c.cd = iso.path } }
                    .buttonStyle(FlatButton()).frame(width: w, height: 28)
                Button("Eject") { c.cd = "" }
                    .buttonStyle(FlatButton()).frame(width: w, height: 28).disabled(c.cd.isEmpty)
            }
            .padding(.trailing, 12)
            Note(text: "Put an ISO in the drive, such as drivers or a rescue disk. Linux sees it from the next start; \(c.displayName) still starts from Disk 1. To start from a disc, use Recovery.")
        }

        SectionBar(title: "Camera and USB", summary: "Not available in Apple Virtualization", open: bar("Camera and USB")) {
            Note(text: "Apple Virtualization doesn't pass Mac cameras or USB devices to Linux. Use the shared folder to move files.")
        }

        SectionBar(title: "Network", summary: c.netType == 1 ? "No network" : "Shared (NAT)", open: bar("Network")) {
            FormRow(label: "Network type") {
                DropdownField(options: VMConfig.netTypeNames, selection: $c.netType, width: 220)
            }
            Note(text: c.netType == 1
                 ? "No network card: Linux is fully offline. Changes apply the next time it starts."
                 : "Linux shares your Mac's connection through NAT. Changes apply the next time it starts.")
            Note(text: "Apple Virtualization has no port forwarding; to reach Linux from the Mac (for example over SSH), use its IP address. Run ip addr in Linux to see it.")
        }
    }

    // MARK: Recovery

    private var recoveryWidth: CGFloat { ActionRow.width(["Back up", "Restore", "Snapshots", "Clean up", "Reset system", "Boot an ISO", "Delete"]) }

    @ViewBuilder private var recovery: some View {
        let w = recoveryWidth, who = c.displayName
        SectionBar(title: "Backup and restore", summary: "Back up, or restore a copy", open: bar("Backup and restore")) {
            ActionRow(text: "Save a copy of \(who), its disks and its settings.", button: "Back up", width: w) { backup() }
            ActionRow(text: "Restore a backup as a separate copy; the original stays.", button: "Restore", width: w) { restore() }
            Note(text: "Shut \(who) down before backing it up.")
        }
        SectionBar(title: "Snapshots", summary: "Points in time for Disk 1", open: bar("Snapshots")) {
            ActionRow(text: "Save Disk 1 as it is now, or go back to a saved point.", button: "Snapshots", width: w) { if stopped() { showSnapshots = true } }
        }
        SectionBar(title: "Storage", summary: "Clean up downloads", open: bar("Storage")) {
            ActionRow(text: "Delete installers that no system still needs.", button: "Clean up", width: w) { cleanUp() }
        }
        SectionBar(title: "Repair", summary: "Reset, or boot an ISO", open: bar("Repair")) {
            ActionRow(text: "Erase \(who) and run its installer again.", button: "Reset system", width: w) { reset() }
            ActionRow(text: "Start \(who) from an ISO, such as a rescue disk.", button: "Boot an ISO", width: w) { bootISO() }
        }
        SectionBar(title: "Delete", summary: "Remove \(who)", open: bar("Delete")) {
            ActionRow(text: "Remove \(who) and everything installed in it.", button: "Delete", width: w, danger: true) { delete() }
        }
    }

    private func stopped(_ doing: String? = nil) -> Bool {
        if !isRunning { return true }
        model.show("Shut \(c.displayName) down " + (doing.map { "before \($0)" } ?? "first") + "\n\nUse Power on Virtual Machines, or the Try Linux menu bar icon.")
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
        guard Ask.confirm("Reset \(c.displayName)?", "This erases everything installed in it, including its snapshots, and starts over with the installer.", "Reset",
                          after: "Back it up first if you might need anything.", destructive: true) else { return }
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
            return "\(cfg.displayName) was reset. Click Install on Virtual Machines to run the installer."
        }
    }

    private func bootISO() {
        guard stopped(), let iso = Ask.file("Boot \(c.displayName) from an ISO") else { return }
        c.iso = iso.path
        c.pendingInstall = true
        if save() { model.boot(c) }
    }

    private func cleanUp() {
        let needed = Set(model.systems.filter(\.pendingInstall).map(\.iso) + model.systems.map(\.cd))
        let files = ((try? FileManager.default.contentsOfDirectory(at: Paths.isos, includingPropertiesForKeys: [.fileSizeKey])) ?? [])
            .filter { !needed.contains($0.path) }
        guard !files.isEmpty else {
            ThemedDialog.show("Nothing to clean up", "Installers that a system still needs are kept.", primary: "OK")
            return
        }
        let total = files.reduce(Int64(0)) { $0 + Int64((try? $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
        let names = files.prefix(8).map(\.lastPathComponent).joined(separator: "\n") + (files.count > 8 ? "\n… and \(files.count - 8) more" : "")
        guard Ask.confirm("Delete \(files.count) downloaded \(files.count == 1 ? "file" : "files")?", "\(formatBytes(total)) in all:",
                          "Delete", path: names, after: "You can download them again later.", destructive: true) else { return }
        var failed = 0
        for f in files { do { try FileManager.default.removeItem(at: f) } catch { failed += 1 } }
        ThemedDialog.show("Freed \(formatBytes(total))", failed > 0 ? "\(failed) file(s) couldn't be deleted." : nil, primary: "OK")
    }

    private func delete() {
        let attached = c.extraDisks.contains { $0.hasPrefix("/") }
        guard stopped(), Ask.confirm("Delete \(c.displayName)?", "This permanently removes its virtual disks, snapshots and everything installed in it.", "Delete",
                                     after: attached ? "Disks added from other folders and downloaded ISOs are kept." : "Downloaded ISOs are kept.",
                                     destructive: true) else { return }
        do { try FileManager.default.removeItem(at: c.dir) } catch { model.show("Couldn't delete it completely: \(error.localizedDescription)") }
        model.goHome()
    }

    // MARK: Footer

    private var footer: some View {
        // Save is the page's main action; start a system from Virtual Machines. Both are the same size.
        HStack(spacing: 10) {
            Button("Back") { model.goHome() }.buttonStyle(FlatButton()).frame(width: 120, height: 42)
            Spacer()
            Button("Save") { if save() { model.goHome() } }.buttonStyle(FlatButton(primary: true)).frame(width: 120, height: 42)
                .keyboardShortcut(.defaultAction)
        }
        .padding(.top, 12)
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
        // Disks were saved as they changed; keep what's on disk for them.
        var toSave = c
        if let fresh = VMConfig.load(c.dir) { toSave.extraDisks = fresh.extraDisks; toSave.diskGB = fresh.diskGB }
        do { try toSave.save() } catch { model.show("Couldn't save settings: \(error.localizedDescription)"); return false }
        if openAtLogin != AppModel.openAtLogin { AppModel.openAtLogin = openAtLogin }
        model.reload()
        return true
    }

    private func open(_ s: String) { if let u = URL(string: s) { NSWorkspace.shared.open(u) } }
}

// A dropdown list styled like the other fields. SwiftUI's Menu draws its own macOS pull-down on the
// Mac whatever its label looks like, so this is a plain button that opens the list as a menu.
struct DropdownField: View {
    let options: [String]
    @Binding var selection: Int
    var width: CGFloat = 220

    var body: some View {
        Button { ChoiceMenu.show(options, selected: selection) { selection = $0 } } label: {
            HStack(spacing: 0) {
                Text(options.indices.contains(selection) ? options[selection] : "")
                    .font(Theme.sans(13)).foregroundColor(Theme.text).lineLimit(1)
                    .padding(.leading, 8)
                Spacer(minLength: 4)
                Image(systemName: "chevron.down").font(.system(size: 9, weight: .bold)).foregroundColor(Theme.text)
                    .frame(width: 24, height: 26)
                    .overlay(Rectangle().frame(width: 1).foregroundColor(Theme.border), alignment: .leading)
            }
            .frame(width: width, height: 26)
            .background(Theme.card)
            .overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

// A list of choices as a menu under the mouse, with the current one checked.
enum ChoiceMenu {
    private final class Action: NSObject {
        let run: () -> Void
        init(_ run: @escaping () -> Void) { self.run = run }
        @objc func fire() { run() }
    }

    @MainActor
    static func show(_ options: [String], selected: Int, pick: @escaping (Int) -> Void) {
        let menu = NSMenu()
        var actions: [Action] = []
        for (i, title) in options.enumerated() {
            let a = Action { pick(i) }
            actions.append(a)
            let item = NSMenuItem(title: title, action: #selector(Action.fire), keyEquivalent: "")
            item.target = a
            item.state = i == selected ? .on : .off
            menu.addItem(item)
        }
        menu.appearance = NSAppearance(named: .darkAqua)
        withExtendedLifetime(actions) { _ = menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil) }
    }
}

// Resize: a new maximum size for a disk, larger or smaller.
enum DiskSizeDialog {
    @MainActor
    static func ask(name: String, capacity: UInt64, used: UInt64) -> Int? {
        var result: Int?
        ThemedDialog.present { close in
            DiskSizeView(name: name, capacity: capacity, used: used) { gb in result = gb; close() }
        }
        return result
    }
}

struct DiskSizeView: View {
    let name: String
    let capacity: UInt64
    let used: UInt64
    let done: (Int?) -> Void
    @State private var gb = 0

    private var current: Int { Int((capacity + (1 << 30) - 1) >> 30) }
    private var minimum: Int { max(1, Int((used + (1 << 30) - 1) >> 30) + 1) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 16) {
                Image(nsImage: Icons.image(size: 40)).resizable().frame(width: 40, height: 40)
                VStack(alignment: .leading, spacing: 10) {
                    Text("Resize \(name)").font(Theme.display(18)).foregroundColor(Theme.text)
                    Text("It's \(current) GB now, and Linux has written \(String(format: "%.1f", Double(used) / 1_073_741_824)) GB to it.")
                        .font(Theme.sans(12)).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
                    HStack(spacing: 10) {
                        Text("New maximum size (GB)").font(Theme.sans(13)).foregroundColor(Theme.text)
                        SizeField(value: $gb, presets: Sizes.disk, range: minimum...4096)
                    }
                    Text(gb < current
                         ? "Shrinking cuts off the end of the disk. Shrink Linux's partitions to fit first, or what's stored there is lost."
                         : "Linux may need to expand its partition to use the new space.")
                        .font(Theme.sans(12)).foregroundColor(gb < current ? Theme.warning : Theme.muted)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            HStack(spacing: 10) {
                Spacer()
                Button("Cancel") { done(nil) }.buttonStyle(FlatButton()).frame(width: 150, height: 36).keyboardShortcut(.cancelAction)
                Button("Resize") { done(gb) }.buttonStyle(FlatButton(primary: true)).frame(width: 150, height: 36)
                    .keyboardShortcut(.defaultAction).disabled(gb == current)
            }
            .padding(.top, 22)
        }
        .padding(24)
        .frame(width: 520)
        .background(Theme.bg)
        .onAppear { gb = max(minimum, current) }
    }
}

// Add disk: a new empty disk of a chosen size, or an existing disk image file.
enum AddDiskDialog {
    struct Pick { var gb = 64; var file: URL? }

    @MainActor
    static func ask() -> Pick? {
        var result: Pick?
        ThemedDialog.present { close in
            AddDiskView { pick in result = pick; close() }
        }
        return result
    }
}

struct AddDiskView: View {
    let done: (AddDiskDialog.Pick?) -> Void
    @State private var gb = 64
    @State private var file: URL?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 16) {
                Image(nsImage: Icons.image(size: 40)).resizable().frame(width: 40, height: 40)
                VStack(alignment: .leading, spacing: 0) {
                    Text("Add a disk").font(Theme.display(18)).foregroundColor(Theme.text)
                    Text("A new disk").font(Theme.sans(13, .semibold)).foregroundColor(Theme.text).padding(.top, 14)
                    HStack(spacing: 10) {
                        Text("Size (GB)").font(Theme.sans(13)).foregroundColor(Theme.text)
                        SizeField(value: $gb, presets: Sizes.disk, range: 1...4096)
                    }
                    .padding(.top, 8)
                    HStack(spacing: 10) {
                        Rectangle().fill(Theme.border).frame(height: 1)
                        Text("OR").font(Theme.sans(11)).foregroundColor(Theme.muted)
                        Rectangle().fill(Theme.border).frame(height: 1)
                    }
                    .padding(.vertical, 14)
                    Text("A disk image on this Mac").font(Theme.sans(13, .semibold)).foregroundColor(Theme.text)
                    Button("Choose A Disk Image…") {
                        if let url = Ask.file("Choose a raw disk image (.img)", iso: false) { file = url }
                    }
                    .buttonStyle(FlatButton()).frame(width: 200, height: 32).padding(.top, 8)
                    Text(file?.lastPathComponent ?? "No file chosen").font(Theme.sans(11.5))
                        .foregroundColor(file == nil ? Theme.muted : Theme.highlight).padding(.top, 6)
                }
            }
            HStack(spacing: 10) {
                Spacer()
                Button("Cancel") { done(nil) }.buttonStyle(FlatButton()).frame(width: 150, height: 36).keyboardShortcut(.cancelAction)
                Button("Add") { done(AddDiskDialog.Pick(gb: gb, file: file)) }.buttonStyle(FlatButton(primary: true)).frame(width: 150, height: 36)
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.top, 22)
        }
        .padding(24)
        .frame(width: 520)
        .background(Theme.bg)
    }
}

struct SnapshotsSheet: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let config: VMConfig
    @State private var items: [Snapshots.Item] = []
    @State private var selected: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Snapshots  ·  \(config.displayName)").font(Theme.display(17)).foregroundColor(Theme.text)
            Note(text: "A snapshot saves Disk 1 as it is now. It's instant and only uses space as the disk changes. Reverting throws away changes made since.")
            VStack(spacing: 4) {
                if items.isEmpty {
                    Note(text: "No snapshots yet.").padding(10)
                }
                ForEach(items) { s in
                    Text(s.id).font(Theme.sans(13)).foregroundColor(Theme.text)
                        .frame(maxWidth: .infinity, alignment: .leading).padding(8)
                        .surface(selected: s.id == selected)
                        .contentShape(Rectangle())
                        .onTapGesture { selected = s.id }
                }
                Spacer(minLength: 0)
            }
            .frame(height: 220, alignment: .top)
            HStack(spacing: 10) {
                Button("Take Snapshot") { act { try Snapshots.take(config) } }.buttonStyle(FlatButton(primary: true)).frame(width: 160, height: 34)
                Button("Revert") {
                    if let s = current, Ask.confirm("Revert to this snapshot?", "", "Revert", path: s.id,
                                                    after: "Changes made to \(config.displayName) since then are lost.", destructive: true) {
                        act { try Snapshots.revert(config, to: s) }
                    }
                }
                .buttonStyle(FlatButton()).frame(width: 100, height: 34).disabled(current == nil)
                Button("Delete") {
                    if let s = current, Ask.confirm("Delete this snapshot?", "", "Delete", path: s.id,
                                                    after: "It can't be restored afterwards.", destructive: true) {
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
