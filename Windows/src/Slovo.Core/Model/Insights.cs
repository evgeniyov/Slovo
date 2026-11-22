namespace Slovo.Core.Model;

public sealed record DailyWordCount(DateTimeOffset Day, int Words);

public sealed record CategoryShare(AppCategory Category, int Words, double Fraction);

public sealed record AppUsage(AppIdentity App, int Words, int Dictations);

/// <summary>Statistics for Home and Insights, derived from history.</summary>
public sealed record Insights
{
    public int TotalWords { get; init; }
    public int DictationCount { get; init; }
    public double WordsPerMinute { get; init; }
    public int FixesMade { get; init; }
    /// <summary>Seconds saved against typing the same words at <see cref="TypingWordsPerMinute"/>, never negative.</summary>
    public double TimeSaved { get; init; }
    public int CurrentStreak { get; init; }
    public int LongestStreak { get; init; }
    /// <summary>One element per day of the last 20 weeks, oldest first.</summary>
    public IReadOnlyList<DailyWordCount> DailyWords { get; init; } = [];
    public IReadOnlyList<CategoryShare> CategoryShares { get; init; } = [];
    public IReadOnlyList<AppUsage> TopApps { get; init; } = [];

    public static readonly Insights Empty = new();

    /// <summary>The typing speed "time saved" compares with, the same figure the gauge shows.</summary>
    public const double TypingWordsPerMinute = 40;
}

/// <summary>Everything the text pipeline needs besides the raw transcript.</summary>
public sealed record TextPipelineContext
{
    public IReadOnlyList<DictionaryItem> Dictionary { get; init; } = [];
    public IReadOnlyList<Snippet> Snippets { get; init; } = [];
    public AutoCleanupOptions Cleanup { get; init; } = AutoCleanupOptions.Default;
    /// <summary>Null leaves capitalization and punctuation as the engine wrote them.</summary>
    public StylePreset? Style { get; init; }
    public LanguageMode LanguageMode { get; init; } = LanguageMode.Mixed;
}

/// <summary>The pipeline's output and what it changed.</summary>
public sealed record TextPipelineResult(string Text, int SnippetsApplied = 0, int ReplacementsApplied = 0, int TermFixesApplied = 0)
{
    /// <summary>The per-dictation "fixes" figure: dictionary replacements plus term corrections.</summary>
    public int FixesCount => ReplacementsApplied + TermFixesApplied;
}
