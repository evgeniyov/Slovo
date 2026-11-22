import XCTest
@testable import BenchKit

final class DiarizationMetricsTests: XCTestCase {
    private func turn(_ speaker: String, _ start: Double, _ end: Double, _ text: String = "",
                      track: String = "microphone") -> ReferenceTurn {
        ReferenceTurn(speaker: speaker, track: track, start: start, end: end, text: text)
    }

    private func segment(_ speaker: String, _ start: Double, _ end: Double) -> SpeakerSegment {
        SpeakerSegment(speaker: speaker, start: start, end: end)
    }

    // MARK: - RTTM and CSV

    func testRTTMRoundTrip() throws {
        let microphone = [segment("B", 3.25, 7.5), segment("A", 0.5, 3.125), segment("Speaker two", 8, 8.4)]
        let text = RTTM.write(microphone, file: "mic") + RTTM.write([segment("F", 1, 2.75)], file: "system")
        XCTAssertTrue(text.hasPrefix("SPEAKER mic 1 0.500 2.625 <NA> <NA> A <NA> <NA>\n"))
        let parsed = try RTTM.parse(";; comment\n\n" + text + "SPKR-INFO mic 1 <NA> <NA> <NA> unknown A <NA> <NA>\n")
        XCTAssertEqual(parsed["mic"], [segment("A", 0.5, 3.125), segment("B", 3.25, 7.5), segment("Speaker_two", 8, 8.4)])
        XCTAssertEqual(parsed["system"], [segment("F", 1, 2.75)])
        XCTAssertEqual(RTTM.write(parsed["mic"] ?? [], file: "mic"), RTTM.write(microphone, file: "mic")
            .replacingOccurrences(of: "Speaker two", with: "Speaker_two"))
    }

    func testMalformedRTTMLineThrows() {
        XCTAssertThrowsError(try RTTM.parse("SPEAKER mic 1 abc 2 <NA> <NA> A <NA> <NA>"))
        XCTAssertThrowsError(try RTTM.parse("SPEAKER mic 1 1.0"))
        XCTAssertEqual(try RTTM.parse("").count, 0)
    }

    func testTruthCSVReadsSecondsAndClockTimes() throws {
        let csv = """
            start,end,name
            # the host opens
            0,12.5,Я
            0:13,1:02,Аня, продакт
            1:00:00,1:00:04.5,Спикер 3
            """
        XCTAssertEqual(try TruthCSV.parse(csv), [
            segment("Я", 0, 12.5), segment("Аня, продакт", 13, 62), segment("Спикер 3", 3_600, 3_604.5)
        ])
        XCTAssertNil(TruthCSV.time("1:75"))
        XCTAssertNil(TruthCSV.time("a:10"))
        XCTAssertThrowsError(try TruthCSV.parse("5,4,Я"))
        XCTAssertThrowsError(try TruthCSV.parse("0,4"))
    }

    // MARK: - Attribution

    func testTurnLengthBuckets() {
        XCTAssertEqual(TurnLength(seconds: 0.4), .underOneSecond)
        XCTAssertEqual(TurnLength(seconds: 1), .oneToThreeSeconds)
        XCTAssertEqual(TurnLength(seconds: 3), .threeToTenSeconds)
        XCTAssertEqual(TurnLength(seconds: 10), .threeToTenSeconds)
        XCTAssertEqual(TurnLength(seconds: 10.5), .overTenSeconds)
    }

    func testAttributionCountsTurnsByLength() {
        let reference = [turn("A", 0, 12), turn("B", 12.5, 13.2), turn("A", 14, 16), turn("B", 17, 22), turn("C", 23, 26)]
        let hypothesis = [segment("S1", 0, 13.2), segment("S2", 14, 15.5), segment("S2", 17, 22), segment("S3", 23, 24)]
        let mapping = ["S1": "A", "S2": "B"]
        let result = DiarizationMetrics.attribution(reference: reference, hypothesis: hypothesis, mapping: mapping)
        XCTAssertEqual(result[.overTenSeconds], Tally(hits: 1, total: 1))
        XCTAssertEqual(result[.underOneSecond], Tally(hits: 0, total: 1), "S1 maps to A, not B")
        XCTAssertEqual(result[.oneToThreeSeconds], Tally(hits: 0, total: 1), "S2 maps to B, not A")
        XCTAssertEqual(result[.threeToTenSeconds], Tally(hits: 1, total: 2), "S3 is unmapped and covers only a third")
        XCTAssertEqual(result.threeSecondsOrLonger, Tally(hits: 2, total: 3))
        XCTAssertEqual(result.all, Tally(hits: 2, total: 5))
        XCTAssertEqual((result + result).all, Tally(hits: 4, total: 10))
    }

