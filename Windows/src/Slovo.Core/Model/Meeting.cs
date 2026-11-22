using System.Text.Json;
using System.Text.Json.Serialization;
using Slovo.Core.Json;
using Slovo.Core.Mcp;
using Slovo.Core.Text;

namespace Slovo.Core.Model;

// The meeting model and its meeting.json format, shared with the macOS app
// (Sources/SlovoCore/Model/Meeting.swift). Decoding is just as tolerant: keys added after
// the first format are optional, unknown enum values fall back instead of throwing, and a
// malformed speaker or highlight is dropped on its own.

public static class MeetingTracks
{
    public static readonly IReadOnlyList<MeetingTrack> All = [MeetingTrack.Microphone, MeetingTrack.System];

    public static MeetingTrack FromSpeaker(Speaker speaker) => speaker == Speaker.Me ? MeetingTrack.Microphone : MeetingTrack.System;

    public static Speaker ToSpeaker(this MeetingTrack track) => track == MeetingTrack.Microphone ? Speaker.Me : Speaker.Others;
}

/// <summary>Bounds for times read from meeting.json, which can have been edited by hand.</summary>
public static class MeetingTime
{
    public const double MaximumSeconds = 359_999_999;

    /// <summary>The time within 0...MaximumSeconds; 0 for NaN and infinities.</summary>
    public static double Clamped(double seconds) =>
        double.IsFinite(seconds) ? Math.Min(Math.Max(0, seconds), MaximumSeconds) : 0;
}

/// <summary>One person, or one unseparated track, in a meeting transcript.</summary>
public sealed record MeetingSpeaker
{
    public const int PaletteSize = 8;
    public const int MaximumNumber = 999_999;

    /// <summary>"s1", "s2", … for separated voices; "me" or "others" for a track that was not separated.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("track")] public MeetingTrack Track { get; init; }
    /// <summary>N in «Спикер N»; null for "me"/"others".</summary>
    [JsonPropertyName("number")] public int? Number { get; init; }
    /// <summary>The user's name for this speaker; null shows the default label.</summary>
    [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
    [JsonPropertyName("colorIndex")] public int ColorIndex { get; init => field = ClampedColor(value); }
    [JsonPropertyName("isMe")] public bool IsMe { get; init; }

    public static MeetingSpeaker Fallback(MeetingTrack track) => track == MeetingTrack.Microphone
        ? new MeetingSpeaker { Id = "me", Track = MeetingTrack.Microphone, ColorIndex = 0 }
        : new MeetingSpeaker { Id = "others", Track = MeetingTrack.System, ColorIndex = 1 };

    public static int ClampedColor(int value) => ((value % PaletteSize) + PaletteSize) % PaletteSize;

    /// <summary>"me", "others", or "s" and a number from 1 to MaximumNumber without leading zeros.</summary>
    public static bool IsValidId(string id)
    {
        if (id is "me" or "others")
        {
            return true;
        }
        if (id.Length < 2 || id[0] != 's')
        {
            return false;
        }
        var digits = id.AsSpan(1);
        if (digits.Length > MaximumNumber.ToString().Length || digits[0] == '0')
        {
            return false;
        }
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    public static int? ValidNumber(int value) => value is >= 1 and <= MaximumNumber ? value : null;

    /// <summary>Decodes one speaker; null when the id or track is unusable (the record drops it).</summary>
    internal static MeetingSpeaker? Decode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !JsonRead.TryString(element, "id", out var id) || !IsValidId(id)
            || !JsonRead.TryString(element, "track", out var rawTrack)
            || !EnumText.TryParse<MeetingTrack>(rawTrack, out var track))
        {
            return null;
        }
        var number = JsonRead.Int(element, "number") is { } n ? ValidNumber(n) : null;
        var storedColor = JsonRead.Int(element, "colorIndex");
        var defaultColor = number is { } value ? value - 1 : Fallback(track).ColorIndex;
        return new MeetingSpeaker
        {
            Id = id,
            Track = track,
            Number = number,
            DisplayName = JsonRead.String(element, "displayName"),
            ColorIndex = storedColor ?? defaultColor,
            IsMe = JsonRead.Bool(element, "isMe") ?? false,
        };
    }
}

