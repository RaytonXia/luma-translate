// swift-tools-version: 5.9

import PackageDescription

let package = Package(
    name: "LumaTranslate",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(name: "LumaTranslate", targets: ["LumaTranslate"])
    ],
    targets: [
        .executableTarget(
            name: "LumaTranslate",
            path: "Sources/LumaTranslate",
            resources: [
                .process("Resources")
            ]
        ),
        .testTarget(
            name: "LumaTranslateTests",
            dependencies: ["LumaTranslate"],
            path: "Tests/LumaTranslateTests"
        )
    ]
)
