using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slovo.Core.Json;

/// <summary>
/// JSON settings for every file Slovo writes. The files are shared with the macOS app,
/// so they follow Swift's <c>JSONEncoder</c> defaults: dates are seconds since
/// 2001-01-01 UTC (<c>timeIntervalSinceReferenceDate</c>), UUIDs are upper-case, keys
/// are written sorted, and property names are spelled exactly as in Swift.
/// </summary>
public static class SlovoJson
{
    /// <summary>Options for reading and writing store files.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions(indented: true);

    /// <summary>Compact options, for values embedded in other text (settings, MCP).</summary>
    public static JsonSerializerOptions Compact { get; } = CreateOptions(indented: false);

    static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            IndentSize = 2,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Swift writes non-ASCII text as is; so does Slovo for Windows, so a Russian
            // transcript stays readable in the file.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new AppleDateConverter());
        options.Converters.Add(new NullableAppleDateConverter());
        options.Converters.Add(new UpperGuidConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Serializes with sorted keys, like Swift's <c>.sortedKeys</c>.</summary>
    public static string Serialize<T>(T value, bool indented = true)
    {
        var node = JsonSerializer.SerializeToNode(value, indented ? Options : Compact);
        return SortedJson.Write(node, indented);
    }

    /// <summary>UTF-8 bytes of <see cref="Serialize{T}"/>.</summary>
    public static byte[] SerializeToUtf8<T>(T value, bool indented = true) =>
        System.Text.Encoding.UTF8.GetBytes(Serialize(value, indented));
}

/// <summary>Dates as Swift writes them: seconds since 2001-01-01T00:00:00Z.</summary>
public static class AppleDate
{
    public static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static double ToReferenceSeconds(DateTimeOffset date) =>
        (date.UtcDateTime - ReferenceDate.UtcDateTime).Ticks / (double)TimeSpan.TicksPerSecond;

    public static DateTimeOffset FromReferenceSeconds(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            return ReferenceDate;
        }
        var maxSeconds = (DateTimeOffset.MaxValue - ReferenceDate).TotalSeconds - 1;
        var minSeconds = (DateTimeOffset.MinValue - ReferenceDate).TotalSeconds + 1;
        var clamped = Math.Clamp(seconds, minSeconds, maxSeconds);
        return ReferenceDate.AddTicks((long)Math.Round(clamped * TimeSpan.TicksPerSecond));
    }
}

public sealed class AppleDateConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return AppleDate.FromReferenceSeconds(reader.GetDouble());
        }
        if (reader.TokenType == JsonTokenType.String)
        {
            // Tolerate ISO 8601, which a hand-edited file may hold.
            var text = reader.GetString();
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }
        throw new JsonException("Expected a date as seconds since 2001-01-01.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(AppleDate.ToReferenceSeconds(value));
}

public sealed class NullableAppleDateConverter : JsonConverter<DateTimeOffset?>
{
    readonly AppleDateConverter inner = new();

    public override bool HandleNull => true;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : inner.Read(ref reader, typeof(DateTimeOffset), options);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } date)
        {
            inner.Write(writer, date, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>UUIDs as Swift writes them: upper-case with hyphens.</summary>
public sealed class UpperGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is not null && Guid.TryParseExact(text, "D", out var value))
        {
            return value;
        }
        throw new JsonException("Expected a UUID.");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

/// <summary>Formats a Guid the way Swift's <c>UUID.uuidString</c> does.</summary>
public static class GuidExtensions
{
    public static string ToUpperString(this Guid value) => value.ToString("D").ToUpperInvariant();
}
