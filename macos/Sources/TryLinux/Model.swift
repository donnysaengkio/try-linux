// App state: which screen is showing, the systems on disk, running sessions, and the install flow.
import AppKit
import ServiceManagement
import SwiftUI
import UniformTypeIdentifiers
import Virtualization

struct InstallPlan: Equatable {
    var distro: Distro?
    var localISO: URL?
    var url: URL?
    var label: String
}

struct ProgressState {
    var status = ""
    var value: Double? = nil
    var detail = ""
    var cancellable = false
}

struct AlertItem: Identifiable {
    let id = UUID()
    let title: String
    let message: String
}

final class AppModel: ObservableObject {
    static let shared = AppModel()

    enum Screen: Equatable {
        case home, choose, progress
        case setup(InstallPlan)
        case settings(String)
    }

    @Published var screen: Screen = .home
    @Published var systems: [VMConfig] = []
    @Published var running: [String: VMSession] = [:]
    @Published var progress = ProgressState()
    @Published var alert: AlertItem?
    @Published var updateAvailable: String?     // a newer release found by the update check
    @Published var stagedUpdate: URL?           // a checked update waiting to be installed at quit
    var cancelRequested = false
    var busy = false
    var onAllStopped: (() -> Void)?

    init(preview systems: [VMConfig]? = nil) {
        if let systems {
            self.systems = systems
        } else {
            reload()
        }
        screen = self.systems.isEmpty ? .choose : .home
    }

    func reload() { systems = VMConfig.all() }

    func goHome() {
        reload()
        screen = systems.isEmpty ? .choose : .home
    }

    // A themed notice. Its title is the message's first part (up to a blank line, a colon or the
    // first sentence) when that's short; otherwise `title`, with the whole message below.
    func show(_ message: String, title: String = "Try Linux") {
        let msg = message.trimmingCharacters(in: .whitespacesAndNewlines)
        var head = msg, body: String? = nil
        for sep in ["\n\n", ": ", ". "] {
            if let r = msg.range(of: sep), r.lowerBound > msg.startIndex {
                head = String(msg[..<r.lowerBound]) + (sep == ". " ? "." : "")
                body = String(msg[r.upperBound...])
                break
            }
        }
        if head.count > 70 { head = title; body = msg }
        if let b = body, let first = b.first { body = first.uppercased() + b.dropFirst() }
        head = head.trimmingCharacters(in: CharacterSet(charactersIn: ".:"))
        let heading = head, text = body?.isEmpty == false ? body : nil
        Task { @MainActor in ThemedDialog.show(heading, text, primary: "OK") }
    }

    func describe(_ c: VMConfig) -> String {
        let res = c.resources()
        let what = Distro.find(c.distroId)?.name ?? URL(fileURLWithPath: c.iso).lastPathComponent
        return [what, "\(res.ramGB) GB, \(res.cores) cores"].filter { !$0.isEmpty }.joined(separator: "  ·  ")
    }

    // The self-test shows systems as running without starting them.
    var previewStatus: [String: String] = [:]

    // Shown on the right of the system's row on Your systems.
    func status(_ c: VMConfig) -> String {
        if let s = previewStatus[c.id] { return s }
        if let s = running[c.id] {
            if s.held { return "Off, console open" }
            return (s.paused ? "Paused" : "Running") + (s.hidden ? ", window closed" : "")
        }
        return c.pendingInstall ? "Ready to install" : ""
    }

    // Powered on: running or paused, not a console opened while off.
    func isOn(_ c: VMConfig) -> Bool {
        if let s = previewStatus[c.id] { return s.hasPrefix("Running") || s.hasPrefix("Paused") }
        if let s = running[c.id] { return !s.held }
        return false
    }

    // MARK: install

