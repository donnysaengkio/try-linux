// Installing into Applications, updating from GitHub releases, and the app's own themed prompts.
import AppKit
import SwiftUI

// MARK: - Themed prompts

enum DialogResult { case primary, secondary, closed }

// A prompt in the app's own style: icon, title, text, an optional highlighted path, and named buttons.
enum ThemedDialog {
    @MainActor @discardableResult
    static func show(_ title: String, _ text: String? = nil, path: String? = nil, after: String? = nil,
                     primary: String, secondary: String? = nil, danger: Bool = false) -> DialogResult {
        var result = DialogResult.closed
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 520, height: 200),
                            styleMask: [.titled, .closable], backing: .buffered, defer: false)
        panel.title = "Try Linux"
        panel.appearance = NSAppearance(named: .darkAqua)
        panel.backgroundColor = NSColor(Theme.bg)
        panel.isReleasedWhenClosed = false
        let view = DialogView(title: title, text: text, path: path, after: after, primary: primary,
                              secondary: secondary, danger: danger) { picked in
            result = picked
            NSApp.stopModal()
        }
        let host = NSHostingView(rootView: view)
        host.frame.size = host.fittingSize
        panel.contentView = host
        panel.setContentSize(host.fittingSize)
        panel.center()
        // The red close button counts as closing the prompt.
        let watcher = CloseWatcher()
        panel.delegate = watcher
        withExtendedLifetime(watcher) {
            NSApp.activate(ignoringOtherApps: true)
            NSApp.runModal(for: panel)
        }
        panel.orderOut(nil)
        return result
    }

    private final class CloseWatcher: NSObject, NSWindowDelegate {
        func windowWillClose(_ notification: Notification) { NSApp.stopModal() }
    }
}

