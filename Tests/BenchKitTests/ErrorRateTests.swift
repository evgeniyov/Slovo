import XCTest
@testable import BenchKit

final class ErrorRateTests: XCTestCase {
    func testNormalizationIgnoresCasePunctuationAndYo() {
        XCTAssertEqual(TextNormalizer.words("Всё, запушь в Main!"), ["все", "запушь", "в", "main"])
    }

    func testDottedNamesSplitIntoWords() {
        XCTAssertEqual(TextNormalizer.words("Обнови Package.swift"), ["обнови", "package", "swift"])
    }

    func testIdenticalTextHasNoErrors() {
        let counts = ErrorRate.words(reference: "Сделай коммит.", hypothesis: "сделай коммит")
        XCTAssertEqual(counts, ErrorCounts(errors: 0, referenceLength: 2))
    }

    func testSubstitutionDeletionAndInsertionEachCountOnce() {
        let counts = ErrorRate.words(
            reference: "сделай коммит и запушь в main",
            hypothesis: "сделай коммит и запуш main срочно"
        )
        XCTAssertEqual(counts, ErrorCounts(errors: 3, referenceLength: 6))
    }

    func testEmptyHypothesisIsAllDeletions() {
        XCTAssertEqual(ErrorRate.words(reference: "запушь в main", hypothesis: ""),
                       ErrorCounts(errors: 3, referenceLength: 3))
    }

    func testLatinTermsCountOnlyWhenWrittenInLatin() {
        let latin = ErrorRate.latinTerms(
            reference: "Деплой на Kubernetes, логи в GitHub Actions",
            hypothesis: "деплой на кубернетис логи в github actions"
        )
        XCTAssertEqual(latin.kept, 2)
        XCTAssertEqual(latin.total, 3)
    }

    func testRepeatedLatinTermNeedsEachOccurrence() {
        let latin = ErrorRate.latinTerms(reference: "API и ещё раз API", hypothesis: "API и ещё раз апи")
        XCTAssertEqual(latin.kept, 1)
        XCTAssertEqual(latin.total, 2)
    }

    func testSummaryPoolsErrorsAndCountsFailuresAsEmpty() {
        let run = EngineRun(engine: "test", date: Date(timeIntervalSince1970: 0), system: "", clips: [
            ClipResult(clip: "r001", text: "Сделай коммит.", audioSeconds: 2, processingSeconds: 0.2),
            ClipResult(clip: "r002", text: "", audioSeconds: 2, processingSeconds: 0.4, error: "failed"),
            ClipResult(clip: "f001", text: "без эталона", audioSeconds: 4, processingSeconds: 0.6)
        ])
        let summary = Report.summarize(run, references: ["r001": "Сделай коммит.", "r002": "Запушь в main."])

        XCTAssertEqual(summary.words, ErrorCounts(errors: 3, referenceLength: 5))
        XCTAssertEqual(summary.clipsScored, 2)
        XCTAssertEqual(summary.failedClips, 1)
        XCTAssertEqual(summary.punctuatedClips, 1)
        XCTAssertEqual(summary.medianSeconds, 0.4, accuracy: 1e-9)
        XCTAssertEqual(summary.realTimeFactor, 0.8 / 6, accuracy: 1e-9)
    }

    func testRunSurvivesJSONRoundTrip() throws {
        let run = EngineRun(engine: "apple-dictation", date: Date(timeIntervalSince1970: 1_000_000), system: "macOS",
                            clips: [ClipResult(clip: "r001", text: "Привет, Swift", audioSeconds: 1.5, processingSeconds: 0.1)])
        let decoded = try EngineRun.decode(run.encoded())
        XCTAssertEqual(decoded.clips.first?.text, "Привет, Swift")
        XCTAssertEqual(decoded.date, run.date)
    }
}
