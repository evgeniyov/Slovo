import AVFoundation
import FluidAudio
import XCTest
@testable import SlovoCore

/// FluidAudio's output mapping, the scorer wrapper, the guard against model preparation
/// and a smoke test against a real model that runs only when one is provided.
final class DiarizationFluidTests: XCTestCase {
    private func segment(_ id: String, _ start: Float, _ end: Float) -> TimedSpeakerSegment {
        TimedSpeakerSegment(speakerId: id, embedding: [], startTimeSeconds: start, endTimeSeconds: end, qualityScore: 1)
    }

    // MARK: - Mapping

    func testSpeakerIDsBecomeZeroBasedClusters() {
        XCTAssertEqual(FluidSpeakerDiarizer.cluster(forSpeakerID: "S1"), 0)
        XCTAssertEqual(FluidSpeakerDiarizer.cluster(forSpeakerID: "S12"), 11)
        for id in ["S0", "S", "s1", "SPEAKER_01", "S+1", "S-1", "S1a", "", "１"] {
            XCTAssertNil(FluidSpeakerDiarizer.cluster(forSpeakerID: id), id)
        }
    }

    func testSegmentsAreMappedSortedAndUnknownIDsSkipped() {
        let segments = [segment("S2", 4, 6), segment("S1", 0, 3.5), segment("noise", 3.5, 4), segment("S3", 7, 7),
                        segment("S1", 6.5, 9)]
        XCTAssertEqual(FluidSpeakerDiarizer.turns(from: segments), [
            SpeakerTurn(start: 0, end: 3.5, cluster: 0),
            SpeakerTurn(start: 4, end: 6, cluster: 1),
            SpeakerTurn(start: 6.5, end: 9, cluster: 0)
        ])
    }

    // MARK: - Scoring

    func testPerfectHypothesisHasNoErrorWhateverItsLabels() {
        let reference = [DiarizationScoring.Segment(speaker: "A", start: 0, end: 5),
                         DiarizationScoring.Segment(speaker: "B", start: 5, end: 10)]
        let hypothesis = [DiarizationScoring.Segment(speaker: "0", start: 0, end: 5),
                          DiarizationScoring.Segment(speaker: "1", start: 5, end: 10)]
        let score = DiarizationScoring.der(reference: reference, hypothesis: hypothesis, collar: 0)
        XCTAssertEqual(score.der, 0, accuracy: 1e-9)
        XCTAssertEqual(score.referenceSpeechSeconds, 10, accuracy: 0.02)
        XCTAssertEqual(score.mapping, ["0": "A", "1": "B"])
    }

    func testMissFalseAlarmAndConfusionAreSeparated() {
        let reference = [DiarizationScoring.Segment(speaker: "A", start: 0, end: 10),
                         DiarizationScoring.Segment(speaker: "B", start: 10, end: 20)]
        // 2 s of B given to A, 1 s of B missed, 1 s of silence claimed.
        let hypothesis = [DiarizationScoring.Segment(speaker: "x", start: 0, end: 12),
                          DiarizationScoring.Segment(speaker: "y", start: 12, end: 19),
                          DiarizationScoring.Segment(speaker: "y", start: 20, end: 21)]
        let score = DiarizationScoring.der(reference: reference, hypothesis: hypothesis, collar: 0)
        XCTAssertEqual(score.confusion, 2, accuracy: 0.02)
        XCTAssertEqual(score.miss, 1, accuracy: 0.02)
        XCTAssertEqual(score.falseAlarm, 1, accuracy: 0.02)
        XCTAssertEqual(score.der, 4.0 / 20.0, accuracy: 0.002)
        XCTAssertEqual(score.mapping, ["x": "A", "y": "B"])
    }

    func testCollarForgivesBoundaryErrors() {
        let reference = [DiarizationScoring.Segment(speaker: "A", start: 0, end: 10),
                         DiarizationScoring.Segment(speaker: "B", start: 10, end: 20)]
        let hypothesis = [DiarizationScoring.Segment(speaker: "x", start: 0, end: 10.1),
                          DiarizationScoring.Segment(speaker: "y", start: 10.1, end: 20)]
        let strict = DiarizationScoring.der(reference: reference, hypothesis: hypothesis, collar: 0)
        let lenient = DiarizationScoring.der(reference: reference, hypothesis: hypothesis, collar: 0.25)
        XCTAssertGreaterThan(strict.der, 0.004)
        XCTAssertEqual(lenient.der, 0, accuracy: 1e-9)
        XCTAssertLessThan(lenient.referenceSpeechSeconds, strict.referenceSpeechSeconds)
    }

    func testInvalidInputDoesNotTrap() {
        let reference = [DiarizationScoring.Segment(speaker: "A", start: -1, end: 2),
                         DiarizationScoring.Segment(speaker: "A", start: .nan, end: 3),
                         DiarizationScoring.Segment(speaker: "B", start: 5, end: 4)]
        let score = DiarizationScoring.der(reference: reference, hypothesis: [], collar: -1)
        XCTAssertEqual(score.miss, 2, accuracy: 0.02)
        XCTAssertEqual(score.der, 1, accuracy: 0.01)
        XCTAssertEqual(DiarizationScoring.der(reference: [], hypothesis: [], collar: 0.25).der, 0)
    }

