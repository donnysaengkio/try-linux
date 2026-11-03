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

    func show(_ message: String, title: String = "Try Linux") {
        alert = AlertItem(title: title, message: message)
    }

    func describe(_ c: VMConfig) -> String {
        let res = c.resources()
        let what = Distro.find(c.distroId)?.name ?? URL(fileURLWithPath: c.iso).lastPathComponent
        let state = running[c.id] != nil ? "running" : (c.pendingInstall ? "ready to install" : "")
        return [what, "\(res.ramGB) GB, \(res.cores) cores", state].filter { !$0.isEmpty }.joined(separator: "  ·  ")
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
            if let d = plan.distro, d.isImage {
                try await installImage(d, config, diskGB: diskGB)
                return
            }
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

    // A ready-made image needs no installer: unpack it as the system's disk and start it.
    @MainActor
    private func installImage(_ d: Distro, _ config: VMConfig, diskGB: Int) async throws {
        let parts = try await downloadImage(d)
        var c = config
        try FileManager.default.createDirectory(at: c.dir, withIntermediateDirectories: true)
        progress = ProgressState(status: "Unpacking \(d.name)", value: 0, detail: "Writing the ready-made disk", cancellable: true)
        do {
            try await unpack(parts, to: c.diskURL)
            try Disk.grow(c.diskURL, gb: diskGB)
            c.factoryImage = parts[0].deletingLastPathComponent().path
            c.pendingInstall = false
            try c.save()
        } catch {
            try? FileManager.default.removeItem(at: c.dir)   // don't leave a half-written system behind
            throw error
        }
        busy = false
        reload()
        boot(c)
    }

    // Downloads (or reuses) every part of a distro's image and verifies each against its published SHA-256.
    @MainActor
    func downloadImage(_ d: Distro) async throws -> [URL] {
        progress.status = "Finding the latest \(d.name) image"
        progress.detail = "github.com/\(d.image?.repo ?? "")"
        let r = try await Resolver.resolveImage(d)
        try checkCancel()
        let folder = Paths.isos.appendingPathComponent("\(d.id)-\(r.tag)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var urls: [URL] = []
        for (i, p) in r.parts.enumerated() {
            let dest = folder.appendingPathComponent(p.name)
            let n = "part \(i + 1) of \(r.parts.count)"
            if !FileManager.default.fileExists(atPath: dest.path) {
                let part = dest.appendingPathExtension("part")
                let what = "Downloading \(d.name) \(r.tag), \(n)"
                progress = ProgressState(status: what, value: 0, detail: "Connecting…", cancellable: true)
                try await Downloader.download(p.url, to: part, expected: p.size, cancelled: { [weak self] in self?.cancelRequested ?? true }) { got, total, rate in
                    DispatchQueue.main.async {
                        self.progress.value = total > 0 ? Double(got) / Double(total) : nil
                        self.progress.detail = Self.transfer(got, total, rate)
                    }
                }
                if let expected = p.sha256 {
                    progress = ProgressState(status: "Verifying \(n)", value: 0, detail: "Comparing with GitHub's SHA-256 for \(p.name)", cancellable: true)
                    let hash = try await Task.detached(priority: .userInitiated) { [weak self] in
                        try Hashing.sha256(part, cancelled: { self?.cancelRequested ?? true }) { f in
                            DispatchQueue.main.async { self?.progress.value = f }
                        }
                    }.value
                    if hash != expected {
                        try? FileManager.default.removeItem(at: part)
                        throw AppError("\(p.name) didn't match its published checksum, so it was deleted. Try again.")
                    }
                }
                try FileManager.default.moveItem(at: part, to: dest)
            }
            urls.append(dest)
        }
        return urls
    }

    @MainActor
    func unpack(_ parts: [URL], to disk: URL) async throws {
        try await Task.detached(priority: .userInitiated) { [weak self] in
            try Unpack.zstd(parts: parts, to: disk, cancelled: { self?.cancelRequested ?? true }) { f, written in
                DispatchQueue.main.async {
                    self?.progress.value = f
                    self?.progress.detail = "\(formatBytes(written)) written"
                }
            }
        }.value
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

    @MainActor
    func boot(_ config: VMConfig, isoOverride: Bool = false) {
        if let s = running[config.id] { s.bringToFront(); return }
        guard VZVirtualMachine.isSupported else { show(Self.unsupported); return }
        var c = config
        do { try Disk.grow(c.diskURL, gb: c.diskGB) } catch { show("Couldn't grow the disk, so it keeps its current size.\n\n\(error.localizedDescription)") }
        let installer = c.pendingInstall && FileManager.default.fileExists(atPath: c.iso)
        screen = .progress
        progress = ProgressState(status: "Starting \(c.displayName)", detail: installer ? "Booting the installer" : "")
        let session: VMSession
        do { session = try VMSession(config: c, installer: installer) } catch {
            goHome()
            show("\(c.displayName) couldn't start.\n\n\(error.localizedDescription)")
            return
        }
        c = session.config
        running[c.id] = session
        session.onStop = { [weak self] s, error in self?.stopped(s, error) }
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
                var fresh = VMConfig.load(c.dir) ?? c
                let f = DateFormatter()
                f.dateFormat = "yyyy-MM-dd HH:mm"
                fresh.lastBoot = f.string(from: Date())
                try? fresh.save()
                self.goHome()
            }
        }
    }

    private func stopped(_ s: VMSession, _ error: Error?) {
        running[s.config.id] = nil
        // An installer session that ran a while has most likely installed the system.
        if s.installer && Date().timeIntervalSince(s.started) > 60, var fresh = VMConfig.load(s.config.dir) {
            fresh.pendingInstall = false
            try? fresh.save()
        }
        if let error { show("\(s.config.displayName) stopped with an error.\n\n\(error.localizedDescription)") }
        if !busy, screen == .home || screen == .choose { goHome() } else { reload() }
        if running.isEmpty, let done = onAllStopped { onAllStopped = nil; done() }
    }

    func confirmForceStop(_ s: VMSession) {
        let alert = NSAlert()
        alert.messageText = "Force \(s.config.displayName) to stop?"
        alert.informativeText = "Unsaved work in Linux is lost."
        alert.addButton(withTitle: "Force Stop")
        alert.addButton(withTitle: "Cancel")
        if alert.runModal() == .alertFirstButtonReturn { s.forceStop() }
    }

    // MARK: settings helpers

    static var openAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do { if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() } }
            catch { AppModel.shared.show("Couldn't change Open at Login: \(error.localizedDescription)") }
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
    static func confirm(_ title: String, _ text: String, _ ok: String, destructive: Bool = false) -> Bool {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.alertStyle = destructive ? .critical : .warning
        a.addButton(withTitle: ok)
        a.addButton(withTitle: "Cancel")
        return a.runModal() == .alertFirstButtonReturn
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