    func testShortTurnRecallAndAbsorptionByTheFirstCluster() {
        let reference = [turn("A", 0, 10), turn("B", 10.2, 10.8), turn("C", 11, 11.6), turn("B", 12, 12.7),
                         turn("C", 20, 20.5)]
        let hypothesis = [segment("S1", 0, 10.8), segment("S3", 11, 11.6), segment("S2", 12, 12.7)]
        let result = DiarizationMetrics.shortTurns(reference: reference, hypothesis: hypothesis,
                                                   mapping: ["S1": "A", "S2": "B", "S3": "C"], firstCluster: "S1")
        XCTAssertEqual(result.total, 4)
        XCTAssertEqual(result.recalled, 2)
        XCTAssertEqual(result.absorbedByFirstCluster, 1)
        XCTAssertEqual(result.recall, 0.5)
        XCTAssertEqual(result.absorbedShare, 0.25)
        XCTAssertNil(ShortTurnResult().recall)
    }

    func testSpeakerCountError() {
        let count = DiarizationMetrics.speakerCount(reference: [segment("A", 0, 1), segment("B", 1, 2), segment("A", 3, 4)],
                                                    hypothesis: [segment("S1", 0, 2), segment("S2", 3, 4), segment("S3", 5, 6),
                                                                 segment("S4", 7, 7)])
        XCTAssertEqual(count, SpeakerCountResult(reference: 2, detected: 3))
        XCTAssertEqual(count.error, 1)
        XCTAssertFalse(count.isExact)
    }

    func testOverlapMappingIsOptimalNotGreedy() {
        // Greedy would pair S1 with A (10 s) and leave S2 with nothing; the best total is 18 s.
        let reference = [segment("A", 0, 10), segment("B", 10, 19), segment("A", 30, 39)]
        let hypothesis = [segment("S1", 0, 10), segment("S1", 10, 19), segment("S2", 30, 39), segment("S3", 50, 51)]
        XCTAssertEqual(DiarizationMetrics.overlapMapping(reference: reference, hypothesis: hypothesis),
                       ["S1": "B", "S2": "A"])
        XCTAssertEqual(DiarizationMetrics.overlapMapping(reference: [], hypothesis: hypothesis), [:])
    }

    func testAssignmentMatchesBruteForce() {
        var generator = SystemRandomNumberGenerator()
        for _ in 0..<50 {
            let rows = Int.random(in: 1...4, using: &generator)
            let columns = Int.random(in: 1...4, using: &generator)
            let score = (0..<rows).map { _ in (0..<columns).map { _ in Double(Int.random(in: 0...9, using: &generator)) } }
            let pairs = Assignment.maximizing(score)
            XCTAssertEqual(Set(pairs.map(\.row)).count, pairs.count)
            XCTAssertEqual(Set(pairs.map(\.column)).count, pairs.count)
            XCTAssertEqual(pairs.reduce(0) { $0 + score[$1.row][$1.column] }, bestTotal(score), "\(score)")
        }
    }

    private func bestTotal(_ score: [[Double]], row: Int = 0, used: Set<Int> = []) -> Double {
        guard row < score.count else { return 0 }
        var best = bestTotal(score, row: row + 1, used: used)
        for column in score[row].indices where !used.contains(column) {
            best = max(best, score[row][column] + bestTotal(score, row: row + 1, used: used.union([column])))
        }
        return best
    }

    // MARK: - Transcripts

