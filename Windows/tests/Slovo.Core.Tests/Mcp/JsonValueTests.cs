using System.Text;
using Slovo.Core.Mcp;

namespace Slovo.Core.Tests.Mcp;

/// <summary>The hand-written JSON parser and writer: exact ids, strictness, one-line output and Swift's number text.</summary>
public sealed class JsonValueTests
{
    static JsonValue Parse(string text) => JsonValue.Parse(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ParsesEveryKindOfValue()
    {
        var value = Parse(""" { "a" : [1, -2, 3.5, 1e3, true, false, null], "b" : "x\"\\\/\b\f\n\r\t\u0041\u00e9\ud83d\ude00" } """);
        Assert.Equal(JsonValue.Array(1, -2, 3.5, 1000.0, true, false, JsonValue.Null), value["a"]);
        Assert.Equal(JsonValue.Str("x\"\\/\b\f\n\r\tAé😀"), value["b"]);
    }

    [Fact]
    public void IntegersStayIntegers()
    {
        Assert.Equal(JsonValue.Integer(42), Parse("42"));
        Assert.Equal(JsonValue.Integer(0), Parse("-0"));
        Assert.Equal(JsonValue.Integer(9_007_199_254_740_993), Parse("9007199254740993"));
        Assert.Equal(JsonValue.Number(1.0), Parse("1.0"));
        Assert.Equal(JsonValue.Number(100), Parse("1E2"));
        // Larger than a 64-bit integer: a number, not a failure.
        Assert.Equal(JsonValue.Number(1.8446744073709551616e19), Parse("18446744073709551616"));
        Assert.Equal(20, JsonValue.Number(20.0).IntValue);
        Assert.Null(JsonValue.Number(20.5).IntValue);
        Assert.NotEqual(JsonValue.Integer(1), JsonValue.Number(1.0));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("[1,]")]
    [InlineData("{\"a\":1,}")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("-")]
    [InlineData("+1")]
    [InlineData("1e")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("tru")]
    [InlineData("nul")]
    [InlineData("\"unterminated")]
    [InlineData("\"raw\ncontrol\"")]
    [InlineData("\"bad \\x escape\"")]
    [InlineData("\"\\ud800\\uzzzz\"")]
    [InlineData("{\"a\" 1}")]
    [InlineData("{1:2}")]
    [InlineData("[1 2]")]
    [InlineData("{} {}")]
    [InlineData("[1]x")]
    [InlineData("1e400")]
    public void RejectsInvalidDocuments(string text) => Assert.Throws<JsonParseException>(() => Parse(text));

    [Fact]
    public void RejectsInvalidUtf8AndDeepNesting()
    {
        Assert.Throws<JsonParseException>(() => JsonValue.Parse([0x22, 0xFF, 0xFE, 0x22]));
        Assert.Throws<JsonParseException>(() => JsonValue.Parse([0x22, 0xED, 0xA0, 0x80, 0x22]));
        Assert.Throws<JsonParseException>(() => Parse(new string('[', 200) + new string(']', 200)));
        Parse(new string('[', 100) + new string(']', 100));
        Assert.Equal(JsonValue.Integer(1), JsonValue.Parse([0xEF, 0xBB, 0xBF, (byte)'1']));
    }

    /// <summary>The grammar allows an escaped surrogate without its other half; it reads as U+FFFD.</summary>
    [Fact]
    public void UnpairedSurrogatesBecomeReplacementCharacters()
    {
        Assert.Equal(JsonValue.Str("\uFFFD"), Parse("\"\\ud800\""));
        Assert.Equal(JsonValue.Str("ab\uFFFD"), Parse("\"ab\\ud83d\""));
        Assert.Equal(JsonValue.Str("\uFFFDx"), Parse("\"\\ude00x\""));
        Assert.Equal(JsonValue.Str("\uFFFDA"), Parse("\"\\ud800\\u0041\""));
        Assert.Equal(JsonValue.Str("\uFFFD\U00010000"), Parse("\"\\ud800\\ud800\\udc00\""));
        Assert.Equal(JsonValue.Str("😀"), Parse("\"\\ud83d\\ude00\""));
    }

    [Fact]
    public void TheSourceTextOfANumericIdIsKept()
    {
        Assert.Equal("12345678901234567890", JsonValue.ParseMessage("""{"id": 12345678901234567890, "x": {"id": 1}}"""u8).NumericId);
        Assert.Equal("1.50", JsonValue.ParseMessage("""{"id":1.50}"""u8).NumericId);
        Assert.Null(JsonValue.ParseMessage("""{"id":"7"}"""u8).NumericId);
        Assert.Null(JsonValue.ParseMessage("""{"x":{"id":7}}"""u8).NumericId);
    }

    [Fact]
    public void RepeatedKeysKeepTheLastValue() => Assert.Equal(JsonValue.Object(("a", 2)), Parse("""{"a":1,"a":2}"""));

    [Fact]
    public void SerializationIsCompactSortedAndOneLine()
    {
        var value = JsonValue.Object(
            ("z", 1), ("a", JsonValue.Array(true, JsonValue.Null, 2.5, 3.0)), ("m", "line\nbreak\r\t\u0001\u2028\"\\/ é😀"));
        Assert.Equal("""{"a":[true,null,2.5,3.0],"m":"line\nbreak\r\t\u0001\u2028\"\\/ é😀","z":1}""", value.SerializedString);
        Assert.Equal(value, JsonValue.Parse(value.Serialized()));
        Assert.Equal("null", JsonValue.Number(double.NaN).SerializedString);
        Assert.Equal("9007199254740993", JsonValue.Integer(9_007_199_254_740_993).SerializedString);
        // A lone surrogate in a .NET string is written as U+FFFD, never as invalid UTF-8.
        Assert.Equal("\"a\uFFFDb\"", JsonValue.Str("a\uD800b").SerializedString);
        // Keys sort by Unicode scalars, as Swift strings do: "_" after upper case, before lower case.
        Assert.Equal("""{"B":1,"_meta":2,"a":3,"é":4,"ﬀ":6,"𝐀":5}""",
            JsonValue.Object(("ﬀ", 6), ("𝐀", 5), ("é", 4), ("a", 3), ("_meta", 2), ("B", 1)).SerializedString);
    }

    [Theory]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(123456789012345680.0, "1.2345678901234568e+17")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(0.0, "0.0")]
    [InlineData(5.0, "5.0")]
    [InlineData(760.2, "760.2")]
    [InlineData(3852.4, "3852.4")]
    [InlineData(1e20, "1e+20")]
    [InlineData(9.3e18, "9.3e+18")]
    [InlineData(359999999.0, "359999999.0")]
    [InlineData(780000000.25, "780000000.25")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(1.0 / 3.0, "0.3333333333333333")]
    [InlineData(2e-7, "2e-07")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(double.MaxValue, "1.7976931348623157e+308")]
    [InlineData(100.0, "100.0")]
    [InlineData(1234567.0, "1234567.0")]
    [InlineData(12345678901234567.0, "1.2345678901234568e+16")]
    [InlineData(1.5e300, "1.5e+300")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(double.NaN, "nan")]
    [InlineData(double.PositiveInfinity, "inf")]
    public void DoublesAreWrittenAsSwiftDescribesThem(double value, string expected) =>
        Assert.Equal(expected, SwiftFormat.Double(value));

