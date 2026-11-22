using System.Text;
using Slovo.Core.Model;

namespace Slovo.Core.Text;

/// <summary>
/// Auto cleanup: removes filler words and immediate repetitions, then fixes spacing around
/// punctuation.
/// </summary>
public sealed class FillerCleaner : ITextStage
{
    public static readonly IReadOnlySet<string> RussianFillers = new HashSet<string>(["э", "ээ", "эм", "ммм"], StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> EnglishFillers = new HashSet<string>(["um", "uh", "erm", "hmm"], StringComparer.Ordinal);

    /// <summary>The cleaned text; with both options off the text comes back untouched.</summary>
    public string Clean(string text, AutoCleanupOptions options) => Clean(text, options, atStart: true, atEnd: true);

    /// <summary>
    /// <paramref name="atStart"/> and <paramref name="atEnd"/> say whether the piece touches
    /// the ends of the whole text, so a piece next to a protected snippet keeps its edge spacing.
    /// </summary>
    internal string Clean(string text, AutoCleanupOptions options, bool atStart, bool atEnd)
    {
        if (!(options.RemoveFillers || options.RemoveRepetitions) || text.Length == 0)
        {
            return text;
        }
        var split = new SplitText(SwiftString.Canonical(text));
        if (options.RemoveFillers)
        {
            split.RemoveWords(IsFiller);
        }
        if (options.RemoveRepetitions)
        {
            split.RemoveRepetitions();
        }
        return FixSpacing(split.Joined, atStart, atEnd);
    }

    /// <summary>
    /// A filler or a drawn-out one: «эээ», «эмм», "ummm". Shorter forms than the listed ones
    /// stay, so «м» and «мм» are not treated as «ммм».
    /// </summary>
    internal static bool IsFiller(string word)
    {
        var letters = TextScanning.Filter(TextScanning.Fold(word), SwiftCharacter.IsLetter);
        if (letters.Length == 0 || !FillerShapes.TryGetValue(CollapseRuns(letters), out var minimum))
        {
            return false;
        }
        return TextElements.Count(letters) >= minimum;
    }

    static readonly Dictionary<string, int> FillerShapes = BuildShapes();

    static Dictionary<string, int> BuildShapes()
    {
        var shapes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var filler in RussianFillers.Concat(EnglishFillers))
        {
            var shape = CollapseRuns(filler);
            shapes[shape] = Math.Min(shapes.TryGetValue(shape, out var existing) ? existing : int.MaxValue, TextElements.Count(filler));
        }
        return shapes;
    }

    static string CollapseRuns(string text)
    {
        var output = new List<string>();
        foreach (var character in TextElements.Characters(text))
        {
            if (output.Count == 0 || output[^1] != character)
            {
                output.Add(character);
            }
        }
        return string.Concat(output);
    }

    // MARK: - Spacing

    static readonly HashSet<string> Closing = new(StringComparer.Ordinal) { ",", ".", ";", ":", "!", "?", "…", ")", "]", "}", "»", "”" };
    static readonly HashSet<string> Opening = new(StringComparer.Ordinal) { "(", "[", "{", "«", "„", "“" };
    static readonly HashSet<string> SentenceEnds = new(StringComparer.Ordinal) { ".", "!", "?", "…" };
    static readonly HashSet<string> Dashes = new(StringComparer.Ordinal) { "—", "–" };

