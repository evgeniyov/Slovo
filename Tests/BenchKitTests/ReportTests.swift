import XCTest
@testable import BenchKit

final class ReportTests: XCTestCase {
    func testLoadFiguresAreDerivedFromResourceUsage() {
        var system = SystemLoad()
        system.idlePowerW = 5
        system.loadPowerW = 13
        system.idleRAMUsedMB = 10_000
        system.peakRAMUsedMB = 10_600
        var usage = ResourceUsage()
        usage.passes = 3
        usage.measuredWallSeconds = 6
        usage.processCPUSeconds = 12
        usage.helperCPUSeconds = 3
        usage.system = system
        let run = EngineRun(engine: "e", date: Date(timeIntervalSince1970: 0), system: "", clips: [
            ClipResult(clip: "r001", text: "a", audioSeconds: 30, processingSeconds: 1),
            ClipResult(clip: "r002", text: "b", audioSeconds: 30, processingSeconds: 3)
        ], resources: usage)

        let summary = Report.summarize(run, references: [:])

        XCTAssertEqual(summary.processCPUCores ?? 0, 2, accuracy: 1e-9)
        XCTAssertEqual(summary.helperCPUCores ?? 0, 0.5, accuracy: 1e-9)
        XCTAssertEqual(summary.extraPowerW ?? 0, 8, accuracy: 1e-9)
        // 8 W for 6 s over three passes of 60 s of audio: 48 J per 180 s, 16 J per minute.
        XCTAssertEqual(summary.energyPerAudioMinuteJ ?? 0, 16, accuracy: 1e-9)
        XCTAssertEqual(summary.systemRAMGrowthMB ?? 0, 600, accuracy: 1e-9)
        XCTAssertEqual(summary.longestSeconds, 3, accuracy: 1e-9)
    }

    func testLoadTableShowsDashWhenNothingWasMeasured() {
        let run = EngineRun(engine: "e", date: Date(timeIntervalSince1970: 0), system: "", clips: [
            ClipResult(clip: "r001", text: "a", audioSeconds: 2, processingSeconds: 0.1)
        ])
        let summary = Report.summarize(run, references: [:])

        XCTAssertNil(summary.energyPerAudioMinuteJ)
        XCTAssertTrue(Report.loadTable([summary]).contains("| e | — |"))
    }

    func testResourcesSurviveJSONRoundTrip() throws {
        var usage = ResourceUsage()
        usage.peakFootprintMB = 812
        usage.system = SystemLoad()
        let run = EngineRun(engine: "e", date: Date(timeIntervalSince1970: 0), system: "", clips: [], resources: usage)

        let decoded = try EngineRun.decode(run.encoded())

        XCTAssertEqual(decoded.resources?.peakFootprintMB, 812)
        XCTAssertNotNil(decoded.resources?.system)
    }
}
