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

    // The self-test renders screens from a scratch folder; this shows the usual folder in its place.
    static var displayRoot: String?

    // A path as the app shows it, with the home folder written as ~.
    static func display(_ url: URL) -> String {
        let p = url.path, r = root.path
        if let d = displayRoot, p.hasPrefix(r) { return d + p.dropFirst(r.count) }
        return (p as NSString).abbreviatingWithTildeInPath
    }
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
    // More disks after Disk 1: a file name inside the system's folder (made by Try Linux) or the full path
    // of an attached disk image.
    var extraDisks: [String] = []
    var cd = ""                     // ISO in the CD/DVD drive; empty when the drive is empty
    var netType = 0                 // 0 shared with this Mac (NAT), 1 no network
    var description = ""            // the user's notes about this system (may be several lines)
    var diskFull = false            // Disk 1 was made with its full size allocated (not sparse)
    // The folder that holds this system's folder when it isn't the storage folder (Create > Location);
    // set from where it's found, not saved.
    var location = ""

    static let profileNames = ["Balanced", "Maximum", "Manual"]
    static let netTypeNames = ["Shared with this Mac (NAT)", "No network"]

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
        extraDisks = get(.extraDisks, [String]())
        cd = get(.cd, "")
        netType = min(1, max(0, get(.netType, 0)))
        description = get(.description, "")
        diskFull = get(.diskFull, false)
    }

    private enum CodingKeys: String, CodingKey {
        case id, displayName, distroId, iso, pendingInstall, profile, ramGB, cores, diskGB, width, height, fullscreen,
             sharedFolder, sharedWrite, sound, mic, clipboard, rosetta, lastBoot, macAddress, extraDisks, cd, netType,
             description, diskFull
    }

    var dir: URL { (location.isEmpty ? Paths.vms : URL(fileURLWithPath: location, isDirectory: true)).appendingPathComponent(id, isDirectory: true) }
    var diskURL: URL { dir.appendingPathComponent("disk.img") }

    func diskFile(_ entry: String) -> URL { entry.hasPrefix("/") ? URL(fileURLWithPath: entry) : dir.appendingPathComponent(entry) }

    // Disk 1 first, then the others in the order they were added.
    var allDisks: [URL] { [diskURL] + extraDisks.map(diskFile) }

    func newDiskName() -> String {
        var n = 2
        while extraDisks.contains("disk\(n).img") || FileManager.default.fileExists(atPath: dir.appendingPathComponent("disk\(n).img").path) { n += 1 }
        return "disk\(n).img"
    }
    var efiURL: URL { dir.appendingPathComponent("efi.vars") }
    var machineIdURL: URL { dir.appendingPathComponent("machine-id") }
    var configURL: URL { dir.appendingPathComponent("config.json") }
    var snapshotsDir: URL { dir.appendingPathComponent("snapshots", isDirectory: true) }

    static func load(_ dir: URL) -> VMConfig? {
        guard let data = try? Data(contentsOf: dir.appendingPathComponent("config.json")),
              var c = try? JSONDecoder().decode(VMConfig.self, from: data) else { return nil }
        c.id = dir.lastPathComponent
        if c.displayName.isEmpty { c.displayName = c.id }
        let parent = dir.deletingLastPathComponent().standardizedFileURL
        c.location = parent.path == Paths.vms.standardizedFileURL.path ? "" : parent.path
        return c
    }

    // Folders of systems made outside the storage folder (Create > Location), so Virtual Machines finds them.
    static var otherDirs: [String] {
        get { UserDefaults.standard.stringArray(forKey: "vmDirs") ?? [] }
        set { UserDefaults.standard.set(newValue, forKey: "vmDirs") }
    }

    // Remembers a system that isn't in the storage folder; forget() is for when it's deleted.
    func remember() { if !location.isEmpty, !Self.otherDirs.contains(dir.path) { Self.otherDirs.append(dir.path) } }
    func forget() { Self.otherDirs.removeAll { $0 == dir.path } }

    func save() throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try enc.encode(self).write(to: configURL, options: .atomic)
    }

    static func all() -> [VMConfig] {
        var dirs = (try? FileManager.default.contentsOfDirectory(at: Paths.vms, includingPropertiesForKeys: nil)) ?? []
        // Then those in a location of their own (a drive that isn't connected just leaves them out).
        for p in otherDirs where !dirs.contains(where: { $0.standardizedFileURL.path == URL(fileURLWithPath: p).standardizedFileURL.path }) {
            dirs.append(URL(fileURLWithPath: p, isDirectory: true))
        }
        return dirs.compactMap { d in
            guard let c = load(d), FileManager.default.fileExists(atPath: c.diskURL.path) else { return nil }
            return c
        }.sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
    }

    // The self-test renders screens as a typical Mac rather than the small CI machine.
    static var previewHost: (cores: Int, ramGB: Int)?
    static var hostCores: Int { previewHost?.cores ?? ProcessInfo.processInfo.processorCount }
    static var hostRamGB: Int { previewHost?.ramGB ?? max(2, Int(ProcessInfo.processInfo.physicalMemory >> 30)) }

    // What a system can be given: macOS keeps 2 cores and a quarter of the memory (and Apple
    // Virtualization has its own limits).
    static var maxCores: Int {
        let n = hostCores
        let vz = previewHost == nil ? VZVirtualMachineConfiguration.maximumAllowedCPUCount : n
        return max(min(2, n), min(n - 2, vz))
    }
    static var maxRamGB: Int {
        let vz = previewHost == nil ? Int(VZVirtualMachineConfiguration.maximumAllowedMemorySize >> 30) : hostRamGB
        return max(2, min(hostRamGB * 3 / 4, vz))
    }

    func resources() -> (cores: Int, ramGB: Int) {
        let n = Self.hostCores, total = Self.hostRamGB
        var c: Int, r: Int
        switch profile {
        case 1: c = Self.maxCores; r = Self.maxRamGB
        case 2: c = cores; r = ramGB
        default: c = max(2, n / 2 - (n / 2) % 2); r = max(2, min(16, total / 2))   // half the cores, rounded down to even
        }
        c = min(max(1, c), Self.maxCores)
        r = min(max(2, r), Self.maxRamGB)
        return (c, r)
    }

    // The folder name for a new system, from its name: unique among your systems and in `parent` (the
    // storage folder unless it has a location of its own).
    static func uniqueId(_ seed: String, in parent: URL = Paths.vms) -> String {
        var base = seed.lowercased().replacingOccurrences(of: "[^a-z0-9_-]+", with: "-", options: .regularExpression)
            .trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        if base.isEmpty { base = "linux" }
        if base.count > 32 { base = String(base.prefix(32)) }
        let taken = Set(all().map(\.id))
        var n = base, i = 2
        while taken.contains(n) || FileManager.default.fileExists(atPath: parent.appendingPathComponent(n).path) { n = "\(base)-\(i)"; i += 1 }
        return n
    }
}

