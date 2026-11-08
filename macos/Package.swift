// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "CasePilot",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(name: "CasePilot", path: "Sources/CasePilot"),
        .testTarget(name: "CasePilotTests", dependencies: ["CasePilot"], path: "Tests/CasePilotTests"),
    ]
)