/// <summary>A moment the user marked as important.</summary>
public sealed record MeetingHighlight
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Seconds on the tracks' shared 16 kHz timeline.</summary>
    [JsonPropertyName("time")] public double Time { get; init; }
    /// <summary>Optional, at most 500 characters, one line.</summary>
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("source")] public HighlightSource Source { get; init; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>A highlight without an id or a usable time is dropped; an unknown source reads as Transcript.</summary>
    internal static MeetingHighlight? Decode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !JsonRead.TryGuid(element, "id", out var id)
            || JsonRead.Double(element, "time") is not { } time
            || !double.IsFinite(time))
        {
            return null;
        }
        return new MeetingHighlight
        {
            Id = id,
            Time = MeetingTime.Clamped(time),
            Note = JsonRead.String(element, "note"),
            Source = EnumText.ParseOr(JsonRead.String(element, "source"), HighlightSource.Transcript),
            CreatedAt = JsonRead.Date(element, "createdAt") ?? AppleDate.ReferenceDate,
        };
    }
}

/// <summary>The outcome of speaker separation for one meeting.</summary>
public sealed record MeetingDiarization
{
    [JsonPropertyName("state")] public DiarizationState State { get; init; }
    /// <summary>The number of people requested for this run; null means automatic.</summary>
    [JsonPropertyName("requestedSpeakers")] public int? RequestedSpeakers { get; init; }
    /// <summary>Separated speakers found per track, keyed by the track's raw value.</summary>
    [JsonPropertyName("speakersPerTrack")] public Dictionary<string, int> SpeakersPerTrack { get; init; } = [];
    [JsonPropertyName("model")] public string? ModelName { get; init; }
    [JsonPropertyName("configuration")] public string? Configuration { get; init; }
    /// <summary>Localized; set for Failed.</summary>
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("processedAt")] public DateTimeOffset? ProcessedAt { get; init; }

    public bool Equals(MeetingDiarization? other) =>
        other is not null && State == other.State && RequestedSpeakers == other.RequestedSpeakers
        && ModelName == other.ModelName && Configuration == other.Configuration && Message == other.Message
        && ProcessedAt == other.ProcessedAt
        && SpeakersPerTrack.Count == other.SpeakersPerTrack.Count
        && SpeakersPerTrack.All(p => other.SpeakersPerTrack.TryGetValue(p.Key, out var v) && v == p.Value);

    public override int GetHashCode() => HashCode.Combine(State, RequestedSpeakers, ModelName, Configuration, Message, ProcessedAt);

    internal static MeetingDiarization? Decode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var perTrack = new Dictionary<string, int>();
        if (element.TryGetProperty("speakersPerTrack", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            var ok = true;
            foreach (var pair in map.EnumerateObject())
            {
                if (pair.Value.ValueKind == JsonValueKind.Number && pair.Value.TryGetInt32(out var count))
                {
                    perTrack[pair.Name] = count;
                }
                else
                {
                    ok = false;
                }
            }
            if (!ok)
            {
                perTrack.Clear();
            }
        }
        return new MeetingDiarization
        {
            State = EnumText.ParseOr(JsonRead.String(element, "state"), DiarizationState.Off),
            RequestedSpeakers = JsonRead.Int(element, "requestedSpeakers"),
            SpeakersPerTrack = perTrack,
            ModelName = JsonRead.String(element, "model"),
            Configuration = JsonRead.String(element, "configuration"),
            Message = JsonRead.String(element, "message"),
            ProcessedAt = JsonRead.Date(element, "processedAt"),
        };
    }
}

/// <summary>One piece of a meeting transcript with times relative to the recording start.</summary>
public sealed record TranscriptSegment
{
    [JsonPropertyName("start")] public double Start { get; init; }
    [JsonPropertyName("end")] public double End { get; init; }
    /// <summary>The track the segment came from (the stored form).</summary>
    [JsonPropertyName("speaker")] public Speaker Speaker { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    /// <summary>The separated speaker; null means the fallback speaker of the segment's track.</summary>
    [JsonPropertyName("speakerID")] public string? SpeakerId { get; init; }

    /// <summary>The keys every build writes stay required; throws when one is missing.</summary>
    internal static TranscriptSegment Decode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || JsonRead.Double(element, "start") is not { } start
            || JsonRead.Double(element, "end") is not { } end
            || !JsonRead.TryString(element, "speaker", out var rawSpeaker)
            || !EnumText.TryParse<Speaker>(rawSpeaker, out var speaker)
            || !JsonRead.TryString(element, "text", out var text))
        {
            throw new JsonException("A transcript segment is missing a required key.");
        }
        return new TranscriptSegment
        {
            Start = MeetingTime.Clamped(start),
            End = MeetingTime.Clamped(end),
            Speaker = speaker,
            Text = text,
            SpeakerId = JsonRead.String(element, "speakerID"),
        };
    }
}