enum Disk {
    // Raw disk images are sparse on APFS, so a 64 GB disk only uses what Linux writes. With `full`, the whole
    // size is allocated on the drive now (F_PREALLOCATE) instead.
    static func create(_ url: URL, gb: Int, full: Bool = false) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard FileManager.default.createFile(atPath: url.path, contents: nil) else { throw AppError("Couldn't create the virtual disk.") }
        let h = try FileHandle(forWritingTo: url)
        let size = UInt64(gb) << 30
        if full {
            var store = fstore_t(fst_flags: UInt32(F_ALLOCATEALL), fst_posmode: F_PEOFPOSMODE, fst_offset: 0,
                                 fst_length: off_t(size), fst_bytesalloc: 0)
            if fcntl(h.fileDescriptor, F_PREALLOCATE, &store) == -1 {
                try? h.close()
                try? FileManager.default.removeItem(at: url)
                throw AppError("Couldn't allocate \(gb) GB for the virtual disk. Check that the drive has room, or untick \"Allocate the full size now\".")
            }
        }
        try h.truncate(atOffset: size)
        try h.close()
    }

    // How much of the drive a file takes now (its allocated blocks), for checking a full allocation.
    static func allocated(_ url: URL) -> Int64 {
        var st = stat()
        return stat(url.path, &st) == 0 ? Int64(st.st_blocks) * 512 : -1
    }

    // Free space on the drive that holds `folder` (the nearest folder that exists), and the drive's name.
    static func free(_ folder: URL) -> (bytes: Int64, volume: String)? {
        var u = folder
        while !FileManager.default.fileExists(atPath: u.path), u.pathComponents.count > 1 { u.deleteLastPathComponent() }
        guard let v = try? u.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey, .volumeNameKey]),
              let bytes = v.volumeAvailableCapacityForImportantUsage else { return nil }
        return (bytes, v.volumeName ?? "this Mac")
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

    // Sets the disk's maximum size, larger or smaller. Shrinking cuts off the end of the disk, so
    // Linux must have been shrunk to fit first.
    static func resize(_ url: URL, gb: Int) throws {
        let h = try FileHandle(forWritingTo: url)
        try h.truncate(atOffset: UInt64(gb) << 30)
        try h.close()
    }

    // Read sizes fresh each time: URL caches resource values, and a stale (smaller) size here
    // would make grow() truncate a disk that has already been grown.
    static func capacity(_ url: URL) -> UInt64 {
        ((try? FileManager.default.attributesOfItem(atPath: url.path))?[.size] as? NSNumber)?.uint64Value ?? 0
    }

    static func used(_ url: URL) -> UInt64 {
        let fresh = URL(fileURLWithPath: url.path)
        return UInt64((try? fresh.resourceValues(forKeys: [.totalFileAllocatedSizeKey]).totalFileAllocatedSize) ?? 0)
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
    // A backup is a folder holding the disks, EFI variables, machine identity and settings. Every disk
    // is copied, attached ones too, as disk2.img, disk3.img and so on.
    static func backup(_ c: VMConfig, into folder: URL) throws -> URL {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        let dest = folder.appendingPathComponent("\(c.id)-\(f.string(from: Date())).trylinux", isDirectory: true)
        if FileManager.default.fileExists(atPath: dest.path) { throw AppError("\(dest.lastPathComponent) already exists there.") }
        try FileManager.default.createDirectory(at: dest, withIntermediateDirectories: true)
        for name in ["disk.img", "efi.vars", "machine-id"] {
            let src = c.dir.appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: src.path) { try FileManager.default.copyItem(at: src, to: dest.appendingPathComponent(name)) }
        }
        var saved = c
        saved.extraDisks = []
        for (i, entry) in c.extraDisks.enumerated() {
            let src = c.diskFile(entry)
            guard FileManager.default.fileExists(atPath: src.path) else { continue }
            let name = "disk\(i + 2).img"
            try FileManager.default.copyItem(at: src, to: dest.appendingPathComponent(name))
            saved.extraDisks.append(name)
        }
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try enc.encode(saved).write(to: dest.appendingPathComponent("config.json"), options: .atomic)
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
        // The other disks come back as files of the copy's own.
        let extra = c.extraDisks.filter { !$0.hasPrefix("/") }
        c.extraDisks = []
        for name in extra {
            let src = folder.appendingPathComponent(name)
            guard FileManager.default.fileExists(atPath: src.path) else { continue }
            let mine = c.newDiskName()
            try FileManager.default.copyItem(at: src, to: c.dir.appendingPathComponent(mine))
            c.extraDisks.append(mine)
        }
        try c.save()
        return c
    }
}

