using System.Globalization;
using System.Text;

namespace Slovo.Core.Mcp;

/// <summary>What kind of value a <see cref="JsonValue"/> holds.</summary>
public enum JsonKind
{
    Null,
    Bool,
    /// <summary>A number written without a fraction or exponent that fits in a 64-bit integer.</summary>
    Integer,
    Number,
    String,
    Array,
    Object,
}

/// <summary>
/// A JSON value that keeps integers apart from other numbers, so request ids are echoed
/// exactly as they arrived (integer ids stay integers, string ids stay strings). Port of the
/// macOS <c>JSONValue</c>.
///
/// Parsing and serialization are hand-written, as on macOS: the reply bytes must be the
/// same on both systems, so object keys are sorted the way Swift sorts strings, doubles are
/// written the way Swift's <c>description</c> writes them (<c>760.0</c>, <c>1e+16</c>) and
/// strings are escaped exactly as the Swift writer escapes them.
/// </summary>
public sealed class JsonValue : IEquatable<JsonValue>
{
    public static readonly JsonValue Null = new(JsonKind.Null);
    public static readonly JsonValue True = new(JsonKind.Bool) { boolean = true };
    public static readonly JsonValue False = new(JsonKind.Bool) { boolean = false };
    static readonly JsonValue EmptyObject = new(JsonKind.Object) { members = new Dictionary<string, JsonValue>(StringComparer.Ordinal) };

    bool boolean;
    long integer;
    double number;
    string? text;
    IReadOnlyList<JsonValue>? elements;
    IReadOnlyDictionary<string, JsonValue>? members;

    JsonValue(JsonKind kind)
    {
        Kind = kind;
    }

    public JsonKind Kind { get; }

    public static JsonValue Bool(bool value) => value ? True : False;

    public static JsonValue Integer(long value) => new(JsonKind.Integer) { integer = value };

    public static JsonValue Number(double value) => new(JsonKind.Number) { number = value };

    public static JsonValue Str(string value) => new(JsonKind.String) { text = value };

    public static JsonValue Array(IEnumerable<JsonValue> values) => new(JsonKind.Array) { elements = values.ToList() };

    public static JsonValue Array(params JsonValue[] values) => new(JsonKind.Array) { elements = values.ToList() };

