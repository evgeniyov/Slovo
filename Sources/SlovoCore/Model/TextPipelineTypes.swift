import Foundation

/// Everything the text pipeline needs besides the raw transcript.
public struct TextPipelineContext: Hashable, Sendable {
    public var dictionary: [DictionaryItem]
    public var snippets: [Snippet]
    public var cleanup: AutoCleanupOptions
    /// Nil leaves capitalization and punctuation as the engine wrote them.
    public var style: StylePreset?
    public var languageMode: LanguageMode

    public init(dictionary: [DictionaryItem] = [], snippets: [Snippet] = [], cleanup: AutoCleanupOptions = .default,
                style: StylePreset? = nil, languageMode: LanguageMode = .mixed) {
        self.dictionary = dictionary
        self.snippets = snippets
        self.cleanup = cleanup
        self.style = style
        self.languageMode = languageMode
    }
}

/// The pipeline's output and what it changed. Stages update it in place, so a new
/// stage only adds to it.
public struct TextPipelineResult: Hashable, Sendable {
    public var text: String
    public var snippetsApplied: Int
    public var replacementsApplied: Int
    public var termFixesApplied: Int

    public init(text: String, snippetsApplied: Int = 0, replacementsApplied: Int = 0, termFixesApplied: Int = 0) {
        self.text = text
        self.snippetsApplied = snippetsApplied
        self.replacementsApplied = replacementsApplied
        self.termFixesApplied = termFixesApplied
    }

    /// The per-dictation "fixes" figure: dictionary replacements plus term corrections.
    public var fixesCount: Int {
        replacementsApplied + termFixesApplied
    }
}