    // MARK: - Guard

    /// Model preparation purges the model folder and downloads again; Slovo loads only
    /// through the offline loader.
    func testNoSourceCallsModelPreparation() throws {
        let sources = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Sources", isDirectory: true)
        let forbidden = "prepare" + "Models("
        var scanned = 0
        var offenders: [String] = []
        let files = FileManager.default.enumerator(at: sources, includingPropertiesForKeys: nil)
        while let file = files?.nextObject() as? URL {
            guard file.pathExtension == "swift" else { continue }
            scanned += 1
            if try String(contentsOf: file, encoding: .utf8).contains(forbidden) {
                offenders.append(file.lastPathComponent)
            }
        }
        XCTAssertGreaterThan(scanned, 50, "Sources not found at \(sources.path)")
        XCTAssertEqual(offenders, [])
    }

    // MARK: - Real model

    /// Runs only when `SLOVO_DIARIZATION_MODELS` names a downloaded model: the
    /// `diarization` folder or the `speaker-diarization` folder FluidAudio keeps inside it.
    func testRealModelSeparatesTwoVoices() async throws {
        guard let path = ProcessInfo.processInfo.environment["SLOVO_DIARIZATION_MODELS"], !path.isEmpty else {
            throw XCTSkip("Set SLOVO_DIARIZATION_MODELS to a downloaded speaker model folder to run this test")
        }
        var modelFolder = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
        if ["speaker-diarization", "speaker-diarization-coreml"].contains(modelFolder.lastPathComponent) {
            modelFolder.deleteLastPathComponent()
        }
        guard DiarizationModelFiles(modelFolder: modelFolder).isComplete else {
            throw XCTSkip("No complete speaker model in \(modelFolder.path)")
        }
        let voices = ["com.apple.voice.compact.ru-RU.Milena", "com.apple.ttsbundle.gryphon-neural_Yelena_ru-RU_premium"]
            .compactMap(AVSpeechSynthesisVoice.init(identifier:))
        guard voices.count == 2 else {
            throw XCTSkip("The Milena and Yelena voices are needed to render two speakers")
        }

        let folder = try DiarizationFixtures.temporaryFolder("SlovoDiarizationSmoke")
        defer { try? FileManager.default.removeItem(at: folder) }
        let lines = [
            "Коллеги, давайте начнём с релиза. Сборка готова, осталось проверить установку на чистой системе.",
            "Хорошо, я возьму проверку на себя и напишу в общий чат, когда закончу.",
            "Отлично. Тогда следующий вопрос: кто подготовит заметки к выпуску до пятницы?",
            "Могу я, но мне нужен список изменений от команды интерфейса до среды."
        ]
        var samples: [Float] = []
        for (index, line) in lines.enumerated() {
            samples += try await Self.speak(line, voice: voices[index % 2])
            samples += [Float](repeating: 0, count: 8_000)
        }
        let url = folder.appendingPathComponent("two-voices.wav")
        try WAVFile.write(RecordedAudio(samples: samples, sampleRate: 16_000), to: url)

        let offline = ModelHub.offlineMode
        let diarizer = try await FluidSpeakerDiarizer.load(modelFolder: modelFolder)
        XCTAssertEqual(ModelHub.offlineMode, offline)
        let progress = DictationFakes.Locked<Double>(0)
        let turns = try await diarizer.diarize(fileAt: url, hint: .automatic) { progress.value = max(progress.value, $0) }
        XCTAssertEqual(ModelHub.offlineMode, offline)
        XCTAssertEqual(progress.value, 1)
        XCTAssertGreaterThanOrEqual(Set(turns.map(\.cluster)).count, 2, "\(turns)")
    }

    /// Renders one line with AVSpeechSynthesizer as 16 kHz mono samples.
    private static func speak(_ text: String, voice: AVSpeechSynthesisVoice) async throws -> [Float] {
        let synthesizer = AVSpeechSynthesizer()
        let utterance = AVSpeechUtterance(string: text)
        utterance.voice = voice
        let collected = DictationFakes.Locked<[AVAudioPCMBuffer]>([])
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            let finished = DictationFakes.Locked(false)
            synthesizer.write(utterance) { buffer in
                guard let pcm = buffer as? AVAudioPCMBuffer else { return }
                if pcm.frameLength == 0 {
                    if !finished.value {
                        finished.value = true
                        continuation.resume()
                    }
                } else {
                    collected.value.append(pcm)
                }
            }
        }
        let folder = try DiarizationFixtures.temporaryFolder("SlovoSpeech")
        defer { try? FileManager.default.removeItem(at: folder) }
        guard let format = collected.value.first?.format else { return [] }
        let url = folder.appendingPathComponent("line.caf")
        do {
            let file = try AVAudioFile(forWriting: url, settings: format.settings, commonFormat: format.commonFormat,
                                       interleaved: format.isInterleaved)
            for buffer in collected.value {
                try file.write(from: buffer)
            }
        }
        return try AudioLoader.samples(from: url)
    }
}