    /// <summary>An object; a repeated key keeps its last value.</summary>
    public static JsonValue Object(IEnumerable<KeyValuePair<string, JsonValue>> values)
    {
        var dictionary = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            dictionary[pair.Key] = pair.Value;
        }
        return new JsonValue(JsonKind.Object) { members = dictionary };
    }

    /// <summary>An object; a repeated key keeps its last value.</summary>
    public static JsonValue Object(params (string Key, JsonValue Value)[] values)
    {
        if (values.Length == 0)
        {
            return EmptyObject;
        }
        var dictionary = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            dictionary[key] = value;
        }
        return new JsonValue(JsonKind.Object) { members = dictionary };
    }

    public static implicit operator JsonValue(bool value) => Bool(value);
    public static implicit operator JsonValue(int value) => Integer(value);
    public static implicit operator JsonValue(long value) => Integer(value);
    public static implicit operator JsonValue(double value) => Number(value);
    public static implicit operator JsonValue(string value) => Str(value);

    /// <summary>A string, or null when there is none; keeps result shapes stable for optional fields.</summary>
    public static JsonValue Optional(string? value) => value is null ? Null : Str(value);

    /// <summary>Seconds rounded to one decimal, the precision results report timeline positions in.</summary>
    public static JsonValue Seconds(double value) =>
        double.IsFinite(value) ? Number(Math.Round(value * 10, MidpointRounding.AwayFromZero) / 10) : Number(0);

    /// <summary>A member of an object; null for other values and missing keys.</summary>
    public JsonValue? this[string key] => members is not null && members.TryGetValue(key, out var value) ? value : null;

    public string? StringValue => Kind == JsonKind.String ? text : null;

    public bool? BoolValue => Kind == JsonKind.Bool ? boolean : null;

    /// <summary>Integers, and other numbers that are whole and fit (JSON Schema's "integer" accepts 20.0).</summary>
    public long? IntValue => Kind switch
    {
        JsonKind.Integer => integer,
        JsonKind.Number => ExactInteger(number),
        _ => null,
    };

    public double? DoubleValue => Kind switch
    {
        JsonKind.Integer => integer,
        JsonKind.Number => number,
        _ => null,
    };

    public IReadOnlyList<JsonValue>? ArrayValue => Kind == JsonKind.Array ? elements : null;

    public IReadOnlyDictionary<string, JsonValue>? ObjectValue => Kind == JsonKind.Object ? members : null;

    public bool IsNull => Kind == JsonKind.Null;

    /// <summary>Swift's <c>Int(exactly:)</c>: the value when it is whole and fits in 64 bits.</summary>
    internal static long? ExactInteger(double value)
    {
        if (!double.IsFinite(value) || Math.Floor(value) != value)
        {
            return null;
        }
        // 2^63 itself does not fit; every double below it that is whole does.
        if (value >= 9223372036854775808.0 || value < -9223372036854775808.0)
        {
            return null;
        }
        return (long)value;
    }

    // MARK: - Equality (Swift's synthesized Equatable: an integer never equals a number)

    public bool Equals(JsonValue? other)
    {
        if (other is null || other.Kind != Kind)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        switch (Kind)
        {
            case JsonKind.Null:
                return true;
            case JsonKind.Bool:
                return boolean == other.boolean;
            case JsonKind.Integer:
                return integer == other.integer;
            case JsonKind.Number:
                // IEEE equality, as Swift's Double ==: NaN differs from itself, -0.0 equals 0.0.
                return number == other.number;
            case JsonKind.String:
                return string.Equals(text, other.text, StringComparison.Ordinal);
            case JsonKind.Array:
                return elements!.Count == other.elements!.Count && elements.Zip(other.elements).All(pair => pair.First.Equals(pair.Second));
            default:
                if (members!.Count != other.members!.Count)
                {
                    return false;
                }
                foreach (var (key, value) in members)
                {
                    if (!other.members.TryGetValue(key, out var otherValue) || !value.Equals(otherValue))
                    {
                        return false;
                    }
                }
                return true;
        }
    }

    public override bool Equals(object? obj) => obj is JsonValue other && Equals(other);

    public override int GetHashCode() => Kind switch
    {
        JsonKind.Bool => boolean.GetHashCode(),
        JsonKind.Integer => integer.GetHashCode(),
        JsonKind.Number => number == 0 ? 0 : number.GetHashCode(),
        JsonKind.String => StringComparer.Ordinal.GetHashCode(text!),
        JsonKind.Array => elements!.Count,
        JsonKind.Object => members!.Count,
        _ => 0,
    };

    public static bool operator ==(JsonValue? left, JsonValue? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(JsonValue? left, JsonValue? right) => !(left == right);

    public override string ToString() => SerializedString;

    // MARK: - Parsing and writing

    /// <summary>
    /// Parses one JSON document; throws <see cref="JsonParseException"/> on anything that is
    /// not valid JSON. An escaped surrogate without its other half, which the JSON grammar
    /// allows, becomes U+FFFD.
    /// </summary>
    public static JsonValue Parse(ReadOnlySpan<byte> data)
    {
        var parser = new JsonParser(data.ToArray(), capturedKey: null);
        return parser.ParseDocument();
    }

    public static JsonValue Parse(string text) => Parse(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Parses a JSON-RPC message and also returns the source text of its top-level <c>id</c>
    /// when that is a number, so the reply can repeat it exactly: an id too large for a
    /// 64-bit integer, or written as <c>1.50</c>, would otherwise come back as a different text.
    /// </summary>
    internal static (JsonValue Message, string? NumericId) ParseMessage(ReadOnlySpan<byte> data)
    {
        var parser = new JsonParser(data.ToArray(), capturedKey: "id");
        var message = parser.ParseDocument();
        return (message, parser.CapturedNumber);
    }

    /// <summary>Compact UTF-8 JSON on one line (newlines inside strings are escaped), keys sorted.</summary>
    public byte[] Serialized() => Encoding.UTF8.GetBytes(SerializedString);

    /// <summary><see cref="Serialized"/> as a string.</summary>
    public string SerializedString
    {
        get
        {
            var output = new StringBuilder(256);
            JsonWriter.Write(this, output);
            return output.ToString();
        }
    }

    internal IEnumerable<KeyValuePair<string, JsonValue>> SortedMembers =>
        members!.OrderBy(pair => pair.Key, SwiftStringComparer.Instance);
}

/// <summary>Why a document is not valid JSON; only the fact matters to callers, which answer −32700.</summary>
public sealed class JsonParseException(int offset) : Exception($"Invalid JSON at byte {offset}")
{
    public int Offset { get; } = offset;
}

/// <summary>
/// Orders strings the way Swift's <c>&lt;</c> does: by the Unicode scalars of their
/// canonical (NFC) forms. For the ASCII keys Slovo writes this is plain byte order.
/// </summary>
internal sealed class SwiftStringComparer : IComparer<string>
{
    public static readonly SwiftStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }
        if (x is null)
        {
            return -1;
        }
        if (y is null)
        {
            return 1;
        }
        if (System.Text.Ascii.IsValid(x) && System.Text.Ascii.IsValid(y))
        {
            return string.CompareOrdinal(x, y);
        }
        var left = Normalized(x).EnumerateRunes();
        var right = Normalized(y).EnumerateRunes();
        while (true)
        {
            var hasLeft = left.MoveNext();
            var hasRight = right.MoveNext();
            if (!hasLeft || !hasRight)
            {
                return hasLeft ? 1 : hasRight ? -1 : 0;
            }
            var difference = left.Current.Value.CompareTo(right.Current.Value);
            if (difference != 0)
            {
                return difference;
            }
        }
    }

    static string Normalized(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // A lone surrogate cannot be normalized; compare it as it is.
            return text;
        }
    }
}

