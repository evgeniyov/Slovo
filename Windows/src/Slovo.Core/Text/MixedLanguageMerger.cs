using System.Globalization;
using System.Text;

namespace Slovo.Core.Text;

/// <summary>
/// Mixed Russian and English dictation: GigaAM writes the text, a multilingual helper
/// (Parakeet or Whisper) heard the same audio, and this puts the helper's English words in
/// only where GigaAM was unsure (port of the macOS MixedLanguageMerger).
/// </summary>
/// <remarks>
/// GigaAM knows only Russian. When it hears English it either falls back to Latin letters
/// with a wrong spelling (Makos, Hithub, Pod Vidos) or writes a transliteration that is not
/// a Russian word (мейн, ребейс). Those words are <i>suspect</i>; everything else GigaAM wrote
/// — its Russian, its punctuation, its capitals — is kept word for word, since GigaAM makes
/// three to ten times fewer Russian errors than any multilingual model.
///
/// Both texts are aligned on a sound key shared by both scripts. Where they differ, a small
/// dynamic programme pairs GigaAM's words with the helper's (one to one, or merging up to
/// three words: Baka Swift → Package.swift), and a suspect run is replaced by its partner
/// when the partner sounds alike and is English (or, for Latin garble, one short Russian
/// function word: Pod → под). The helper's own mistakes in Russian, its dropped sentences
/// and anything it invents in a pause never reach the text.
///
/// Deterministic and identical to the macOS app; <c>Shared/fixtures/mixed-merge.json</c>
/// keeps the two in step.
/// </remarks>
public sealed class MixedLanguageMerger(IWordLexicon lexicon)
{
    /// <summary>Below this, two runs do not sound alike enough to be the same words.</summary>
    const double MinimumSimilarity = 0.55;
    /// <summary>A capitalised Cyrillic word this short is an abbreviation (БД, ООО), not a transliteration.</summary>
    const int AbbreviationLength = 5;

    /// <summary>Short Russian words GigaAM sometimes writes in Latin next to an English word (Pod Vidos).</summary>
    static readonly HashSet<string> FunctionWords =
    [
        "в", "во", "на", "под", "над", "с", "со", "к", "ко", "по", "о", "об", "от", "до", "за", "из",
        "у", "и", "а", "но", "не", "для", "про", "без", "при",
    ];

    /// <summary>How many words of each side one pairing may join.</summary>
    static readonly (int G, int O)[] Moves = [(1, 0), (0, 1), (1, 1), (2, 1), (1, 2), (3, 1), (1, 3)];

    /// <summary>The merged text and how many runs of GigaAM's words were replaced.</summary>
    public readonly record struct Result(string Text, int Replacements);

    /// <summary>Whether GigaAM's text has a word the helper could improve; without one there is no point in running the helper.</summary>
    public bool NeedsHelper(string russian) => Tokens(russian).Any(IsSuspect);

