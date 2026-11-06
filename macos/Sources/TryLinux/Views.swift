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
            case .setup(let plan): SetupView(plan: plan)
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
                Button("Settings") { if let c = current { model.screen = .settings(c.id) } }.buttonStyle(FlatButton()).frame(width: 151, height: 42)
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
        menu.appearance = NSAppearance(named: .darkAqua)
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
                    OptionRow(title: d.name, subtitle: d.about, selected: i == selected, twoLine: true)
                        .onTapGesture(count: 2) { selected = i; next() }
                        .onTapGesture { selected = i }
                }
            }
            .modifier(BoxStyle())
            .padding(.top, 24)
            OptionRow(title: "Use your own installer", subtitle: "An .iso file already on this Mac, or a URL link to download one",
                      selected: selected == -1)
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
        if selected == -1 {
            if let plan = OwnInstallerDialog.ask() { model.screen = .setup(plan) }
            return
        }
        let d = Distro.catalog[selected]
        model.screen = .setup(InstallPlan(distro: d, label: d.name))
    }
}

// "Use your own installer": choose an ISO file on this Mac, or paste a URL link, in one dialog.
// Choosing a file clears the link and typing a link clears the file, so Save uses whichever is set.
enum OwnInstallerDialog {
    @MainActor
    static func ask() -> InstallPlan? {
        var plan: InstallPlan?
        ThemedDialog.present { close in
            OwnInstallerView { result in plan = result; close() }
        }
        return plan
    }
}

struct OwnInstallerView: View {
    let done: (InstallPlan?) -> Void
    @State private var iso: URL?
    @State private var link = ""
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 16) {
                Image(nsImage: Icons.image(size: 40)).resizable().frame(width: 40, height: 40)
                VStack(alignment: .leading, spacing: 0) {
                    Text("Use your own installer").font(Theme.display(18)).foregroundColor(Theme.text)
                    Text("An ISO file on this Mac").font(Theme.sans(13, .semibold)).foregroundColor(Theme.text).padding(.top, 14)
                    Button("Choose an ISO file…") { choose() }.buttonStyle(FlatButton()).frame(width: 200, height: 32).padding(.top, 8)
                    Text(iso?.lastPathComponent ?? "No file chosen").font(Theme.sans(11.5))
                        .foregroundColor(iso == nil ? Theme.muted : Theme.highlight).padding(.top, 6)
                    // "OR", centred on a divider line.
                    HStack(spacing: 10) {
                        Rectangle().fill(Theme.border).frame(height: 1)
                        Text("OR").font(Theme.sans(11)).foregroundColor(Theme.muted)
                        Rectangle().fill(Theme.border).frame(height: 1)
                    }
                    .padding(.vertical, 14)
                    Text("URL Link").font(Theme.sans(13, .semibold)).foregroundColor(Theme.text)
                    TextInput(text: $link, placeholder: "https://… (address of an .iso file)").padding(.top, 8)
                        .onChange(of: link) { v in if !v.isEmpty { iso = nil; error = nil } }
                    Text(error ?? "Paste the address of an ARM64 (aarch64) .iso file.").font(Theme.sans(11.5))
                        .foregroundColor(error == nil ? Theme.muted : Theme.danger).padding(.top, 6)
                }
            }
            HStack(spacing: 10) {
                Spacer()
                Button("Cancel") { done(nil) }.buttonStyle(FlatButton()).frame(width: 150, height: 36)
                    .keyboardShortcut(.cancelAction)
                Button("Save") { save() }.buttonStyle(FlatButton(primary: true)).frame(width: 150, height: 36)
                    .keyboardShortcut(.defaultAction)
                    .disabled(iso == nil && link.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            .padding(.top, 22)
        }
        .padding(24)
        .frame(width: 520)
        .background(Theme.bg)
    }

    private func choose() {
        guard let url = Ask.file("Choose a Linux installer (ARM64 / aarch64)") else { return }
        iso = url
        link = ""
        error = nil
    }

    private func save() {
        if let iso {
            done(InstallPlan(localISO: iso, label: iso.deletingPathExtension().lastPathComponent))
            return
        }
        let s = link.trimmingCharacters(in: .whitespaces)
        guard let url = URL(string: s), ["http", "https"].contains(url.scheme?.lowercased() ?? "") else {
            error = "That isn't a web address. It should start with https://"
            return
        }
        done(InstallPlan(url: url, label: url.deletingPathExtension().lastPathComponent))
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
                Text("up to \(maxCores) of \(VMConfig.hostCores)" + (maxCores > 1 ? ", in steps of 2" : ""))
                    .font(Theme.sans(11.5)).foregroundColor(Theme.muted)
            }
            Note(text: summary)
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

    private var summary: String {
        let r = probe.resources()
        let c = min(r.cores, maxCores), g = min(r.ramGB, maxRam)
        return "Next start: \(c) \(c == 1 ? "core" : "cores") and \(g) GB of memory. Changes apply the next time it starts.\n"
            + (otherRamGB > 0 ? "macOS keeps 2 cores and a quarter of the memory; other running systems use \(otherCores) cores and \(otherRamGB) GB."
                              : "macOS keeps 2 cores and a quarter of the memory.")
    }
}