/// <summary>A strict RFC 8259 parser over UTF-8 bytes.</summary>
file sealed class JsonParser(byte[] bytes, string? capturedKey)
{
    /// <summary>Deeper nesting is rejected, so a line of a million <c>[</c> cannot exhaust the stack.</summary>
    const int MaximumDepth = 128;
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    int position;
    int depth;

    /// <summary>The source text of the top-level <c>id</c> member when its value is a number.</summary>
    public string? CapturedNumber { get; private set; }

    public JsonValue ParseDocument()
    {
        // A UTF-8 byte order mark is not JSON text, but some writers add one.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            position = 3;
        }
        SkipWhitespace();
        var value = ParseValue();
        SkipWhitespace();
        if (position != bytes.Length)
        {
            throw Failure();
        }
        return value;
    }

    JsonParseException Failure() => new(position);

    void SkipWhitespace()
    {
        while (position < bytes.Length && bytes[position] is 0x20 or 0x09 or 0x0A or 0x0D)
        {
            position++;
        }
    }

    JsonValue ParseValue()
    {
        if (position >= bytes.Length)
        {
            throw Failure();
        }
        switch (bytes[position])
        {
            case (byte)'{':
                return ParseObject();
            case (byte)'[':
                return ParseArray();
            case (byte)'"':
                return JsonValue.Str(ParseString());
            case (byte)'t':
                Expect("true"u8);
                return JsonValue.True;
            case (byte)'f':
                Expect("false"u8);
                return JsonValue.False;
            case (byte)'n':
                Expect("null"u8);
                return JsonValue.Null;
            case (byte)'-':
            case >= (byte)'0' and <= (byte)'9':
                return ParseNumber();
            default:
                throw Failure();
        }
    }

    void Expect(ReadOnlySpan<byte> literal)
    {
        if (position + literal.Length > bytes.Length || !bytes.AsSpan(position, literal.Length).SequenceEqual(literal))
        {
            throw Failure();
        }
        position += literal.Length;
    }

    void Enter()
    {
        depth++;
        if (depth > MaximumDepth)
        {
            throw Failure();
        }
    }

    JsonValue ParseObject()
    {
        Enter();
        try
        {
            position++;
            var members = new List<KeyValuePair<string, JsonValue>>();
            SkipWhitespace();
            if (position < bytes.Length && bytes[position] == (byte)'}')
            {
                position++;
                return JsonValue.Object(members);
            }
            while (true)
            {
                SkipWhitespace();
                if (position >= bytes.Length || bytes[position] != (byte)'"')
                {
                    throw Failure();
                }
                var key = ParseString();
                SkipWhitespace();
                if (position >= bytes.Length || bytes[position] != (byte)':')
                {
                    throw Failure();
                }
                position++;
                SkipWhitespace();
                // A repeated key keeps its last value, as most parsers do.
                var valueStart = position;
                var value = ParseValue();
                members.Add(new(key, value));
                if (depth == 1 && key == capturedKey)
                {
                    CapturedNumber = value.Kind is JsonKind.Integer or JsonKind.Number
                        ? Encoding.UTF8.GetString(bytes, valueStart, position - valueStart)
                        : null;
                }
                SkipWhitespace();
                if (position >= bytes.Length)
                {
                    throw Failure();
                }
                if (bytes[position] == (byte)',')
                {
                    position++;
                }
                else if (bytes[position] == (byte)'}')
                {
                    position++;
                    return JsonValue.Object(members);
                }
                else
                {
                    throw Failure();
                }
            }
        }
        finally
        {
            depth--;
        }
    }

    JsonValue ParseArray()
    {
        Enter();
        try
        {
            position++;
            var elements = new List<JsonValue>();
            SkipWhitespace();
            if (position < bytes.Length && bytes[position] == (byte)']')
            {
                position++;
                return JsonValue.Array(elements);
            }
            while (true)
            {
                SkipWhitespace();
                elements.Add(ParseValue());
                SkipWhitespace();
                if (position >= bytes.Length)
                {
                    throw Failure();
                }
                if (bytes[position] == (byte)',')
                {
                    position++;
                }
                else if (bytes[position] == (byte)']')
                {
                    position++;
                    return JsonValue.Array(elements);
                }
                else
                {
                    throw Failure();
                }
            }
        }
        finally
        {
            depth--;
        }
    }

    string ParseString()
    {
        position++;
        var output = new StringBuilder();
        var runStart = position;
        while (position < bytes.Length)
        {
            var current = bytes[position];
            switch (current)
            {
                case (byte)'"':
                    FlushRun(runStart, position, output);
                    position++;
                    return output.ToString();
                case (byte)'\\':
                    FlushRun(runStart, position, output);
                    position++;
                    output.Append(ParseEscape().ToString());
                    runStart = position;
                    break;
                case < 0x20:
                    // Raw control characters must be escaped inside JSON strings.
                    throw Failure();
                default:
                    position++;
                    break;
            }
        }
        throw Failure();
    }

    /// <summary>Validates UTF-8: an invalid sequence makes the whole document invalid.</summary>
    void FlushRun(int start, int end, StringBuilder output)
    {
        if (end <= start)
        {
            return;
        }
        try
        {
            output.Append(StrictUtf8.GetString(bytes, start, end - start));
        }
        catch (DecoderFallbackException)
        {
            throw new JsonParseException(start);
        }
    }

    Rune ParseEscape()
    {
        if (position >= bytes.Length)
        {
            throw Failure();
        }
        var current = bytes[position];
        position++;
        switch (current)
        {
            case (byte)'"': return new Rune('"');
            case (byte)'\\': return new Rune('\\');
            case (byte)'/': return new Rune('/');
            case (byte)'b': return new Rune(0x08);
            case (byte)'f': return new Rune(0x0C);
            case (byte)'n': return new Rune('\n');
            case (byte)'r': return new Rune('\r');
            case (byte)'t': return new Rune('\t');
            case (byte)'u':
                var first = ParseHex4();
                if (first is >= 0xD800 and < 0xDC00)
                {
                    // A high surrogate pairs with an escaped low surrogate right after it.
                    // Without one it stands alone, which the grammar allows and a string cannot
                    // hold: it becomes U+FFFD rather than costing the whole message and with it
                    // the id the client waits on. A following escape that is not a low
                    // surrogate is left to be read on its own.
                    if (!(position + 5 < bytes.Length && bytes[position] == (byte)'\\' && bytes[position + 1] == (byte)'u'))
                    {
                        return Rune.ReplacementChar;
                    }
                    var resume = position;
                    position += 2;
                    var second = ParseHex4();
                    if (second is < 0xDC00 or >= 0xE000)
                    {
                        position = resume;
                        return Rune.ReplacementChar;
                    }
                    return new Rune(0x10000 + ((first - 0xD800) << 10) + (second - 0xDC00));
                }
                // A low surrogate on its own, the other half of the same case.
                return Rune.IsValid(first) ? new Rune(first) : Rune.ReplacementChar;
            default:
                throw Failure();
        }
    }

    int ParseHex4()
    {
        if (position + 4 > bytes.Length)
        {
            throw Failure();
        }
        var value = 0;
        for (var index = 0; index < 4; index++)
        {
            var current = bytes[position];
            int digit = current switch
            {
                >= (byte)'0' and <= (byte)'9' => current - '0',
                >= (byte)'a' and <= (byte)'f' => current - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => current - 'A' + 10,
                _ => throw Failure(),
            };
            value = (value << 4) | digit;
            position++;
        }
        return value;
    }

    JsonValue ParseNumber()
    {
        var start = position;
        var isInteger = true;
        if (bytes[position] == (byte)'-')
        {
            position++;
        }
        if (position >= bytes.Length)
        {
            throw Failure();
        }
        if (bytes[position] == (byte)'0')
        {
            position++;
        }
        else if (IsDigit(bytes[position]))
        {
            while (position < bytes.Length && IsDigit(bytes[position])) position++;
        }
        else
        {
            throw Failure();
        }
        if (position < bytes.Length && bytes[position] == (byte)'.')
        {
            isInteger = false;
            position++;
            if (position >= bytes.Length || !IsDigit(bytes[position]))
            {
                throw Failure();
            }
            while (position < bytes.Length && IsDigit(bytes[position])) position++;
        }
        if (position < bytes.Length && bytes[position] is (byte)'e' or (byte)'E')
        {
            isInteger = false;
            position++;
            if (position < bytes.Length && bytes[position] is (byte)'+' or (byte)'-')
            {
                position++;
            }
            if (position >= bytes.Length || !IsDigit(bytes[position]))
            {
                throw Failure();
            }
            while (position < bytes.Length && IsDigit(bytes[position])) position++;
        }
        var text = Encoding.ASCII.GetString(bytes, start, position - start);
        if (isInteger && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
        {
            return JsonValue.Integer(whole);
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            throw new JsonParseException(start);
        }
        return JsonValue.Number(value);
    }

    static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
}

