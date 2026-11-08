// The launcher's pages: your systems, choosing a distribution, setup and progress.
import AppKit
import SwiftUI

struct RootView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        VStack(spacing: 0) {
            NoticeArea()
            screens
        }
        .background(Theme.bg)
    }

    @ViewBuilder private var screens: some View {
        Group {
            switch model.screen {
            case .home: HomeView()
            case .choose: ChooseView()
            case .own(let last): OwnInstallerPage(last: last)
            case .setup(let plan): SetupView(plan: plan).id(plan.disk?.path ?? plan.localISO?.path ?? plan.url?.absoluteString ?? plan.label)
            case .progress: ProgressScreen()
            case .settings(let id):
                if let c = model.systems.first(where: { $0.id == id }) { SettingsView(config: c).id(c.id) } else { HomeView() }
            }
        }
    }
}

// The window's notification area, above every page: updates, results and problems that don't need an answer.
// Each has a colored edge and icon by kind, an optional action button and a dismiss X.
struct NoticeArea: View {
    @EnvironmentObject var model: AppModel

    private var all: [Notice] {
        var list: [Notice] = []
        if model.stagedUpdate != nil, let v = model.updateAvailable {
            list.append(Notice(id: "update", kind: .info, text: "Try Linux \(v) is downloaded and installs when you quit Try Linux."))
        } else if let v = model.updateAvailable {
            list.append(Notice(id: "update", kind: .info, text: "Try Linux \(v) is available. You have \(AppInfo.version).",
                               actionTitle: "Update", action: { Task { @MainActor in await model.startUpdate() } }))
        }
        return list.filter { !model.dismissedNotices.contains($0.id) } + model.notices
    }

    var body: some View {
        if !all.isEmpty {
            VStack(spacing: 8) {
                ForEach(all) { n in NoticeRow(notice: n) }
            }
            .frame(width: 640)
            .padding(.top, 12)
            .padding(.bottom, 4)
            .frame(maxWidth: .infinity)
        }
    }
}

struct NoticeRow: View {
    @EnvironmentObject var model: AppModel
    let notice: Notice

    private var color: Color {
        switch notice.kind {
        case .success: return Theme.success
        case .warning: return Theme.warning
        case .error: return Theme.danger
        case .info: return Theme.highlight
        }
    }

    private var symbol: String {
        switch notice.kind {
        case .success: return "checkmark.circle"
        case .warning: return "exclamationmark.triangle"
        case .error: return "xmark.circle"
        case .info: return "info.circle"
        }
    }

    var body: some View {
        HStack(spacing: 10) {
            Rectangle().fill(color).frame(width: 4)
            Image(systemName: symbol).foregroundColor(color).font(.system(size: 14)).frame(width: 22)
            Text(notice.text).font(Theme.sans(13)).foregroundColor(Theme.text)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, 12)
            if let title = notice.actionTitle, let action = notice.action {
                Button(title, action: action).buttonStyle(NoticeButton(color: color)).frame(width: 110, height: 30)
            }
            Button { model.dismiss(notice.id) } label: {
                Image(systemName: "xmark").font(.system(size: 10, weight: .semibold)).foregroundColor(Theme.muted)
                    .frame(width: 28, height: 28).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .padding(.trailing, 6)
        }
        .frame(minHeight: 44)
        .background(Theme.card)
        .overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
    }
}

// A notification's action: its kind's color for the text and outline.
private struct NoticeButton: ButtonStyle {
    let color: Color
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.font(Theme.sans(13, .semibold)).foregroundColor(color)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(configuration.isPressed ? Theme.cardHover : Theme.card)
            .overlay(Rectangle().stroke(color, lineWidth: 1))
            .contentShape(Rectangle())
    }
}

// Column headers for the rows' status and stats, on the title row and lined up with OptionRow's columns.
struct StatHeaders: View {
    var body: some View {
        HStack(spacing: 0) {
            Text("STATUS").frame(width: OptionRow.statusW, alignment: .leading)
            Spacer().frame(width: OptionRow.gap)
            ForEach(["CPU", "MEM", "DISK"], id: \.self) { Text($0).frame(width: OptionRow.statW, alignment: .leading) }
            Spacer().frame(width: OptionRow.trailing)
        }
        .font(Theme.sans(11)).foregroundColor(Theme.muted)
    }
}

struct HomeView: View {
    @EnvironmentObject var model: AppModel
    @State private var selectedId: String?