struct SetupView: View {
    @EnvironmentObject var model: AppModel
    let plan: InstallPlan
    @State private var name = ""
    @State private var profile = 0
    @State private var ramGB = 8
    @State private var cores = max(2, VMConfig.hostCores / 2)
    @State private var diskGB = 64
    @State private var storage = Paths.display(Paths.root)

    var body: some View {
        Page {
            VStack(alignment: .leading, spacing: 14) {
                // The name the system gets, shown on Virtual Machines.
                FormRow(label: "Name") { TextInput(text: $name).frame(width: 250) }
                ResourcePicker(profile: $profile, ramGB: $ramGB, cores: $cores)
                FormRow(label: "Disk size (GB)") {
                    SizeField(value: $diskGB, presets: Sizes.disk, range: 16...2048)
                    Text("grows only as you use it").font(Theme.sans(11.5)).foregroundColor(Theme.muted)
                }
                FormRow(label: "Storage") {
                    Text(storage).font(Theme.sans(11.5)).foregroundColor(Theme.muted).lineLimit(1).truncationMode(.middle)
                    if model.systems.isEmpty {
                        Button("Change") { changeStorage() }.buttonStyle(FlatButton()).frame(width: 100, height: 28)
                    }
                }
                Note(text: "The installer only sees a virtual disk, a file on your Mac. Your Mac's drive is never touched.")
                    .padding(.top, 8)
            }
            .padding(.top, 28)
            HStack {
                Button("Back") { model.screen = .choose }.buttonStyle(FlatButton()).frame(width: 120, height: 42)
                Spacer()
                Button("Install") { install() }.buttonStyle(FlatButton(primary: true)).frame(width: 140, height: 42)
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.top, 30)
        }
        .onAppear {
            if name.isEmpty {
                // The distribution's name, with " 2" and up if a system already has it.
                let base = plan.distro?.name ?? plan.label
                var n = base, i = 2
                while model.systems.contains(where: { $0.displayName.caseInsensitiveCompare(n) == .orderedSame }) { n = "\(base) \(i)"; i += 1 }
                name = n
            }
            diskGB = plan.distro?.diskGB ?? 64
        }
    }

    private func changeStorage() {
        guard let url = Ask.folder("Where should Try Linux keep its systems and downloads?") else { return }
        let path = url.appendingPathComponent("TryLinux", isDirectory: true).path
        UserDefaults.standard.set(path, forKey: "storage")
        storage = Paths.display(Paths.root)
    }

    private func install() {
        let display = name.trimmingCharacters(in: .whitespaces)
        guard (1...60).contains(display.count) else {
            model.show("Give the system a name of 1 to 60 characters.")
            return
        }
        if model.systems.contains(where: { $0.displayName.caseInsensitiveCompare(display) == .orderedSame }) {
            model.show("A system named \"\(display)\" already exists. Choose another name.")
            return
        }
        // The folder name comes from the display name; the display name is what Virtual Machines shows.
        var c = VMConfig(id: VMConfig.uniqueId(display), displayName: display)
        c.distroId = plan.distro?.id ?? ""
        c.profile = profile
        c.ramGB = ramGB
        c.cores = cores
        Task { await model.install(plan, c, diskGB: diskGB) }
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