    @MainActor
    func install(_ plan: InstallPlan, _ config: VMConfig, diskGB: Int) async {
        guard VZVirtualMachine.isSupported else { show(Self.unsupported); return }
        busy = true
        cancelRequested = false
        screen = .progress
        progress = ProgressState(status: "Getting ready", cancellable: true)
        do {
            var iso = plan.localISO
            if iso == nil {
                progress.status = "Finding the latest \(plan.label)"
                progress.detail = URL(string: plan.distro?.base ?? "")?.host ?? plan.url?.host ?? ""
                let r: ResolvedISO
                if let d = plan.distro { r = try await Resolver.resolve(d) } else { r = await Resolver.fromURL(plan.url!) }
                try checkCancel()
                let dest = Paths.isos.appendingPathComponent(r.fileName)
                if !FileManager.default.fileExists(atPath: dest.path) {
                    try FileManager.default.createDirectory(at: Paths.isos, withIntermediateDirectories: true)
                    let part = dest.appendingPathExtension("part")
                    let what = "Downloading \(r.fileName)"
                    progress = ProgressState(status: what, value: r.size > 0 ? 0 : nil, detail: "Connecting…", cancellable: true)
                    try await Downloader.download(r.url, to: part, expected: r.size, cancelled: { [weak self] in self?.cancelRequested ?? true }) { got, total, rate in
                        DispatchQueue.main.async {
                            self.progress.value = total > 0 ? Double(got) / Double(total) : nil
                            self.progress.detail = Self.transfer(got, total, rate)
                        }
                    }
                    if let expected = r.sha256 {
                        progress = ProgressState(status: "Verifying the download", value: 0,
                                                 detail: "Comparing with the official SHA-256 checksum", cancellable: true)
                        let hash = try await Task.detached(priority: .userInitiated) { [weak self] in
                            try Hashing.sha256(part, cancelled: { self?.cancelRequested ?? true }) { f in
                                DispatchQueue.main.async { self?.progress.value = f }
                            }
                        }.value
                        if hash != expected {
                            try? FileManager.default.removeItem(at: part)
                            throw AppError("The download didn't match its official checksum, so it was deleted. Try again.")
                        }
                    }
                    try FileManager.default.moveItem(at: part, to: dest)
                }
                iso = dest
            }
            progress = ProgressState(status: "Creating the virtual disk", detail: "\(diskGB) GB, grows only as you use it")
            var c = config
            c.iso = iso!.path
            c.pendingInstall = true
            try Disk.create(c.diskURL, gb: diskGB)
            try c.save()
            busy = false
            reload()
            boot(c)
        } catch is CancellationError {
            busy = false
            goHome()
        } catch {
            busy = false
            goHome()
            show(error.localizedDescription)
        }
    }

    private func checkCancel() throws { if cancelRequested { throw CancellationError() } }

    static func transfer(_ got: Int64, _ total: Int64, _ rate: Double) -> String {
        var s = formatBytes(got) + (total > 0 ? " of " + formatBytes(total) : "") + "  ·  " + formatBytes(Int64(rate)) + "/s"
        if total > 0 && rate > 1 {
            let secs = Double(total - got) / rate
            s += "  ·  " + (secs < 60 ? "under a minute" : "\(Int((secs / 60).rounded(.up))) min") + " left"
        }
        return s
    }

    static let unsupported = "This Mac can't run virtual machines. Try Linux needs a Mac with Apple silicon (M1 or later) running macOS 13 or later."

    // MARK: running

    // Starts a system, or brings a running one's window to the front. `console` opens its window
    // with the system still off; Start or Power on starts it there.
    @MainActor
    func boot(_ config: VMConfig, isoOverride: Bool = false, console: Bool = false) {
        if let s = running[config.id] { s.bringToFront(); return }
        guard VZVirtualMachine.isSupported else { show(Self.unsupported); return }
        var c = config
        do { try Disk.grow(c.diskURL, gb: c.diskGB) } catch { show("Couldn't grow the disk, so it keeps its current size.\n\n\(error.localizedDescription)") }
        let installer = c.pendingInstall && FileManager.default.fileExists(atPath: c.iso)
        if !console {
            screen = .progress
            progress = ProgressState(status: "Starting \(c.displayName)", detail: installer ? "Booting the installer" : "")
        }
        let session: VMSession
        do { session = try VMSession(config: c, installer: installer) } catch {
            goHome()
            show("\(c.displayName) couldn't start.\n\n\(error.localizedDescription)")
            return
        }
        c = session.config
        running[c.id] = session
        session.onStop = { [weak self] s, error in self?.stopped(s, error) }
        session.onChange = { [weak self] in self?.objectWillChange.send() }
        session.onWindowClosed = { Self.showLauncher() }
        if console {
            session.showConsole()
            return
        }
        session.start { [weak self] error in
            DispatchQueue.main.async {
                guard let self else { return }
                if let error {
                    self.running[c.id] = nil
                    self.goHome()
                    self.show("\(c.displayName) couldn't start.\n\n\(error.localizedDescription)")
                    return
                }
                session.showWindow()
                self.recordBoot(c)
                self.goHome()
            }
        }
    }