    func testSpeakerAttributedWERWithAGivenMapping() {
        let reference = [turn("A", 0, 3, "начнём с релиза"), turn("B", 3, 5, "согласна"), turn("A", 6, 9, "потом задачи")]
        let perfect = [AttributedText(speaker: "s1", start: 0, end: 3, text: "Начнём с релиза."),
                       AttributedText(speaker: "s2", start: 3, end: 5, text: "Согласна!"),
                       AttributedText(speaker: "s1", start: 6, end: 9, text: "потом задачи")]
        let mapping = ["s1": "A", "s2": "B"]
        XCTAssertEqual(DiarizationMetrics.speakerAttributedWER(reference: reference, hypothesis: perfect, mapping: mapping),
                       ErrorCounts(errors: 0, referenceLength: 6))

        // The right words under the wrong speaker: B's word is deleted from B and inserted into A.
        var swapped = perfect
        swapped[1].speaker = "s1"
        XCTAssertEqual(DiarizationMetrics.speakerAttributedWER(reference: reference, hypothesis: swapped, mapping: mapping),
                       ErrorCounts(errors: 2, referenceLength: 6))
        XCTAssertEqual(DiarizationMetrics.plainWER(reference: reference, hypothesis: swapped),
                       ErrorCounts(errors: 0, referenceLength: 6))

        // An unmapped speaker's words are all insertions.
        var unmapped = perfect
        unmapped[1].speaker = "s9"
        XCTAssertEqual(DiarizationMetrics.speakerAttributedWER(reference: reference, hypothesis: unmapped, mapping: mapping),
                       ErrorCounts(errors: 2, referenceLength: 6))
    }

    func testWordsCutAtClipBoundaries() {
        // Four words over 4 s: one word per second.
        let reference = [turn("A", 10, 14, "раз два три четыре")]
        XCTAssertEqual(DiarizationMetrics.wordsCutAtClipBoundaries(reference: reference, clips: [segment("s1", 10, 14)]),
                       Tally(hits: 0, total: 4))
        XCTAssertEqual(DiarizationMetrics.wordsCutAtClipBoundaries(reference: reference,
                                                                   clips: [segment("s1", 9, 11.5), segment("s1", 11.5, 12.02)]),
                       Tally(hits: 1, total: 4), "A boundary at 12.02 is within the margin of a word edge")
    }

    func testAutomaticMeNeedsEightyPercent() {
        XCTAssertEqual(DiarizationMetrics.automaticMe(microphone: [segment("S1", 0, 8), segment("S2", 8, 10)]), "S1")
        XCTAssertNil(DiarizationMetrics.automaticMe(microphone: [segment("S1", 0, 7), segment("S2", 7, 10)]))
        XCTAssertNil(DiarizationMetrics.automaticMe(microphone: []))
    }

    func testCosineSimilarity() throws {
        XCTAssertEqual(try XCTUnwrap(DiarizationMetrics.cosineSimilarity([1, 0], [1, 0])), 1, accuracy: 1e-9)
        XCTAssertEqual(try XCTUnwrap(DiarizationMetrics.cosineSimilarity([1, 0], [0, 2])), 0, accuracy: 1e-9)
        XCTAssertNil(DiarizationMetrics.cosineSimilarity([1], [1, 2]))
        XCTAssertNil(DiarizationMetrics.cosineSimilarity([0, 0], [1, 2]))
    }

    // MARK: - Results and report

    func testPooledDERWeighsByReferenceSpeech() {
        let pooled = DERBreakdown.pooled([DERBreakdown(miss: 1, falseAlarm: 0, confusion: 1, referenceSeconds: 10),
                                          DERBreakdown(miss: 0, falseAlarm: 3, confusion: 1, referenceSeconds: 30)])
        XCTAssertEqual(pooled.der, 6.0 / 40.0, accuracy: 1e-12)
        XCTAssertEqual(DERBreakdown(miss: 0, falseAlarm: 1, confusion: 0, referenceSeconds: 0).der, 0)
    }

    private func result(_ configuration: String, hint: String = "automatic", condition: String, scenario: String = "cowork-4",
                        der: Double, detected: Int = 4, seconds: Double = 10) -> DiarizationCaseResult {
        let breakdown = DERBreakdown(miss: der * 100, falseAlarm: 0, confusion: 0, referenceSeconds: 100)
        let track = TrackResult(track: "microphone", audioSeconds: 600, der: breakdown, derStrict: breakdown, derRaw: breakdown,
                                speakers: SpeakerCountResult(reference: 4, detected: detected), mapping: ["S1": "A"],
                                wallSeconds: seconds)
        return DiarizationCaseResult(caseName: "\(scenario)-\(condition)", scenario: scenario, condition: condition,
                                     configuration: configuration, hint: hint, diarizer: "fake-oracle",
                                     date: Date(timeIntervalSince1970: 1_800_000_000), system: "macOS", collar: 0.25,
                                     tracks: [track],
                                     attribution: AttributionResult(byLength: ["3–10 s": Tally(hits: 9, total: 10)]),
                                     shortTurns: ShortTurnResult(total: 4, recalled: 3, absorbedByFirstCluster: 1),
                                     meLabel: nil, transcription: nil, echo: [], modelLoadSeconds: 2,
                                     diarizationSeconds: seconds, peakFootprintMB: 300, peakResidentMB: 400)
    }