/// <summary>Where a meeting came from, kept only when it is worth saying.</summary>
public sealed record MeetingSource
{
    public const int MaximumDeviceCharacters = 60;

    [JsonPropertyName("origin")] public MeetingOrigin Origin { get; init; }
    /// <summary>What the device calls itself; null when it did not say.</summary>
    [JsonPropertyName("device")] public string? Device { get; init => field = value is null ? null : SanitizedDevice(value); }

    public static string? SanitizedDevice(string name)
    {
        var single = TextElements.NormalizeWhitespace(name);
        var kept = TextElements.TrimSpaces(TextElements.Prefix(single, MaximumDeviceCharacters));
        return kept.Length == 0 ? null : kept;
    }

    /// <summary>An origin a later build added reads as no source at all.</summary>
    internal static MeetingSource? Decode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !JsonRead.TryString(element, "origin", out var rawOrigin)
            || !EnumText.TryParse<MeetingOrigin>(rawOrigin, out var origin))
        {
            return null;
        }
        return new MeetingSource { Origin = origin, Device = JsonRead.String(element, "device") };
    }
}

/// <summary>
/// What Claude wrote about a meeting after reading it. Held to the same rules as the text
/// the MCP server hands out: one line per value, no control characters.
/// </summary>
public sealed record MeetingNotes
{
    public const int MaximumSummaryCharacters = 2_000;
    public const int MaximumItemCharacters = 500;
    public const int MaximumItems = 50;
    public const int MaximumAuthorCharacters = 60;
    public const string DefaultAuthor = "Claude";

    [JsonPropertyName("summary")] public string Summary { get; }
    [JsonPropertyName("decisions")] public IReadOnlyList<string> Decisions { get; }
    [JsonPropertyName("tasks")] public IReadOnlyList<string> Tasks { get; }
    [JsonPropertyName("author")] public string Author { get; }
    [JsonPropertyName("writtenAt")] public DateTimeOffset WrittenAt { get; }

    [JsonConstructor]
    public MeetingNotes(string summary = "", IReadOnlyList<string>? decisions = null, IReadOnlyList<string>? tasks = null,
                        string author = DefaultAuthor, DateTimeOffset? writtenAt = null)
    {
        Summary = Sanitized(summary, MaximumSummaryCharacters) ?? "";
        Decisions = SanitizedItems(decisions ?? []);
        Tasks = SanitizedItems(tasks ?? []);
        Author = Sanitized(author, MaximumAuthorCharacters) ?? DefaultAuthor;
        WrittenAt = writtenAt ?? DateTimeOffset.UtcNow;
    }

    [JsonIgnore]
    public bool IsEmpty => Summary.Length == 0 && Decisions.Count == 0 && Tasks.Count == 0;

    public bool Equals(MeetingNotes? other) =>
        other is not null && Summary == other.Summary && Author == other.Author && WrittenAt == other.WrittenAt
        && Decisions.SequenceEqual(other.Decisions) && Tasks.SequenceEqual(other.Tasks);

    public override int GetHashCode() => HashCode.Combine(Summary, Author, WrittenAt, Decisions.Count, Tasks.Count);

    /// <summary>Cleaned, on one line, and at most <paramref name="limit"/> characters; null when nothing is left.</summary>
    public static string? Sanitized(string text, int limit)
    {
        var single = TextElements.NormalizeWhitespace(McpSanitizer.Sanitize(text));
        var kept = TextElements.TrimSpaces(TextElements.Prefix(single, limit));
        return kept.Length == 0 ? null : kept;
    }

