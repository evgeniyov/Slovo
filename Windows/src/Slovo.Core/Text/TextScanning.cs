using Slovo.Core.Model;

namespace Slovo.Core.Text;

/// <summary>A half-open range of positions in a text's grapheme clusters (Swift's <c>Range&lt;Int&gt;</c>).</summary>
internal readonly record struct CharacterRange(int LowerBound, int UpperBound)
{
    public int Count => UpperBound - LowerBound;
}

/// <summary>
/// Word scanning shared by the text stages. Positions are offsets into the text's grapheme
/// clusters (<see cref="TextElements.Characters"/>), so stages can edit characters without
/// juggling string indices.
/// </summary>
internal static class TextScanning
{
    /// <summary>Joiners that keep hyphenated words and contractions whole: «кое-что», "Let's".</summary>
    public static readonly HashSet<string> WordJoiners = new(StringComparer.Ordinal) { "-", "'", "’" };
    /// <summary>Also keeps file and package names whole: "Package.swift".</summary>
    public static readonly HashSet<string> NameJoiners = new(StringComparer.Ordinal) { "-", "'", "’", "." };
    /// <summary>Only contractions: a hyphen splits «IP-адрес» into two words.</summary>
    public static readonly HashSet<string> ApostropheJoiners = new(StringComparer.Ordinal) { "'", "’" };
    static readonly HashSet<string> NoJoiners = new(StringComparer.Ordinal);

    public static bool IsWordCharacter(string character) =>
        SwiftCharacter.IsLetter(character) || SwiftCharacter.IsNumber(character);

    /// <summary>
    /// Word ranges: runs of letters and digits; a joiner between two word characters stays
    /// inside the word.
    /// </summary>
    public static List<CharacterRange> Words(IReadOnlyList<string> characters, HashSet<string>? joiners = null)
    {
        joiners ??= NoJoiners;
        var ranges = new List<CharacterRange>();
        var index = 0;
        while (index < characters.Count)
        {
            if (!IsWordCharacter(characters[index]))
            {
                index += 1;
                continue;
            }
            var start = index;
            index += 1;
            while (index < characters.Count)
            {
                if (IsWordCharacter(characters[index]))
                {
                    index += 1;
                }
                else if (joiners.Contains(characters[index]) && index + 1 < characters.Count && IsWordCharacter(characters[index + 1]))
                {
                    index += 2;
                }
                else
                {
                    break;
                }
            }
            ranges.Add(new CharacterRange(start, index));
        }
        return ranges;
    }

    /// <summary>
    /// The comparison form of a character: lowercased, with ё read as е and the typographic
    /// apostrophe ’ read as '.
    /// </summary>
    public static string FoldCharacter(string character)
    {
        var lowered = SwiftUnicode.Lowercased(character);
        return lowered switch
        {
            "ё" => "е",
            "’" => "'",
            _ => lowered,
        };
    }