    internal static string FixSpacing(string text, bool atStart, bool atEnd)
    {
        var characters = CollapseWhitespace(TextElements.Characters(text));
        var output = new List<string>(characters.Count);
        var emoticonEnd = 0;
        for (var index = 0; index < characters.Count; index++)
        {
            var character = characters[index];
            var followsEmoticon = emoticonEnd > 0 && index == emoticonEnd;
            if (index >= emoticonEnd)
            {
                emoticonEnd = index + EmoticonLength(characters, index);
            }
            // An emoticon is not punctuation: "Смайлик :) и всё" keeps both spaces.
            if (index < emoticonEnd)
            {
                output.Add(character);
                continue;
            }
            // A dot directly before a letter starts a name: ".gitignore", ".env".
            var startsName = character == "." && index + 1 < characters.Count && TextScanning.IsWordCharacter(characters[index + 1]);
            if (Closing.Contains(character) && !startsName)
            {
                RemoveTrailingSpaces(output);
            }
            if (character == " " && !followsEmoticon && output.Count > 0 && Opening.Contains(output[^1]))
            {
                continue;
            }
            if (character == "," && LastVisible(output) == ",")
            {
                continue;
            }
            if (Dashes.Contains(character) && LastVisible(output) is { } previous && Dashes.Contains(previous))
            {
                continue;
            }
            if (SentenceEnds.Contains(character) && LastVisible(output) == ",")
            {
                RemoveTrailingSpaces(output);
                output.RemoveAt(output.Count - 1);
            }
            // «да,нет» and «Готово!Проверишь»; after ! and ? only before a capital, so a "?q="
            // query string stays whole. Links, addresses and paths keep every mark.
            if (SwiftCharacter.IsLetter(character) && output.Count > 0)
            {
                var last = output[^1];
                if ((last == "," || last == ";" || ((last == "!" || last == "?") && SwiftCharacter.IsUppercase(character))) && !EndsInLink(output))
                {
                    output.Add(" ");
                }
            }
            output.Add(character);
        }
        characters = CollapseWhitespace(output);
        var first = 0;
        var end = characters.Count;
        if (atStart)
        {
            while (first < end && (SwiftCharacter.IsWhitespace(characters[first]) || characters[first] is "," or ";" or ":"))
            {
                first += 1;
            }
        }
        if (atEnd)
        {
            while (end > first && SwiftCharacter.IsWhitespace(characters[end - 1]))
            {
                end -= 1;
            }
        }
        return SwiftString.Join(characters, first, end);
    }

    /// <summary>
    /// The length of an emoticon starting at <paramref name="index"/> (":)", ";-)", ":(",
    /// ":D"), or 0. It must end its word, so "10:30" and ":Data" are not emoticons.
    /// </summary>
    internal static int EmoticonLength(IReadOnlyList<string> characters, int index)
    {
        if (characters[index] is not (":" or ";"))
        {
            return 0;
        }
        var mouth = index + 1;
        if (mouth < characters.Count && characters[mouth] == "-")
        {
            mouth += 1;
        }
        if (mouth >= characters.Count || characters[mouth] is not (")" or "(" or "D" or "P"))
        {
            return 0;
        }
        var end = mouth + 1;
        if (end != characters.Count && TextScanning.IsWordCharacter(characters[end]))
        {
            return 0;
        }
        return end - index;
    }

    /// <summary>Runs of spaces and tabs become one space; spaces next to a line break go.</summary>
    static List<string> CollapseWhitespace(IReadOnlyList<string> characters)
    {
        var output = new List<string>(characters.Count);
        foreach (var character in characters)
        {
            if (SwiftCharacter.IsNewline(character))
            {
                RemoveTrailingSpaces(output);
                output.Add(character);
            }
            else if (SwiftCharacter.IsWhitespace(character))
            {
                if (output.Count > 0 && (output[^1] == " " || SwiftCharacter.IsNewline(output[^1])))
                {
                    continue;
                }
                output.Add(" ");
            }
            else
            {
                output.Add(character);
            }
        }
        return output;
    }

    static void RemoveTrailingSpaces(List<string> characters)
    {
        while (characters.Count > 0 && characters[^1] == " ")
        {
            characters.RemoveAt(characters.Count - 1);
        }
    }

    static string? LastVisible(List<string> characters)
    {
        for (var index = characters.Count - 1; index >= 0; index--)
        {
            if (characters[index] != " ")
            {
                return characters[index];
            }
        }
        return null;
    }

    /// <summary>
    /// Whether the text ends inside a link, an e-mail address or a path: the run of
    /// characters since the last whitespace has "://", "@", "/" or starts with "www.".
    /// </summary>
    internal static bool EndsInLink(List<string> characters)
    {
        var start = 0;
        for (var index = characters.Count - 1; index >= 0; index--)
        {
            if (SwiftCharacter.IsWhitespace(characters[index]))
            {
                start = index + 1;
                break;
            }
        }
        var token = SwiftString.Join(characters, start, characters.Count);
        return SwiftString.Contains(token, "://") || SwiftString.Contains(token, "@") || SwiftString.Contains(token, "/")
            || SwiftString.HasPrefix(SwiftUnicode.Lowercased(token), "www.");
    }

