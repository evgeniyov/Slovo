import Darwin
import Foundation
import XCTest
@testable import SlovoCore

/// Opt-in integration checks use the actual downloaded model and Metal backend.
@MainActor
final class DictationWhisperLifecycleTests: XCTestCase {
    private static let audio = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("Shared/fixtures/audio/ru-hello.wav")

    private final class Probe {
        weak var engine: WhisperEngine?
    }

    private static func footprintMB() throws -> Double {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
        let status = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        guard status == KERN_SUCCESS else { throw SlovoError("task_info failed: \(status)") }
        return Double(info.phys_footprint) / 1_048_576
    }

    private func realEngine(language: LanguageMode = .mixed) throws -> WhisperEngine {
        guard let path = ProcessInfo.processInfo.environment["SLOVO_WHISPER_MODEL"] else {
            throw XCTSkip("Set SLOVO_WHISPER_MODEL to run the real model checks")
        }
        return WhisperEngine(modelFile: URL(fileURLWithPath: path), languageMode: language)
    }

    func testEnglishHintTranscribesEnglishWithoutTranslation() async throws {
        let engine = try realEngine(language: .english)
        try await engine.prepare()
        let folder = Self.audio.deletingLastPathComponent()
        let text = try await engine.transcribe(fileURL: folder.appendingPathComponent("en-hello.wav"))
        let expected = try String(contentsOf: folder.appendingPathComponent("en-hello.txt"), encoding: .utf8)
        func words(_ string: String) -> [String] {
            string.lowercased().split(whereSeparator: { !$0.isLetter }).map(String.init)
        }
        XCTAssertEqual(words(text), words(expected), text)
    }

    func testCancelledWarmupCanBePreparedAgain() async throws {
        let engine = try realEngine()
        let pending = Task { try await engine.prepare() }
        try await Task.sleep(for: .milliseconds(150))
        pending.cancel()
        do {
            try await pending.value
            XCTFail("Cancelled warmup must stop")
        } catch {
            XCTAssertTrue(error is CancellationError, "\(error)")
        }
        try await engine.prepare()
        let text = try await engine.transcribe(fileURL: Self.audio)
        XCTAssertTrue(text.lowercased().contains("мероприятий"), text)
    }

    func testRepeatedRealModelLoadTranscribeAndUnload() async throws {
        guard let path = ProcessInfo.processInfo.environment["SLOVO_WHISPER_MODEL"] else {
            throw XCTSkip("Set SLOVO_WHISPER_MODEL to run the real model lifecycle stress test")
        }
        let suite = "slovo.tests.whisperLifecycle.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        settings.accurateMixedDictation = true
        settings.idleUnload = .minutes5
        let models = DictationFakes.Models()
        models.statuses[.whisperTurbo] = .ready(diskBytes: WhisperEngine.Model.size)
        let probe = Probe()
        let uptime = DictationFakes.Uptime()
        let manager = EngineManager(settings: settings, models: models, factory: { id, mode in
            let engine = WhisperEngine(id: id.rawValue, modelFile: URL(fileURLWithPath: path), languageMode: mode)
            probe.engine = engine
            return engine
        }, uptime: uptime)
        defer { manager.unloadAll() }
        let baseline = try Self.footprintMB()
        var afterUnload: [Double] = []
        let clock = ContinuousClock()
        print("WHISPER_LIFECYCLE baselineMB=\(baseline)")
        for cycle in 1...8 {
            let started = clock.now
            // This inner function drops caller-owned strong references before checking deinit.
            func useModel() async throws {
                let engine = try await manager.engine(for: .whisperTurbo)
                let reused = try await manager.engine(for: .whisperTurbo)
                XCTAssertTrue(engine === reused, "Reuse one context while loaded")
                try await engine.prepare() // Idempotent; no second warmup.
                for _ in 0..<3 {
                    let text = try await engine.transcribe(fileURL: Self.audio)
                    XCTAssertTrue(text.lowercased().contains("мероприятий"), text)
                }
                print("WHISPER_LIFECYCLE cycle=\(cycle) loadedMB=\(try Self.footprintMB()) loadAnd3Transcriptions=\(started.duration(to: clock.now))")
            }
            try await useModel()
            if cycle.isMultiple(of: 2) {
                uptime.advance(301)
                XCTAssertEqual(manager.unloadIdleEngines(), [.whisperTurbo])
            } else {
                manager.unload(.whisperTurbo)
            }
            await manager.waitForLoad(.whisperTurbo)
            let freed = await DictationFakes.waitUntil { probe.engine == nil }
            XCTAssertTrue(freed, "Cycle \(cycle): completed tasks must not retain the context")
            // Give the Metal driver time to retire command buffers and release its allocations.
            try await Task.sleep(for: .milliseconds(150))
            let footprint = try Self.footprintMB()
            afterUnload.append(footprint)
            print("WHISPER_LIFECYCLE cycle=\(cycle) unloadedMB=\(footprint)")
            XCTAssertEqual(manager.loadState(of: .whisperTurbo), .unloaded)
        }
        // Runtime/compiler caches survive the first load. Subsequent unloads must plateau,
        // rather than retain another ~850 MB context on every cycle.
        let settled = Array(afterUnload.dropFirst(2))
        XCTAssertLessThan((settled.max() ?? 0) - (settled.min() ?? 0), 64,
                          "Unloaded memory keeps growing: \(afterUnload)")
        XCTAssertLessThan(afterUnload.last ?? .infinity, baseline + 200,
                          "The model's weights/working buffers were not released")
    }
}