    func testCaseResultSurvivesJSON() throws {
        var original = result("step=0.1", condition: "clean", der: 0.05)
        original.meLabel = MeLabelResult(expected: "A", labelled: "A")
        original.echo = [EchoPair(microphoneCluster: "S2", systemCluster: "S1", microphoneSpeaker: "B", systemSpeaker: "B",
                                  cosine: 0.8)]
        original.transcription = TranscriptionResult(engine: "gigaam-v3-ctc",
                                                     attributedWER: WordErrors(ErrorCounts(errors: 3, referenceLength: 50)),
                                                     plainWER: WordErrors(ErrorCounts(errors: 2, referenceLength: 50)),
                                                     cutWords: Tally(hits: 1, total: 50))
        let decoded = try DiarizationCaseResult.decode(original.encoded())
        XCTAssertEqual(decoded, original)
        XCTAssertEqual(decoded.realTimeMultiple, 60)
        XCTAssertTrue(decoded.meLabel?.isCorrect == true)
        XCTAssertTrue(decoded.echo.first?.isSameVoice == true)
    }

    func testRankingSortsByNoisyThenCleanThenSpeed() {
        let results = [
            result("slow-good", condition: "far-noisy-20", der: 0.10, seconds: 20),
            result("slow-good", condition: "clean", der: 0.04, seconds: 20),
            result("fast-good", condition: "far-noisy-20", der: 0.10, seconds: 5),
            result("fast-good", condition: "clean", der: 0.04, seconds: 5),
            result("clean-best", condition: "far-noisy-20", der: 0.10, seconds: 1),
            result("clean-best", condition: "clean", der: 0.02, seconds: 30),
            result("noisy-bad", condition: "far-noisy-20", der: 0.30, seconds: 1),
            result("noisy-bad", condition: "clean", der: 0.01, seconds: 1)
        ]
        let ranking = DiarizationReport.rank(results)
        XCTAssertEqual(ranking.map(\.configuration), ["clean-best", "fast-good", "slow-good", "noisy-bad"])
        XCTAssertEqual(ranking.first?.cases, 2)
        let table = DiarizationReport.rankingTable(ranking)
        XCTAssertTrue(table.contains("| DER clean | DER far-noisy-20 |"), table)
        XCTAssertEqual(table.split(separator: "\n").count, 6)
        XCTAssertTrue(DiarizationReport.caseTable(results).contains("fake") == false)
    }

    func testGateReportsPassedFailedAndUnmeasuredBars() {
        let results = [
            result("c", condition: "clean", der: 0.08),
            result("c", condition: "far", der: 0.25),
            result("c", condition: "far-noisy-20", der: 0.15, detected: 5),
            result("c", hint: "exact", condition: "far-noisy-20", der: 0.12),
            result("other", condition: "clean", der: 0.5)
        ]
        let checks = DiarizationReport.gate(configuration: "c", results: results)
        func passed(_ prefix: String) -> Bool?? {
            checks.first { $0.bar.hasPrefix(prefix) }.map(\.passed)
        }
        XCTAssertEqual(passed("DER clean"), .some(true))
        XCTAssertEqual(passed("DER far ≤"), .some(false))
        XCTAssertEqual(passed("DER far-noisy-20"), .some(true))
        XCTAssertEqual(passed("DER rerecorded-standard"), .some(nil))
        XCTAssertEqual(passed("Число спикеров без подсказки"), .some(false), "2 of 3 cowork cases is under 80%")
        XCTAssertEqual(passed("Число спикеров с подсказкой"), .some(true))
        XCTAssertEqual(passed("Реплики ≥ 3 с"), .some(true))
        XCTAssertEqual(passed("WER по спикерам"), .some(nil))
        XCTAssertEqual(passed("Час записи"), .some(nil))
        XCTAssertTrue(DiarizationReport.gateTable(checks).contains("| не измерено | — |"))
    }
}