    private func recordBoot(_ c: VMConfig) {
        var fresh = VMConfig.load(c.dir) ?? c
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm"
        fresh.lastBoot = f.string(from: Date())
        try? fresh.save()
    }

    // Console: a running system's window, or for one that's off, its window with the system still off.
    // In fullscreen that would be a blank screen, so it offers to power the system on instead.
    @MainActor
    func openConsole(_ c: VMConfig) {
        if let s = running[c.id] { s.bringToFront(); return }
        if c.fullscreen {
            if ThemedDialog.show("\(c.displayName) isn't running", "Would you like to power it on?",
                                 primary: "Power on", secondary: "Cancel") == .primary { boot(c) }
            return
        }
        boot(c, console: true)
    }

    // Start on a system whose console is open: powers it on in that window.
    @MainActor
    func powerOn(_ s: VMSession) {
        s.powerOn { [weak self] error in
            guard let self else { return }
            if let error {
                self.show("\(s.config.displayName) couldn't start.\n\n\(error.localizedDescription)")
                return
            }
            self.recordBoot(s.config)
            s.bringToFront()
        }
    }

    // MARK: power menu

    @MainActor
    func shutDown(_ s: VMSession) {
        guard ThemedDialog.show("Shut down \(s.config.displayName)?",
                                "Linux shuts down cleanly. Its desktop may ask first; GNOME goes ahead on its own after 60 seconds.",
                                primary: "Shut down", secondary: "Cancel") == .primary else { return }
        s.bringToFront()   // so any question Linux asks can be answered
        s.requestShutdown()
    }

    @MainActor
    func restart(_ s: VMSession) {
        guard ThemedDialog.show("Restart \(s.config.displayName)?",
                                "Linux shuts down cleanly, then starts again with any settings you changed. Its desktop may ask first; GNOME goes ahead on its own after 60 seconds.",
                                primary: "Restart", secondary: "Cancel") == .primary else { return }
        s.restartAsked = Date()
        s.bringToFront()
        s.requestShutdown()
    }

    @MainActor
    func forceStop(_ s: VMSession) {
        guard ThemedDialog.show("Force \(s.config.displayName) to stop?",
                                "This turns it off immediately, like unplugging a Mac. Unsaved work in Linux is lost.",
                                primary: "Force stop", secondary: "Cancel", danger: true) == .primary else { return }
        s.quiet = true
        s.forceStop()
    }

    @MainActor
    func reset(_ s: VMSession) {
        guard ThemedDialog.show("Reset \(s.config.displayName)?",
                                "This restarts it immediately, like a reset button. Unsaved work in Linux is lost.",
                                primary: "Reset", secondary: "Cancel", danger: true) == .primary else { return }
        s.resetAsked = true
        s.forceStop()
    }

    static func showLauncher() {
        if let w = NSApp.windows.first(where: { $0.identifier?.rawValue == "main" || $0.title == "Try Linux" }) {
            w.makeKeyAndOrderFront(nil)
        }
        NSApp.activate(ignoringOtherApps: true)
    }

    private func stopped(_ s: VMSession, _ error: Error?) {
        running[s.config.id] = nil
        // An installer session that ran a while has most likely installed the system.
        if s.installer && !s.held && Date().timeIntervalSince(s.started) > 60, var fresh = VMConfig.load(s.config.dir) {
            fresh.pendingInstall = false
            try? fresh.save()
        }
        // Restart or Reset from the Power menu: start it again, with any changed settings.
        // (Apple Virtualization reports a restart from inside Linux the same way as a power-off.)
        let again = s.resetAsked || (error == nil && Date().timeIntervalSince(s.restartAsked) < 300)
        if again, let fresh = VMConfig.load(s.config.dir) {
            reload()
            Task { @MainActor in self.boot(fresh) }
            return
        }
        if let error, !s.quiet { show("\(s.config.displayName) stopped with an error.\n\n\(error.localizedDescription)") }
        if !busy, screen == .home || screen == .choose { goHome() } else { reload() }
        if running.isEmpty, let done = onAllStopped { onAllStopped = nil; done() }
    }

    // MARK: settings helpers

