// The distributions Try Linux offers, and how it finds, downloads and checks their ARM64 installers.
import CryptoKit
import Foundation

struct Distro: Equatable {
    let id: String
    let name: String
    let about: String
    let base: String
    // Each step is appended to `base`. A step starting with ~ is a regex matched against the
    // links in the current directory listing; the newest match wins.
    let steps: [String]
    let checksum: String?
    var diskGB = 64
    // Set for ready-made disk images published as GitHub release assets instead of installer ISOs.
    var image: GitHubImage? = nil

    var isImage: Bool { image != nil }

    static let catalog: [Distro] = [
        Distro(id: "fedora", name: "Fedora Workstation",
               about: "A community distribution sponsored by Red Hat that ships new GNOME, kernel and Mesa releases soon after they come out",
               base: "https://mirrors.kernel.org/fedora/releases/",
               steps: [#"~^\d+/$"#, "Workstation/aarch64/iso/", #"~^Fedora-Workstation-Live-.*\.aarch64\.iso$"#],
               checksum: "~CHECKSUM$"),
        Distro(id: "ubuntu", name: "Ubuntu Desktop",
               about: "The most widely used desktop Linux, made by Canonical, with long-term support releases and a large software catalog",
               base: "https://cdimage.ubuntu.com/releases/",
               steps: [#"~^\d\d\.\d\d(\.\d+)?/$"#, "release/", #"~^ubuntu-.*-desktop-arm64\.iso$"#],
               checksum: "SHA256SUMS"),
        Distro(id: "debian", name: "Debian",
               about: "One of the oldest community distributions, known for stability and the base of Ubuntu, Mint and many others",
               base: "https://cdimage.debian.org/debian-cd/current/arm64/iso-cd/",
               steps: [#"~^debian-.*-arm64-netinst\.iso$"#],
               checksum: "SHA256SUMS"),
        Distro(id: "opensuse", name: "openSUSE Tumbleweed",
               about: "A rolling-release distribution from the openSUSE community that always has the newest KDE Plasma and kernel",
               base: "https://download.opensuse.org/ports/aarch64/tumbleweed/iso/",
               steps: ["openSUSE-Tumbleweed-KDE-Live-aarch64-Current.iso"],
               checksum: "{iso}.sha256"),
        Distro(id: "omarchy", name: "Omarchy",
               about: "Arch Linux with DHH's keyboard-driven Hyprland desktop, from the official ARM64 image. Ready to use with no installer; graphics are software-rendered here",
               base: "", steps: [], checksum: nil,
               image: GitHubImage(repo: "omacom/try-omarchy", assetPrefix: "omarchy-aarch64-edge.img.zst.")),
    ]

    static func find(_ id: String) -> Distro? { catalog.first { $0.id == id } }
}

// A zstd-compressed raw disk image split into numbered parts on a repository's latest GitHub release.
struct GitHubImage: Equatable {
    let repo: String
    let assetPrefix: String
}

struct ResolvedImage {
    struct Part {
        let name: String
        let url: URL
        let size: Int64
        let sha256: String?
    }
    let tag: String
    let parts: [Part]
    var totalSize: Int64 { parts.reduce(0) { $0 + $1.size } }
}

struct AppError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

struct ResolvedISO {
    var url: URL
    var fileName: String
    var sha256: String?
    var size: Int64 = -1
}

enum Net {
    static let userAgent = "TryLinux-macOS/\(AppInfo.version)"

    static func request(_ url: URL, method: String = "GET") -> URLRequest {
        var r = URLRequest(url: url)
        r.httpMethod = method
        r.timeoutInterval = 30
        r.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        return r
    }

    static func string(_ url: URL) async throws -> String {
        let (data, response) = try await URLSession.shared.data(for: request(url))
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            throw AppError("\(url.host ?? "The server") returned HTTP \(http.statusCode).")
        }
        return String(decoding: data, as: UTF8.self)
    }

    static func headSize(_ url: URL) async -> Int64 {
        guard let result = try? await URLSession.shared.data(for: request(url, method: "HEAD")) else { return -1 }
        return result.1.expectedContentLength
    }
}

enum Resolver {
    static func resolve(_ d: Distro) async throws -> ResolvedISO {
        guard let found = try await walk(d.base, d.steps[...]), let url = URL(string: found) else {
            throw AppError("Couldn't find the current \(d.name) download. The mirror may be updating; try again later.")
        }
        var r = ResolvedISO(url: url, fileName: lastSegment(url))
        r.size = await Net.headSize(url)
        if let spec = d.checksum { r.sha256 = try? await findHash(url, spec, r.fileName) }
        return r
    }

    static func resolveImage(_ d: Distro) async throws -> ResolvedImage {
        guard let img = d.image, let api = URL(string: "https://api.github.com/repos/\(img.repo)/releases/latest") else {
            throw AppError("\(d.name) has no image source.")
        }
        struct Release: Decodable {
            struct Asset: Decodable {
                let name: String
                let size: Int64
                let browser_download_url: String
                let digest: String?
            }
            let tag_name: String
            let assets: [Asset]
        }
        var req = Net.request(api)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        let (data, response) = try await URLSession.shared.data(for: req)
        if let http = response as? HTTPURLResponse, http.statusCode != 200 {
            throw AppError("GitHub returned HTTP \(http.statusCode) for \(img.repo)'s latest release. Try again later.")
        }
        let release = try JSONDecoder().decode(Release.self, from: data)
        let parts = release.assets
            .filter { $0.name.hasPrefix(img.assetPrefix) && $0.name.dropFirst(img.assetPrefix.count).allSatisfy(\.isNumber) }
            .sorted { $0.name < $1.name }
            .compactMap { a -> ResolvedImage.Part? in
                guard let url = URL(string: a.browser_download_url) else { return nil }
                let sha = a.digest.flatMap { $0.hasPrefix("sha256:") ? String($0.dropFirst(7)).lowercased() : nil }
                return ResolvedImage.Part(name: a.name, url: url, size: a.size, sha256: sha)
            }
        guard !parts.isEmpty else {
            throw AppError("\(img.repo)'s latest release (\(release.tag_name)) has no \(d.name) image. It may be updating; try again later.")
        }
        return ResolvedImage(tag: release.tag_name, parts: parts)
    }

    static func fromURL(_ url: URL) async -> ResolvedISO {
        var name = lastSegment(url)
        if !name.lowercased().hasSuffix(".iso") { name += ".iso" }
        name = name.replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: ":", with: "_")
        var r = ResolvedISO(url: url, fileName: name)
        r.size = await Net.headSize(url)
        return r
    }

    static func walk(_ url: String, _ steps: ArraySlice<String>) async throws -> String? {
        guard let step = steps.first else { return url }
        let rest = steps.dropFirst()
        if !step.hasPrefix("~") { return try await walk(url + step, rest) }
        // Newest first; fall back to an older entry when the newest isn't fully published yet.
        for m in try await matches(url, String(step.dropFirst())).prefix(3) {
            if let found = try? await walk(url + m, rest) { return found }
        }
        return nil
    }

    static func matches(_ url: String, _ pattern: String) async throws -> [String] {
        guard let u = URL(string: url) else { return [] }
        let html = try await Net.string(u)
        let href = try NSRegularExpression(pattern: "href=\"([^\"?#]+)\"", options: .caseInsensitive)
        let re = try NSRegularExpression(pattern: pattern)
        let ns = html as NSString
        var found = Set<String>()
        for m in href.matches(in: html, range: NSRange(location: 0, length: ns.length)) {
            var h = ns.substring(with: m.range(at: 1))
            let isDir = h.hasSuffix("/")
            while h.hasSuffix("/") { h.removeLast() }
            var seg = h.components(separatedBy: "/").last ?? ""
            if seg.isEmpty || seg == ".." || seg == "." { continue }
            if isDir { seg += "/" }
            if re.matches(plain(seg)) { found.insert(seg) }
        }
        // A (?<v>...) group in the pattern marks the version; it outranks the rest of the name.
        let named = pattern.contains("(?<v>")
        func version(_ s: String) -> String {
            let p = plain(s)
            guard named, let m = re.firstMatch(in: p, range: NSRange(location: 0, length: (p as NSString).length)),
                  let r = Range(m.range(withName: "v"), in: p) else { return s }
            return String(p[r])
        }
        return found.sorted { a, b in
            let c = compare(version(a), version(b))
            return c != 0 ? c > 0 : compare(a, b) > 0
        }
    }

    static func compare(_ a: String, _ b: String) -> Int {
        func nums(_ s: String) -> [Int] {
            // Architecture tags are not version numbers.
            let t = s.replacingOccurrences(of: "(x86_|amd)64|aarch64|arm64|64-?bit", with: "", options: [.regularExpression, .caseInsensitive])
            return t.components(separatedBy: CharacterSet.decimalDigits.inverted).compactMap { Int($0) }
        }
        let x = nums(a), y = nums(b)
        for (p, q) in zip(x, y) where p != q { return p < q ? -1 : 1 }
        if x.count != y.count { return x.count < y.count ? -1 : 1 }
        return a == b ? 0 : (a < b ? -1 : 1)
    }

    static func findHash(_ iso: URL, _ spec: String, _ fileName: String) async throws -> String? {
        let s = iso.absoluteString
        guard let slash = s.lastIndex(of: "/") else { return nil }
        let dir = String(s[...slash]), isoName = String(s[s.index(after: slash)...])
        let name: String
        if spec.hasPrefix("~") {
            guard let n = try await matches(dir, String(spec.dropFirst())).first else { return nil }
            name = n
        } else {
            name = spec.replacingOccurrences(of: "{iso}", with: isoName)
        }
        guard let u = URL(string: dir + name) else { return nil }
        let text = try await Net.string(u)
        let hex = try NSRegularExpression(pattern: "\\b[0-9a-fA-F]{64}\\b")
        for line in text.components(separatedBy: "\n") where line.contains(fileName) {
            if let m = hex.firstMatch(in: line, range: NSRange(location: 0, length: (line as NSString).length)) {
                return (line as NSString).substring(with: m.range).lowercased()
            }
        }
        // A checksum file for a single image (e.g. openSUSE's *-Current.iso.sha256).
        let all = Set(hex.matches(in: text, range: NSRange(location: 0, length: (text as NSString).length))
            .map { (text as NSString).substring(with: $0.range).lowercased() })
        return all.count == 1 ? all.first : nil
    }

    static func lastSegment(_ url: URL) -> String {
        let name = url.lastPathComponent
        return name.removingPercentEncoding ?? name
    }

    private static func plain(_ s: String) -> String { s.removingPercentEncoding ?? s }
}

extension NSRegularExpression {
    func matches(_ s: String) -> Bool {
        firstMatch(in: s, range: NSRange(location: 0, length: (s as NSString).length)) != nil
    }
}

// Downloads to a .part file, resuming a previous partial download when the server allows it.
final class Downloader: NSObject, URLSessionDataDelegate {
    private let part: URL
    private let expected: Int64
    private let cancelled: () -> Bool
    private let report: (Int64, Int64, Double) -> Void
    private var handle: FileHandle?
    private var have: Int64 = 0, total: Int64 = -1, startBytes: Int64 = 0
    private let started = Date()
    private var lastReport = Date.distantPast
    private var continuation: CheckedContinuation<Void, Error>?

