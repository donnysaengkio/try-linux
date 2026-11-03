// Where systems live on disk, their settings, and disk/snapshot/backup operations.
import Foundation
import Virtualization

enum Paths {
    static var defaultRoot: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("TryLinux", isDirectory: true)
    }
    static var root: URL {
        if let s = UserDefaults.standard.string(forKey: "storage"), !s.isEmpty { return URL(fileURLWithPath: s, isDirectory: true) }
        return defaultRoot
    }
    static var vms: URL { root.appendingPathComponent("vms", isDirectory: true) }
    static var isos: URL { root.appendingPathComponent("isos", isDirectory: true) }
}

struct VMConfig: Codable, Identifiable, Equatable {
    var id: String
    var displayName: String
    var distroId = ""
    var iso = ""
    var pendingInstall = false
    var profile = 0                 // 0 balanced, 1 maximum, 2 manual
    var ramGB = 8
    var cores = 4
    var diskGB = 0                  // requested capacity; 0 keeps the current size
    var width = 1920
    var height = 1200
    var fullscreen = false
    var sharedFolder = ""
    var sharedWrite = false
    var sound = true
    var mic = false
    var clipboard = true
    var rosetta = false
    var lastBoot = ""
    var macAddress = ""

    static let profileNames = ["Balanced", "Maximum", "Manual"]

    init(id: String, displayName: String) {
        self.id = id
        self.displayName = displayName
    }

    // Missing keys keep their defaults, so settings saved by older versions still load.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        func get<T: Decodable>(_ key: CodingKeys, _ fallback: T) -> T { ((try? c.decodeIfPresent(T.self, forKey: key)) ?? nil) ?? fallback }
        id = get(.id, "")
        displayName = get(.displayName, "")
        distroId = get(.distroId, "")
        iso = get(.iso, "")
        pendingInstall = get(.pendingInstall, false)
        profile = get(.profile, 0)
        ramGB = get(.ramGB, 8)
        cores = get(.cores, 4)
        diskGB = get(.diskGB, 0)
        width = get(.width, 1920)
        height = get(.height, 1200)
        fullscreen = get(.fullscreen, false)
        sharedFolder = get(.sharedFolder, "")
        sharedWrite = get(.sharedWrite, false)
        sound = get(.sound, true)
        mic = get(.mic, false)
        clipboard = get(.clipboard, true)
        rosetta = get(.rosetta, false)
        lastBoot = get(.lastBoot, "")
        macAddress = get(.macAddress, "")
    }

    var dir: URL { Paths.vms.appendingPathComponent(id, isDirectory: true) }
    var diskURL: URL { dir.appendingPathComponent("disk.img") }
    var efiURL: URL { dir.appendingPathComponent("efi.vars") }
    var machineIdURL: URL { dir.appendingPathComponent("machine-id") }
    var configURL: URL { dir.appendingPathComponent("config.json") }
    var snapshotsDir: URL { dir.appendingPathComponent("snapshots", isDirectory: true) }

    static func load(_ dir: URL) -> VMConfig? {
        guard let data = try? Data(contentsOf: dir.appendingPathComponent("config.json")),
              var c = try? JSONDecoder().decode(VMConfig.self, from: data) else { return nil }
        c.id = dir.lastPathComponent
        if c.displayName.isEmpty { c.displayName = c.id }
        return c
    }

    func save() throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try enc.encode(self).write(to: configURL, options: .atomic)
    }

    static func all() -> [VMConfig] {
        let dirs = (try? FileManager.default.contentsOfDirectory(at: Paths.vms, includingPropertiesForKeys: nil)) ?? []
        return dirs.compactMap { d in
            guard let c = load(d), FileManager.default.fileExists(atPath: c.diskURL.path) else { return nil }
            return c
        }.sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
    }

    static var hostCores: Int { ProcessInfo.processInfo.processorCount }
    static var hostRamGB: Int { max(2, Int(ProcessInfo.processInfo.physicalMemory >> 30)) }

    func resources() -> (cores: Int, ramGB: Int) {
        let n = Self.hostCores, total = Self.hostRamGB
        var c: Int, r: Int
        switch profile {
        case 1: c = max(2, n - 2); r = max(2, total * 3 / 4)
        case 2: c = cores; r = ramGB
        default: c = max(2, n / 2); r = max(2, min(16, total / 2))
        }
        let maxCPU = VZVirtualMachineConfiguration.maximumAllowedCPUCount
        let maxRam = Int(VZVirtualMachineConfiguration.maximumAllowedMemorySize >> 30)
        c = min(max(1, c), min(n, maxCPU))
        r = min(max(2, r), min(total, maxRam))
        return (c, r)
    }

    static func uniqueId(_ seed: String) -> String {
        var base = seed.lowercased().replacingOccurrences(of: "[^a-z0-9_-]+", with: "-", options: .regularExpression)
            .trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        if base.isEmpty { base = "linux" }
        if base.count > 32 { base = String(base.prefix(32)) }
        var n = base, i = 2
        while FileManager.default.fileExists(atPath: Paths.vms.appendingPathComponent(n).path) { n = "\(base)-\(i)"; i += 1 }
        return n
    }
}

