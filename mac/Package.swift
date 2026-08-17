// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "lanclip",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "LanClipCore"),
        .executableTarget(name: "lanclipd", dependencies: ["LanClipCore"]),
        .testTarget(name: "LanClipCoreTests", dependencies: ["LanClipCore"]),
    ]
)