    public Result Merge(string russian, string helper)
    {
        var g = Tokens(russian);
        var o = Tokens(helper);
        if (g.Count == 0 || o.Count == 0)
        {
            return new Result(string.Join(' ', g), 0);
        }
        var gk = g.Select(t => Sound(Core(t))).ToList();
        var ok = o.Select(t => Sound(Core(t))).ToList();
        var output = new List<string>();
        var replacements = 0;
        foreach (var opcode in new SequenceMatcher<string>(gk, ok).Opcodes())
        {
            var gblock = g.GetRange(opcode.AStart, opcode.AEnd - opcode.AStart);
            var oblock = o.GetRange(opcode.BStart, opcode.BEnd - opcode.BStart);
            if (opcode.Tag == SequenceMatcher<string>.OpTag.Equal)
            {
                // Sounding the same is not spelling the same: Makos/macOS, Гит/Git, мейн/main.
                for (var index = 0; index < gblock.Count; index++)
                {
                    var gt = gblock[index];
                    var ot = oblock[index];
                    if (IsSuspect(gt) && Lower(Core(gt)) != Lower(Core(ot)) && Acceptable([gt], [ot]))
                    {
                        output.AddRange(ReplaceRun([gt], [ot], IsSentenceStart(output)));
                        replacements++;
                    }
                    else
                    {
                        output.Add(gt);
                    }
                }
                continue;
            }
            if (opcode.Tag != SequenceMatcher<string>.OpTag.Replace || !gblock.Any(IsSuspect))
            {
                output.AddRange(gblock);
                continue;
            }
            var k = 0;
            foreach (var piece in Pair(gblock, oblock))
            {
                output.AddRange(gblock.GetRange(k, piece.GStart - k));
                var run = gblock.GetRange(piece.GStart, piece.GEnd - piece.GStart);
                var partner = oblock.GetRange(piece.OStart, piece.OEnd - piece.OStart);
                if (run.All(IsSuspect) && Acceptable(run, partner)
                    && !run.Select(t => Lower(Core(t))).SequenceEqual(partner.Select(t => Lower(Core(t)))))
                {
                    output.AddRange(ReplaceRun(run, partner, IsSentenceStart(output)));
                    replacements++;
                }
                else
                {
                    output.AddRange(run);
                }
                k = piece.GEnd;
            }
            output.AddRange(gblock.GetRange(k, gblock.Count - k));
        }
        return new Result(string.Join(' ', output), replacements);
    }

    // Suspect words

    /// <summary>
    /// Latin garble, or a Cyrillic word that is not Russian. Mixed scripts (Telegram-бот) and
    /// capitalised abbreviations (БД) are GigaAM's choice and stay.
    /// </summary>
    internal bool IsSuspect(string token)
    {
        var word = Core(token);
        if (word.Length == 0 || !word.Any(IsWordCharacter))
        {
            return false;
        }
        var latin = word.Any(IsLatin);
        var cyrillic = word.Any(IsCyrillic);
        if (latin && cyrillic)
        {
            return false;
        }
        if (latin)
        {
            return true;
        }
        if (IsAllUppercase(word) && word.Length <= AbbreviationLength)
        {
            return false;
        }
        return cyrillic && !IsKnown(word);
    }

    /// <summary>
    /// A real Russian word; a hyphenated one when every part is. Without a Russian dictionary
    /// every word counts as known, so only Latin garble is replaced.
    /// </summary>
    internal bool IsKnown(string word)
    {
        if (!lexicon.HasLanguage("ru"))
        {
            return true;
        }
        var parts = Lower(word).Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(part => lexicon.IsCorrect(part, "ru") || lexicon.IsCorrect(part.Replace('ё', 'е'), "ru"));
    }

    /// <summary>
    /// The helper supplies English: its Latin words may replace GigaAM's unsure ones. A Latin run
    /// may also become one short Russian function word (Pod → под), never a longer Russian word.
    /// </summary>
    internal static bool Acceptable(IReadOnlyList<string> run, IReadOnlyList<string> partner)
    {
        var words = partner.Select(Core).ToList();
        if (words.Any(w => w.Length == 0))
        {
            return false;
        }
        if (words.All(w => w.Any(IsLatin)))
        {
            return true;
        }
        var latinRun = run.Any(t => Core(t).Any(IsLatin));
        return latinRun && words.Count == 1 && FunctionWords.Contains(Lower(words[0]));
    }

    // Pairing inside a differing stretch

    internal readonly record struct Piece(int GStart, int GEnd, int OStart, int OEnd);