struct DialogView: View {
    let title: String
    let text: String?
    let path: String?
    let after: String?
    let primary: String
    let secondary: String?
    let danger: Bool
    let pick: (DialogResult) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 16) {
                Image(nsImage: Icons.image(size: 40)).resizable().frame(width: 40, height: 40)
                VStack(alignment: .leading, spacing: 10) {
                    Text(title).font(Theme.mono(16, .bold)).foregroundColor(Theme.text)
                        .fixedSize(horizontal: false, vertical: true)
                    if let text { paragraph(text) }
                    if let path {
                        Text(path).font(Theme.mono(11.5)).foregroundColor(Theme.accent)
                            .padding(8).frame(maxWidth: .infinity, alignment: .leading).background(Theme.card)
                    }
                    if let after { paragraph(after) }
                }
            }
            HStack(spacing: 10) {
                Spacer()
                if let secondary {
                    if danger {
                        Button(secondary) { pick(.secondary) }.buttonStyle(FlatButton()).frame(width: 150, height: 36)
                            .keyboardShortcut(.defaultAction)
                    } else {
                        Button(secondary) { pick(.secondary) }.buttonStyle(FlatButton()).frame(width: 150, height: 36)
                    }
                }
                if danger {
                    Button(primary) { pick(.primary) }.buttonStyle(FlatButton(danger: true)).frame(width: 150, height: 36)
                } else {
                    Button(primary) { pick(.primary) }.buttonStyle(FlatButton(primary: true)).frame(width: 150, height: 36)
                        .keyboardShortcut(.defaultAction)
                }
            }
            .padding(.top, 22)
        }
        .padding(24)
        .frame(width: 520)
        .background(Theme.bg)
        // Esc closes the prompt like its close button.
        .background(Button("") { pick(.closed) }.keyboardShortcut(.cancelAction).opacity(0))
    }

    private func paragraph(_ s: String) -> some View {
        Text(s).font(Theme.mono(12)).foregroundColor(Theme.muted)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

// MARK: - Installing into Applications

enum Installer {
    static var bundleURL: URL { Bundle.main.bundleURL }

    // Where the app really is. macOS can run a downloaded app from a randomized, read-only
    // copy ("App Translocation"); this finds the original.
    static var originalURL: URL {
        if bundleURL.path.contains("/AppTranslocation/"), let u = translocatedOriginal(bundleURL) { return u }
        return bundleURL
    }

    private static func translocatedOriginal(_ url: URL) -> URL? {
        guard let h = dlopen("/System/Library/Frameworks/Security.framework/Security", RTLD_LAZY),
              let sym = dlsym(h, "SecTranslocateCreateOriginalPathForURL") else { return nil }
        typealias Original = @convention(c) (CFURL, UnsafeMutablePointer<Unmanaged<CFError>?>?) -> Unmanaged<CFURL>?
        let f = unsafeBitCast(sym, to: Original.self)
        return f(url as CFURL, nil)?.takeRetainedValue() as URL?
    }

    static var userApplications: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications", isDirectory: true)
    }

    static var isInApplications: Bool {
        let folder = originalURL.deletingLastPathComponent().standardizedFileURL.path
        return folder.hasPrefix("/Applications") || folder.hasPrefix(userApplications.path)
    }

    // /Applications when it can be written to (admin accounts), otherwise ~/Applications.
    static var destinationFolder: URL {
        FileManager.default.isWritableFile(atPath: "/Applications") ? URL(fileURLWithPath: "/Applications", isDirectory: true) : userApplications
    }

    static var destination: URL { destinationFolder.appendingPathComponent("Try Linux.app", isDirectory: true) }

    static func version(of app: URL) -> String? {
        Bundle(url: app)?.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
    }

    // Copies the app into `folder`, replacing an older copy. Returns the installed app.
    @discardableResult
    static func install(from source: URL = originalURL, to folder: URL = destinationFolder) throws -> URL {
        let fm = FileManager.default
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let dest = folder.appendingPathComponent("Try Linux.app", isDirectory: true)
        let staging = folder.appendingPathComponent(".Try Linux.app.installing", isDirectory: true)
        try? fm.removeItem(at: staging)
        try fm.copyItem(at: source, to: staging)
        clearQuarantine(staging)
        if fm.fileExists(atPath: dest.path) {
            _ = try fm.replaceItemAt(dest, withItemAt: staging)
        } else {
            try fm.moveItem(at: staging, to: dest)
        }
        return dest
    }

    // The user already chose to open this copy, so the installed copy shouldn't ask again.
    static func clearQuarantine(_ app: URL) {
        _ = run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", app.path])
    }

    @discardableResult
    static func run(_ tool: String, _ args: [String]) -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return -1 }
        p.waitUntilExit()
        return p.terminationStatus
    }

    // Opens `app` as a new instance, then quits this one.
    @MainActor
    static func relaunch(_ app: URL) {
        let config = NSWorkspace.OpenConfiguration()
        config.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: app, configuration: config) { _, _ in
            DispatchQueue.main.async { NSApp.terminate(nil) }
        }
    }

    static var installText: String {
        "Try Linux copies itself to this folder, so it's in Launchpad and Spotlight, and moves the download to the Trash:"
    }

    // On first launch from anywhere but Applications, offers to install. Returns once the app should carry on.
    @MainActor
    static func offerAtLaunch() {
        guard !isInApplications else { return }
        let defaults = UserDefaults.standard
        if defaults.string(forKey: "declinedInstall") == AppInfo.version { return }
        let dest = destination
        let existing = version(of: dest)
        if existing == AppInfo.version { return }
        let updating = existing != nil
        while true {
            let choice = updating
                ? ThemedDialog.show("Update Try Linux?", "A different version of Try Linux is in this folder:",
                                    path: Paths.display(dest.deletingLastPathComponent()),
                                    after: "Update replaces it with version \(AppInfo.version) and opens it. Your Linux systems aren't affected.\nRun opens this copy without changing the installed one.",
                                    primary: "Update", secondary: "Run")
                : ThemedDialog.show("Install Try Linux?", installText, path: Paths.display(dest.deletingLastPathComponent()),
                                    after: "Run opens it from here without installing; you can install it later from Settings > Advanced > About.\n\nTo uninstall, quit Try Linux and drag it from Applications to the Trash.",
                                    primary: "Install", secondary: "Run")
            if choice == .primary { break }
            if choice == .secondary { defaults.set(AppInfo.version, forKey: "declinedInstall"); return }
            // Closing the prompt quits without remembering a choice, so it's offered again next time.
            if ThemedDialog.show("Quit Try Linux?", "Try Linux will close without \(updating ? "updating" : "installing"). You'll be asked again the next time you open it.",
                                 primary: "Quit", secondary: "Back") == .primary {
                exit(0)
            }
        }
        let installed: URL
        do { installed = try install() } catch {
            ThemedDialog.show("Couldn't install Try Linux", error.localizedDescription, after: "It will run from here instead.", primary: "OK")
            return
        }
        try? FileManager.default.trashItem(at: originalURL, resultingItemURL: nil)
        let open = updating
            ? ThemedDialog.show("Try Linux is updated", "You now have version \(AppInfo.version) in your Applications folder.", primary: "Open")
            : ThemedDialog.show("Try Linux is installed", "Try Linux is now available in your Applications folder.", primary: "Open")
        if open == .primary { relaunch(installed) } else { exit(0) }
    }
}

// MARK: - Updates

// Looks for a newer release on GitHub at most once a day. A release carries the app zip and a
// SHA256SUMS.txt; the zip is checked against it before the app is replaced.
enum Updater {
    static var api = URL(string: "https://api.github.com/repos/donnysaengkio/try-linux-macos/releases?per_page=10")!
    static let releasesPage = URL(string: "https://github.com/donnysaengkio/try-linux-macos/releases")!

    struct Release {
        var version: String
        var page: URL
        var zip: URL?
        var sums: URL?
    }

