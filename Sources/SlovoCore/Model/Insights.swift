import Foundation

/// Words dictated on one calendar day.
public struct DailyWordCount: Codable, Hashable, Sendable {
    /// Start of the day in the calendar used for the calculation.
    public var day: Date
    public var words: Int

    public init(day: Date, words: Int) {
        self.day = day
        self.words = words
    }
}

public struct CategoryShare: Codable, Hashable, Sendable {
    public var category: AppCategory
    public var words: Int
    /// From 0 to 1.
    public var fraction: Double

    public init(category: AppCategory, words: Int, fraction: Double) {
        self.category = category
        self.words = words
        self.fraction = fraction
    }
}

public struct AppUsage: Codable, Hashable, Sendable {
    public var app: AppIdentity
    public var words: Int
    public var dictations: Int

    public init(app: AppIdentity, words: Int, dictations: Int) {
        self.app = app
        self.words = words
        self.dictations = dictations
    }
}

/// Statistics for Home and Insights, derived from history.
public struct Insights: Codable, Hashable, Sendable {
    public var totalWords: Int
    public var dictationCount: Int
    public var wordsPerMinute: Double
    /// Dictionary replacements plus term corrections.
    public var fixesMade: Int
    /// Seconds saved against typing the same words at `typingWordsPerMinute`, never negative.
    public var timeSaved: TimeInterval
    /// Consecutive days with a dictation, ending today or yesterday.
    public var currentStreak: Int
    public var longestStreak: Int
    /// One element per day of the last 20 weeks, oldest first.
    public var dailyWords: [DailyWordCount]
    /// Every category in `AppCategory.allCases` order.
    public var categoryShares: [CategoryShare]
    /// Most used apps first.
    public var topApps: [AppUsage]

    /// The typing speed "time saved" compares with, the same figure the gauge shows.
    public static let typingWordsPerMinute: Double = 40

    public init(totalWords: Int = 0, dictationCount: Int = 0, wordsPerMinute: Double = 0, fixesMade: Int = 0,
                timeSaved: TimeInterval = 0, currentStreak: Int = 0, longestStreak: Int = 0, dailyWords: [DailyWordCount] = [],
                categoryShares: [CategoryShare] = [], topApps: [AppUsage] = []) {
        self.totalWords = totalWords
        self.dictationCount = dictationCount
        self.wordsPerMinute = wordsPerMinute
        self.fixesMade = fixesMade
        self.timeSaved = timeSaved
        self.currentStreak = currentStreak
        self.longestStreak = longestStreak
        self.dailyWords = dailyWords
        self.categoryShares = categoryShares
        self.topApps = topApps
    }

    public static let empty = Insights()
}
