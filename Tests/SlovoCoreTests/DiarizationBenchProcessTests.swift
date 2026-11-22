import AVFoundation
import XCTest
@testable import SlovoCore

/// The speaker evaluation harness end to end, without the model: `slovo-bench
/// diarize-synth` renders synthetic tone voices, `diarize --fake-diarizer oracle` scores
/// them, and a real run stops when the model is missing. Everything happens in a
/// temporary bench folder; skipped when the bench binary is not built.
final class DiarizationBenchProcessTests: XCTestCase {
    private var bench: URL!
    private var binary: URL!

    override func setUpWithError() throws {
        let products = Bundle(for: Self.self).bundleURL.deletingLastPathComponent()
        let candidates = [
            products.appendingPathComponent("slovo-bench"),
            URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
                .deletingLastPathComponent().appendingPathComponent(".build/debug/slovo-bench")
        ]
        guard let found = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0.path) }) else {
            throw XCTSkip("slovo-bench is not built; run swift build --build-tests first")
        }
        binary = found
        bench = try DiarizationFixtures.temporaryFolder("SlovoBenchHarness")
        try FileManager.default.createDirectory(at: bench.appendingPathComponent("scenarios"), withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        if let bench {
            try? FileManager.default.removeItem(at: bench)
        }
    }

    // MARK: - Fixtures

    private let cowork = """
        {"name": "tiny-cowork", "seed": 3, "me": "A",
         "voices": {"A": {"source": "tone", "pitchHz": 110}, "B": {"source": "tone", "pitchHz": 210},
                    "C": {"source": "tone", "pitchHz": 160}},
         "turns": [
          {"speaker": "A", "seconds": 6, "gap": 0, "text": "начнём с релиза"},
          {"speaker": "B", "seconds": 0.6, "gap": 0.4, "text": "да"},
          {"speaker": "C", "seconds": 12, "gap": -0.2, "text": "сборка готова"},
          {"speaker": "A", "seconds": 2.5, "gap": 0.8, "text": "хорошо"},
          {"speaker": "B", "seconds": 8, "gap": 1.2, "text": "заметки к пятнице"},
          {"speaker": "C", "seconds": 4, "gap": 0.5, "text": "согласна"}
         ]}
        """

    private let call = """
        {"name": "tiny-call", "seed": 5, "me": "A",
         "voices": {"A": {"source": "tone", "pitchHz": 120}, "B": {"source": "tone", "pitchHz": 220, "track": "system"}},
         "turns": [
          {"speaker": "A", "seconds": 5, "gap": 0, "text": "привет"},
          {"speaker": "B", "seconds": 7, "gap": 0.5, "text": "добрый день"},
          {"speaker": "A", "seconds": 0.8, "gap": 0.3, "text": "угу"},
          {"speaker": "B", "seconds": 9, "gap": 0.6, "text": "по API всё готово"},
          {"speaker": "A", "seconds": 6, "gap": 0.9, "text": "отлично"}
         ]}
        """

    @discardableResult
    private func run(_ arguments: [String], file: StaticString = #filePath, line: UInt = #line,
                     expectSuccess: Bool = true) throws -> String {
        let process = Process()
        process.executableURL = binary
        process.arguments = arguments + ["--bench", bench.path]
        process.currentDirectoryURL = bench
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        try process.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let output = String(decoding: data, as: UTF8.self)
        if expectSuccess {
            XCTAssertEqual(process.terminationStatus, 0, output, file: file, line: line)
        } else {
            XCTAssertNotEqual(process.terminationStatus, 0, output, file: file, line: line)
        }
        return output
    }

    private func synthesize(_ json: String, name: String, condition: String, extra: [String] = []) throws -> URL {
        let scenario = bench.appendingPathComponent("scenarios/\(name).json")
        try Data(json.utf8).write(to: scenario)
        try run(["diarize-synth", "--scenario", scenario.path, "--condition", condition] + extra)
        return bench.appendingPathComponent("diarization/out/\(name)-\(condition)", isDirectory: true)
    }

    private func json(_ url: URL) throws -> Any {
        try JSONSerialization.jsonObject(with: Data(contentsOf: url))
    }

    private func files(in folder: URL) -> [String] {
        ((try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []).sorted()
    }

    private func der(_ result: [String: Any]) -> Double {
        let tracks = result["tracks"] as? [[String: Any]] ?? []
        var errors = 0.0
        var reference = 0.0
        for track in tracks {
            let der = track["der"] as? [String: Double] ?? [:]
            errors += (der["miss"] ?? 0) + (der["falseAlarm"] ?? 0) + (der["confusion"] ?? 0)
            reference += der["referenceSeconds"] ?? 0
        }
        return reference > 0 ? errors / reference : .nan
    }

    private func rms(_ url: URL, from start: Double, to end: Double) throws -> Double {
        let samples = try AudioLoader.samples(from: url)
        let lower = max(0, Int(start * 16_000))
        let upper = min(samples.count, Int(end * 16_000))
        guard upper > lower else { return 0 }
        let sum = samples[lower..<upper].reduce(0.0) { $0 + Double($1) * Double($1) }
        return (sum / Double(upper - lower)).squareRoot()
    }

    // MARK: - Tests

    func testSynthesizedCaseHasSlovoTracksTruthAndRenderInfo() throws {
        let folder = try synthesize(cowork, name: "tiny-cowork", condition: "far-noisy-20", extra: ["--seed", "9"])
        XCTAssertEqual(files(in: folder), ["mic.caf", "mix.wav", "render.json", "truth.rttm", "turns.json"])

        let microphone = folder.appendingPathComponent("mic.caf")
        let file = try AVAudioFile(forReading: microphone)
        XCTAssertEqual(file.fileFormat.sampleRate, 16_000)
        XCTAssertEqual(file.fileFormat.channelCount, 1)
        XCTAssertEqual(file.fileFormat.commonFormat, .pcmFormatFloat32)
        XCTAssertNoThrow(try MeetingTrackReader(url: microphone), "The meeting pipeline must read the track")

        let turns = try XCTUnwrap(try json(folder.appendingPathComponent("turns.json")) as? [[String: Any]])
        XCTAssertEqual(turns.map { $0["speaker"] as? String }, ["A", "B", "C", "A", "B", "C"])
        XCTAssertEqual(Set(turns.compactMap { $0["track"] as? String }), ["microphone"])
        let starts = turns.compactMap { $0["start"] as? Double }
        let ends = turns.compactMap { $0["end"] as? Double }
        XCTAssertEqual(starts[0], 0.5, accuracy: 0.001)
        XCTAssertEqual(ends[0] - starts[0], 6, accuracy: 0.01)
        XCTAssertEqual(starts[1] - ends[0], 0.4, accuracy: 0.01)
        XCTAssertEqual(starts[2] - ends[1], -0.2, accuracy: 0.01, "A negative gap overlaps the previous turn")
        XCTAssertEqual(Double(file.length) / 16_000, (ends.max() ?? 0) + 1, accuracy: 0.01)

        let truth = try String(contentsOf: folder.appendingPathComponent("truth.rttm"), encoding: .utf8)
        let lines = truth.split(separator: "\n")
        XCTAssertEqual(lines.count, 6)
        XCTAssertEqual(lines.first, "SPEAKER mic 1 0.500 6.000 <NA> <NA> A <NA> <NA>")

        let render = try XCTUnwrap(try json(folder.appendingPathComponent("render.json")) as? [String: Any])
        XCTAssertEqual(render["seed"] as? Int, 9)
        XCTAssertEqual(render["condition"] as? String, "far-noisy-20")
        XCTAssertEqual(render["me"] as? String, "A")
        XCTAssertEqual((render["noise"] as? [String: Double])?["snrDecibels"], 20)
        let voices = try XCTUnwrap(render["voices"] as? [String: [String: Any]])
        for (id, voice) in voices {
            let gain = try XCTUnwrap(voice["gainDecibels"] as? Double)
            XCTAssertTrue((-18.0 ... -6.0).contains(gain), "\(id): \(gain) dB")
        }

        // The same seed renders the same case.
        let again = bench.appendingPathComponent("again", isDirectory: true)
        try run(["diarize-synth", "--scenario", bench.appendingPathComponent("scenarios/tiny-cowork.json").path,
                 "--condition", "far-noisy-20", "--seed", "9", "--out", again.path])
        XCTAssertEqual(try Data(contentsOf: again.appendingPathComponent("truth.rttm")), try Data(contentsOf: folder.appendingPathComponent("truth.rttm")))
        let first = try AudioLoader.samples(from: microphone)
        let second = try AudioLoader.samples(from: again.appendingPathComponent("mic.caf"))
        XCTAssertEqual(first.count, second.count)
        let difference = zip(first, second).reduce(Float(0)) { max($0, abs($1.0 - $1.1)) }
        XCTAssertLessThan(difference, 1e-4)
    }

    func testOracleRunScoresTheCaseAndAppendsTheReport() throws {
        let folder = try synthesize(cowork, name: "tiny-cowork", condition: "clean")
        let output = try run(["diarize", "--case", folder.path, "--fake-diarizer", "oracle", "--collar", "0.25"])
        XCTAssertTrue(output.contains("fake-oracle"), output)

        let slug = "step0.1-minSeg0-threshold0.775-zeroVoteon-auto"
        XCTAssertTrue(files(in: folder).contains("hyp-\(slug).rttm"))
        let result = try XCTUnwrap(try json(folder.appendingPathComponent("result-\(slug).json")) as? [String: Any])
        XCTAssertEqual(result["diarizer"] as? String, "fake-oracle")
        XCTAssertEqual(result["hint"] as? String, "automatic")
        XCTAssertEqual(result["configuration"] as? String, DiarizationTuning.standard.description)
        XCTAssertEqual(result["collar"] as? Double, 0.25)
        XCTAssertLessThan(der(result), 0.1)
        let track = try XCTUnwrap((result["tracks"] as? [[String: Any]])?.first)
        XCTAssertEqual(track["speakers"] as? [String: Int], ["reference": 3, "detected": 3])

        let hypothesis = try String(contentsOf: folder.appendingPathComponent("hyp-\(slug).rttm"), encoding: .utf8)
        XCTAssertTrue(hypothesis.hasPrefix("SPEAKER mic 1 "), hypothesis)
        let report = try String(contentsOf: bench.appendingPathComponent("results/diarization.md"), encoding: .utf8)
        XCTAssertTrue(report.contains("## tiny-cowork-clean"), report)
        XCTAssertTrue(report.contains("fake-oracle"))
    }

    func testSweepRunsNinetySixConfigurationsAndTheSummaryRanksThem() throws {
        let folder = try synthesize(cowork, name: "tiny-cowork", condition: "far")
        try run(["diarize", "--case", folder.path, "--fake-diarizer", "oracle", "--sweep"])
        let results = files(in: folder).filter { $0.hasPrefix("result-") }
        XCTAssertEqual(results.count, 96)
        XCTAssertEqual(results.filter { $0.hasSuffix("-n3.json") }.count, 48, "The exact hint uses the true count")

        let summary = try run(["diarize", "--summary"])
        XCTAssertTrue(summary.contains("### Пороги выпуска: \(DiarizationTuning.standard.description)"), summary)
        XCTAssertTrue(summary.contains("подменённым диаризатором"), summary)
    }

    func testCallCaseHasEchoOnTheMicrophoneAndChecksTheMeLabel() throws {
        let folder = try synthesize(call, name: "tiny-call", condition: "call-speakers")
        XCTAssertTrue(files(in: folder).contains("system.caf"))
        let turns = try XCTUnwrap(try json(folder.appendingPathComponent("turns.json")) as? [[String: Any]])
        let remote = try XCTUnwrap(turns.first { $0["speaker"] as? String == "B" })
        let start = try XCTUnwrap(remote["start"] as? Double) + 0.2
        let end = try XCTUnwrap(remote["end"] as? Double) - 0.2
        let system = try rms(folder.appendingPathComponent("system.caf"), from: start, to: end)
        let echo = try rms(folder.appendingPathComponent("mic.caf"), from: start, to: end)
        XCTAssertGreaterThan(echo, 0.0005, "The remote voice leaks into the microphone")
        XCTAssertLessThan(echo, system * 0.3, "…at a much lower level")

        try run(["diarize", "--case", folder.path, "--fake-diarizer", "oracle", "--speakers", "2"])
        let result = try XCTUnwrap(try json(folder.appendingPathComponent(
            "result-step0.1-minSeg0-threshold0.775-zeroVoteon-n2.json")) as? [String: Any])
        XCTAssertEqual(result["hint"] as? String, "exact")
        XCTAssertEqual((result["tracks"] as? [[String: Any]])?.compactMap { $0["track"] as? String }, ["microphone", "system"])
        XCTAssertEqual(result["meLabel"] as? [String: String], ["expected": "A", "labelled": "A"])
        let pairs = try XCTUnwrap(result["echo"] as? [[String: Any]])
        XCTAssertEqual(pairs.count, 1)
        XCTAssertEqual(try XCTUnwrap(pairs.first?["cosine"] as? Double), 0, accuracy: 1e-9)
    }

    func testMissingModelStopsTheRunWithoutDownloading() throws {
        let folder = try synthesize(cowork, name: "tiny-cowork", condition: "clean")
        let models = bench.appendingPathComponent("models", isDirectory: true)
        let output = try run(["diarize", "--case", folder.path, "--models", models.path], expectSuccess: false)
        XCTAssertTrue(output.contains("не скачивает"), output)
        XCTAssertFalse(FileManager.default.fileExists(atPath: models.path), "Nothing may be created where models go")
        XCTAssertFalse(files(in: folder).contains { $0.hasPrefix("result-") })
    }

    func testMeetingRunsReadTheRecordingOnlyAndAlignTheTruth() throws {
        let folder = try synthesize(cowork, name: "tiny-cowork", condition: "clean")
        // A recording that starts 2.3 s before the played mix.
        let meeting = bench.appendingPathComponent("Meetings/2F1C", isDirectory: true)
        try FileManager.default.createDirectory(at: meeting, withIntermediateDirectories: true)
        let samples = [Float](repeating: 0, count: Int(2.3 * 16_000)) + (try AudioLoader.samples(from: folder.appendingPathComponent("mic.caf")))
        let format = try XCTUnwrap(AVAudioFormat(standardFormatWithSampleRate: 16_000, channels: 1))
        let buffer = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(samples.count)))
        samples.withUnsafeBufferPointer { source in
            if let base = source.baseAddress {
                buffer.floatChannelData?[0].update(from: base, count: samples.count)
            }
        }
        buffer.frameLength = AVAudioFrameCount(samples.count)
        do {
            let track = try AVAudioFile(forWriting: meeting.appendingPathComponent("mic.caf"), settings: format.settings,
                                        commonFormat: .pcmFormatFloat32, interleaved: false)
            try track.write(from: buffer)
        }
        let truth = bench.appendingPathComponent("truth.csv")
        try Data("start,end,name\n0:02.8,0:08.8,Я\n0:09.6,0:21.6,Аня\n".utf8).write(to: truth)
        let before = try snapshot(meeting)

        let aligned = try run(["diarize", "--meeting", meeting.path, "--case", folder.path, "--fake-diarizer", "oracle",
                               "--condition", "rerecorded-standard"])
        let offset = try XCTUnwrap(aligned.range(of: #"сдвинут на (-?\d+\.\d+) с"#, options: .regularExpression)
            .map { String(aligned[$0]) }?.split(separator: " ").dropFirst(2).first.flatMap { Double($0) }, aligned)
        XCTAssertEqual(offset, 2.3, accuracy: 0.11)
        let results = bench.appendingPathComponent("results/diarization-meetings/meeting-2F1C", isDirectory: true)
        let result = try XCTUnwrap(try json(results.appendingPathComponent(
            "result-step0.1-minSeg0-threshold0.775-zeroVoteon-auto.json")) as? [String: Any])
        XCTAssertEqual(result["condition"] as? String, "rerecorded-standard")
        XCTAssertLessThan(der(result), 0.1)

        try run(["diarize", "--meeting", meeting.path, "--truth", truth.path, "--fake-diarizer", "oracle"])
        XCTAssertEqual(try snapshot(meeting), before, "Meeting files are only read")
    }

    func testBadArgumentsFail() throws {
        let scenario = bench.appendingPathComponent("scenarios/tiny-cowork.json")
        try Data(cowork.utf8).write(to: scenario)
        let condition = try run(["diarize-synth", "--scenario", scenario.path, "--condition", "loud"], expectSuccess: false)
        XCTAssertTrue(condition.contains("Неизвестное условие"), condition)
        try run(["diarize-synth", "--scenario", scenario.path, "--condition", "call-speakers"], expectSuccess: false)
        let folder = try synthesize(cowork, name: "tiny-cowork", condition: "clean")
        try run(["diarize", "--case", folder.path, "--config", "speed=2", "--fake-diarizer", "oracle"], expectSuccess: false)
        let manual = try run(["diarize-synth", "--scenario", scenario.path, "--condition", "rerecorded-standard"])
        XCTAssertTrue(manual.contains("записывается вручную"), manual)
    }

    private func snapshot(_ folder: URL) throws -> [String: [String]] {
        var entries: [String: [String]] = [:]
        let keys: [URLResourceKey] = [.fileSizeKey, .contentModificationDateKey, .fileResourceIdentifierKey]
        for case let url as URL in FileManager.default.enumerator(at: folder, includingPropertiesForKeys: keys) ?? .init() {
            let values = try url.resourceValues(forKeys: Set(keys))
            entries[url.lastPathComponent] = ["\(values.fileSize ?? -1)", "\(values.contentModificationDate?.timeIntervalSince1970 ?? 0)",
                                              "\(String(describing: values.fileResourceIdentifier))"]
        }
        return entries
    }
}