    static var openAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do { if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() } }
            catch { AppModel.shared.show("Couldn't change Open at Login: \(error.localizedDescription)") }
        }
    }

    // MARK: updates

    @MainActor
    func checkForUpdatesInBackground() {
        Task { @MainActor in
            if let v = try? await Updater.check(force: false) { self.updateAvailable = v }
        }
    }

    // Asks, downloads and checks the update, then replaces the app and restarts it. Linux runs inside
    // Try Linux, so with systems running the update waits until Try Linux quits.
    @MainActor
    func startUpdate() async {
        guard let version = updateAvailable, !busy, stagedUpdate == nil else { return }
        let choice = ThemedDialog.show("Update to Try Linux \(version)?",
            "You have version \(AppInfo.version). Try Linux downloads the update from GitHub, checks it against its published checksum and restarts.",
            after: "Your Linux systems aren't affected.", primary: "Update", secondary: "What's new")
        if choice == .secondary {
            NSWorkspace.shared.open(Updater.releasesPage.appendingPathComponent("tag").appendingPathComponent("v" + version))
            return
        }
        guard choice == .primary else { return }
        if !running.isEmpty, ThemedDialog.show("Update when you quit?",
            "Linux runs inside Try Linux, so restarting now would stop your running systems. Try Linux can download the update now and install it the next time it quits.",
            primary: "Update when I quit", secondary: "Cancel") != .primary { return }
        busy = true
        screen = .progress
        progress = ProgressState(status: "Downloading Try Linux \(version)", detail: "Connecting…")
        do {
            let release = try await Updater.fetch()
            let app = try await Updater.download(release) { got, total, rate in
                DispatchQueue.main.async {
                    self.progress.value = total > 0 ? Double(got) / Double(total) : nil
                    self.progress.detail = Self.transfer(got, total, rate)
                }
            }
            busy = false
            if !running.isEmpty { stagedUpdate = app; goHome(); return }
            try Updater.apply(app)
            Installer.relaunch(Installer.bundleURL)
        } catch {
            busy = false
            goHome()
            if ThemedDialog.show("Couldn't update Try Linux", error.localizedDescription, primary: "Release page", secondary: "Close") == .primary {
                NSWorkspace.shared.open(Updater.releasesPage)
            }
        }
    }

    // Runs slow disk work behind the progress screen, then returns to that system's settings.
    @MainActor
    func runTool(_ status: String, for id: String, _ work: @escaping () throws -> String?) {
        busy = true
        screen = .progress
        progress = ProgressState(status: status, detail: "This can take a moment for large disks")
        Task.detached(priority: .userInitiated) {
            let result: (message: String?, failure: String?)
            do { result = (try work(), nil) } catch { result = (nil, error.localizedDescription) }
            let message = result.message, failure = result.failure
            await MainActor.run {
                self.busy = false
                self.reload()
                self.screen = self.systems.contains { $0.id == id } ? .settings(id) : (self.systems.isEmpty ? .choose : .home)
                if let failure { self.show(failure) } else if let message { self.show(message) }
            }
        }
    }
}

enum Ask {
    // A themed yes/no prompt. Destructive ones have a red button, and Return picks Cancel.
    @MainActor
    static func confirm(_ title: String, _ text: String, _ ok: String, path: String? = nil, after: String? = nil,
                        destructive: Bool = false) -> Bool {
        ThemedDialog.show(title, text.isEmpty ? nil : text, path: path, after: after,
                          primary: ok, secondary: "Cancel", danger: destructive) == .primary
    }

    static func text(_ title: String, _ prompt: String) -> String? {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = prompt
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 360, height: 24))
        field.placeholderString = "https://"
        a.accessoryView = field
        a.addButton(withTitle: "OK")
        a.addButton(withTitle: "Cancel")
        a.window.initialFirstResponder = field
        guard a.runModal() == .alertFirstButtonReturn else { return nil }
        let v = field.stringValue.trimmingCharacters(in: .whitespaces)
        return v.isEmpty ? nil : v
    }

    static func file(_ title: String, iso: Bool = true) -> URL? {
        let p = NSOpenPanel()
        p.title = title
        p.canChooseFiles = true
        p.canChooseDirectories = false
        p.allowsMultipleSelection = false
        if iso { p.allowedContentTypes = [.init(filenameExtension: "iso") ?? .data, .diskImage] }
        return p.runModal() == .OK ? p.url : nil
    }

    static func folder(_ title: String, prompt: String = "Choose") -> URL? {
        let p = NSOpenPanel()
        p.title = title
        p.prompt = prompt
        p.canChooseFiles = false
        p.canChooseDirectories = true
        p.canCreateDirectories = true
        p.allowsMultipleSelection = false
        return p.runModal() == .OK ? p.url : nil
    }
}
