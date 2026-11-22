import BenchKit
import Foundation

enum ScoreCommand {
    /// Every saved engine run in Bench/results, sorted by file name.
    static func loadRuns(_ paths: BenchPaths) throws -> [EngineRun] {
        let files = (try? FileManager.default.contentsOfDirectory(at: paths.results, includingPropertiesForKeys: nil)) ?? []
        return try files
            .filter { $0.pathExtension == "json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
            .map { try EngineRun.decode(Data(contentsOf: $0)) }
    }

    static func run(paths: BenchPaths) throws {
        let references = try Clip.references(in: paths.clips)
        let runs = try loadRuns(paths)

        guard !runs.isEmpty else {
            print("В \(paths.results.path) нет результатов. Сначала: swift run slovo-bench run --engine all")
            return
        }
        if references.isEmpty {
            print("Нет эталонных текстов (.txt рядом с записями), поэтому ошибки посчитать не из чего.")
        }

        let summaries = runs
            .map { Report.summarize($0, references: references) }
            .sorted { $0.words.rate < $1.words.rate }
        let accuracy = Report.accuracyTable(summaries)
        let speed = Report.speedTable(summaries)
        let load = Report.loadTable(summaries)
        let document = """
            # Сравнение движков

            ## Точность

            \(accuracy)

            ## Скорость

            \(speed)

            ## Нагрузка

            \(load)

            ## Как читать

            \(Report.legend)

            ## По записям

            \(Report.details(runs: runs, references: references))
            """
        let url = paths.results.appendingPathComponent("report.md")
        try document.write(to: url, atomically: true, encoding: .utf8)

        print("Точность\n\(accuracy)\n\nСкорость\n\(speed)\n\nНагрузка\n\(load)")
        print("\nПояснения к столбцам и разбор по каждой записи: \(url.path)")
    }
}
