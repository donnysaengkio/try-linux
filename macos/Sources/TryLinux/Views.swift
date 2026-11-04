// The launcher's pages: your systems, choosing a distribution, setup and progress.
import SwiftUI

struct RootView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
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
        .alert(item: $model.alert) { a in
            Alert(title: Text(a.title), message: Text(a.message), dismissButton: .default(Text("OK")))
        }
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
            Heading(text: "Your systems").padding(.top, 16)
            VStack(spacing: 6) {
                ForEach(model.systems) { vm in
                    OptionRow(title: vm.displayName, subtitle: model.describe(vm), selected: vm.id == current?.id)
                        .onTapGesture(count: 2) { selectedId = vm.id; model.boot(vm) }
                        .onTapGesture { selectedId = vm.id }
                }
                Spacer(minLength: 0)
            }
            .frame(height: 270, alignment: .top)
            .padding(.top, 10)
            HStack(spacing: 12) {
                Button("Add a system") { model.screen = .choose }.buttonStyle(FlatButton()).frame(width: 170, height: 42)
                Button("Settings") { if let c = current { model.screen = .settings(c.id) } }.buttonStyle(FlatButton()).frame(width: 140, height: 42)
                Spacer()
                Button(startTitle) { if let c = current { model.boot(c) } }.buttonStyle(FlatButton(primary: true)).frame(width: 170, height: 42)
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.top, 16)
            Note(text: "Double-click a system to start it. Running systems are in the Try Linux menu bar icon.").padding(.top, 12)
        }
    }

    private var startTitle: String {
        guard let c = current else { return "Start" }
        if model.running[c.id] != nil { return "Show" }
        return c.pendingInstall ? "Install" : "Start"
    }
}

struct ChooseView: View {
    @EnvironmentObject var model: AppModel
    @State private var selected = 0

    var body: some View {
        Page {
            Heading(text: "Choose a Linux to try", centered: true).padding(.top, 10)
            Text("Try Linux downloads the official ARM64 installer and checks it before use.")
                .font(Theme.mono(11.5)).foregroundColor(Theme.muted)
                .frame(maxWidth: .infinity).padding(.top, 6)
            VStack(spacing: 6) {
                ForEach(Distro.catalog.indices, id: \.self) { i in
                    let d = Distro.catalog[i]
                    OptionRow(title: d.name, subtitle: d.about, selected: i == selected, twoLine: true)
                        .onTapGesture(count: 2) { selected = i; next() }
                        .onTapGesture { selected = i }
                }
            }
            .padding(.top, 12)
            HStack(spacing: 10) {
                Text("Or install from").font(Theme.mono(11.5)).foregroundColor(Theme.muted).frame(width: 150, alignment: .leading)
                Button("An ISO file…") { fromFile() }.buttonStyle(FlatButton()).frame(width: 160, height: 36)
                Button("A link…") { fromLink() }.buttonStyle(FlatButton()).frame(width: 130, height: 36)
                Spacer()
            }
            .padding(.top, 12)
            HStack {
                if !model.systems.isEmpty {
                    Button("Back") { model.goHome() }.buttonStyle(FlatButton()).frame(width: 120, height: 42)
                }
                Spacer()
                Button("Next") { next() }.buttonStyle(FlatButton(primary: true)).frame(width: 140, height: 42)
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.top, 16)
        }
    }

    private func next() {
        let d = Distro.catalog[selected]
        model.screen = .setup(InstallPlan(distro: d, label: d.name))
    }

    private func fromFile() {
        guard let url = Ask.file("Choose a Linux installer (ARM64 / aarch64)") else { return }
        model.screen = .setup(InstallPlan(localISO: url, label: url.deletingPathExtension().lastPathComponent))
    }

    private func fromLink() {
        guard let s = Ask.text("Download from a link", "Address of an ARM64 (aarch64) .iso file"),
              let url = URL(string: s), ["http", "https"].contains(url.scheme?.lowercased() ?? "") else {
            return
        }
        model.screen = .setup(InstallPlan(url: url, label: url.deletingPathExtension().lastPathComponent))
    }
}

