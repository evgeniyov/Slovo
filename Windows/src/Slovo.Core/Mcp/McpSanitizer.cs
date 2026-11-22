using System.Text;

namespace Slovo.Core.Mcp;

/// <summary>
/// Cleans text taken from a meeting before Claude reads it (port of the macOS
/// <c>MCPSanitizer</c>; both apps must produce the same output).
///
/// Transcripts, titles, names and notes are untrusted: a hand-edited <c>meeting.json</c> or an
/// unlucky recognition result must not be able to draw on a terminal, reorder text with
/// invisible direction marks, hide text Claude reads but the user cannot see, or close the
/// wrapper that marks content as data.
/// </summary>
public static class McpSanitizer
{
    const int Escape = 0x1B;
    const int Bell = 0x07;
    const int StringTerminator = 0x9C;
    const int ZeroWidthJoiner = 0x200D;

    /// <summary>
    /// Removes C0/C1 control characters, ANSI escape sequences, bidirectional controls
    /// (U+200E, U+200F, U+202A–U+202E, U+2066–U+2069) and invisible characters (U+200B,
    /// U+200C, U+2060–U+2064, U+FEFF, the tag characters U+E0000–U+E007F, and U+200D except
    /// inside an emoji sequence), and turns <c>&lt;transcript</c> and <c>&lt;/transcript</c>
    /// into <c>‹transcript</c> and <c>‹/transcript</c>, so content cannot close the wrapper
    /// that marks it as untrusted.
    ///
    /// The tag is recognised in any case, with <c>＜</c>, <c>﹤</c>, <c>／</c>, fullwidth letters
    /// and Cyrillic look-alikes standing in for their ASCII forms, and with any whitespace or
    /// invisible character around the slash or between the letters: whitespace is collapsed
    /// into plain spaces later, when lines are built, and a no-break space must not survive
    /// this step only to become the space of <c>&lt; /transcript&gt;</c> afterwards.
    ///
    /// Tabs and line breaks become spaces instead of disappearing, so the words they
    /// separated stay apart.
    /// </summary>
    public static string Sanitize(string text)
    {
        var needs = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (NeedsAttention(rune))
            {
                needs = true;
                break;
            }
        }
        if (!needs)
        {
            return text;
        }
        var scalars = new List<Rune>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            scalars.Add(rune);
        }
        return NeutralizeTags(StripControls(scalars));
    }

    /// <summary>
    /// True for a scalar <see cref="Sanitize"/> would drop or turn into a space: a control
    /// character, part of an ANSI escape, or a bidirectional control. <c>&lt;</c> is not one
    /// of them, because only the transcript tag built from it is neutralized.
    ///
    /// Text coming the other way, from Claude into a meeting, is refused rather than cleaned,
    /// and this is the test it is refused by.
    /// </summary>
    public static bool IsControl(Rune scalar)
    {
        var value = scalar.Value;
        return value < 0x20 || value is >= 0x7F and <= 0x9F || IsBidiControl(value);
    }

    /// <summary>
    /// True for an invisible character <see cref="Sanitize"/> removes: zero-width spaces and
    /// joiners, word joiners and invisible operators, the byte order mark, and the tag
    /// characters that can spell out text a model reads and a person never sees.
    /// <see cref="Sanitize"/> keeps the zero-width joiner only where it joins two emoji into one picture.
    /// </summary>
    public static bool IsInvisible(Rune scalar)
    {
        var value = scalar.Value;
        return value is >= 0x200B and <= 0x200D or >= 0x2060 and <= 0x2064 or 0xFEFF or >= 0xE0000 and <= 0xE007F;
    }

    static bool NeedsAttention(Rune scalar) => IsControl(scalar) || IsInvisible(scalar) || IsTagOpener(scalar.Value);

    static bool IsBidiControl(int value) =>
        value is 0x200E or 0x200F or >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069;

    /// <summary><c>&lt;</c>, the small form <c>﹤</c> and the fullwidth <c>＜</c>.</summary>
    static bool IsTagOpener(int value) => value is 0x3C or 0xFE64 or 0xFF1C;

    /// <summary><c>/</c> and the fullwidth <c>／</c>.</summary>
    static bool IsSlash(int value) => value is 0x2F or 0xFF0F;

    static List<Rune> StripControls(List<Rune> scalars)
    {
        var output = new List<Rune>(scalars.Count);
        var index = 0;
        while (index < scalars.Count)
        {
            var value = scalars[index].Value;
            switch (value)
            {
                case Escape:
                    index = SkipEscapeSequence(scalars, index);
                    break;
                case 0x9B:
                    // C1 control sequence introducer, the one-character form of ESC [.
                    index = SkipControlSequence(scalars, index + 1);
                    break;
                case 0x90 or 0x98 or 0x9D or 0x9E or 0x9F:
                    // C1 device control, start of string, OSC, privacy message, APC: text up to a terminator.
                    index = SkipControlString(scalars, index + 1);
                    break;
                case 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x85:
                    output.Add(new Rune(' '));
                    index++;
                    break;
                case < 0x20:
                case >= 0x7F and <= 0x9F:
                    index++;
                    break;
                case ZeroWidthJoiner:
                    // Inside an emoji sequence (👩\u200D💻) the joiner is part of one picture; anywhere
                    // else it only hides something.
                    if (JoinsEmoji(output, scalars, index + 1))
                    {
                        output.Add(scalars[index]);
                    }
                    index++;
                    break;
                default:
                    if (!IsBidiControl(value) && !IsInvisible(scalars[index]))
                    {
                        output.Add(scalars[index]);
                    }
                    index++;
                    break;
            }
        }
        return output;
    }

    /// <summary>Whether the text kept so far ends in an emoji (ignoring variation selectors) and the next scalar starts one.</summary>
    static bool JoinsEmoji(List<Rune> output, List<Rune> scalars, int next)
    {
        if (next >= scalars.Count || !IsEmojiPart(scalars[next]))
        {
            return false;
        }
        for (var index = output.Count - 1; index >= 0; index--)
        {
            if (output[index].Value is >= 0xFE00 and <= 0xFE0F)
            {
                continue;
            }
            return IsEmojiPart(output[index]);
        }
        return false;
    }

    /// <summary>A pictographic emoji or a skin tone; digits and <c>#</c>, which Unicode also counts as emoji for keycaps, are not joined.</summary>
    static bool IsEmojiPart(Rune scalar) =>
        scalar.Value > 0x7F && (McpUnicode.IsEmoji(scalar.Value) || McpUnicode.IsEmojiModifier(scalar.Value));

    /// <summary>Returns the index after an escape sequence that starts with ESC at <paramref name="start"/>.</summary>
    static int SkipEscapeSequence(List<Rune> scalars, int start)
    {
        var next = start + 1;
        if (next >= scalars.Count)
        {
            return next;
        }
        switch (scalars[next].Value)
        {
            case 0x5B: // ESC [ : control sequence
                return SkipControlSequence(scalars, next + 1);
            case 0x5D or 0x50 or 0x58 or 0x5E or 0x5F: // ESC ] OSC, ESC P DCS, ESC X SOS, ESC ^ PM, ESC _ APC
                return SkipControlString(scalars, next + 1);
            case >= 0x20 and <= 0x2F: // intermediate bytes, then one final byte
            {
                var index = next;
                while (index < scalars.Count && scalars[index].Value is >= 0x20 and <= 0x2F) index++;
                if (index < scalars.Count && scalars[index].Value is >= 0x30 and <= 0x7E) index++;
                return index;
            }
            case >= 0x30 and <= 0x7E: // two-character escape
                return next + 1;
            default:
                return next;
        }
    }

    /// <summary>Parameter bytes 0x30–0x3F, intermediate bytes 0x20–0x2F, then a final byte 0x40–0x7E.</summary>
    static int SkipControlSequence(List<Rune> scalars, int start)
    {
        var index = start;
        while (index < scalars.Count && scalars[index].Value is >= 0x30 and <= 0x3F) index++;
        while (index < scalars.Count && scalars[index].Value is >= 0x20 and <= 0x2F) index++;
        if (index < scalars.Count && scalars[index].Value is >= 0x40 and <= 0x7E) index++;
        return index;
    }

    /// <summary>
    /// An operating system command or similar string ends at BEL, ESC \ or ST; without a
    /// terminator a terminal would swallow the rest, so the rest is dropped too.
    /// </summary>
    static int SkipControlString(List<Rune> scalars, int start)
    {
        var index = start;
        while (index < scalars.Count)
        {
            var value = scalars[index].Value;
            if (value is Bell or StringTerminator)
            {
                return index + 1;
            }
            if (value == Escape && index + 1 < scalars.Count && scalars[index + 1].Value == 0x5C)
            {
                return index + 2;
            }
            index++;
        }
        return index;
    }

    /// <summary>
    /// <c>&lt;transcript</c>, <c>&lt;/transcript</c> and the look-alikes described at
    /// <see cref="Sanitize"/> lose their angle bracket. A space-separated form is not a valid
    /// tag, but a model may still read it as one.
    /// </summary>
    static string NeutralizeTags(List<Rune> scalars)
    {
        const string word = "transcript";
        var output = new StringBuilder(scalars.Count + 8);
        for (var index = 0; index < scalars.Count; index++)
        {
            var scalar = scalars[index];
            if (!IsTagOpener(scalar.Value))
            {
                output.Append(scalar.ToString());
                continue;
            }
            var cursor = SkipFiller(scalars, index + 1);
            if (cursor < scalars.Count && IsSlash(scalars[cursor].Value))
            {
                cursor = SkipFiller(scalars, cursor + 1);
            }
            var matches = true;
            foreach (var letter in word)
            {
                cursor = SkipInvisible(scalars, cursor);
                if (cursor >= scalars.Count || Folded(scalars[cursor]) != letter)
                {
                    matches = false;
                    break;
                }
                cursor++;
            }
            output.Append(matches ? "‹" : scalar.ToString());
        }
        return output.ToString();
    }

    /// <summary>Skips whitespace of any kind and invisible characters.</summary>
    static int SkipFiller(List<Rune> scalars, int start)
    {
        var index = start;
        while (index < scalars.Count
               && (Rune.IsWhiteSpace(scalars[index]) || McpUnicode.IsDefaultIgnorable(scalars[index].Value)))
        {
            index++;
        }
        return index;
    }

    /// <summary>Skips characters that take no room, such as a soft hyphen or a variation selector.</summary>
    static int SkipInvisible(List<Rune> scalars, int start)
    {
        var index = start;
        while (index < scalars.Count && McpUnicode.IsDefaultIgnorable(scalars[index].Value))
        {
            index++;
        }
        return index;
    }

    /// <summary>
    /// A letter as the tag match compares it: fullwidth forms and Cyrillic look-alikes as
    /// ASCII, then lowercased; -1 for anything that is not ASCII after that.
    /// </summary>
    static int Folded(Rune scalar)
    {
        var value = scalar.Value;
        // Cyrillic letters drawn exactly like the Latin letters of "transcript", which a model
        // reading the text would take for them.
        switch (value)
        {
            case 0x0430 or 0x0410: return 'a';
            case 0x0441 or 0x0421: return 'c';
            case 0x0440 or 0x0420: return 'p';
            case 0x0456 or 0x0406: return 'i';
            case 0x0455 or 0x0405: return 's';
            case 0x0422: return 't';
        }
        if (value is >= 0xFF01 and <= 0xFF5E)
        {
            value -= 0xFEE0;
        }
        if (value > 0x7F)
        {
            return -1;
        }
        return value is >= 'A' and <= 'Z' ? value + 0x20 : value;
    }
}
