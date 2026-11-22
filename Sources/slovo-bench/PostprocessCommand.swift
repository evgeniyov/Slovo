import BenchKit
import Foundation
import SlovoCore

/// `score --postprocess`: runs Slovo's dictionary stage over the saved transcripts and
/// compares accuracy before and after. Reads Bench/clips and Bench/results only.
enum PostprocessCommand {
    static func run(paths: BenchPaths) throws {
        let runs = try ScoreCommand.loadRuns(paths)
        guard !runs.isEmpty else {
            print("В \(paths.results.path) нет результатов. Сначала: swift run slovo-bench run --engine all")
            return
        }
        let references = try Clip.references(in: paths.clips)
        if references.isEmpty {
            print("Нет эталонных текстов (.txt рядом с записями), поэтому ошибки посчитать не из чего.")
        }
        let terms = try EngineCatalog.vocabulary(paths)

        // Only the dictionary stage: no snippets, no cleanup, no style, like the
        // Dictionary screen filled with Bench/vocabulary.txt.
        let pipeline = TextPipeline(stages: [DictionaryCorrector()])
        let context = TextPipelineContext(dictionary: terms.map { DictionaryItem.term($0) }, cleanup: .off)
        let transform: Postprocess.Transform = { text in
            let result = pipeline.process(text, context: context)
            return (result.text, result.fixesCount)
        }

        let comparisons = runs
            .map { Postprocess.compare($0, references: references, transform: transform) }
            .sorted { $0.after.words.rate < $1.after.words.rate }

        print("Словарь: \(terms.count) терминов из \(paths.vocabulary.path)\n")
        print("Точность до и после словаря\n\(Postprocess.table(comparisons))")

        let spoiled = Postprocess.changedReferences(references, transform: transform)
        if spoiled.isEmpty {
            print("\nЭталонные тексты словарь не меняет: ложных срабатываний на них нет.")
        } else {
            print("\nСловарь меняет эталонные тексты (ложные срабатывания):")
            for change in spoiled {
                print("  \(change.clip)  \(change.before)\n        → \(change.after)")
            }
        }

        print("\nИсправления по записям")
        for comparison in comparisons where !comparison.changes.isEmpty {
            print("\n\(comparison.engine)")
            for change in comparison.changes {
                print("  \(change.clip)  \(change.before)\n        → \(change.after)")
            }
        }
    }
}