    static var enabled: Bool {
        get { UserDefaults.standard.object(forKey: "checkUpdates") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "checkUpdates") }
    }

    // The newest release, previews included (draft releases are skipped).
    static func fetch() async throws -> Release {
        let data = try await Net.data(api)
        guard let list = try JSONSerialization.jsonObject(with: data) as? [[String: Any]],
              let r = list.first(where: { ($0["draft"] as? Bool) != true }),
              let tag = r["tag_name"] as? String else { throw AppError("GitHub didn't list any Try Linux for Mac releases.") }
        var release = Release(version: tag.hasPrefix("v") ? String(tag.dropFirst()) : tag,
                              page: URL(string: r["html_url"] as? String ?? "") ?? releasesPage)
        for a in r["assets"] as? [[String: Any]] ?? [] {
            guard let name = a["name"] as? String, let url = URL(string: a["browser_download_url"] as? String ?? "") else { continue }
            if name.hasSuffix("-macOS.zip") { release.zip = url }
            if name == "SHA256SUMS.txt" { release.sums = url }
        }
        return release
    }

    // Compares the numbers only, so "1.0.1-preview" is newer than "1.0.0".
    static func isNewer(_ candidate: String, than current: String) -> Bool {
        func numbers(_ s: String) -> [Int] {
            s.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
        }
        let a = numbers(candidate), b = numbers(current)
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0, y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    // Returns the newer version, or nil. `force` ignores the setting and the once-a-day limit,
    // and only then are errors thrown; otherwise a failed check is skipped quietly.
    static func check(force: Bool) async throws -> String? {
        let d = UserDefaults.standard
        if !force && !enabled { return nil }
        let last = d.object(forKey: "lastUpdateCheck") as? Date ?? .distantPast
        var latest = d.string(forKey: "latestVersion") ?? ""
        if force || Date().timeIntervalSince(last) > 24 * 3600 {
            do {
                latest = try await fetch().version
                d.set(latest, forKey: "latestVersion")
                d.set(Date(), forKey: "lastUpdateCheck")
            } catch {
                if force { throw error }
                return nil
            }
        }
        return isNewer(latest, than: AppInfo.version) ? latest : nil
    }

    // Downloads the release zip, checks it and unpacks it. Returns the new Try Linux.app.
    static func download(_ r: Release, report: @escaping (Int64, Int64, Double) -> Void) async throws -> URL {
        guard let zipURL = r.zip, let sumsURL = r.sums else {
            throw AppError("This release can't be installed automatically. Download it from the release page instead.")
        }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("TryLinux-update-\(r.version)", isDirectory: true)
        try? FileManager.default.removeItem(at: dir)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let zip = dir.appendingPathComponent(zipURL.lastPathComponent)
        try await Downloader.download(zipURL, to: zip, expected: -1, cancelled: { false }, report: report)
        let sums = try await Net.string(sumsURL)
        return try unpack(zip, sums: sums, into: dir)
    }

    // Checks `zip` against its line in `sums`, unpacks it and checks the app's signature.
    static func unpack(_ zip: URL, sums: String, into dir: URL) throws -> URL {
        let name = zip.lastPathComponent
        let expected = sums.split(separator: "\n").compactMap { line -> String? in
            let parts = line.split(whereSeparator: { $0 == " " || $0 == "*" }).map(String.init)
            return parts.count >= 2 && parts.last == name ? parts[0].lowercased() : nil
        }.first
        guard let expected else { throw AppError("The release's SHA256SUMS.txt has no checksum for \(name).") }
        guard try Hashing.sha256(zip, cancelled: { false }, report: { _ in }) == expected else {
            try? FileManager.default.removeItem(at: zip)
            throw AppError("The downloaded update doesn't match its published checksum, so it wasn't used. Try again later.")
        }
        let out = dir.appendingPathComponent("unpacked", isDirectory: true)
        try? FileManager.default.removeItem(at: out)
        guard Installer.run("/usr/bin/ditto", ["-x", "-k", zip.path, out.path]) == 0 else { throw AppError("The update couldn't be unpacked.") }
        let app = out.appendingPathComponent("Try Linux.app", isDirectory: true)
        guard FileManager.default.fileExists(atPath: app.path),
              Installer.run("/usr/bin/codesign", ["--verify", "--deep", app.path]) == 0 else {
            throw AppError("The downloaded update isn't a valid Try Linux app, so it wasn't used.")
        }
        return app
    }

    // Replaces `target` (this app, by default) with the new app.
    static func apply(_ newApp: URL, to target: URL = Installer.bundleURL) throws {
        guard !target.path.contains("/AppTranslocation/") else {
            throw AppError("macOS is running Try Linux from a temporary copy. Install it into Applications first, then update.")
        }
        Installer.clearQuarantine(newApp)
        _ = try FileManager.default.replaceItemAt(target, withItemAt: newApp)
    }
}