    private init(part: URL, expected: Int64, cancelled: @escaping () -> Bool, report: @escaping (Int64, Int64, Double) -> Void) {
        self.part = part
        self.expected = expected
        self.cancelled = cancelled
        self.report = report
    }

    static func download(_ url: URL, to part: URL, expected: Int64,
                         cancelled: @escaping () -> Bool,
                         report: @escaping (Int64, Int64, Double) -> Void) async throws {
        try await Downloader(part: part, expected: expected, cancelled: cancelled, report: report).run(url)
    }

    private func run(_ url: URL) async throws {
        let fm = FileManager.default
        if !fm.fileExists(atPath: part.path) { fm.createFile(atPath: part.path, contents: nil) }
        have = Int64((try? part.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
        if expected > 0 && have == expected { return }
        if expected > 0 && have > expected {
            try? fm.removeItem(at: part)
            fm.createFile(atPath: part.path, contents: nil)
            have = 0
        }
        var req = Net.request(url)
        req.timeoutInterval = 60
        if have > 0 { req.setValue("bytes=\(have)-", forHTTPHeaderField: "Range") }
        let session = URLSession(configuration: .default, delegate: self, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            continuation = c
            session.dataTask(with: req).resume()
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let http = response as? HTTPURLResponse else { completionHandler(.cancel); return }
        if http.statusCode == 416 && have > 0 { completionHandler(.cancel); finish(nil); return }   // already complete
        guard (200..<300).contains(http.statusCode) else {
            completionHandler(.cancel)
            finish(AppError("The server returned HTTP \(http.statusCode)."))
            return
        }
        do {
            handle = try FileHandle(forWritingTo: part)
            if http.statusCode == 206 { try handle?.seekToEnd() } else { try handle?.truncate(atOffset: 0); have = 0 }
        } catch {
            completionHandler(.cancel)
            finish(error)
            return
        }
        startBytes = have
        total = http.expectedContentLength >= 0 ? http.expectedContentLength + have : expected
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        if cancelled() { dataTask.cancel(); finish(CancellationError()); return }
        do { try handle?.write(contentsOf: data) } catch { dataTask.cancel(); finish(error); return }
        have += Int64(data.count)
        if Date().timeIntervalSince(lastReport) >= 0.2 {
            lastReport = Date()
            report(have, total, Double(have - startBytes) / max(0.001, Date().timeIntervalSince(started)))
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        try? handle?.close()
        handle = nil
        if let error {
            finish((error as NSError).code == NSURLErrorCancelled ? CancellationError() : error)
            return
        }
        if total > 0 && have < total { finish(AppError("The download was interrupted. Try again to resume it.")); return }
        finish(nil)
    }

    private func finish(_ error: Error?) {
        guard let c = continuation else { return }
        continuation = nil
        if let error { c.resume(throwing: error) } else { c.resume() }
    }
}

enum Hashing {
    static func sha256(_ url: URL, cancelled: () -> Bool, report: (Double) -> Void) throws -> String {
        let h = try FileHandle(forReadingFrom: url)
        defer { try? h.close() }
        var hasher = SHA256()
        let total = Double(max(1, (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 1))
        var done = 0.0, tick = 0
        while let chunk = try h.read(upToCount: 4 << 20), !chunk.isEmpty {
            if cancelled() { throw CancellationError() }
            hasher.update(data: chunk)
            done += Double(chunk.count)
            tick += 1
            if tick % 8 == 0 { report(done / total) }
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}

func formatBytes(_ bytes: Int64) -> String {
    if bytes >= 1 << 30 { return String(format: "%.2f GB", Double(bytes) / 1_073_741_824) }
    if bytes >= 1 << 20 { return String(format: "%.1f MB", Double(bytes) / 1_048_576) }
    return "\(bytes / 1024) KB"
}