    /// <summary>
    /// The best monotone pairing of GigaAM's words with the helper's, scored by how alike the
    /// paired words sound; words may also stay unpaired.
    /// </summary>
    internal static List<Piece> Pair(IReadOnlyList<string> g, IReadOnlyList<string> o)
    {
        var n = g.Count;
        var m = o.Count;
        var width = m + 1;
        var cellCount = (n + 1) * width;
        var scores = new double[cellCount];
        Array.Fill(scores, double.NegativeInfinity);
        var steps = new byte[cellCount];
        var sounds = new List<char[]>();
        var soundIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var left = SoundIds(g, sounds, soundIds);
        var right = SoundIds(o, sounds, soundIds);
        var similarities = new Dictionary<(int Left, int Right), double>();
        scores[0] = 0;
        for (var i = 0; i <= n; i++)
        {
            for (var j = 0; j <= m; j++)
            {
                var current = scores[i * width + j];
                for (var step = 0; step < Moves.Length; step++)
                {
                    var (di, dj) = Moves[step];
                    if (i + di > n || j + dj > m)
                    {
                        continue;
                    }
                    var score = current;
                    if (di > 0 && dj > 0)
                    {
                        var key = (Left: left[i][di - 1], Right: right[j][dj - 1]);
                        if (!similarities.TryGetValue(key, out var similarity))
                        {
                            similarity = Similarity(sounds[key.Left], sounds[key.Right]);
                            if (similarities.Count < 4_096)
                            {
                                similarities.Add(key, similarity);
                            }
                        }
                        if (similarity < MinimumSimilarity)
                        {
                            continue;
                        }
                        score += similarity * (di + dj);
                    }
                    var destination = (i + di) * width + j + dj;
                    if (score > scores[destination])
                    {
                        scores[destination] = score;
                        steps[destination] = (byte)step;
                    }
                }
            }
        }
        var path = new List<Piece>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            var (di, dj) = Moves[steps[i * width + j]];
            if (di > 0 && dj > 0)
            {
                path.Add(new Piece(i - di, i, j - dj, j));
            }
            i -= di;
            j -= dj;
        }
        path.Reverse();
        return path;
    }

    static int[][] SoundIds(IReadOnlyList<string> tokens, List<char[]> sounds, Dictionary<string, int> ids)
    {
        var words = tokens.Select(Core).ToArray();
        var keys = new int[words.Length][];
        for (var start = 0; start < words.Length; start++)
        {
            var lengths = new int[Math.Min(3, words.Length - start)];
            for (var length = 1; length <= lengths.Length; length++)
            {
                var key = Sound(string.Join(' ', Slice(words, start, length)));
                if (!ids.TryGetValue(key, out var id))
                {
                    id = sounds.Count;
                    ids.Add(key, id);
                    sounds.Add(key.ToCharArray());
                }
                lengths[length - 1] = id;
            }
            keys[start] = lengths;
        }
        return keys;
    }

    static IEnumerable<string> Slice(IReadOnlyList<string> words, int start, int count)
    {
        for (var index = start; index < start + count; index++)
        {
            yield return words[index];
        }
    }

    // Writing a replacement

    /// <summary>
    /// The helper's words, with GigaAM's punctuation around the run and GigaAM's idea of
    /// whether a Russian word starts a sentence.
    /// </summary>
    internal static List<string> ReplaceRun(IReadOnlyList<string> run, IReadOnlyList<string> partner, bool sentenceStart)
    {
        var words = partner.Select(t => Core(t) is { Length: > 0 } core ? core : t).ToList();
        words[0] = LeadingPunctuation(run[0]) + words[0];
        words[^1] += TrailingPunctuation(run[^1]);
        var first = Core(words[0]);
        if (first.Length > 0 && IsCyrillic(first[0]) && !IsAllUppercase(first))
        {
            // GigaAM capitalises its Latin guesses, so only a sentence start or GigaAM's own
            // capital on a Russian word decides.
            var gigaamWord = Core(run[0]);
            var gigaamCapital = gigaamWord.Length > 0 && char.IsUpper(gigaamWord[0]) && !gigaamWord.Any(IsLatin);
            var initial = first[0].ToString();
            var wanted = sentenceStart || gigaamCapital ? initial.ToUpperInvariant() : initial.ToLowerInvariant();
            var at = words[0].IndexOf(first, StringComparison.Ordinal);
            if (at >= 0)
            {
                words[0] = words[0][..at] + wanted + first[1..] + words[0][(at + first.Length)..];
            }
        }
        return words;
    }

    static bool IsSentenceStart(List<string> output) =>
        output.Count == 0 || output[^1].Length == 0 || ".!?…".Contains(output[^1][^1]);

    // Text helpers shared with the macOS merger

    internal static List<string> Tokens(string text) =>
        [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];

    internal static bool IsLatin(char character) => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    internal static bool IsCyrillic(char character) => character is >= 'А' and <= 'я' or 'Ё' or 'ё';

    static bool IsWordCharacter(char character) => IsLatin(character) || IsCyrillic(character) || character is >= '0' and <= '9';

    /// <summary>The word without the punctuation around it (inner dots and hyphens stay).</summary>
    internal static string Core(string token)
    {
        var start = 0;
        while (start < token.Length && !IsWordCharacter(token[start]))
        {
            start++;
        }
        var end = token.Length - 1;
        while (end >= start && !IsWordCharacter(token[end]))
        {
            end--;
        }
        return start > end ? "" : token[start..(end + 1)];
    }

    static string LeadingPunctuation(string token)
    {
        var length = 0;
        while (length < token.Length && !IsWordCharacter(token[length]))
        {
            length++;
        }
        return token[..length];
    }

    static string TrailingPunctuation(string token)
    {
        var start = token.Length;
        while (start > 0 && !IsWordCharacter(token[start - 1]))
        {
            start--;
        }
        return token[start..];
    }

    /// <summary>At least one cased letter and no lowercase one ("БД", "2X").</summary>
    static bool IsAllUppercase(string word) => word.Any(char.IsUpper) && !word.Any(char.IsLower);

    static string Lower(string text) => text.ToLowerInvariant();

    static readonly Dictionary<char, string> Transliteration = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sh", ['ъ'] = "", ['ы'] = "i", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya",
    };

    /// <summary>Spellings that sound alike in either script, applied in this order.</summary>
    static readonly (string From, string To)[] SoundRules =
    [
        ("ph", "f"), ("th", "t"), ("ck", "k"), ("c", "k"), ("q", "k"), ("w", "v"), ("x", "ks"),
        ("y", "i"), ("ou", "u"), ("ee", "i"), ("oo", "u"), ("kh", "h"), ("j", "dzh"), ("ei", "e"), ("ai", "e"),
    ];

    /// <summary>A rough sound key shared by both scripts: macOS and Makos, мейн and main meet.</summary>
    internal static string Sound(string text)
    {
        var builder = new StringBuilder();
        foreach (var character in Lower(text))
        {
            builder.Append(Transliteration.TryGetValue(character, out var latin) ? latin : character.ToString(CultureInfo.InvariantCulture));
        }
        var key = builder.ToString();
        foreach (var (from, to) in SoundRules)
        {
            key = key.Replace(from, to, StringComparison.Ordinal);
        }
        var collapsed = new StringBuilder();
        foreach (var character in key)
        {
            if (character is (< 'a' or > 'z') and (< '0' or > '9'))
            {
                continue;
            }
            if (collapsed.Length == 0 || collapsed[^1] != character)
            {
                collapsed.Append(character);
            }
        }
        return collapsed.ToString();
    }

    internal static double Similarity(IEnumerable<string> a, IEnumerable<string> b)
    {
        return Similarity(Sound(string.Join(' ', a)).ToCharArray(), Sound(string.Join(' ', b)).ToCharArray());
    }

    static double Similarity(char[] left, char[] right) =>
        left.Length == 0 || right.Length == 0 ? 0 : new SequenceMatcher<char>(left, right).Ratio();
}

