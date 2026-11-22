using System.Globalization;
using System.Text;

namespace Slovo.Core.Text;

/// <summary>
/// String helpers that count the way Swift does. Swift's <c>String.count</c> and
/// <c>prefix(_:)</c> work on grapheme clusters (what a reader sees as one character),
/// so limits such as "at most 40 characters" must count text elements, not UTF-16 units,
/// for both apps to cut a name or note at the same place.
/// </summary>
public static class TextElements
{
    /// <summary>The number of grapheme clusters.</summary>
    public static int Count(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>
    /// The grapheme clusters of a text, Swift's <c>Array(text)</c>: each element is what
    /// Swift calls a <c>Character</c>. Positions into this array are what the text stages
    /// call offsets.
    /// </summary>
    public static string[] Characters(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }
        var characters = new List<string>(text.Length);
        var span = text.AsSpan();
        var index = 0;
        while (index < span.Length)
        {
            var length = Math.Max(1, StringInfo.GetNextTextElementLength(span[index..]));
            characters.Add(length == 1 && span[index] < AsciiStrings.Length ? AsciiStrings[span[index]] : text.Substring(index, length));
            index += length;
        }
        return characters.ToArray();
    }

    /// <summary>One-character strings for ASCII, so splitting mostly-ASCII text allocates little.</summary>
    static readonly string[] AsciiStrings = Enumerable.Range(0, 128).Select(c => ((char)c).ToString()).ToArray();

    /// <summary>The first <paramref name="count"/> grapheme clusters.</summary>
    public static string Prefix(string text, int count)
    {
        if (count <= 0)
        {
            return "";
        }
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var taken = 0;
        var end = 0;
        while (taken < count && enumerator.MoveNext())
        {
            end = enumerator.ElementIndex + ((string)enumerator.Current).Length;
            taken++;
        }
        return taken < count ? text : text[..end];
    }

    /// <summary>Swift's <c>Character.isWhitespace || isNewline</c> for a single scalar.</summary>
    public static bool IsWhitespace(Rune rune) => Rune.IsWhiteSpace(rune);

    /// <summary>Words separated by any whitespace, joined by single spaces (Swift's <c>normalizedText</c>).</summary>
    public static string NormalizeWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>Trims spaces and tabs (Swift's <c>.whitespaces</c> set, without newlines).</summary>
    public static string TrimSpaces(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsHorizontalSpace(text[start])) start++;
        while (end > start && IsHorizontalSpace(text[end - 1])) end--;
        return text[start..end];
    }

    /// <summary>Trims whitespace and newlines (Swift's <c>.whitespacesAndNewlines</c>).</summary>
    public static string TrimAll(string text) => text.Trim();

    static bool IsHorizontalSpace(char c) =>
        c == ' ' || c == '\t' || (char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator);
}