    private var current: VMConfig? {
        model.systems.first { $0.id == selectedId } ?? model.systems.first
    }

    var body: some View {
        Page {
            // The title, with the rows' column headers on the same line, over a divider.
            HStack(alignment: .lastTextBaseline, spacing: 0) {
                Heading(text: "Virtual Machines").fixedSize()
                Spacer(minLength: 0)
                StatHeaders()
            }
            .padding(.top, 32)
            Rectangle().fill(Theme.border).frame(height: 1).padding(.top, 8)
            VStack(spacing: 6) {
                ForEach(model.systems) { vm in
                    OptionRow(title: vm.displayName, subtitle: model.osLabel(vm), selected: vm.id == current?.id,
                              status: model.status(vm), statusColor: model.isOn(vm) ? Theme.success : Theme.muted,
                              stats: model.stats(vm))
                        .onTapGesture(count: 2) { selectedId = vm.id; model.boot(vm) }
                        .onTapGesture { selectedId = vm.id }
                }
                Spacer(minLength: 0)
            }
            .frame(height: 270, alignment: .top)
            .padding(.top, 12)
            // Four equal buttons across the 640-wide column, 12 apart.
            HStack(spacing: 12) {
                Button("Create") { model.screen = .choose }.buttonStyle(FlatButton()).frame(width: 151, height: 42)
                // Settings opens with every section closed.
                Button("Settings") { if let c = current { SettingsView.reset(); model.screen = .settings(c.id) } }
                    .buttonStyle(FlatButton()).frame(width: 151, height: 42)
                // Console opens the system's window; for a system that's off, the window opens with it still off.
                Button("Console") { if let c = current { model.openConsole(c) } }.buttonStyle(FlatButton()).frame(width: 151, height: 42)
                if let c = current, model.isOn(c) {
                    // Power replaces Start while a system runs: a menu, so nothing powers off from one click.
                    Button("Power  \u{25BE}") { if let s = model.running[c.id] { PowerMenu.show(model, s) } }
                        .buttonStyle(FlatButton()).frame(width: 151, height: 42)
                } else {
                    Button(startTitle) { start() }.buttonStyle(FlatButton(primary: true)).frame(width: 151, height: 42)
                        .keyboardShortcut(.defaultAction)
                }
            }
            .padding(.top, 32)
        }
    }

    private var startTitle: String {
        guard let c = current else { return "Start" }
        return c.pendingInstall ? "Install" : "Start"
    }

    // Start powers on a console opened while the system was off, or starts the system.
    private func start() {
        guard let c = current else { return }
        if let s = model.running[c.id], s.held { model.powerOn(s) } else { model.boot(c) }
    }
}

// The Power commands for a running system, as a menu under the mouse (the home page's Power button).
enum PowerMenu {
    private final class Action: NSObject {
        let run: () -> Void
        init(_ run: @escaping () -> Void) { self.run = run }
        @objc func fire() { run() }
    }

    @MainActor
    static func show(_ model: AppModel, _ s: VMSession) {
        let menu = NSMenu()
        var actions: [Action] = []
        func add(_ title: String, danger: Bool = false, _ run: @escaping () -> Void) {
            let a = Action(run)
            actions.append(a)
            let item = NSMenuItem(title: title, action: #selector(Action.fire), keyEquivalent: "")
            item.target = a
            if danger { item.attributedTitle = NSAttributedString(string: title, attributes: [.foregroundColor: NSColor(Theme.danger)]) }
            menu.addItem(item)
        }
        add("Shut down") { model.shutDown(s) }
        add("Restart") { model.restart(s) }
        add(s.paused ? "Resume" : "Pause") { s.setPaused(!s.paused) }
        menu.addItem(.separator())
        add("Force stop", danger: true) { model.forceStop(s) }
        add("Reset", danger: true) { model.reset(s) }
        menu.appearance = Theme.appearance
        withExtendedLifetime(actions) { _ = menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil) }
    }
}

// The same commands in the menu bar icon's menu.
struct PowerMenuItems: View {
    @EnvironmentObject var model: AppModel
    let s: VMSession

    var body: some View {
        if s.held {
            Button("Power on") { model.powerOn(s) }
            Button("Close console") { s.forceStop() }
        } else {
            Button("Shut down") { model.shutDown(s) }
            Button("Restart") { model.restart(s) }
            Button(s.paused ? "Resume" : "Pause") { s.setPaused(!s.paused) }
            Divider()
            Button("Force stop") { model.forceStop(s) }
            Button("Reset") { model.reset(s) }
        }
    }
}

