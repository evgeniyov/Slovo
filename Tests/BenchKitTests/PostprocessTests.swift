import XCTest
@testable import BenchKit

final class PostprocessTests: XCTestCase {
    private let run = EngineRun(engine: "e", date: Date(timeIntervalSince1970: 0), system: "", clips: [
        ClipResult(clip: "r001", text: "Деплой на кубернетес", audioSeconds: 2, processingSeconds: 0.1),
        ClipResult(clip: "r002", text: "Запушь в main", audioSeconds: 2, processingSeconds: 0.1)
    ])
    private let references = ["r001": "Деплой на Kubernetes", "r002": "Запушь в main"]

    private func fixKubernetes(_ text: String) -> (text: String, fixes: Int) {
        let fixed = text.replacingOccurrences(of: "кубернетес", with: "Kubernetes")
        return (fixed, fixed == text ? 0 : 1)
    }

    func testApplyTransformsEveryClipAndRecordsChanges() {
        let processed = Postprocess.apply(run, transform: fixKubernetes)
        XCTAssertEqual(processed.run.clips.map(\.text), ["Деплой на Kubernetes", "Запушь в main"])
        XCTAssertEqual(processed.fixes, 1)
        XCTAssertEqual(processed.changes, [ClipChange(clip: "r001", before: "Деплой на кубернетес", after: "Деплой на Kubernetes")])
    }

    func testCompareScoresBeforeAndAfter() {
        let comparison = Postprocess.compare(run, references: references, transform: fixKubernetes)
        XCTAssertEqual(comparison.before.words, ErrorCounts(errors: 1, referenceLength: 6))
        XCTAssertEqual(comparison.after.words, ErrorCounts(errors: 0, referenceLength: 6))
        XCTAssertEqual(comparison.before.latinKept, 1)
        XCTAssertEqual(comparison.after.latinKept, 2)
        XCTAssertEqual(comparison.fixes, 1)
    }

    func testChangedReferencesAreFalsePositives() {
        XCTAssertTrue(Postprocess.changedReferences(references, transform: fixKubernetes).isEmpty)
        let spoiled = Postprocess.changedReferences(references) { ($0.replacingOccurrences(of: "main", with: "Main"), 1) }
        XCTAssertEqual(spoiled, [ClipChange(clip: "r002", before: "Запушь в main", after: "Запушь в Main")])
    }

    func testTableShowsBothSides() {
        let table = Postprocess.table([Postprocess.compare(run, references: references, transform: fixKubernetes)])
        XCTAssertTrue(table.contains("| e | 16.7% | 0.0% |"), table)
        XCTAssertTrue(table.hasSuffix("| 1/2 | 2/2 | 1 |"), table)
    }
}