// What an existing disk image tells about itself: its size, and the settings of the Try Linux system it
// came from when it sits in one (a system's folder holds config.json beside disk.img).
struct DiskSource: Equatable {
    var size: Int64 = -1
    var name = "", description = "", from = ""
    var ramGB = 0, cores = 0

    static func read(_ disk: URL) -> DiskSource {
        var s = DiskSource()
        s.size = (try? disk.resourceValues(forKeys: [.fileSizeKey]).fileSize).map { Int64($0) } ?? -1
        let folder = disk.deletingLastPathComponent()
        if disk.lastPathComponent == "disk.img", let c = VMConfig.load(folder) {
            s.name = c.displayName
            s.description = c.description
            let r = c.resources()
            s.ramGB = r.ramGB
            s.cores = r.cores
            s.from = "Try Linux"
        }
        return s
    }

    // One line for Use Your Own Installer, e.g. "64 GB  ·  settings from Try Linux: 8 GB, 4 cores".
    var summary: String {
        var parts: [String] = []
        if size > 0 { parts.append(String(format: "%.0f GB", Double(size) / 1_073_741_824)) }
        parts.append(from.isEmpty ? "no saved settings found beside it" : "settings from \(from): \(ramGB) GB, \(cores) \(cores == 1 ? "core" : "cores")")
        return parts.joined(separator: "  ·  ")
    }
}