struct ChooseView: View {
    @EnvironmentObject var model: AppModel
    @State private var selected = 0

    var body: some View {
        Page {
            Heading(text: "Choose a Linux OS", centered: true).padding(.top, 20)
            Text("Try Linux downloads the official ARM64 installer and checks it before use.")
                .font(Theme.sans(11.5)).foregroundColor(Theme.muted)
                .frame(maxWidth: .infinity).padding(.top, 6)
            // Two containers: the distributions, then your own installer. One selection across both (-1 is your own).
            VStack(spacing: 6) {
                ForEach(Distro.catalog.indices, id: \.self) { i in
                    let d = Distro.catalog[i]
                    OptionRow(title: d.name, subtitle: d.about, selected: i == selected, twoLine: true, link: d.home.flatMap(URL.init(string:)))
                        .onTapGesture(count: 2) { selected = i; next() }
                        .onTapGesture { selected = i }
                }
            }
            .modifier(BoxStyle())
            .padding(.top, 24)
            OptionRow(title: "Use Your Own Installer", subtitle: "An existing disk image to import, an .iso file on this Mac, or a URL link to one",
                      selected: selected == -1, twoLine: true)
                .onTapGesture(count: 2) { selected = -1; next() }
                .onTapGesture { selected = -1 }
                .modifier(BoxStyle())
                .padding(.top, 12)
            HStack {
                if !model.systems.isEmpty {
                    Button("Back") { model.goHome() }.buttonStyle(FlatButton()).frame(width: 120, height: 42)
                }
                Spacer()
                Button("Next") { next() }.buttonStyle(FlatButton(primary: true)).frame(width: 140, height: 42)
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.top, 24)
        }
    }

    private func next() {
        if selected == -1 { model.screen = .own(nil); return }
        let d = Distro.catalog[selected]
        model.screen = .setup(InstallPlan(distro: d, label: d.name))
    }
}

// "OR", centred on a divider line across the column.
struct OrDivider: View {
    var body: some View {
        HStack(spacing: 10) {
            Rectangle().fill(Theme.border).frame(height: 1)
            Text("OR").font(Theme.sans(11)).foregroundColor(Theme.muted)
            Rectangle().fill(Theme.border).frame(height: 1)
        }
        .padding(.vertical, 12)
    }
}

// Use Your Own Installer, a Create page before General: an existing disk image to import, an ISO file on
// this Mac, or a URL link. Choosing one clears the others. `last` is what was chosen before, coming Back.
struct OwnInstallerPage: View {
    @EnvironmentObject var model: AppModel
    var last: InstallPlan?
    @State private var disk: URL?
    @State private var source: DiskSource?
    @State private var iso: URL?
    @State private var link = ""
    @State private var error: String?

    // Both buttons as wide as the longer label needs, so they line up without much space around the text.
    private let buttonW = ActionRow.width(["Choose A Disk File", "Choose An ISO File"])

