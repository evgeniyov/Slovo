import Foundation

/// A saved transcript before and after a text post-processing step.
public struct ClipChange: Equatable, Sendable {
    public let clip: String
    public let before: String
    public let after: String

    public init(clip: String, before: String, after: String) {
        self.clip = clip
        self.before = before
        self.after = after
    }
}

/// One engine's accuracy before and after a post-processing step.
public struct PostprocessComparison: Sendable {
    public let engine: String
    public let before: EngineSummary
    public let after: EngineSummary
    /// Corrections the step reported over all clips.
    public let fixes: Int
    public let changes: [ClipChange]
}

/// Scores a text step, such as Slovo's dictionary correction, on transcripts that are
/// already saved, so tuning it needs no new engine runs.
public enum Postprocess {
    public typealias Transform = (String) -> (text: String, fixes: Int)

    /// The run with every clip's text passed through `transform`.
    public static func apply(_ run: EngineRun, transform: Transform) -> (run: EngineRun, fixes: Int, changes: [ClipChange]) {
        var processed = run
        var fixes = 0
        var changes: [ClipChange] = []
        for index in processed.clips.indices {
            let before = processed.clips[index].text
            let result = transform(before)
            processed.clips[index].text = result.text
            fixes += result.fixes
            if result.text != before {
                changes.append(ClipChange(clip: processed.clips[index].clip, before: before, after: result.text))
            }
        }
        return (processed, fixes, changes)
    }

    public static func compare(_ run: EngineRun, references: [String: String], transform: Transform) -> PostprocessComparison {
        let processed = apply(run, transform: transform)
        return PostprocessComparison(engine: run.engine,
                                     before: Report.summarize(run, references: references),
                                     after: Report.summarize(processed.run, references: references),
                                     fixes: processed.fixes,
                                     changes: processed.changes)
    }

    /// References the step would change. A reference is already correct, so every
    /// change is a false positive.
    public static func changedReferences(_ references: [String: String], transform: Transform) -> [ClipChange] {
        references.keys.sorted().compactMap { clip in
            guard let reference = references[clip] else { return nil }
            let after = transform(reference).text
            return after == reference ? nil : ClipChange(clip: clip, before: reference, after: after)
        }
    }

    public static func table(_ comparisons: [PostprocessComparison]) -> String {
        var lines = [
            "| Движок | WER до | WER после | CER до | CER после | Англ. термины латиницей до | после | Исправлений |",
            "|---|---:|---:|---:|---:|---:|---:|---:|"
        ]
        for comparison in comparisons {
            let before = comparison.before
            let after = comparison.after
            let cells = [
                comparison.engine,
                Report.percent(before.words.rate),
                Report.percent(after.words.rate),
                Report.percent(before.characters.rate),
                Report.percent(after.characters.rate),
                "\(before.latinKept)/\(before.latinTotal)",
                "\(after.latinKept)/\(after.latinTotal)",
                "\(comparison.fixes)"
            ]
            lines.append("| " + cells.joined(separator: " | ") + " |")
        }
        return lines.joined(separator: "\n")
    }
}