    [Fact]
    public void RoundTripsRandomDocuments()
    {
        var random = new Random(7);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var value = RandomValue(random, 0);
            Assert.Equal(value, JsonValue.Parse(value.Serialized()));
        }
    }

    static JsonValue RandomValue(Random random, int depth)
    {
        switch (random.Next(depth > 3 ? 5 : 7))
        {
            case 0:
                return JsonValue.Null;
            case 1:
                return random.Next(2) == 0;
            case 2:
                return random.NextInt64(long.MinValue, long.MaxValue);
            case 3:
                return (random.NextDouble() - 0.5) * 2e12;
            case 4:
                var builder = new StringBuilder();
                for (var index = random.Next(12); index > 0; index--)
                {
                    var scalar = random.Next(0x3000);
                    if (scalar is < 0xD800 or > 0xDFFF)
                    {
                        builder.Append(char.ConvertFromUtf32(scalar));
                    }
                }
                return builder.ToString();
            case 5:
                return JsonValue.Array(Enumerable.Range(0, random.Next(4)).Select(_ => RandomValue(random, depth + 1)).ToList());
            default:
                return JsonValue.Object(Enumerable.Range(0, random.Next(4))
                    .Select(index => new KeyValuePair<string, JsonValue>($"k{index}", RandomValue(random, depth + 1))));
        }
    }
}