/// <summary>
/// Python's <c>difflib.SequenceMatcher</c> without junk heuristics: the same matching blocks,
/// the same opcodes and the same ratio, so both mergers and the prototype that tuned them
/// agree to the word.
/// </summary>
internal sealed class SequenceMatcher<T> where T : notnull
{
    public enum OpTag
    {
        Equal,
        Replace,
        Delete,
        Insert,
    }

    public readonly record struct Opcode(OpTag Tag, int AStart, int AEnd, int BStart, int BEnd);

    readonly IReadOnlyList<T> a;
    readonly IReadOnlyList<T> b;
    readonly Dictionary<T, List<int>> positions = [];

    public SequenceMatcher(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        this.a = a;
        this.b = b;
        for (var index = 0; index < b.Count; index++)
        {
            if (!positions.TryGetValue(b[index], out var list))
            {
                positions[b[index]] = list = [];
            }
            list.Add(index);
        }
    }

    /// <summary>The longest equal block; the earliest in a, then in b, among equally long ones.</summary>
    (int A, int B, int Size) LongestMatch(int aLow, int aHigh, int bLow, int bHigh)
    {
        int bestI = aLow, bestJ = bLow, bestSize = 0;
        var lengths = new Dictionary<int, int>();
        for (var i = aLow; i < aHigh; i++)
        {
            var next = new Dictionary<int, int>();
            if (positions.TryGetValue(a[i], out var list))
            {
                foreach (var j in list)
                {
                    if (j < bLow)
                    {
                        continue;
                    }
                    if (j >= bHigh)
                    {
                        break;
                    }
                    var k = lengths.GetValueOrDefault(j - 1) + 1;
                    next[j] = k;
                    if (k > bestSize)
                    {
                        bestI = i - k + 1;
                        bestJ = j - k + 1;
                        bestSize = k;
                    }
                }
            }
            lengths = next;
        }
        return (bestI, bestJ, bestSize);
    }

