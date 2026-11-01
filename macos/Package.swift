// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "MchoseHubMac",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "MchoseCore", targets: ["MchoseCore"]),
        .executable(name: "mchose", targets: ["MchoseCLI"]),
        .executable(name: "MchoseHub", targets: ["MchoseApp"])
    ],
    targets: [
        .target(name: "MchoseCore", resources: [.copy("Resources/models.json"), .copy("Resources/functions.json"), .copy("Resources/factory.json")]),
        .target(name: "MchoseHID", dependencies: ["MchoseCore"]),
        .executableTarget(name: "MchoseCLI", dependencies: ["MchoseCore", "MchoseHID"]),
        .executableTarget(name: "MchoseApp", dependencies: ["MchoseCore", "MchoseHID"]),
        .testTarget(name: "MchoseCoreTests", dependencies: ["MchoseCore", "MchoseHID"],
                    resources: [.copy("Fixtures")])
    ],
    swiftLanguageModes: [.v6]
)