    public TextPipelineResult Apply(TextPipelineResult result, TextPipelineContext context)
    {
        var characters = TextElements.Characters(SwiftString.Canonical(result.Text));
        var protectedRanges = SnippetProtection.Ranges(characters, result, context.Snippets);
        if (protectedRanges.Count == 0)
        {
            return result with { Text = Clean(result.Text, context.Cleanup) };
        }
        var segments = SnippetProtection.Segments(characters, protectedRanges);
        var text = new StringBuilder(result.Text.Length);
        for (var index = 0; index < segments.Count; index++)
        {
            var (segment, isProtected) = segments[index];
            if (isProtected)
            {
                text.Append(segment);
                continue;
            }
            var cleaned = Clean(segment, context.Cleanup, atStart: index == 0, atEnd: index == segments.Count - 1);
            // Keep a space between the cleaned words and a neighbouring expansion.
            if (index > 0 && StartsWithWhitespace(segment) && !StartsWithWhitespace(cleaned))
            {
                cleaned = " " + cleaned;
            }
            if (index < segments.Count - 1 && EndsWithWhitespace(segment) && !EndsWithWhitespace(cleaned))
            {
                cleaned += " ";
            }
            text.Append(cleaned);
        }
        return result with { Text = text.ToString() };
    }

    static bool StartsWithWhitespace(string text) => text.Length > 0 && SwiftCharacter.IsWhitespace(TextElements.Characters(text)[0]);

    static bool EndsWithWhitespace(string text) => text.Length > 0 && SwiftCharacter.IsWhitespace(TextElements.Characters(text)[^1]);
}

/// <summary>
/// A text as words and the separators around them: <c>Separators[i]</c> precedes
/// <c>Words[i]</c>, and the last separator trails the text.
/// </summary>
internal sealed class SplitText
{
    public List<string> Separators { get; } = [];
    public List<string> Words { get; } = [];

    public SplitText(string text)
    {
        var characters = TextElements.Characters(text);
        var cursor = 0;
        foreach (var range in TextScanning.Words(characters, TextScanning.WordJoiners))
        {
            Separators.Add(SwiftString.Join(characters, cursor, range.LowerBound));
            Words.Add(SwiftString.Join(characters, range.LowerBound, range.UpperBound));
            cursor = range.UpperBound;
        }
        Separators.Add(SwiftString.Join(characters, cursor, characters.Length));
    }

    public string Joined
    {
        get
        {
            var text = new StringBuilder();
            for (var index = 0; index < Words.Count; index++)
            {
                text.Append(Separators[index]).Append(Words[index]);
            }
            return text.Append(Separators.Count > 0 ? Separators[^1] : "").ToString();
        }
    }

    public void RemoveWords(Func<string, bool> shouldRemove)
    {
        var index = 0;
        while (index < Words.Count)
        {
            if (!shouldRemove(Words[index]))
            {
                index += 1;
                continue;
            }
            var startsSentence = index == 0 || TextScanning.Any(Separators[index],
                character => character is "." or "!" or "?" or "…" || SwiftCharacter.IsNewline(character));
            var wasCapitalized = FirstCharacterIs(Words[index], SwiftCharacter.IsUppercase);
            Separators[index] = Merge(Separators[index], Separators[index + 1], isTextEnd: index == Words.Count - 1, startsSentence: startsSentence);
            Words.RemoveAt(index);
            Separators.RemoveAt(index + 1);
            // «Э, давай» becomes «Давай»: the sentence keeps its capital letter.
            if (startsSentence && wasCapitalized && index < Words.Count && FirstCharacterIs(Words[index], SwiftCharacter.IsLowercase))
            {
                var characters = TextElements.Characters(Words[index]);
                TextScanning.SetCase(characters, 0, uppercase: true);
                Words[index] = string.Concat(characters);
            }
        }
    }