    var body: some View {
        Page {
            Heading(text: "Use Your Own Installer", centered: true).padding(.top, 20)
            VStack(alignment: .leading, spacing: 0) {
                // A disk that already has Linux installed (from Try Linux, or another raw disk image).
                Text("An Existing Virtual Disk").font(Theme.display(15)).foregroundColor(Theme.text)
                HStack(spacing: 14) {
                    Button("Choose A Disk File") { chooseDisk() }.buttonStyle(FlatButton()).frame(width: buttonW, height: 32)
                    Text(disk?.lastPathComponent ?? "No disk chosen").font(Theme.sans(13))
                        .foregroundColor(disk == nil ? Theme.muted : Theme.highlight).lineLimit(1).truncationMode(.middle)
                }
                .padding(.top, 8)
                Note(text: disk != nil ? (source?.summary ?? "") : "A raw disk image (.img or .raw). It's copied; the original isn't changed.")
                    .padding(.top, 6)
                OrDivider()
                Text("An ISO File On This Mac").font(Theme.display(15)).foregroundColor(Theme.text)
                HStack(spacing: 14) {
                    Button("Choose An ISO File") { chooseISO() }.buttonStyle(FlatButton()).frame(width: buttonW, height: 32)
                    Text(iso?.lastPathComponent ?? "No file chosen").font(Theme.sans(13))
                        .foregroundColor(iso == nil ? Theme.muted : Theme.highlight).lineLimit(1).truncationMode(.middle)
                }
                .padding(.top, 8)
                OrDivider()
                Text("URL Link").font(Theme.display(15)).foregroundColor(Theme.text)
                TextInput(text: $link, placeholder: "https://… (address of an .iso file)").padding(.top, 8)
                    .onChange(of: link) { v in if !v.isEmpty { iso = nil; disk = nil; source = nil; error = nil } }
                Text(error ?? "Paste the address of an ARM64 (aarch64) .iso file.").font(Theme.sans(11.5))
                    .foregroundColor(error == nil ? Theme.muted : Theme.danger).padding(.top, 6)
            }
            .padding(.top, 24)
            HStack {
                Button("Back") { model.screen = .choose }.buttonStyle(FlatButton()).frame(width: 120, height: 42)
                Spacer()
                Button("Next") { next() }.buttonStyle(FlatButton(primary: true)).frame(width: 140, height: 42)
                    .keyboardShortcut(.defaultAction)
                    .disabled(disk == nil && iso == nil && link.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            .padding(.top, 24)
        }
        .onAppear {
            guard let last else { return }
            disk = last.disk; source = last.source; iso = last.localISO; link = last.url?.absoluteString ?? ""
        }
    }

    private func chooseDisk() {
        guard let url = Ask.file("Choose a disk image to import (.img or .raw)", iso: false) else { return }
        disk = url
        source = DiskSource.read(url)
        iso = nil
        link = ""
        error = nil
    }

    private func chooseISO() {
        guard let url = Ask.file("Choose a Linux installer (ARM64 / aarch64)") else { return }
        iso = url
        disk = nil
        source = nil
        link = ""
        error = nil
    }

    private func next() {
        if let disk {
            model.screen = .setup(InstallPlan(label: disk.deletingPathExtension().lastPathComponent, disk: disk, source: source, own: true))
            return
        }
        if let iso {
            model.screen = .setup(InstallPlan(localISO: iso, label: iso.deletingPathExtension().lastPathComponent, own: true))
            return
        }
        let s = link.trimmingCharacters(in: .whitespaces)
        guard let url = URL(string: s), ["http", "https"].contains(url.scheme?.lowercased() ?? "") else {
            error = "That isn't a web address. It should start with https://"
            return
        }
        model.screen = .setup(InstallPlan(url: url, label: url.deletingPathExtension().lastPathComponent, own: true))
    }
}

// Balanced, Maximum or Manual, with memory and CPU cores. They only go up to what's free: macOS keeps
// 2 cores and a quarter of the memory, and other running systems keep theirs (`otherCores`, `otherRamGB`).
// Balanced and Maximum show what they'll give in the greyed-out fields; Manual starts from there.
struct ResourcePicker: View {
    @Binding var profile: Int
    @Binding var ramGB: Int
    @Binding var cores: Int
    var otherCores = 0
    var otherRamGB = 0

    private var maxRam: Int { max(2, VMConfig.maxRamGB - otherRamGB) }
    private var maxCores: Int { max(1, VMConfig.maxCores - otherCores) }

    private var probe: VMConfig {
        var p = VMConfig(id: "", displayName: "")
        p.profile = profile
        p.ramGB = ramGB
        p.cores = cores
        return p
    }

    private var shownRam: Binding<Int> {
        Binding(get: { profile == 2 ? min(ramGB, maxRam) : min(probe.resources().ramGB, maxRam) }, set: { ramGB = $0 })
    }
    private var shownCores: Binding<Int> {
        Binding(get: { profile == 2 ? min(cores, maxCores) : min(probe.resources().cores, maxCores) }, set: { cores = $0 })
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            FormRow(label: "Resource profile") {
                Segmented(options: VMConfig.profileNames, selection: $profile).frame(width: 400, height: 30)
            }
            FormRow(label: "Memory (GB)") {
                SizeField(value: shownRam, presets: Sizes.ram, range: 2...maxRam).disabled(profile != 2)
                Text("up to \(maxRam) of \(VMConfig.hostRamGB) GB").font(Theme.sans(11.5)).foregroundColor(Theme.muted)
            }
            FormRow(label: "CPU cores") {
                SizeField(value: shownCores, presets: Sizes.cores(maxCores), range: 1...maxCores, step: Sizes.coreStep(maxCores))
                    .disabled(profile != 2)
                Text("up to \(maxCores) of \(VMConfig.hostCores)")
                    .font(Theme.sans(11.5)).foregroundColor(Theme.muted)
            }
        }
        .onAppear {
            // Manual values saved by an older version may be odd or over the limit.
            if profile == 2 {
                if cores > maxCores { cores = maxCores }
                if maxCores > 1, cores % 2 == 1 { cores = min(maxCores - maxCores % 2, cores + 1) }
                if ramGB > maxRam { ramGB = maxRam }
            }
        }
    }
}

// Create, in three steps after choosing what to install: General (name, description, location),
// Resources (profile, memory, cores, disk size) and Summary. Back and Next keep what was entered.
struct SetupView: View {
    @EnvironmentObject var model: AppModel
    let plan: InstallPlan
    @State var step = 0                 // 0 General, 1 Resources, 2 Summary
    @State private var name = ""
    @State private var notes = ""
    @State private var ownLocation = false
    @State private var location = ""
    @State private var profile = 0
    @State private var ramGB = 8
    @State private var cores = max(2, VMConfig.hostCores / 2)
    @State private var diskGB = 64
    @State private var diskFull = false
    @State private var started = false

