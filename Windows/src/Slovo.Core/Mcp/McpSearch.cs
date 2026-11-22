using System.Globalization;
using System.Text;

namespace Slovo.Core.Mcp;

/// <summary>
/// Search matching: both sides lowercased, ё folded to е, diacritics folded and runs of
/// whitespace collapsed (port of the macOS <c>MCPSearch</c>).
/// </summary>
internal static class McpSearch
{
    public static string Normalized(string text) => FastNormalized(text) ?? SlowNormalized(text);

    /// <summary>Query words with punctuation and symbols trimmed from their edges.</summary>
    public static List<string> Words(string query) =>
        Normalized(query).Split(' ')
            .Select(TrimPunctuationAndSymbols)
            .Where(word => word.Length > 0)
            .ToList();

    public static bool Matches(string normalizedText, IReadOnlyList<string> words) =>
        words.All(word => normalizedText.Contains(word, StringComparison.Ordinal));

    /// <summary>
    /// About <paramref name="radius"/> characters on each side of the first match, with
    /// ellipses where the text was cut; the start of the text when nothing matches.
    /// </summary>
    public static string Snippet(string text, IReadOnlyList<string> words, int radius = 120)
    {
        var characters = McpUnicode.Characters(text);
        var folded = new StringBuilder(text.Length);
        var origins = new List<int>(characters.Count);
        var lastWasSpace = true;
        for (var offset = 0; offset < characters.Count; offset++)
        {
            var character = characters[offset];
            if (McpUnicode.IsWhitespaceCharacter(character))
            {
                if (!lastWasSpace)
                {
                    folded.Append(' ');
                    origins.Add(offset);
                    lastWasSpace = true;
                }
                continue;
            }
            lastWasSpace = false;
            foreach (var piece in McpUnicode.Characters(Normalized(character)))
            {
                folded.Append(piece);
                origins.Add(offset);
            }
        }
        var haystack = folded.ToString();
        (int Position, int Length)? first = null;
        foreach (var word in words)
        {
            var index = haystack.IndexOf(word, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }
            var position = Text.TextElements.Count(haystack[..index]);
            if (first is null || position < first.Value.Position)
            {
                first = (position, Text.TextElements.Count(word));
            }
        }
        if (first is not { } match || origins.Count == 0)
        {
            return McpMeetingView.Capped(text, radius * 2);
        }
        var start = origins[Math.Min(match.Position, origins.Count - 1)];
        var end = origins[Math.Min(match.Position + Math.Max(0, match.Length - 1), origins.Count - 1)] + 1;
        var lower = Math.Max(0, start - radius);
        var upper = Math.Min(characters.Count, end + radius);
        var result = string.Concat(characters.Skip(lower).Take(upper - lower));
        if (lower > 0)
        {
            result = "…" + result;
        }
        if (upper < characters.Count)
        {
            result += "…";
        }
        return result;
    }