    /// <summary>
    /// Joins the separators on both sides of a removed word. A comma that only sets the word
    /// off goes with it: «Э, давай» → «Давай», «Давай, э.» → «Давай.». With commas on both
    /// sides one stays, because engines put commas there mostly at clause boundaries:
    /// «Спасибо, э, до встречи» → «Спасибо, до встречи». A filler the engine wrote as a
    /// sentence of its own takes its closing mark along: «Э. Давай» → «Давай»,
    /// «Так. Эм. Что дальше?» → «Так. Что дальше?», "Hmm? What?" → "What?".
    /// </summary>
    internal static string Merge(string left, string right, bool isTextEnd, bool startsSentence = false)
    {
        var leading = new List<string>(TextElements.Characters(left));
        var trailing = new List<string>(TextElements.Characters(right));
        var hadSpace = leading.Any(SwiftCharacter.IsWhitespace) || trailing.Any(SwiftCharacter.IsWhitespace);
        TrimTrailingWhitespace(leading);
        TrimLeadingWhitespace(trailing);
        // Brackets that held only the filler go with it: «(э)». Empty brackets the speaker
        // dictated, as in foo(), stay.
        if (leading.Count > 0 && trailing.Count > 0
            && ((leading[^1] == "(" && trailing[0] == ")") || (leading[^1] == "[" && trailing[0] == "]")))
        {
            leading.RemoveAt(leading.Count - 1);
            trailing.RemoveAt(0);
            TrimTrailingWhitespace(leading);
            TrimLeadingWhitespace(trailing);
        }
        if (startsSentence && trailing.Count > 0 && SentenceMarks.Contains(trailing[0]))
        {
            var marks = 0;
            while (marks < trailing.Count && SentenceMarks.Contains(trailing[marks]))
            {
                marks += 1;
            }
            trailing.RemoveRange(0, marks);
            TrimLeadingWhitespace(trailing);
            return string.Concat(leading) + (hadSpace ? " " : "") + string.Concat(trailing);
        }
        var leftComma = leading.Count > 0 && leading[^1] == ",";
        if (trailing.Count > 0 && trailing[0] == ",")
        {
            trailing.RemoveAt(0);
        }
        else if (leftComma)
        {
            var endsHere = trailing.Count == 0 && isTextEnd;
            var beforeStop = trailing.Count > 0 && trailing[0] is "." or "!" or "?" or "…" or ";" or ":";
            if (endsHere || beforeStop)
            {
                leading.RemoveAt(leading.Count - 1);
            }
        }
        TrimTrailingWhitespace(leading);
        TrimLeadingWhitespace(trailing);
        return string.Concat(leading) + (hadSpace ? " " : "") + string.Concat(trailing);
    }

    /// <summary>
    /// Drops an immediately repeated group of one to three words: «я я» → «я», «в общем в
    /// общем» → «в общем». Only spaces may separate the repeats: «Нет, нет, нет!» is emphasis,
    /// not a stutter. Numbers alone never count, nor do grammatical doubles such as "had had".
    /// </summary>
    public void RemoveRepetitions()
    {
        var index = 0;
        while (index < Words.Count)
        {
            var removed = false;
            for (var size = 3; size >= 1; size--)
            {
                if (index + 2 * size > Words.Count || !IsRepetition(index, size))
                {
                    continue;
                }
                Words.RemoveRange(index + size, size);
                Separators.RemoveRange(index + size, size);
                removed = true;
                break;
            }
            if (!removed)
            {
                index += 1;
            }
        }
    }

    bool IsRepetition(int index, int size)
    {
        var hasLetters = false;
        for (var offset = 0; offset < size; offset++)
        {
            var first = Words[index + offset];
            if (TextScanning.Fold(first) != TextScanning.Fold(Words[index + size + offset]))
            {
                return false;
            }
            hasLetters = hasLetters || TextScanning.Any(first, SwiftCharacter.IsLetter);
        }
        if (!hasLetters)
        {
            return false;
        }
        if (size == 1 && GrammaticalDoubles.Contains(TextScanning.Fold(Words[index])))
        {
            return false;
        }
        for (var separator = index + 1; separator <= index + 2 * size - 1; separator++)
        {
            if (!TextScanning.All(Separators[separator], character => SwiftCharacter.IsWhitespace(character) && !SwiftCharacter.IsNewline(character)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>English words that correct sentences repeat: "I had had enough", "I know that that is true".</summary>
    public static readonly IReadOnlySet<string> GrammaticalDoubles = new HashSet<string>(["had", "that"], StringComparer.Ordinal);

    /// <summary>Marks that close a sentence.</summary>
    static readonly HashSet<string> SentenceMarks = new(StringComparer.Ordinal) { ".", "!", "?", "…" };

    static bool FirstCharacterIs(string word, Func<string, bool> predicate)
    {
        var characters = TextElements.Characters(word);
        return characters.Length > 0 && predicate(characters[0]);
    }

    static void TrimTrailingWhitespace(List<string> characters)
    {
        while (characters.Count > 0 && SwiftCharacter.IsWhitespace(characters[^1]))
        {
            characters.RemoveAt(characters.Count - 1);
        }
    }

    static void TrimLeadingWhitespace(List<string> characters)
    {
        var count = 0;
        while (count < characters.Count && SwiftCharacter.IsWhitespace(characters[count]))
        {
            count += 1;
        }
        characters.RemoveRange(0, count);
    }
}