    public static IReadOnlyList<string> SanitizedItems(IEnumerable<string> items) =>
        items.Take(MaximumItems).Select(item => Sanitized(item, MaximumItemCharacters)).OfType<string>().ToList();

    /// <summary>Every field is optional on disk.</summary>
    internal static MeetingNotes? Decode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return new MeetingNotes(
            JsonRead.String(element, "summary") ?? "",
            JsonRead.StringArray(element, "decisions") ?? [],
            JsonRead.StringArray(element, "tasks") ?? [],
            JsonRead.String(element, "author") ?? DefaultAuthor,
            JsonRead.Date(element, "writtenAt") ?? AppleDate.ReferenceDate);
    }
}

/// <summary>A meeting as stored in <c>Meetings/&lt;id&gt;/meeting.json</c>.</summary>
[JsonConverter(typeof(MeetingRecordConverter))]
public sealed record MeetingRecord
{
    /// <summary>The format new builds write. Files without formatVersion are format 1.</summary>
    public const int CurrentFormatVersion = 2;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public double Duration { get; init; }
    public MeetingStatus Status { get; init; } = MeetingStatus.Recording;
    /// <summary>Whether system audio was recorded.</summary>
    public bool HasSystemAudio { get; init; }
    /// <summary>False once retention or the user removed the audio files.</summary>
    public bool HasAudio { get; init; } = true;
    /// <summary>Whether <c>screen.mov</c> was recorded (macOS only so far).</summary>
    public bool HasScreenRecording { get; init; }
    public EngineId? EngineId { get; init; }
    public IReadOnlyList<TranscriptSegment> Segments { get; init; } = [];
    /// <summary>Localized message for Failed.</summary>
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<MeetingSpeaker> Speakers { get; init; } = [];
    public IReadOnlyList<MeetingHighlight> Highlights { get; init; } = [];
    public MeetingDiarization? Diarization { get; init; }
    public MeetingSource? Source { get; init; }
    /// <summary>When the audio became this computer's to keep; null means CreatedAt.</summary>
    public DateTimeOffset? AudioStoredAt { get; init; }
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public int? HighestSpeakerNumber { get; init; }
    public MeetingNotes? ClaudeNotes { get; init; }

    public bool Equals(MeetingRecord? other) =>
        other is not null && Id == other.Id && Title == other.Title && CreatedAt == other.CreatedAt
        && Duration.Equals(other.Duration) && Status == other.Status && HasSystemAudio == other.HasSystemAudio
        && HasAudio == other.HasAudio && HasScreenRecording == other.HasScreenRecording && EngineId == other.EngineId
        && Segments.SequenceEqual(other.Segments) && ErrorMessage == other.ErrorMessage
        && Speakers.SequenceEqual(other.Speakers) && Highlights.SequenceEqual(other.Highlights)
        && Equals(Diarization, other.Diarization) && Equals(Source, other.Source) && AudioStoredAt == other.AudioStoredAt
        && FormatVersion == other.FormatVersion && HighestSpeakerNumber == other.HighestSpeakerNumber
        && Equals(ClaudeNotes, other.ClaudeNotes);

    public override int GetHashCode() => HashCode.Combine(Id, Title, CreatedAt, Status, Segments.Count);

    /// <summary>
    /// The stored speakers plus the fallback speaker of each track whose segments do not
    /// name a stored speaker of that track, microphone speakers first.
    /// </summary>
    public IReadOnlyList<MeetingSpeaker> EffectiveSpeakers
    {
        get
        {
            var result = Speakers.ToList();
            foreach (var track in MeetingTracks.All)
            {
                var fallback = MeetingSpeaker.Fallback(track);
                if (result.Any(s => s.Id == fallback.Id))
                {
                    continue;
                }
                var usesFallback = Segments.Any(segment =>
                    MeetingTracks.FromSpeaker(segment.Speaker) == track && SpeakerIdFor(segment) == fallback.Id);
                if (usesFallback)
                {
                    result.Add(fallback);
                }
            }
            return result.Select((speaker, offset) => (speaker, offset))
                .OrderBy(p => TrackOrder(p.speaker.Track))
                .ThenBy(p => p.offset)
                .Select(p => p.speaker)
                .ToList();
        }
    }

