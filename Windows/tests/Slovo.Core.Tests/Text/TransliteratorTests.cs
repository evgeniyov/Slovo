using Slovo.Core.Text;

namespace Slovo.Core.Tests.Text;

/// <summary>Port of TextTransliteratorTests.</summary>
public class TransliteratorTests
{
    [Fact]
    public void SpecExamples()
    {
        Assert.Equal("kubernetes", Transliterator.Latin("кубернетес"));
        Assert.Equal("githabekshens", Transliterator.CompactLatin("гитхаб экшенс"));
        Assert.Equal("yuzer defolts", Transliterator.Latin("юзер дефолтс"));
    }

    [Fact]
    public void LatinDigitsAndPunctuationPassThroughLowercased() =>
        Assert.Equal("swift 6, yozhik!", Transliterator.Latin("Swift 6, Ёжик!"));

    [Fact]
    public void SoftAndHardSignsVanish()
    {
        Assert.Equal("model", Transliterator.Latin("модель"));
        Assert.Equal("shchuka, obekt, chay, tsvet, y", Transliterator.Latin("щука, объект, чай, цвет, ы"));
    }

    [Fact]
    public void CompactLatinKeepsOnlyLettersAndDigits()
    {
        Assert.Equal("packageswift", Transliterator.CompactLatin("Package.swift"));
        Assert.Equal("cafe5", Transliterator.CompactLatin("Café №5"));
        // Diacritics fold away; letters with no decomposition (ø, ł, ß) are not ASCII and go.
        Assert.Equal("eaou", Transliterator.CompactLatin("éàöü øłß"));
        // A decomposed «й» is still «й».
        Assert.Equal("moy", Transliterator.CompactLatin("мои\u0306"));
    }

    [Fact]
    public void SpellingKeyBringsRussianAndEnglishSpellingsTogether()
    {
        Assert.Equal(SpellingKey.Make(["Docker"]), SpellingKey.Make(["докер"]));
        Assert.Equal(SpellingKey.Make(["Postgres"]), SpellingKey.Make(["постгресс"]));
        Assert.Equal(SpellingKey.Make(["Option"]), SpellingKey.Make(["опшн"]));
        Assert.Equal(SpellingKey.Make(["Whisper"]), SpellingKey.Make(["виспер"]));
        Assert.Equal("githubakshns", SpellingKey.Make(["GitHub", "Actions"]));
        Assert.Equal("ekskode", SpellingKey.Make(["Xcode"]));
        Assert.Equal("jison", SpellingKey.Make(["Джисон"]));
    }

    [Fact]
    public void VoicingPairsCostHalfInConsonantDistance()
    {
        Assert.Equal(0.5, SpellingKey.ConsonantDistance("slnd", "slnt"));
        Assert.Equal(1, SpellingKey.ConsonantDistance("vrs", "vrsl"));
    }
}