/// <summary>Compact serialization with sorted keys, escaping exactly as the Swift writer does.</summary>
file static class JsonWriter
{
    const string HexDigits = "0123456789abcdef";

    public static void Write(JsonValue value, StringBuilder output)
    {
        switch (value.Kind)
        {
            case JsonKind.Null:
                output.Append("null");
                break;
            case JsonKind.Bool:
                output.Append(value.BoolValue == true ? "true" : "false");
                break;
            case JsonKind.Integer:
                output.Append(value.IntValue!.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonKind.Number:
                // JSON has no NaN or infinity; whole doubles keep a ".0" so they read back as numbers.
                var number = value.DoubleValue!.Value;
                output.Append(double.IsFinite(number) ? SwiftFormat.Double(number) : "null");
                break;
            case JsonKind.String:
                WriteString(value.StringValue!, output);
                break;
            case JsonKind.Array:
                output.Append('[');
                var first = true;
                foreach (var element in value.ArrayValue!)
                {
                    if (!first)
                    {
                        output.Append(',');
                    }
                    first = false;
                    Write(element, output);
                }
                output.Append(']');
                break;
            default:
                output.Append('{');
                var firstMember = true;
                foreach (var (key, member) in value.SortedMembers)
                {
                    if (!firstMember)
                    {
                        output.Append(',');
                    }
                    firstMember = false;
                    WriteString(key, output);
                    output.Append(':');
                    Write(member, output);
                }
                output.Append('}');
                break;
        }
    }

    public static void WriteString(string text, StringBuilder output)
    {
        output.Append('"');
        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case 0x22:
                    output.Append("\\\"");
                    break;
                case 0x5C:
                    output.Append("\\\\");
                    break;
                case 0x0A:
                    output.Append("\\n");
                    break;
                case 0x0D:
                    output.Append("\\r");
                    break;
                case 0x09:
                    output.Append("\\t");
                    break;
                // Control characters, and the two separators some JavaScript line readers
                // split on, are escaped so a message always stays on one line.
                case < 0x20 or 0x7F or 0x2028 or 0x2029:
                    var code = rune.Value;
                    output.Append("\\u")
                        .Append(HexDigits[(code >> 12) & 0xF]).Append(HexDigits[(code >> 8) & 0xF])
                        .Append(HexDigits[(code >> 4) & 0xF]).Append(HexDigits[code & 0xF]);
                    break;
                default:
                    // A lone surrogate enumerates as U+FFFD, so the output is always valid UTF-8.
                    output.Append(rune.ToString());
                    break;
            }
        }
        output.Append('"');
    }
}

