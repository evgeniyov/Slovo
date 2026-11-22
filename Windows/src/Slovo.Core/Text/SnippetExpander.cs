using System.Text;
using Slovo.Core.Model;

namespace Slovo.Core.Text;

/// <summary>
/// Replaces spoken trigger phrases with their expansions, whether the trigger is the whole
/// dictation or a phrase inside it. Case-insensitive, ignores punctuation, treats ё as е and
/// ’ as '.
///
/// A hyphen counts as a word break, so the trigger «IP адрес» matches «IP-адрес» and
/// "e mail" matches "e-mail"; a match never starts or ends inside a hyphenated compound, so
/// «адрес» alone does not match inside «IP-адрес».
/// </summary>
public sealed class SnippetExpander : ITextStage
{
    /// <summary>The expanded text and how many triggers were replaced.</summary>
    public (string Text, int Count) Expand(string text, IReadOnlyList<Snippet> snippets)
    {
        text = SwiftString.Canonical(text);
        var triggers = Prepare(snippets);
        if (triggers.Count == 0 || text.Length == 0)
        {
            return (text, 0);
        }

        var characters = TextElements.Characters(text);
        var ranges = TextScanning.Words(characters, TextScanning.ApostropheJoiners);
        var words = ranges.Select(range => TextScanning.Fold(SwiftString.Join(characters, range.LowerBound, range.UpperBound))).ToArray();
        if (words.Length == 0)
        {
            return (text, 0);
        }
        // hyphenated[i]: word i is glued to word i - 1 by a hyphen, as in «IP-адрес».
        var hyphenated = new bool[ranges.Count];
        for (var index = 1; index < ranges.Count; index++)
        {
            hyphenated[index] = ranges[index].LowerBound - ranges[index - 1].UpperBound == 1
                && characters[ranges[index - 1].UpperBound] == "-";
        }

        // The whole dictation is the trigger: the engine's punctuation around it goes too.
        foreach (var trigger in triggers)
        {
            if (trigger.Words.SequenceEqual(words, StringComparer.Ordinal))
            {
                return (trigger.Expansion, 1);
            }
        }

        var output = new StringBuilder(text.Length);
        var copiedUpTo = 0;
        var count = 0;
        var wordIndex = 0;
        while (wordIndex < words.Length)
        {
            var match = triggers.FirstOrDefault(trigger => Matches(trigger.Words, words, hyphenated, wordIndex));
            if (match is null)
            {
                wordIndex += 1;
                continue;
            }
            var start = ranges[wordIndex].LowerBound;
            var end = ranges[wordIndex + match.Words.Length - 1].UpperBound;
            output.Append(SwiftString.Join(characters, copiedUpTo, start));
            output.Append(match.Expansion);
            copiedUpTo = end;
            // The engine's own period after the trigger would double the expansion's: «иван..».
            var mark = match.LastCharacter;
            if (mark is "." or "!" or "?" && end < characters.Length && characters[end] == mark
                && (end + 1 == characters.Length || characters[end + 1] != mark))
            {
                copiedUpTo = end + 1;
            }
            count += 1;
            wordIndex += match.Words.Length;
        }
        output.Append(SwiftString.Join(characters, copiedUpTo, characters.Length));
        return (output.ToString(), count);
    }

    public TextPipelineResult Apply(TextPipelineResult result, TextPipelineContext context)
    {
        var (text, count) = Expand(result.Text, context.Snippets);
        return result with { Text = text, SnippetsApplied = result.SnippetsApplied + count };
    }

    sealed record Trigger(string[] Words, string Expansion, string? LastCharacter);

    /// <summary>Triggers as folded words, longest first so «мой рабочий адрес» wins over «мой адрес».</summary>
    static List<Trigger> Prepare(IReadOnlyList<Snippet> snippets)
    {
        var prepared = new List<(Trigger Trigger, int Offset, int Length)>();
        foreach (var snippet in snippets)
        {
            var characters = TextElements.Characters(SwiftString.Canonical(snippet.Trigger));
            var words = TextScanning.Words(characters, TextScanning.ApostropheJoiners)
                .Select(range => TextScanning.Fold(SwiftString.Join(characters, range.LowerBound, range.UpperBound)))
                .ToArray();
            if (words.Length == 0)
            {
                continue;
            }
            var expansion = SwiftString.Canonical(snippet.Expansion);
            var expansionCharacters = TextElements.Characters(expansion);
            var trigger = new Trigger(words, expansion, expansionCharacters.Length > 0 ? expansionCharacters[^1] : null);
            prepared.Add((trigger, prepared.Count, words.Sum(TextElements.Count)));
        }
        prepared.Sort((lhs, rhs) =>
        {
            if (lhs.Trigger.Words.Length != rhs.Trigger.Words.Length)
            {
                return rhs.Trigger.Words.Length.CompareTo(lhs.Trigger.Words.Length);
            }
            return lhs.Length != rhs.Length ? rhs.Length.CompareTo(lhs.Length) : lhs.Offset.CompareTo(rhs.Offset);
        });
        return prepared.Select(item => item.Trigger).ToList();
    }

    /// <summary>The trigger's words at <paramref name="index"/>, not starting or ending inside a hyphenated compound.</summary>
    static bool Matches(string[] trigger, string[] words, bool[] hyphenated, int index)
    {
        var end = index + trigger.Length;
        if (end > words.Length || hyphenated[index] || (end != words.Length && hyphenated[end]))
        {
            return false;
        }
        for (var offset = 0; offset < trigger.Length; offset++)
        {
            if (!string.Equals(words[index + offset], trigger[offset], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
