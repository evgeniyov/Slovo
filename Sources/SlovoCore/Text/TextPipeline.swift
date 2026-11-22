import Foundation

/// One step of the text pipeline. Stages update the result in place, so adding a
/// stage (for example a local LLM rewrite) needs no change to the others.
public protocol TextStage: Sendable {
    func apply(to result: inout TextPipelineResult, context: TextPipelineContext)
}

/// Turns an engine transcript into the text Slovo inserts. Pure and synchronous.
public struct TextPipeline: TextProcessing {
    public let stages: [any TextStage]

    public init(stages: [any TextStage] = TextPipeline.standardStages) {
        self.stages = stages
    }

    /// Snippets, dictionary replacements and terms, auto cleanup, style — in that order.
    public static var standardStages: [any TextStage] {
        standardStages(lexicon: .system)
    }

    /// The standard stages asking `lexicon` instead of the system spell checker, so the
    /// output depends on nothing outside the call (tests and the shared fixtures).
    static func standardStages(lexicon: WordLexicon) -> [any TextStage] {
        [SnippetExpander(), DictionaryCorrector(lexicon: lexicon), FillerCleaner(), StyleFormatter(lexicon: lexicon)]
    }

    public func process(_ raw: String, context: TextPipelineContext) -> TextPipelineResult {
        var result = TextPipelineResult(text: raw)
        for stage in stages {
            stage.apply(to: &result, context: context)
        }
        return result
    }
}