/// <summary>Swift's textual forms of numbers, where the two apps must print the same bytes.</summary>
public static class SwiftFormat
{
    /// <summary>
    /// <c>Double.description</c>: the shortest digits that read back as the same value, in
    /// plain notation with at least one decimal (<c>760.0</c>) when the decimal exponent is
    /// from −4 to 15, otherwise as <c>1e+16</c> or <c>1.5e-05</c>.
    /// </summary>
    public static string Double(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }
        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }
        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }
        // .NET's "R" is the shortest round-trip form; only its layout differs from Swift's.
        var shortest = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = shortest[0] == '-';
        var body = negative ? shortest[1..] : shortest;
        var exponent = 0;
        var exponentIndex = body.IndexOfAny(['E', 'e']);
        if (exponentIndex >= 0)
        {
            exponent = int.Parse(body.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            body = body[..exponentIndex];
        }
        var point = body.IndexOf('.');
        var integerDigits = point >= 0 ? body[..point] : body;
        var fractionDigits = point >= 0 ? body[(point + 1)..] : "";
        var digits = (integerDigits + fractionDigits).TrimStart('0');
        var leadingZeros = (integerDigits + fractionDigits).Length - digits.Length;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }
        // The value is 0.d1d2… × 10^(pointPosition); the scientific exponent is one less.
        var scientific = integerDigits.Length + exponent - leadingZeros - 1;

        var builder = new StringBuilder(32);
        if (negative)
        {
            builder.Append('-');
        }
        if (scientific < -4 || scientific >= 16)
        {
            builder.Append(digits[0]);
            if (digits.Length > 1)
            {
                builder.Append('.').Append(digits, 1, digits.Length - 1);
            }
            builder.Append('e').Append(scientific < 0 ? '-' : '+');
            builder.Append(Math.Abs(scientific).ToString("00", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
        if (scientific < 0)
        {
            builder.Append("0.").Append('0', -scientific - 1).Append(digits);
            return builder.ToString();
        }
        var wholeCount = scientific + 1;
        if (digits.Length <= wholeCount)
        {
            builder.Append(digits).Append('0', wholeCount - digits.Length).Append(".0");
        }
        else
        {
            builder.Append(digits, 0, wholeCount).Append('.').Append(digits, wholeCount, digits.Length - wholeCount);
        }
        return builder.ToString();
    }
}