    /// <summary>A stored speaker, or the fallback speaker when the id is "me" or "others".</summary>
    public MeetingSpeaker? SpeakerWithId(string id) =>
        Speakers.FirstOrDefault(s => s.Id == id)
        ?? MeetingTracks.All.Select(MeetingSpeaker.Fallback).FirstOrDefault(s => s.Id == id);

    /// <summary>The id of the speaker a segment is shown under.</summary>
    public string SpeakerIdFor(TranscriptSegment segment)
    {
        var track = MeetingTracks.FromSpeaker(segment.Speaker);
        if (segment.SpeakerId is { } id && Speakers.Any(s => s.Id == id && s.Track == track))
        {
            return id;
        }
        return MeetingSpeaker.Fallback(track).Id;
    }

    /// <summary>Seconds of transcribed speech shown under a speaker.</summary>
    public double TalkTime(string speakerId) =>
        Segments.Where(s => SpeakerIdFor(s) == speakerId).Sum(s => Math.Max(0, s.End - s.Start));

    static int TrackOrder(MeetingTrack track) => track == MeetingTrack.Microphone ? 0 : 1;

    internal static IReadOnlyList<MeetingSpeaker> ConsistentSpeakers(IEnumerable<MeetingSpeaker> speakers)
    {
        var seenMe = false;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MeetingSpeaker>();
        foreach (var speaker in speakers)
        {
            if (!seenIds.Add(speaker.Id))
            {
                continue;
            }
            var copy = speaker;
            if (copy.IsMe)
            {
                copy = copy with { IsMe = !seenMe };
                seenMe = true;
            }
            result.Add(copy);
        }
        return result;
    }
}

