// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "Slovo",
    defaultLocalization: "en",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "Slovo", targets: ["Slovo"]),
        .executable(name: "slovo-bench", targets: ["slovo-bench"]),
        .library(name: "SlovoCore", targets: ["SlovoCore"])
    ],
    dependencies: [
        // GigaAM v3 runs through sherpa-onnx on ONNX Runtime.
        .package(url: "https://github.com/k2-fsa/sherpa-onnx", exact: "1.13.8"),
        // Parakeet TDT v3 runs through FluidAudio on Core ML.
        .package(url: "https://github.com/FluidInference/FluidAudio", exact: "0.15.7"),
        // sherpa-onnx 1.13.8 asks for onnxruntime-libs 1.28.2, whose tag upstream moved to a
        // commit that still lists 1.28.1. On 2026-09-17 upstream re-uploaded the 1.28.2
        // binaries and published their checksums in "Fix v1.28.2"; pinning that commit keeps
        // clean checkouts building against what the release serves now.
        .package(url: "https://github.com/csukuangfj/onnxruntime-libs", revision: "8ebc5ebf92190903c274a7621ba4c96a732335ec")
    ],
    targets: [
        // whisper.cpp v1.9.4 (nightly build b5130 is the same commit), the official prebuilt
        // framework: Whisper large-v3 turbo as the English helper of mixed dictation, on Metal.
        // A dynamic framework; build.py embeds and signs it in Contents/Frameworks.
        .binaryTarget(
            name: "whisper",
            url: "https://github.com/ggml-org/whisper.cpp/releases/download/b5130/whisper-b5130-xcframework.zip",
            checksum: "033a43b0174e8cf9b366f72e4a428cdcf126f93ad1c87d3fa119a96bed6f231a"
        ),
        // Engines, audio capture, text pipeline, storage and the dictation and meeting
        // services shared by the app and the bench. No SwiftUI views live here.
        .target(
            name: "SlovoCore",
            dependencies: [
                .product(name: "sherpa-onnx", package: "sherpa-onnx"),
                .product(name: "FluidAudio", package: "FluidAudio"),
                "whisper"
            ],
            path: "Sources/SlovoCore",
            resources: [.process("Resources")],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        // The menu bar app: windows, Flow bar, settings and onboarding.
        .executableTarget(
            name: "Slovo",
            dependencies: ["SlovoCore"],
            path: "Sources/Slovo",
            resources: [.process("Resources")],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        // Pure scoring logic, kept free of AVFoundation so it is easy to test.
        .target(
            name: "BenchKit",
            path: "Sources/BenchKit",
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .executableTarget(
            name: "slovo-bench",
            dependencies: ["SlovoCore", "BenchKit"],
            path: "Sources/slovo-bench",
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .testTarget(
            name: "BenchKitTests",
            dependencies: ["BenchKit"],
            path: "Tests/BenchKitTests",
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .testTarget(
            name: "SlovoCoreTests",
            dependencies: ["SlovoCore"],
            path: "Tests/SlovoCoreTests",
            swiftSettings: [.swiftLanguageMode(.v5)]
        )
    ]
)
