// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "TryLinux",
    platforms: [.macOS(.v13)],
    targets: [
        // zstd 1.5.7's decompressor (BSD license), for unpacking ready-made disk images such as Omarchy's.
        .target(
            name: "CZstd",
            path: "Sources/CZstd",
            exclude: ["LICENSE"],
            publicHeadersPath: "include",
            cSettings: [.define("ZSTD_DISABLE_ASM", to: "1")]
        ),
        .executableTarget(name: "TryLinux", dependencies: ["CZstd"], path: "Sources/TryLinux"),
    ]
)