/// <summary>Reads and writes meeting.json with the tolerant rules of the macOS decoder.</summary>
public sealed class MeetingRecordConverter : JsonConverter<MeetingRecord>
{
    public override MeetingRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return Decode(document.RootElement);
    }

    public static MeetingRecord Decode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("meeting.json must hold an object.");
        }
        if (!JsonRead.TryGuid(root, "id", out var id)) throw new JsonException("meeting.json has no id.");
        if (!JsonRead.TryString(root, "title", out var title)) throw new JsonException("meeting.json has no title.");
        var createdAt = JsonRead.Date(root, "createdAt") ?? throw new JsonException("meeting.json has no createdAt.");
        var duration = JsonRead.Double(root, "duration") ?? throw new JsonException("meeting.json has no duration.");
        var hasSystemAudio = JsonRead.Bool(root, "hasSystemAudio") ?? throw new JsonException("meeting.json has no hasSystemAudio.");
        var hasAudio = JsonRead.Bool(root, "hasAudio") ?? throw new JsonException("meeting.json has no hasAudio.");
        if (!root.TryGetProperty("segments", out var segmentsElement) || segmentsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("meeting.json has no segments.");
        }
        var segments = segmentsElement.EnumerateArray().Select(TranscriptSegment.Decode).ToList();
        if (!JsonRead.TryString(root, "status", out var rawStatus)) throw new JsonException("meeting.json has no status.");

        MeetingStatus status;
        if (EnumText.TryParse<MeetingStatus>(rawStatus, out var known))
        {
            status = known;
        }
        else if (hasAudio)
        {
            status = MeetingStatus.Recorded;
        }
        else
        {
            status = segments.Count == 0 ? MeetingStatus.Failed : MeetingStatus.Transcribed;
        }

        var speakers = JsonRead.LossyArray(root, "speakers", MeetingSpeaker.Decode);
        var highlights = JsonRead.LossyArray(root, "highlights", MeetingHighlight.Decode);
        var uniqueHighlights = new List<MeetingHighlight>();
        var seen = new HashSet<Guid>();
        foreach (var highlight in highlights)
        {
            if (seen.Add(highlight.Id))
            {
                uniqueHighlights.Add(highlight);
            }
        }

        var notes = root.TryGetProperty("claudeNotes", out var notesElement) ? MeetingNotes.Decode(notesElement) : null;
        return new MeetingRecord
        {
            Id = id,
            Title = title,
            CreatedAt = createdAt,
            Duration = MeetingTime.Clamped(duration),
            Status = status,
            HasSystemAudio = hasSystemAudio,
            HasAudio = hasAudio,
            HasScreenRecording = JsonRead.Bool(root, "hasScreenRecording") ?? false,
            EngineId = EnumText.Parse<EngineId>(JsonRead.String(root, "engineID")),
            Segments = segments,
            ErrorMessage = JsonRead.String(root, "errorMessage"),
            Speakers = MeetingRecord.ConsistentSpeakers(speakers),
            Highlights = uniqueHighlights,
            Diarization = root.TryGetProperty("diarization", out var d) ? MeetingDiarization.Decode(d) : null,
            Source = root.TryGetProperty("source", out var s) ? MeetingSource.Decode(s) : null,
            AudioStoredAt = JsonRead.Date(root, "audioStoredAt"),
            FormatVersion = JsonRead.Int(root, "formatVersion") ?? 1,
            HighestSpeakerNumber = JsonRead.Int(root, "highestSpeakerNumber") is { } h ? MeetingSpeaker.ValidNumber(h) : null,
            ClaudeNotes = notes is { IsEmpty: false } ? notes : null,
        };
    }

    public override void Write(Utf8JsonWriter writer, MeetingRecord value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id.ToUpperString());
        writer.WriteString("title", value.Title);
        writer.WriteNumber("createdAt", AppleDate.ToReferenceSeconds(value.CreatedAt));
        writer.WriteNumber("duration", value.Duration);
        writer.WriteString("status", EnumText.Raw(value.Status));
        writer.WriteBoolean("hasSystemAudio", value.HasSystemAudio);
        writer.WriteBoolean("hasAudio", value.HasAudio);
        writer.WriteBoolean("hasScreenRecording", value.HasScreenRecording);
        if (value.EngineId is { } engine)
        {
            writer.WriteString("engineID", EnumText.Raw(engine));
        }
        writer.WritePropertyName("segments");
        JsonSerializer.Serialize(writer, value.Segments, options);
        if (value.ErrorMessage is { } error)
        {
            writer.WriteString("errorMessage", error);
        }
        writer.WritePropertyName("speakers");
        JsonSerializer.Serialize(writer, value.Speakers, options);
        writer.WritePropertyName("highlights");
        JsonSerializer.Serialize(writer, value.Highlights, options);
        if (value.Diarization is { } diarization)
        {
            writer.WritePropertyName("diarization");
            JsonSerializer.Serialize(writer, diarization, options);
        }
        if (value.Source is { } source)
        {
            writer.WritePropertyName("source");
            JsonSerializer.Serialize(writer, source, options);
        }
        if (value.AudioStoredAt is { } stored)
        {
            writer.WriteNumber("audioStoredAt", AppleDate.ToReferenceSeconds(stored));
        }
        writer.WriteNumber("formatVersion", value.FormatVersion);
        if (value.HighestSpeakerNumber is { } highest)
        {
            writer.WriteNumber("highestSpeakerNumber", highest);
        }
        if (value.ClaudeNotes is { } notes)
        {
            writer.WritePropertyName("claudeNotes");
            JsonSerializer.Serialize(writer, notes, options);
        }
        writer.WriteEndObject();
    }
}

/// <summary>Tolerant readers for hand-editable JSON: a wrong type reads as absent.</summary>
internal static class JsonRead
{
    public static bool TryString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return true;
        }
        value = "";
        return false;
    }

    public static string? String(JsonElement element, string name) =>
        TryString(element, name, out var value) ? value : null;

    public static bool TryGuid(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return TryString(element, name, out var text) && Guid.TryParseExact(text, "D", out value);
    }

    public static double? Double(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetDouble() : null;

    /// <summary>An integer; a number with a fraction or out of range reads as absent, as in Swift.</summary>
    public static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out var value) ? value : null;

    public static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean() : null;

    public static DateTimeOffset? Date(JsonElement element, string name) =>
        Double(element, name) is { } seconds ? AppleDate.FromReferenceSeconds(seconds) : null;

    public static IReadOnlyList<string>? StringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            result.Add(item.GetString() ?? "");
        }
        return result;
    }

    public static List<T> LossyArray<T>(JsonElement element, string name, Func<JsonElement, T?> decode) where T : class
    {
        var result = new List<T>();
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in property.EnumerateArray())
            {
                if (decode(item) is { } value)
                {
                    result.Add(value);
                }
            }
        }
        return result;
    }
}
