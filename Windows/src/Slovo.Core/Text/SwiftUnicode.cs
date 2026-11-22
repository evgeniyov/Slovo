using System.Text;

namespace Slovo.Core.Text;

/// <summary>
/// Unicode scalar properties and case mapping exactly as Swift's standard library answers
/// them (<c>Unicode.Scalar.Properties</c>, <c>String.uppercased()</c>, <c>lowercased()</c>),
/// read from tables generated out of Swift itself (<c>SwiftUnicodeTables.g.cs</c>). .NET
/// has no Alphabetic property, maps case one UTF-16 unit at a time (ß stays ß) and takes
/// its casing from ICU or NLS, whose versions differ between machines; the text pipeline
/// must not.
/// </summary>
internal static partial class SwiftUnicode
{
    /// <summary><c>properties.isAlphabetic</c>: what Swift's <c>Character.isLetter</c> checks on the first scalar.</summary>
    public static bool IsAlphabetic(int scalar) =>
        scalar < 0x80 ? (uint)((scalar | 0x20) - 'a') <= 'z' - 'a' : InRuns(AlphabeticRuns, scalar);

    /// <summary><c>properties.numericType != nil</c>: what Swift's <c>Character.isNumber</c> checks.</summary>
    public static bool IsNumeric(int scalar) =>
        scalar < 0x80 ? (uint)(scalar - '0') <= 9 : InRuns(NumericRuns, scalar);

    public static bool IsUppercase(int scalar) =>
        scalar < 0x80 ? (uint)(scalar - 'A') <= 'Z' - 'A' : InRuns(UppercaseRuns, scalar);

    public static bool IsLowercase(int scalar) =>
        scalar < 0x80 ? (uint)(scalar - 'a') <= 'z' - 'a' : InRuns(LowercaseRuns, scalar);

    public static bool IsCased(int scalar) =>
        scalar < 0x80 ? (uint)((scalar | 0x20) - 'a') <= 'z' - 'a' : InRuns(CasedRuns, scalar);

    /// <summary><c>properties.isWhitespace</c> (White_Space), which .NET's list matches exactly.</summary>
    public static bool IsWhitespace(int scalar) => Rune.IsValid(scalar) && Rune.IsWhiteSpace(new Rune(scalar));

    /// <summary>The ASCII letter or digit that diacritic-insensitive folding leaves of a scalar («é» → e), or 0.</summary>
    public static int AsciiFolding(int scalar)
    {
        var pairs = AsciiFoldingPairs;
        int low = 0, high = pairs.Length / 2 - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            var key = pairs[middle * 2];
            if (key == scalar)
            {
                return pairs[middle * 2 + 1];
            }
            if (key < scalar)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return 0;
    }

    /// <summary>Swift's <c>String.uppercased()</c>: full case mapping of every scalar, without context (ß → SS).</summary>
    public static string Uppercased(string text) => ChangeCase(text, upper: true);

    /// <summary>Swift's <c>String.lowercased()</c>: full case mapping of every scalar, without context (Σ is always σ).</summary>
    public static string Lowercased(string text) => ChangeCase(text, upper: false);

    static string ChangeCase(string text, bool upper)
    {
        if (IsAscii(text))
        {
            return upper ? AsciiUpper(text) : AsciiLower(text);
        }
        var builder = new StringBuilder(text.Length + 4);
        var changed = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var special = upper ? UppercaseSpecial : LowercaseSpecial;
            if (special.TryGetValue(rune.Value, out var mapped))
            {
                builder.Append(mapped);
                changed = true;
                continue;
            }
            var target = MapSimple(upper ? UppercaseMappingRuns : LowercaseMappingRuns, rune.Value);
            changed |= target != rune.Value;
            if (target < 0x10000)
            {
                builder.Append((char)target);
            }
            else
            {
                builder.Append(char.ConvertFromUtf32(target));
            }
        }
        if (!changed)
        {
            return text;
        }
        // Swift compares strings by canonical equivalence; keeping every string in NFC lets
        // the port compare ordinally ("ΐ" uppercases to Ι + two marks, which NFC recombines).
        return SwiftString.Canonical(builder.ToString());
    }

    static int MapSimple(ReadOnlySpan<int> runs, int scalar)
    {
        int low = 0, high = runs.Length / 4 - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            var first = runs[middle * 4];
            if (scalar < first)
            {
                high = middle - 1;
            }
            else if (scalar > runs[middle * 4 + 1])
            {
                low = middle + 1;
            }
            else
            {
                return (scalar - first) % runs[middle * 4 + 2] == 0 ? scalar + runs[middle * 4 + 3] : scalar;
            }
        }
        return scalar;
    }

    static bool InRuns(ReadOnlySpan<int> runs, int scalar)
    {
        int low = 0, high = runs.Length / 3 - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            var first = runs[middle * 3];
            if (scalar < first)
            {
                high = middle - 1;
            }
            else if (scalar > runs[middle * 3 + 1])
            {
                low = middle + 1;
            }
            else
            {
                return (scalar - first) % runs[middle * 3 + 2] == 0;
            }
        }
        return false;
    }

    static bool IsAscii(string text)
    {
        foreach (var c in text)
        {
            if (c >= 0x80)
            {
                return false;
            }
        }
        return true;
    }

    static string AsciiUpper(string text)
    {
        var index = text.AsSpan().IndexOfAnyInRange('a', 'z');
        if (index < 0)
        {
            return text;
        }
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
            }
        });
    }

    static string AsciiLower(string text)
    {
        var index = text.AsSpan().IndexOfAnyInRange('A', 'Z');
        if (index < 0)
        {
            return text;
        }
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
    }
}