    private var parent: URL { ownLocation && !location.isEmpty ? URL(fileURLWithPath: location, isDirectory: true) : Paths.vms }

    var body: some View {
        Page {
            Heading(text: ["General", "Resources", "Summary"][step], centered: true).padding(.top, 20)
            Group {
                switch step {
                case 1: resources
                case 2: summary
                default: general
                }
            }
            .padding(.top, 28)
            HStack {
                Button("Back") { back() }.buttonStyle(FlatButton()).frame(width: 120, height: 42)
                Spacer()
                Button(step < 2 ? "Next" : plan.disk != nil ? "Import" : "Install") { next() }
                    .buttonStyle(FlatButton(primary: true)).frame(width: 140, height: 42)
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.top, 30)
        }
        .onAppear { start() }
    }

    // MARK: General

    private var general: some View {
        VStack(alignment: .leading, spacing: 14) {
            FormRow(label: "Name") { TextInput(text: $name) }
            HStack(alignment: .top, spacing: 10) {
                Text("Description").font(Theme.sans(13)).foregroundColor(Theme.text).frame(width: 180, alignment: .leading).padding(.top, 4)
                VStack(alignment: .leading, spacing: 4) {
                    NotesInput(text: $notes).frame(height: 76)
                    Note(text: "(Optional): Shown in Settings > General > Info.").padding(.leading, 8)
                }
            }
            // The default is the storage folder; a different one is used only while the box is ticked.
            FormRow(label: "Location") {
                Text(ownLocation ? (location.isEmpty ? "Choose a folder" : Paths.display(URL(fileURLWithPath: location))) : Paths.display(Paths.vms))
                    .font(Theme.sans(13)).foregroundColor(ownLocation ? Theme.text : Theme.muted)
                    .lineLimit(1).truncationMode(.middle)
                    .padding(.horizontal, 8).frame(maxWidth: .infinity, minHeight: 26, alignment: .leading)
                    .background(ownLocation ? Theme.card : Theme.bg).overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
                Button("Browse") { browse() }.buttonStyle(FlatButton()).frame(width: 100, height: 28).disabled(!ownLocation)
            }
            FormRow(label: "") { CheckRow(title: "Choose a different location", on: $ownLocation) }
            FormRow(label: "") {
                Note(text: ownLocation ? "Try Linux makes a folder for this system inside it." : "The default: where Try Linux keeps its systems.")
            }
        }
    }

    private func browse() {
        if let url = Ask.folder("Where should Try Linux keep this system?") { location = url.path }
    }

    // MARK: Resources

    private var freeLine: String {
        guard let f = Disk.free(parent) else { return "" }
        return "up to \(f.bytes >> 30) GB free on \(f.volume)"
    }

