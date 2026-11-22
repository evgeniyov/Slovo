using System.Text.Json.Serialization;

namespace Slovo.Core.Model;

/// <summary>One finished dictation, as stored in <c>history.json</c>.</summary>
public sealed record HistoryEntry
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("date")] public DateTimeOffset Date { get; init; }
    /// <summary>Text after the pipeline, as inserted.</summary>
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    /// <summary>Text exactly as the engine returned it.</summary>
    [JsonPropertyName("rawText")] public string RawText { get; init; } = "";
    [JsonPropertyName("engineID")] public EngineId EngineId { get; init; }
    [JsonPropertyName("languageMode")] public LanguageMode LanguageMode { get; init; }
    /// <summary>The app's identifier: a bundle id on macOS, the lower-case executable name on Windows.</summary>
    [JsonPropertyName("appBundleID")] public string? AppBundleId { get; init; }
    [JsonPropertyName("appName")] public string? AppName { get; init; }
    [JsonPropertyName("category")] public AppCategory Category { get; init; }
    /// <summary>Recording length in seconds.</summary>
    [JsonPropertyName("duration")] public double Duration { get; init; }
    [JsonPropertyName("wordCount")] public int WordCount { get; init; }
    /// <summary>Dictionary replacements plus term corrections applied to this dictation.</summary>
    [JsonPropertyName("fixesCount")] public int FixesCount { get; init; }
    /// <summary>Path relative to the recordings folder, such as <c>2026-09-15/&lt;uuid&gt;.wav</c>.</summary>
    [JsonPropertyName("audioFileName")] public string? AudioFileName { get; init; }

    /// <summary>Words split on whitespace, the count history entries and insights use.</summary>
    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>A Dictionary row: either a term to spell correctly or an explicit replacement.</summary>
public sealed record DictionaryItem
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("kind")] public DictionaryItemKind Kind { get; init; }
    /// <summary>The term itself, or the "from" phrase of a replacement.</summary>
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    /// <summary>The "to" text of a replacement; empty for terms.</summary>
    [JsonPropertyName("replacement")] public string Replacement { get; init; } = "";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static DictionaryItem Term(string text, DateTimeOffset? createdAt = null) =>
        new() { Kind = DictionaryItemKind.Term, Text = text, CreatedAt = createdAt ?? DateTimeOffset.UtcNow };

    public static DictionaryItem ReplacementRule(string from, string to, DateTimeOffset? createdAt = null) =>
        new() { Kind = DictionaryItemKind.Replacement, Text = from, Replacement = to, CreatedAt = createdAt ?? DateTimeOffset.UtcNow };
}

/// <summary>A trigger phrase that expands into longer text.</summary>
public sealed record Snippet
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("trigger")] public string Trigger { get; init; } = "";
    [JsonPropertyName("expansion")] public string Expansion { get; init; } = "";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A Scratchpad note.</summary>
public sealed record Note
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The first non-empty line, trimmed; empty for an empty note.</summary>
    [JsonIgnore]
    public string Title
    {
        get
        {
            foreach (var line in Text.Split(['\n', '\r', '\u2028', '\u2029', '\u0085', '\v', '\f']))
            {
                var trimmed = line.Trim(' ', '\t');
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
            return "";
        }
    }
}

/// <summary>A user's choice to put an app into a category, overriding the built-in defaults.</summary>
public sealed record AppCategoryAssignment(
    [property: JsonPropertyName("bundleID")] string BundleId,
    [property: JsonPropertyName("appName")] string AppName,
    [property: JsonPropertyName("category")] AppCategory Category);

/// <summary>Auto cleanup toggles from the Style screen.</summary>
public sealed record AutoCleanupOptions(
    [property: JsonPropertyName("removeFillers")] bool RemoveFillers = true,
    [property: JsonPropertyName("removeRepetitions")] bool RemoveRepetitions = true)
{
    public static readonly AutoCleanupOptions Default = new();
    public static readonly AutoCleanupOptions Off = new(false, false);
}

/// <summary>The foreground application at the moment a dictation started.</summary>
/// <param name="BundleId">On Windows, the lower-case executable name such as <c>telegram.exe</c>.</param>
public sealed record AppIdentity(
    [property: JsonPropertyName("bundleID")] string BundleId,
    [property: JsonPropertyName("name")] string Name);

public static class CategoryDefaults
{
    public static StylePreset DefaultStyle(this AppCategory category) =>
        category == AppCategory.Personal ? StylePreset.Casual : StylePreset.Formal;

    public static readonly IReadOnlyList<AppCategory> All = [AppCategory.Personal, AppCategory.Work, AppCategory.Email, AppCategory.Other];
}

public static class Retention
{
    public static readonly IReadOnlyList<RetentionPeriod> HistoryChoices = [RetentionPeriod.Forever, RetentionPeriod.Days30, RetentionPeriod.Days7, RetentionPeriod.Hours24];
    public static readonly IReadOnlyList<RetentionPeriod> DictationAudioChoices = [RetentionPeriod.Days7, RetentionPeriod.Days30, RetentionPeriod.Hours24, RetentionPeriod.Never];
    public static readonly IReadOnlyList<RetentionPeriod> MeetingAudioChoices = [RetentionPeriod.Forever, RetentionPeriod.Days30, RetentionPeriod.Days7, RetentionPeriod.AfterTranscription];

    /// <summary>Maximum age in seconds; null when age alone never removes an item.</summary>
    public static double? MaximumAge(this RetentionPeriod period) => period switch
    {
        RetentionPeriod.Days30 => 30 * 24 * 3600,
        RetentionPeriod.Days7 => 7 * 24 * 3600,
        RetentionPeriod.Hours24 => 24 * 3600,
        RetentionPeriod.Never => 0,
        _ => null,
    };
}
