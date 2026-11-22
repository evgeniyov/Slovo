using System.Text;

namespace Slovo.Core.Text;

/// <summary>
/// Swift <c>String</c> semantics the text pipeline relies on. Every string the pipeline
/// handles is first brought to NFC (<see cref="Canonical"/>), so ordinal comparison of
/// strings and of grapheme clusters equals Swift's comparison by canonical equivalence.
/// </summary>
internal static class SwiftString
{
    /// <summary>
    /// The string as Swift would hold it, in NFC: a lone surrogate (which a Swift string
    /// cannot contain) becomes U+FFFD, and canonically equivalent spellings become one.
    /// </summary>
    public static string Canonical(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }
        if (HasLoneSurrogate(text))
        {
            text = RepairSurrogates(text);
        }
        return text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);
    }

    /// <summary>Swift's <c>==</c> on strings: canonical equivalence.</summary>
    public static bool CanonicalEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal)
        || string.Equals(Canonical(left), Canonical(right), StringComparison.Ordinal);

    /// <summary>
    /// Swift's <c>&lt;</c> on NFC strings: Unicode scalar order, which UTF-16 ordinal order
    /// breaks where a surrogate pair meets U+E000…U+FFFF.
    /// </summary>
    public static int CompareScalars(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var a = left[index];
            var b = right[index];
            if (a != b)
            {
                return ScalarOrder(a).CompareTo(ScalarOrder(b));
            }
        }
        return left.Length.CompareTo(right.Length);
    }

    /// <summary>Swift's <c>String.contains(_:)</c> for another string: whole grapheme clusters only ("/" is not in "/\u0301").</summary>
    public static bool Contains(string text, string needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }
        if (!text.Contains(needle, StringComparison.Ordinal))
        {
            return false;
        }
        return IndexOf(TextElements.Characters(text), TextElements.Characters(needle), 0) >= 0;
    }

    /// <summary>Swift's <c>hasPrefix(_:)</c>: the prefix must end on a grapheme cluster boundary.</summary>
    public static bool HasPrefix(string text, string prefix)
    {
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        if (text.Length == prefix.Length || prefix.Length == 0)
        {
            return true;
        }
        var characters = TextElements.Characters(text);
        var consumed = 0;
        foreach (var character in characters)
        {
            if (consumed == prefix.Length)
            {
                return true;
            }
            if (consumed > prefix.Length)
            {
                return false;
            }
            consumed += character.Length;
        }
        return consumed == prefix.Length;
    }

    /// <summary>The first index at or after <paramref name="start"/> where <paramref name="needle"/> occurs in <paramref name="characters"/>, or -1.</summary>
    public static int IndexOf(IReadOnlyList<string> characters, IReadOnlyList<string> needle, int start)
    {
        if (needle.Count == 0)
        {
            return start <= characters.Count ? start : -1;
        }
        for (var index = start; index + needle.Count <= characters.Count; index++)
        {
            if (SequenceEqualAt(characters, index, needle))
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>Whether <paramref name="needle"/> occurs in <paramref name="characters"/> at <paramref name="index"/>.</summary>
    public static bool SequenceEqualAt(IReadOnlyList<string> characters, int index, IReadOnlyList<string> needle)
    {
        if (index < 0 || index + needle.Count > characters.Count)
        {
            return false;
        }
        for (var offset = 0; offset < needle.Count; offset++)
        {
            if (!string.Equals(characters[index + offset], needle[offset], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Joins grapheme clusters back into a string (Swift's <c>String(characters[range])</c>).</summary>
    public static string Join(IReadOnlyList<string> characters, int start, int end)
    {
        if (end <= start)
        {
            return "";
        }
        if (end - start == 1)
        {
            return characters[start];
        }
        var builder = new StringBuilder();
        for (var index = start; index < end; index++)
        {
            builder.Append(characters[index]);
        }
        return builder.ToString();
    }

    public static string Join(IReadOnlyList<string> characters) => Join(characters, 0, characters.Count);

    static int ScalarOrder(char c) => c >= 0xD800 ? (c >= 0xE000 ? c - 0x800 : c + 0x2000) : c;

    static bool HasLoneSurrogate(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
                continue;
            }
            if (char.IsSurrogate(c))
            {
                return true;
            }
        }
        return false;
    }

    static string RepairSurrogates(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                builder.Append(c).Append(text[index + 1]);
                index++;
            }
            else
            {
                builder.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
            }
        }
        return builder.ToString();
    }
}

/// <summary>
/// Swift <c>Character</c> properties for one grapheme cluster held as a string. Most look
/// at the first scalar only, as Swift does: «é» written as e + U+0301 is a letter.
/// </summary>
internal static class SwiftCharacter
{
    public static int FirstScalar(string character) =>
        character.Length > 1 && char.IsSurrogatePair(character[0], character[1])
            ? char.ConvertToUtf32(character[0], character[1])
            : character.Length > 0 ? character[0] : 0;

    /// <summary><c>Character.isLetter</c>: the first scalar is Alphabetic.</summary>
    public static bool IsLetter(string character) => SwiftUnicode.IsAlphabetic(FirstScalar(character));

    /// <summary><c>Character.isNumber</c>: the first scalar has a numeric type.</summary>
    public static bool IsNumber(string character) => SwiftUnicode.IsNumeric(FirstScalar(character));

    /// <summary><c>Character.isWhitespace</c>, newlines included.</summary>
    public static bool IsWhitespace(string character) => SwiftUnicode.IsWhitespace(FirstScalar(character));

    /// <summary><c>Character.isNewline</c>: LF, VT, FF, CR, CR-LF, NEL, LS, PS.</summary>
    public static bool IsNewline(string character) => FirstScalar(character) is (>= 0x0A and <= 0x0D) or 0x85 or 0x2028 or 0x2029;

    /// <summary><c>Character.isUppercase</c>.</summary>
    public static bool IsUppercase(string character)
    {
        if (IsSingleScalar(character) && SwiftUnicode.IsUppercase(FirstScalar(character)))
        {
            return true;
        }
        return SwiftString.CanonicalEquals(SwiftUnicode.Uppercased(character), character) && IsCased(character);
    }

    /// <summary><c>Character.isLowercase</c>.</summary>
    public static bool IsLowercase(string character)
    {
        if (IsSingleScalar(character) && SwiftUnicode.IsLowercase(FirstScalar(character)))
        {
            return true;
        }
        return SwiftString.CanonicalEquals(SwiftUnicode.Lowercased(character), character) && IsCased(character);
    }

    /// <summary><c>Character.isCased</c>: changes under some case conversion.</summary>
    public static bool IsCased(string character)
    {
        if (IsSingleScalar(character) && SwiftUnicode.IsCased(FirstScalar(character)))
        {
            return true;
        }
        return !SwiftString.CanonicalEquals(SwiftUnicode.Uppercased(character), character)
            || !SwiftString.CanonicalEquals(SwiftUnicode.Lowercased(character), character);
    }

    static bool IsSingleScalar(string character) =>
        character.Length == 1 || (character.Length == 2 && char.IsSurrogatePair(character[0], character[1]));
}