enum Disk {
    // Raw disk images are sparse on APFS, so a 64 GB disk only uses what Linux writes.
    static func create(_ url: URL, gb: Int) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard FileManager.default.createFile(atPath: url.path, contents: nil) else { throw AppError("Couldn't create the virtual disk.") }
        let h = try FileHandle(forWritingTo: url)
        try h.truncate(atOffset: UInt64(gb) << 30)
        try h.close()
    }

    // Grows the disk to `gb`; never shrinks it.
    static func grow(_ url: URL, gb: Int) throws {
        guard gb > 0 else { return }
        let want = UInt64(gb) << 30
        if want > capacity(url) {
            let h = try FileHandle(forWritingTo: url)
            try h.truncate(atOffset: want)
            try h.close()
        }
    }

    static func capacity(_ url: URL) -> UInt64 {
        UInt64((try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
    }

    static func used(_ url: URL) -> UInt64 {
        UInt64((try? url.resourceValues(forKeys: [.totalFileAllocatedSizeKey]).totalFileAllocatedSize) ?? 0)
    }

    static func freeSpace(_ url: URL) -> Int64 {
        (try? url.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]).volumeAvailableCapacityForImportantUsage) ?? -1
    }
}

// Snapshots are APFS clones of the disk and EFI variables: instant, and they only use space as the disk changes.
enum Snapshots {
    struct Item: Identifiable { let id: String; let date: Date }

    static func list(_ c: VMConfig) -> [Item] {
        let dirs = (try? FileManager.default.contentsOfDirectory(at: c.snapshotsDir, includingPropertiesForKeys: [.creationDateKey])) ?? []
        return dirs.map { d in
            Item(id: d.lastPathComponent, date: (try? d.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast)
        }.sorted { $0.date > $1.date }
    }

    static func take(_ c: VMConfig) throws {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let dest = c.snapshotsDir.appendingPathComponent(f.string(from: Date()), isDirectory: true)
        try FileManager.default.createDirectory(at: dest, withIntermediateDirectories: true)
        try FileManager.default.copyItem(at: c.diskURL, to: dest.appendingPathComponent("disk.img"))
        if FileManager.default.fileExists(atPath: c.efiURL.path) {
            try FileManager.default.copyItem(at: c.efiURL, to: dest.appendingPathComponent("efi.vars"))
        }
    }

    static func revert(_ c: VMConfig, to item: Item) throws {
        let src = c.snapshotsDir.appendingPathComponent(item.id, isDirectory: true)
        try replace(c.diskURL, with: src.appendingPathComponent("disk.img"))
        let efi = src.appendingPathComponent("efi.vars")
        if FileManager.default.fileExists(atPath: efi.path) { try replace(c.efiURL, with: efi) }
    }

    static func delete(_ c: VMConfig, _ item: Item) throws {
        try FileManager.default.removeItem(at: c.snapshotsDir.appendingPathComponent(item.id, isDirectory: true))
    }

    private static func replace(_ target: URL, with source: URL) throws {
        let tmp = target.appendingPathExtension("reverting")
        try? FileManager.default.removeItem(at: tmp)
        try FileManager.default.copyItem(at: source, to: tmp)
        _ = try FileManager.default.replaceItemAt(target, withItemAt: tmp)
    }
}

enum Backups {
    // A backup is a folder holding the disk, EFI variables, machine identity and settings.
    static func backup(_ c: VMConfig, into folder: URL) throws -> URL {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        let dest = folder.appendingPathComponent("\(c.id)-\(f.string(from: Date())).trylinux", isDirectory: true)
        if FileManager.default.fileExists(atPath: dest.path) { throw AppError("\(dest.lastPathComponent) already exists there.") }
        try FileManager.default.createDirectory(at: dest, withIntermediateDirectories: true)
        for name in ["disk.img", "efi.vars", "machine-id", "config.json"] {
            let src = c.dir.appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: src.path) { try FileManager.default.copyItem(at: src, to: dest.appendingPathComponent(name)) }
        }
        return dest
    }

    static func restore(from folder: URL) throws -> VMConfig {
        guard FileManager.default.fileExists(atPath: folder.appendingPathComponent("disk.img").path) else {
            throw AppError("That folder isn't a Try Linux backup (it has no disk.img).")
        }
        var c = VMConfig.load(folder) ?? VMConfig(id: "", displayName: folder.deletingPathExtension().lastPathComponent)
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        let when = (try? folder.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? Date()
        c.id = VMConfig.uniqueId((c.id.isEmpty ? "restored" : c.id) + "-restored")
        c.displayName += " (restored \(f.string(from: when)))"
        c.pendingInstall = false
        c.macAddress = ""
        try FileManager.default.createDirectory(at: c.dir, withIntermediateDirectories: true)
        for name in ["disk.img", "efi.vars", "machine-id"] {
            let src = folder.appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: src.path) { try FileManager.default.copyItem(at: src, to: c.dir.appendingPathComponent(name)) }
        }
        try c.save()
        return c
    }
}