    private var resources: some View {
        VStack(alignment: .leading, spacing: 14) {
            ResourcePicker(profile: $profile, ramGB: $ramGB, cores: $cores)
            if let src = plan.disk {
                FormRow(label: "Disk") {
                    Text(src.lastPathComponent + (plan.source.map { $0.size > 0 ? "  ·  \($0.size >> 30) GB" : "" } ?? ""))
                        .font(Theme.sans(13)).foregroundColor(Theme.text).lineLimit(1).truncationMode(.middle)
                }
                FormRow(label: "") { Note(text: "Keeps its own size  ·  " + freeLine) }
            } else {
                FormRow(label: "Disk size (GB)") {
                    // The size is fixed while it's to be allocated in full; untick to change it.
                    SizeField(value: $diskGB, presets: Sizes.disk, range: 16...2048).disabled(diskFull)
                    Text(freeLine).font(Theme.sans(11.5)).foregroundColor(Theme.muted)
                }
                FormRow(label: "") { CheckRow(title: "Allocate the full size now", on: $diskFull) }
            }
        }
    }

    // MARK: Summary

    private var summary: some View {
        let r = probe.resources()
        let id = VMConfig.uniqueId(name.trimmingCharacters(in: .whitespaces), in: parent)
        let first = notes.split(separator: "\n", omittingEmptySubsequences: false).first.map(String.init) ?? ""
        let source: String = plan.disk.map { "Import " + Paths.display($0) }
            ?? plan.localISO.map { Paths.display($0) }
            ?? plan.url.map { "Download " + $0.absoluteString }
            ?? "Download from \(URL(string: plan.distro?.base ?? "")?.host ?? "the official site"), checksum verified"
        let disk = plan.disk != nil
            ? (plan.source.map { $0.size > 0 ? "\($0.size >> 30) GB" : "As imported" } ?? "As imported") + " (Dynamic)"
            : "\(diskGB) GB" + (diskFull ? " (Preallocated)" : " (Dynamic)")
        let rows: [(String, String)] = [
            ("Name", name.trimmingCharacters(in: .whitespaces)),
            ("Description", notes.isEmpty ? "None" : first + (notes.contains("\n") ? " …" : "")),
            ("Operating system", plan.distro?.name ?? (plan.disk != nil ? "From the imported disk" : plan.label)),
            (plan.disk != nil ? "Disk" : "Installer", source),
            ("Location", Paths.display(parent.appendingPathComponent(id))),
            ("Resource profile", VMConfig.profileNames[min(2, max(0, profile))]),
            ("Memory", "\(r.ramGB) GB"),
            ("CPU cores", "\(r.cores)"),
            ("Disk size", disk),
        ]
        // The details in a bordered box, like the lists on Choose a Linux OS.
        return VStack(alignment: .leading, spacing: 0) {
            ForEach(rows.indices, id: \.self) { i in
                HStack(spacing: 10) {
                    Text(rows[i].0).font(Theme.sans(13)).foregroundColor(Theme.muted).frame(width: 170, alignment: .leading)
                    Text(rows[i].1).font(Theme.sans(13)).foregroundColor(Theme.text).lineLimit(1).truncationMode(.middle)
                    Spacer(minLength: 0)
                }
                .frame(height: 28)
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 10)
        .background(Theme.box)
        .overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
    }

    private var probe: VMConfig {
        var p = VMConfig(id: "", displayName: "")
        p.profile = profile
        p.ramGB = ramGB
        p.cores = cores
        return p
    }

    // MARK: steps

    private func start() {
        guard !started else { return }
        started = true
        // A disk's own settings (beside it, from Try Linux) fill in what they can.
        let src = plan.source
        let base = (src?.name.isEmpty == false ? src!.name : nil) ?? plan.distro?.name ?? plan.label
        // The distribution's name, with " 2" and up if a system already has it.
        var n = base, i = 2
        while model.systems.contains(where: { $0.displayName.caseInsensitiveCompare(n) == .orderedSame }) { n = "\(base) \(i)"; i += 1 }
        name = n
        if let src {
            notes = src.description
            if src.ramGB > 0 || src.cores > 0 {
                profile = 2   // Manual, starting from the disk's own memory and cores
                if src.ramGB > 0 { ramGB = min(VMConfig.maxRamGB, max(2, src.ramGB)) }
                if src.cores > 0 { cores = min(VMConfig.maxCores, max(1, src.cores)) }
            }
        }
        diskGB = plan.distro?.diskGB ?? 64
    }

    private func back() {
        if step > 0 { step -= 1; return }
        model.screen = plan.own ? .own(plan) : .choose
    }

    private func next() {
        switch step {
        case 0:
            let display = name.trimmingCharacters(in: .whitespaces)
            guard (1...60).contains(display.count) else { model.show("Give the system a name of 1 to 60 characters."); return }
            if model.systems.contains(where: { $0.displayName.caseInsensitiveCompare(display) == .orderedSame }) {
                model.show("A system named \"\(display)\" already exists. Choose another name.")
                return
            }
            if ownLocation && location.isEmpty { model.show("Choose a folder for the location, or untick \"Choose a different location\"."); return }
            step = 1
        case 1:
            if diskFull && plan.disk == nil, let f = Disk.free(parent), f.bytes < (Int64(diskGB) << 30) + (1 << 30) {
                model.show("The drive doesn't have \(diskGB) GB free to allocate now. Choose a smaller size, or untick \"Allocate the full size now\".")
                return
            }
            step = 2
        default:
            create()
        }
    }

    private func create() {
        let display = name.trimmingCharacters(in: .whitespaces)
        // The folder name comes from the display name; the display name is what Virtual Machines shows.
        var c = VMConfig(id: VMConfig.uniqueId(display, in: parent), displayName: display)
        if ownLocation && parent.standardizedFileURL.path != Paths.vms.standardizedFileURL.path { c.location = parent.path }
        c.distroId = plan.distro?.id ?? ""
        c.description = notes.trimmingCharacters(in: .whitespacesAndNewlines)
        c.profile = profile
        c.ramGB = ramGB
        c.cores = cores
        c.diskFull = plan.disk == nil && diskFull
        if plan.disk != nil { Task { await model.importDisk(plan, c) } } else { Task { await model.install(plan, c, diskGB: diskGB) } }
    }
}

// A text box of several lines (a description), drawn like TextInput.
struct NotesInput: View {
    @Binding var text: String