    /// <summary>
    /// Foundation's <c>folding(options: [.caseInsensitive, .diacriticInsensitive,
    /// .widthInsensitive])</c> followed by <c>lowercased()</c>, ё → е and collapsed whitespace,
    /// for anything outside the common Latin and Cyrillic letters.
    /// </summary>
    public static string SlowNormalized(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            Fold(rune, output);
        }
        var lowered = output.ToString().Replace('ё', 'е');
        return McpMeetingView.JoinedWords(lowered);
    }

    /// <summary>
    /// One scalar folded onto <paramref name="output"/>: width forms to their ordinary forms,
    /// canonical decomposition with the marks dropped, then full case folding. A mark that
    /// would start the text has nothing to decorate and is kept, as Foundation keeps it.
    /// </summary>
    static void Fold(Rune rune, StringBuilder output)
    {
        var value = rune.Value;
        // Letters Foundation leaves whole although Unicode decomposes them: Hebrew presentation
        // forms and the Greek spacing accents built on psili and dasia.
        if ((value is >= 0xFB1D and <= 0xFB4F || value is >= 0x1FCD and <= 0x1FCF or >= 0x1FDD and <= 0x1FDF) && !IsMark(rune))
        {
            output.Append(rune.ToString());
            return;
        }
        foreach (var part in WidthFolded(rune).Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (IsMark(part) && output.Length > 0)
            {
                continue;
            }
            foreach (var folded in CaseFolded(part.ToString()).EnumerateRunes())
            {
                output.Append(Rune.ToLowerInvariant(folded).ToString());
            }
        }
    }

    static bool IsMark(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>Fullwidth and halfwidth forms as their ordinary width (Foundation's <c>.widthInsensitive</c>).</summary>
    static string WidthFolded(Rune rune) => rune.Value switch
    {
        >= 0xFF01 and <= 0xFF5E => ((char)(rune.Value - 0xFEE0)).ToString(),
        0xFF5F => "⦅",
        0xFF60 => "⦆",
        >= 0xFF61 and <= 0xFFDC or >= 0xFFE0 and <= 0xFFE2 or >= 0xFFE4 and <= 0xFFE6 or >= 0xFFE8 and <= 0xFFEE =>
            rune.ToString().Normalize(NormalizationForm.FormKC),
        0x3000 => " ",
        _ => rune.ToString(),
    };

    /// <summary>Full case folding where it differs from lowercasing (CaseFolding.txt, status F and C).</summary>
    static string CaseFolded(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        foreach (var rune in text.EnumerateRunes())
        {
            builder.Append(rune.Value switch
            {
                0x00B5 => "μ",
                0x00DF or 0x1E9E => "ss",
                0x0130 => "i",
                0x0149 => "ʼn",
                0x017F => "s",
                0x01F0 => "ǰ",
                0x0345 => "ι",
                0x0390 => "ΐ",
                0x03B0 => "ΰ",
                0x03C2 => "σ",
                0x03D0 => "β",
                0x03D1 => "θ",
                0x03D5 => "φ",
                0x03D6 => "π",
                0x03F0 => "κ",
                0x03F1 => "ρ",
                0x03F5 => "ε",
                0x0587 => "եւ",
                0x1E96 => "ẖ",
                0x1E97 => "ẗ",
                0x1E98 => "ẘ",
                0x1E99 => "ẙ",
                0x1E9A => "aʾ",
                0x1E9B => "ṡ",
                0x1FBE => "ι",
                0xFB00 => "ff",
                0xFB01 => "fi",
                0xFB02 => "fl",
                0xFB03 => "ffi",
                0xFB04 => "ffl",
                0xFB05 or 0xFB06 => "st",
                0xFB13 => "մն",
                0xFB14 => "մե",
                0xFB15 => "մի",
                0xFB16 => "վն",
                0xFB17 => "մխ",
                _ => Rune.ToLowerInvariant(rune).ToString(),
            });
        }
        return builder.ToString();
    }

    /// <summary>
    /// The same result as <see cref="SlowNormalized"/> for ASCII, the basic Cyrillic letters
    /// and common punctuation, without the per-string folding cost, because search reads every
    /// line of every meeting. Null when the text holds anything else.
    /// </summary>
    public static string? FastNormalized(string text)
    {
        var output = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            int mapped;
            switch (value)
            {
                case 0x20 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0xA0 or >= 0x2000 and <= 0x200A or 0x202F:
                    pendingSpace = output.Length > 0;
                    continue;
                case >= 0x41 and <= 0x5A:
                    mapped = value + 0x20;
                    break;
                case >= 0x21 and <= 0x7E:
                    mapped = value;
                    break;
                case 0x0401 or 0x0451:
                    mapped = 0x0435; // Ё ё → е
                    break;
                case 0x0419 or 0x0439:
                    mapped = 0x0438; // Й й → и, as diacritic folding does
                    break;
                case >= 0x0410 and <= 0x042F:
                    mapped = value + 0x20;
                    break;
                case >= 0x0430 and <= 0x044F:
                    mapped = value;
                    break;
                case 0xAB or 0xBB or >= 0x2010 and <= 0x2027 or >= 0x2030 and <= 0x205E:
                    mapped = value;
                    break;
                default:
                    return null;
            }
            if (pendingSpace)
            {
                output.Append(' ');
                pendingSpace = false;
            }
            output.Append((char)mapped);
        }
        return output.ToString();
    }

    /// <summary>Swift's <c>trimmingCharacters(in: .punctuationCharacters.union(.symbols))</c>.</summary>
    static string TrimPunctuationAndSymbols(string word)
    {
        var runes = word.EnumerateRunes().ToList();
        var start = 0;
        var end = runes.Count;
        while (start < end && IsPunctuationOrSymbol(runes[start])) start++;
        while (end > start && IsPunctuationOrSymbol(runes[end - 1])) end--;
        return string.Concat(runes.Skip(start).Take(end - start).Select(rune => rune.ToString()));
    }

    static bool IsPunctuationOrSymbol(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
        or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
        or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation
        or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol
        or UnicodeCategory.OtherSymbol;
}