struct ResourcePicker: View {
    @Binding var profile: Int
    @Binding var ramGB: Int
    @Binding var cores: Int

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            FormRow(label: "Resources") {
                Segmented(options: VMConfig.profileNames, selection: $profile).frame(width: 450, height: 32)
            }
            FormRow(label: "") {
                Text("Memory GB").font(Theme.mono(11.5)).foregroundColor(Theme.muted)
                NumberField(value: $ramGB, range: 2...VMConfig.hostRamGB).disabled(profile != 2)
                Text("Cores").font(Theme.mono(11.5)).foregroundColor(Theme.muted).padding(.leading, 20)
                NumberField(value: $cores, range: 1...VMConfig.hostCores).disabled(profile != 2)
            }
            FormRow(label: "") { Note(text: summary) }
        }
    }

    private var summary: String {
        var probe = VMConfig(id: "", displayName: "")
        probe.profile = profile
        probe.ramGB = ramGB
        probe.cores = cores
        let r = probe.resources()
        return "Uses \(r.cores) of \(VMConfig.hostCores) cores and \(r.ramGB) of \(VMConfig.hostRamGB) GB memory"
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
            Heading(text: "Set up \(plan.label)").padding(.top, 12)
            VStack(alignment: .leading, spacing: 14) {
                FormRow(label: "Name") { TextInput(text: $name).frame(width: 250) }
                ResourcePicker(profile: $profile, ramGB: $ramGB, cores: $cores)
                FormRow(label: "Disk size") {
                    NumberField(value: $diskGB, range: 16...2048, step: 8)
                    Text("GB, grows only as you use it").font(Theme.mono(11.5)).foregroundColor(Theme.muted)
                }
                FormRow(label: "Storage") {
                    Text(storage).font(Theme.mono(11.5)).foregroundColor(Theme.muted).lineLimit(1).truncationMode(.middle)
                    if model.systems.isEmpty {
                        Button("Change") { changeStorage() }.buttonStyle(FlatButton()).frame(width: 100, height: 28)
                    }
                }
                Note(text: "The installer only sees a virtual disk, a file on your Mac. Your Mac's drive is never touched.")
                    .padding(.top, 8)
            }
            .padding(.top, 18)
            HStack {
                Button("Back") { model.screen = .choose }.buttonStyle(FlatButton()).frame(width: 120, height: 42)
                Spacer()
                Button("Install") { install() }.buttonStyle(FlatButton(primary: true)).frame(width: 140, height: 42)
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.top, 30)
        }
        .onAppear {
            if name.isEmpty { name = VMConfig.uniqueId(plan.distro?.id ?? plan.label) }
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
        let id = name.trimmingCharacters(in: .whitespaces)
        guard id.range(of: "^[A-Za-z0-9_-]{1,40}$", options: .regularExpression) != nil else {
            model.show("Use 1-40 letters, numbers, - or _ for the name.")
            return
        }
        var c = VMConfig(id: id, displayName: plan.distro?.name ?? plan.label)
        if FileManager.default.fileExists(atPath: c.diskURL.path) {
            model.show("A system named '\(id)' already exists.")
            return
        }
        if model.systems.contains(where: { $0.displayName == c.displayName }) { c.displayName += " (\(id))" }
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
        "Running systems live in the Try Linux menu bar icon; use it to shut them down cleanly.",
        "The installer only sees a virtual disk, a file on your Mac. Your Mac's drive is never touched.",
        "Copy and paste works between macOS and Linux once Linux has spice-vdagent (Fedora and Ubuntu include it).",
        "Settings can share a Mac folder with Linux: sudo mount -t virtiofs host /mnt/host",
        "Turn on Rosetta in Settings to run x86_64 Linux apps on Apple silicon.",
        "Quitting Try Linux shuts running systems down, so save your work in Linux first.",
    ]

    var body: some View {
        Page {
            VStack(spacing: 0) {
                Text(model.progress.status).font(Theme.mono(13)).foregroundColor(Theme.text)
                    .lineLimit(1).truncationMode(.middle).padding(.top, 40)
                Bar(value: model.progress.value).frame(width: 400).padding(.top, 14)
                Text(model.progress.detail).font(Theme.mono(11.5)).foregroundColor(Theme.muted).padding(.top, 10)
                TimelineView(.periodic(from: .now, by: 7)) { tl in
                    let i = Int(tl.date.timeIntervalSince1970 / 7) % Self.tips.count
                    Text("Tip: " + Self.tips[i]).font(Theme.mono(11.5)).foregroundColor(Theme.accent)
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
