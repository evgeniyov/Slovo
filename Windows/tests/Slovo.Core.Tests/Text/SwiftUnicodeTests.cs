using System.Globalization;
using Slovo.Core.Text;

namespace Slovo.Core.Tests.Text;

/// <summary>
/// Swift's String and Character semantics the port reproduces: grapheme clusters, the
/// Alphabetic property, full case mapping, canonical equivalence and scalar ordering.
/// </summary>
public class SwiftUnicodeTests
{
    [Theory]
    [InlineData("ß", "SS")]
    [InlineData("straße", "STRASSE")]
    [InlineData("ﬁle", "FILE")]
    [InlineData("ı", "I")]
    [InlineData("ǰ", "J\u030C")]
    [InlineData("ёлка", "ЁЛКА")]
    public void UppercasingIsFullCaseMapping(string text, string expected) => Assert.Equal(expected, SwiftUnicode.Uppercased(text));

    [Theory]
    [InlineData("İ", "i\u0307")]
    [InlineData("ΟΔΟΣ", "οδοσ")]
    [InlineData("ẞ", "ß")]
    [InlineData("ЁЛКА Docker", "ёлка docker")]
    [InlineData("ǅ", "ǆ")]
    public void LowercasingIsFullCaseMappingWithoutContext(string text, string expected) => Assert.Equal(expected, SwiftUnicode.Lowercased(text));

    [Fact]
    public void CaseMappingResultsAreCanonical()
    {
        // ΐ uppercases to Ι + two combining marks, which NFC writes as Ϊ + one mark.
        Assert.Equal("Ϊ\u0301", SwiftUnicode.Uppercased("ΐ"));
        Assert.Equal("ASCII ONLY", SwiftUnicode.Uppercased("ASCII only"));
    }

    [Fact]
    public void LettersAreAlphabeticScalars()
    {
        // Circled letters and combining vowel signs are Alphabetic, though .NET does not call them letters.
        Assert.True(SwiftUnicode.IsAlphabetic('Ⓐ'));
        Assert.False(char.IsLetter('Ⓐ'));
        Assert.True(SwiftUnicode.IsAlphabetic(0x093E));
        Assert.True(SwiftUnicode.IsAlphabetic('ж'));
        Assert.False(SwiftUnicode.IsAlphabetic('5'));
        Assert.False(SwiftUnicode.IsAlphabetic('-'));
        Assert.True(SwiftCharacter.IsLetter("e\u0301"));
        Assert.False(SwiftCharacter.IsLetter("\u0301"));
    }

    [Fact]
    public void NumbersHaveANumericType()
    {
        Assert.True(SwiftUnicode.IsNumeric('7'));
        Assert.True(SwiftUnicode.IsNumeric('½'));
        Assert.True(SwiftUnicode.IsNumeric('Ⅻ'));
        // CJK numerals have a numeric type in the Unicode data, though .NET gives them no value.
        Assert.True(SwiftUnicode.IsNumeric('一'));
        Assert.False(SwiftUnicode.IsNumeric('a'));
    }

    [Fact]
    public void CharacterCaseFollowsSwift()
    {
        Assert.True(SwiftCharacter.IsUppercase("Ⓐ"));
        Assert.True(SwiftCharacter.IsLowercase("ⓐ"));
        Assert.True(SwiftCharacter.IsUppercase("Ж"));
        Assert.True(SwiftCharacter.IsUppercase("E\u0301"));
        // A titlecase letter is neither.
        Assert.False(SwiftCharacter.IsUppercase("ǅ"));
        Assert.False(SwiftCharacter.IsLowercase("ǅ"));
        Assert.False(SwiftCharacter.IsUppercase("5"));
        Assert.False(SwiftCharacter.IsLowercase("5"));
        Assert.True(SwiftCharacter.IsLowercase("ß"));
    }

    [Fact]
    public void WhitespaceAndNewlines()
    {
        Assert.True(SwiftCharacter.IsNewline("\r\n"));
        Assert.True(SwiftCharacter.IsWhitespace("\r\n"));
        Assert.True(SwiftCharacter.IsNewline("\u2028"));
        Assert.True(SwiftCharacter.IsWhitespace("\u00A0"));
        Assert.False(SwiftCharacter.IsNewline("\u00A0"));
        Assert.False(SwiftCharacter.IsWhitespace("\u200B"));
    }

    [Theory]
    [InlineData("e\u0301", 1)]
    [InlineData("\r\n", 1)]
    [InlineData("👨\u200D👩\u200D👧", 1)]
    [InlineData("🇷🇺🇺🇸", 2)]
    [InlineData("докер", 5)]
    public void CharactersAreGraphemeClusters(string text, int count)
    {
        Assert.Equal(count, TextElements.Characters(text).Length);
        Assert.Equal(count, TextElements.Count(text));
        Assert.Equal(text, string.Concat(TextElements.Characters(text)));
    }

    [Fact]
    public void StringsCompareByScalarValue()
    {
        // UTF-16 puts the surrogate pair of 😀 before U+FF01; Swift compares scalars.
        Assert.True(string.CompareOrdinal("！", "😀") > 0);
        Assert.True(SwiftString.CompareScalars("！", "😀") < 0);
        Assert.True(SwiftString.CompareScalars("Docker", "Redis") < 0);
        Assert.Equal(0, SwiftString.CompareScalars("докер", "докер"));
        Assert.True(SwiftString.CompareScalars("док", "докер") < 0);
    }

    [Fact]
    public void ContainsAndHasPrefixMatchWholeCharacters()
    {
        Assert.False(SwiftString.Contains("a/\u0301b", "/"));
        Assert.True(SwiftString.Contains("a/b", "/"));
        Assert.False(SwiftString.HasPrefix("www.\u0301x", "www."));
        Assert.True(SwiftString.HasPrefix("www.x", "www."));
        Assert.True(SwiftString.HasPrefix("www.", "www."));
    }

    [Fact]
    public void CanonicalFormIsNfcWithoutLoneSurrogates()
    {
        Assert.Equal("ё", SwiftString.Canonical("е\u0308"));
        Assert.Equal("a\uFFFDb", SwiftString.Canonical("a\uDC00b"));
        Assert.True(SwiftString.CanonicalEquals("й", "и\u0306"));
    }

    [Fact]
    public void FoldingReadsYoAsYeAndTheTypographicApostropheAsPlain()
    {
        Assert.Equal("елка's", TextScanning.Fold("Ёлка’s"));
        Assert.Equal("е", TextScanning.FoldCharacter("Ё"));
        // A stressed ё is another character and stays.
        Assert.Equal("ё\u0301", TextScanning.Fold("Ё\u0301"));
    }

    [Fact]
    public void DiacriticFoldingKeepsTheBaseLetter()
    {
        Assert.Equal('e', SwiftUnicode.AsciiFolding('é'));
        Assert.Equal('a', SwiftUnicode.AsciiFolding('å'));
        Assert.Equal(0, SwiftUnicode.AsciiFolding('ø'));
        Assert.Equal(0, SwiftUnicode.AsciiFolding('ж'));
    }

    [Fact]
    public void TablesDoNotDependOnTheCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("I", SwiftUnicode.Uppercased("i"));
            Assert.Equal("i", SwiftUnicode.Lowercased("I"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
