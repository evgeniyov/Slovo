using System.Globalization;
using System.Text.Json;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Text;

namespace Slovo.Core.Inbox;

/// <summary>
/// The <c>.json</c> a phone writes next to a clip in the inbox (port of the macOS
/// <c>InboxSidecar</c>).
///
/// The phone writes it once the audio is complete, so a sidecar next to a clip also means the
/// clip is finished. With it the meeting keeps the phone's own time, name and marked moments
/// instead of the moment the file happened to reach the computer.
///
/// Reading is defensive throughout: a sidecar from a newer phone build, or one cut off
/// half-written, may cost the extra data but never the clip itself.
/// </summary>
public sealed record InboxSidecar
{
    /// <summary>What the phone was recording. A meeting is separated into speakers like one recorded here; a note is one voice.</summary>
    public enum SidecarKind
    {
        Note,
        Meeting,
    }

    /// <summary>A moment marked on the phone while it was recording.</summary>
    /// <param name="Time">Seconds from the start of the clip.</param>
    public sealed record Moment(double Time, string? Note);

    public const string FileExtension = "json";
    /// <summary>
    /// Longer than any name typed on a phone. A title also becomes the <c># heading</c> of
    /// <c>transcript.md</c>, so it is kept to one line.
    /// </summary>
    public const int MaximumTitleCharacters = 200;

    public SidecarKind Kind { get; init; } = SidecarKind.Note;
    /// <summary>When the phone started recording; null when it did not say, or said something that is not a date.</summary>
    public DateTimeOffset? RecordedAt { get; init; }
    public string? Device { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<Moment> Moments { get; init; } = [];

    public bool Equals(InboxSidecar? other) =>
        other is not null && Kind == other.Kind && RecordedAt == other.RecordedAt && Device == other.Device
        && Title == other.Title && Moments.SequenceEqual(other.Moments);

    public override int GetHashCode() => HashCode.Combine(Kind, RecordedAt, Device, Title, Moments.Count);

    /// <summary>The sidecar that belongs to a clip: <c>slovo-20260916-125458.m4a</c> → <c>…-125458.json</c>.</summary>
    public static string PathFor(string clipPath) => Path.ChangeExtension(clipPath, FileExtension);

    /// <summary>Null when the file cannot be read or is not a JSON object; anything inside it that does not fit the contract is dropped on its own.</summary>
    public static InboxSidecar? Read(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(data);
            return document.RootElement.ValueKind == JsonValueKind.Object ? Parse(document.RootElement) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the fields of format 1. <c>version</c> is not checked and unknown keys are
    /// ignored: the format grows by adding keys, and a phone one release ahead should still
    /// import. <c>durationMs</c> is ignored too — the decoded audio is what every later step measures.
    /// </summary>
    public static InboxSidecar Parse(JsonElement root)
    {
        var moments = new List<Moment>();
        if (root.TryGetProperty("highlights", out var highlights) && highlights.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in highlights.EnumerateArray())
            {
                if (MomentFrom(element) is { } moment)
                {
                    moments.Add(moment);
                }
            }
        }
        return new InboxSidecar
        {
            Kind = StringValue(root, "kind") switch
            {
                "meeting" => SidecarKind.Meeting,
                _ => SidecarKind.Note,
            },
            RecordedAt = StringValue(root, "recordedAt") is { } date ? DateFrom(date) : null,
            Device = StringValue(root, "device") is { } device ? MeetingSource.SanitizedDevice(device) : null,
            Title = StringValue(root, "title") is { } title ? SanitizedTitle(title) : null,
            Moments = moments,
        };
    }

    // MARK: - Fields

    static readonly string[] WholeSecondFormats = ["yyyy-MM-dd'T'HH:mm:sszzz"];
    static readonly string[] FractionalFormats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];

    /// <summary>ISO 8601 with the phone's offset (or <c>Z</c>), with or without fractional seconds.</summary>
    static DateTimeOffset? DateFrom(string text)
    {
        var normalized = text.EndsWith('Z') ? text[..^1] + "+00:00" : text;
        foreach (var formats in new[] { WholeSecondFormats, FractionalFormats })
        {
            if (DateTimeOffset.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
        }
        return null;
    }

    static string? SanitizedTitle(string title)
    {
        var kept = TextElements.TrimSpaces(TextElements.Prefix(TranscriptMerger.NormalizedText(title), MaximumTitleCharacters));
        return kept.Length == 0 ? null : kept;
    }

    /// <summary>A moment without a usable position is dropped rather than placed at the start, where it would point at speech nobody marked.</summary>
    static Moment? MomentFrom(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("atMs", out var position) || position.ValueKind != JsonValueKind.Number
            || !position.TryGetDouble(out var milliseconds))
        {
            return null;
        }
        var seconds = milliseconds / 1000;
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            return null;
        }
        return new Moment(MeetingTime.Clamped(seconds), MeetingEdits.SanitizedNote(StringValue(element, "note")));
    }

    static string? StringValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
