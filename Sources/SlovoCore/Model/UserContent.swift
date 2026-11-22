import Foundation

/// One finished dictation, as stored in `history.json`.
public struct HistoryEntry: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    public var date: Date
    /// Text after the pipeline, as inserted.
    public var text: String
    /// Text exactly as the engine returned it.
    public var rawText: String
    public var engineID: EngineID
    public var languageMode: LanguageMode
    public var appBundleID: String?
    public var appName: String?
    public var category: AppCategory
    /// Recording length in seconds.
    public var duration: TimeInterval
    public var wordCount: Int
    /// Dictionary replacements plus term corrections applied to this dictation.
    public var fixesCount: Int
    /// Path relative to `AppDirectories.recordingsFolder`, such as
    /// `2026-09-15/<uuid>.wav`; nil when the audio was not kept or has been removed.
    public var audioFileName: String?

    public init(id: UUID = UUID(), date: Date, text: String, rawText: String, engineID: EngineID,
                languageMode: LanguageMode, appBundleID: String?, appName: String?, category: AppCategory,
                duration: TimeInterval, wordCount: Int, fixesCount: Int, audioFileName: String?) {
        self.id = id
        self.date = date
        self.text = text
        self.rawText = rawText
        self.engineID = engineID
        self.languageMode = languageMode
        self.appBundleID = appBundleID
        self.appName = appName
        self.category = category
        self.duration = duration
        self.wordCount = wordCount
        self.fixesCount = fixesCount
        self.audioFileName = audioFileName
    }

    /// Words split on whitespace, the count history entries and insights use.
    public static func countWords(in text: String) -> Int {
        text.split(whereSeparator: { $0.isWhitespace || $0.isNewline }).count
    }
}

/// A Dictionary row: either a term to spell correctly or an explicit replacement.
public struct DictionaryItem: Codable, Hashable, Sendable, Identifiable {
    public enum Kind: String, Codable, Sendable, CaseIterable {
        /// A word or phrase, usually English, that fuzzy correction restores.
        case term
        /// An explicit "from → to" rule.
        case replacement
    }

    public var id: UUID
    public var kind: Kind
    /// The term itself, or the "from" phrase of a replacement.
    public var text: String
    /// The "to" text of a replacement; empty for terms.
    public var replacement: String
    public var createdAt: Date

    public init(id: UUID = UUID(), kind: Kind, text: String, replacement: String = "", createdAt: Date = Date()) {
        self.id = id
        self.kind = kind
        self.text = text
        self.replacement = replacement
        self.createdAt = createdAt
    }

    public static func term(_ text: String, createdAt: Date = Date()) -> DictionaryItem {
        DictionaryItem(kind: .term, text: text, createdAt: createdAt)
    }

    public static func replacement(from: String, to: String, createdAt: Date = Date()) -> DictionaryItem {
        DictionaryItem(kind: .replacement, text: from, replacement: to, createdAt: createdAt)
    }
}

/// A trigger phrase that expands into longer text.
public struct Snippet: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    public var trigger: String
    public var expansion: String
    public var createdAt: Date
    public var updatedAt: Date

    public init(id: UUID = UUID(), trigger: String, expansion: String,
                createdAt: Date = Date(), updatedAt: Date? = nil) {
        self.id = id
        self.trigger = trigger
        self.expansion = expansion
        self.createdAt = createdAt
        self.updatedAt = updatedAt ?? createdAt
    }
}

/// A Scratchpad note.
public struct Note: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    public var text: String
    public var createdAt: Date
    public var updatedAt: Date

    public init(id: UUID = UUID(), text: String = "", createdAt: Date = Date(), updatedAt: Date? = nil) {
        self.id = id
        self.text = text
        self.createdAt = createdAt
        self.updatedAt = updatedAt ?? createdAt
    }

    /// The first non-empty line, trimmed; empty for an empty note, which the UI
    /// shows with a localized placeholder.
    public var title: String {
        text.split(whereSeparator: \.isNewline)
            .lazy
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .first { !$0.isEmpty } ?? ""
    }
}