    List<(int A, int B, int Size)> MatchingBlocks()
    {
        var queue = new Stack<(int, int, int, int)>();
        queue.Push((0, a.Count, 0, b.Count));
        var blocks = new List<(int A, int B, int Size)>();
        while (queue.Count > 0)
        {
            var (aLow, aHigh, bLow, bHigh) = queue.Pop();
            var match = LongestMatch(aLow, aHigh, bLow, bHigh);
            if (match.Size == 0)
            {
                continue;
            }
            blocks.Add(match);
            if (aLow < match.A && bLow < match.B)
            {
                queue.Push((aLow, match.A, bLow, match.B));
            }
            if (match.A + match.Size < aHigh && match.B + match.Size < bHigh)
            {
                queue.Push((match.A + match.Size, aHigh, match.B + match.Size, bHigh));
            }
        }
        blocks.Sort();
        var merged = new List<(int A, int B, int Size)>();
        foreach (var block in blocks)
        {
            if (merged.Count > 0 && merged[^1].A + merged[^1].Size == block.A && merged[^1].B + merged[^1].Size == block.B)
            {
                merged[^1] = (merged[^1].A, merged[^1].B, merged[^1].Size + block.Size);
            }
            else
            {
                merged.Add(block);
            }
        }
        merged.Add((a.Count, b.Count, 0));
        return merged;
    }

    public List<Opcode> Opcodes()
    {
        int i = 0, j = 0;
        var result = new List<Opcode>();
        foreach (var (blockA, blockB, size) in MatchingBlocks())
        {
            OpTag? tag = i < blockA && j < blockB ? OpTag.Replace : i < blockA ? OpTag.Delete : j < blockB ? OpTag.Insert : null;
            if (tag is { } found)
            {
                result.Add(new Opcode(found, i, blockA, j, blockB));
            }
            i = blockA + size;
            j = blockB + size;
            if (size > 0)
            {
                result.Add(new Opcode(OpTag.Equal, blockA, i, blockB, j));
            }
        }
        return result;
    }

    public double Ratio()
    {
        var matches = MatchingBlocks().Sum(block => block.Size);
        var total = a.Count + b.Count;
        return total > 0 ? 2.0 * matches / total : 1.0;
    }
}