    var body: some View {
        TextEditor(text: $text)
            .font(Theme.sans(13))
            .foregroundColor(Theme.text)
            .scrollContentBackgroundHidden()
            .padding(.horizontal, 4).padding(.vertical, 4)
            .background(Theme.card)
            .overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
    }
}

extension View {
    // TextEditor draws its own white background until macOS 13's scrollContentBackground(.hidden).
    @ViewBuilder func scrollContentBackgroundHidden() -> some View {
        if #available(macOS 13.0, *) { self.scrollContentBackground(.hidden) } else { self }
    }
}

struct ProgressScreen: View {
    @EnvironmentObject var model: AppModel

    static let tips = [
        "Closing a system's window leaves it running. Double-click it on Virtual Machines to open it again.",
        "The installer only sees a virtual disk, a file on your Mac. Your Mac's drive is never touched.",
        "Copy and paste works between macOS and Linux once Linux has spice-vdagent (Fedora and Ubuntu include it).",
        "Settings can share a Mac folder with Linux: sudo mount -t virtiofs host /mnt/host",
        "Turn on Rosetta in Settings to run x86_64 Linux apps on Apple silicon.",
        "Quitting Try Linux shuts running systems down, so save your work in Linux first.",
    ]
    static var fixedTip: Int?       // the self-test pins one tip so its screenshot is stable

    var body: some View {
        Page {
            VStack(spacing: 0) {
                Text(model.progress.status).font(Theme.sans(13)).foregroundColor(Theme.text)
                    .lineLimit(1).truncationMode(.middle).padding(.top, 40)
                Bar(value: model.progress.value).frame(width: 400).padding(.top, 14)
                Text(model.progress.detail).font(Theme.sans(11.5)).foregroundColor(Theme.muted).padding(.top, 10)
                TimelineView(.periodic(from: .now, by: 7)) { tl in
                    let i = Self.fixedTip ?? Int(tl.date.timeIntervalSince1970 / 7) % Self.tips.count
                    Text("Tip: " + Self.tips[i]).font(Theme.sans(11.5)).foregroundColor(Theme.highlight)
                        .multilineTextAlignment(.center).frame(width: 560).padding(.top, 30)
                }
                if model.progress.cancellable {
                    Button("Cancel") {
                        if Ask.confirm("Stop setting up?", "Anything already downloaded is kept, so next time it picks up where it left off.", "Stop") {
                            model.cancelRequested = true
                            model.progress.status = "Stopping…"
                        }
                    }
                    .buttonStyle(FlatButton()).frame(width: 120, height: 38).padding(.top, 30)
                }
            }
            .frame(maxWidth: .infinity)
        }
    }
}