    /// <summary>The comparison form of a string: lowercased, with ё read as е and ’ as '.</summary>
    public static string Fold(string text)
    {
        var lowered = SwiftUnicode.Lowercased(text);
        if (!lowered.Contains('ё') && !lowered.Contains('’'))
        {
            return lowered;
        }
        // Foundation's replacingOccurrences matches whole characters: «ё\u0301» (ё with a stress
        // mark) is not «ё» and stays.
        var characters = TextElements.Characters(lowered);
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = characters[index] switch
            {
                "ё" => "е",
                "’" => "'",
                var other => other,
            };
        }
        return string.Concat(characters);
    }

    public static bool IsCyrillic(string character)
    {
        foreach (var rune in character.EnumerateRunes())
        {
            if (rune.Value is >= 0x0400 and <= 0x04FF)
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsLatinLetter(string character)
    {
        if (!SwiftCharacter.IsLetter(character))
        {
            return false;
        }
        var scalar = SwiftCharacter.FirstScalar(character);
        return scalar < 0x80 || scalar is >= 0x00C0 and <= 0x024F;
    }

    /// <summary>
    /// Replaces the character at <paramref name="index"/> when the case mapping stays one
    /// character ("ß" uppercases to "SS" and is left alone).
    /// </summary>
    public static void SetCase(IList<string> characters, int index, bool uppercase)
    {
        var source = characters[index];
        var mapped = uppercase ? SwiftUnicode.Uppercased(source) : SwiftUnicode.Lowercased(source);
        if (TextElements.Count(mapped) != 1)
        {
            return;
        }
        characters[index] = mapped;
    }

    /// <summary>Whether any character of the string satisfies the predicate (Swift's <c>contains(where:)</c> on a String).</summary>
    public static bool Any(string text, Func<string, bool> predicate)
    {
        foreach (var character in TextElements.Characters(text))
        {
            if (predicate(character))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether every character of the string satisfies the predicate (Swift's <c>allSatisfy</c> on a String).</summary>
    public static bool All(string text, Func<string, bool> predicate)
    {
        foreach (var character in TextElements.Characters(text))
        {
            if (!predicate(character))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The characters that satisfy the predicate, joined (Swift's <c>filter</c> on a String).</summary>
    public static string Filter(string text, Func<string, bool> predicate) =>
        string.Concat(TextElements.Characters(text).Where(predicate));
}

/// <summary>
/// Snippet expansions are inserted verbatim, so the stages after <see cref="SnippetExpander"/>
/// find them in the text and leave them alone.
/// </summary>
internal static class SnippetProtection
{
    /// <summary>
    /// Merged, sorted ranges of expansions present in the text; empty when the pipeline
    /// applied no snippet.
    /// </summary>
    public static List<CharacterRange> Ranges(IReadOnlyList<string> characters, TextPipelineResult result, IReadOnlyList<Snippet> snippets)
    {
        if (result.SnippetsApplied <= 0)
        {
            return [];
        }
        var found = new List<CharacterRange>();
        foreach (var snippet in snippets)
        {
            var expansion = TextElements.Characters(SwiftString.Canonical(snippet.Expansion));
            if (expansion.Length == 0 || expansion.Length > characters.Count)
            {
                continue;
            }
            var start = 0;
            while (start + expansion.Length <= characters.Count)
            {
                if (characters[start] == expansion[0] && SwiftString.SequenceEqualAt(characters, start, expansion))
                {
                    found.Add(new CharacterRange(start, start + expansion.Length));
                    start += expansion.Length;
                }
                else
                {
                    start += 1;
                }
            }
        }
        // Swift sorts by lower bound only; equal lower bounds merge into one range either way.
        found.Sort((left, right) => left.LowerBound.CompareTo(right.LowerBound));
        var merged = new List<CharacterRange>();
        foreach (var range in found)
        {
            if (merged.Count > 0 && range.LowerBound <= merged[^1].UpperBound)
            {
                var last = merged[^1];
                merged[^1] = new CharacterRange(last.LowerBound, Math.Max(last.UpperBound, range.UpperBound));
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }

    /// <summary>The text cut into consecutive pieces, marking the protected ones.</summary>
    public static List<(string Text, bool IsProtected)> Segments(IReadOnlyList<string> characters, IReadOnlyList<CharacterRange> protectedRanges)
    {
        var pieces = new List<(string, bool)>();
        var cursor = 0;
        foreach (var range in protectedRanges)
        {
            if (range.LowerBound > cursor)
            {
                pieces.Add((SwiftString.Join(characters, cursor, range.LowerBound), false));
            }
            pieces.Add((SwiftString.Join(characters, range.LowerBound, range.UpperBound), true));
            cursor = range.UpperBound;
        }
        if (cursor < characters.Count || pieces.Count == 0)
        {
            pieces.Add((SwiftString.Join(characters, cursor, characters.Count), false));
        }
        return pieces;
    }
}
