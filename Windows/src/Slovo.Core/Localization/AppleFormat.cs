using System.Globalization;
using System.Text;

namespace Slovo.Core.Localization;

/// <summary>
/// Fills printf-style patterns the way <c>String(format:)</c> does for the specifiers the
/// shared tables use: <c>%@</c>, <c>%d</c>/<c>%ld</c>/<c>%lld</c>/<c>%i</c>/<c>%u</c>/<c>%lu</c>,
/// <c>%f</c>/<c>%.1f</c>, <c>%%</c>, and positional forms such as <c>%2$@</c>.
/// </summary>
public static class AppleFormat
{
    public static string Format(string pattern, CultureInfo culture, params object?[] arguments)
    {
        if (pattern.IndexOf('%') < 0)
        {
            return pattern;
        }
        var builder = new StringBuilder(pattern.Length + 16);
        var sequential = 0;
        var index = 0;
        while (index < pattern.Length)
        {
            var c = pattern[index];
            if (c != '%')
            {
                builder.Append(c);
                index++;
                continue;
            }
            var start = index;
            index++;
            if (index < pattern.Length && pattern[index] == '%')
            {
                builder.Append('%');
                index++;
                continue;
            }
            // Optional position "N$".
            int? position = null;
            var digitsStart = index;
            while (index < pattern.Length && char.IsAsciiDigit(pattern[index])) index++;
            if (index < pattern.Length && pattern[index] == '$' && index > digitsStart)
            {
                position = int.Parse(pattern.AsSpan(digitsStart, index - digitsStart), CultureInfo.InvariantCulture);
                index++;
            }
            else
            {
                index = digitsStart;
            }
            // Flags and width are ignored except precision.
            while (index < pattern.Length && pattern[index] is '-' or '+' or ' ' or '#' or '0') index++;
            while (index < pattern.Length && char.IsAsciiDigit(pattern[index])) index++;
            int? precision = null;
            if (index < pattern.Length && pattern[index] == '.')
            {
                index++;
                var precisionStart = index;
                while (index < pattern.Length && char.IsAsciiDigit(pattern[index])) index++;
                precision = index > precisionStart
                    ? int.Parse(pattern.AsSpan(precisionStart, index - precisionStart), CultureInfo.InvariantCulture)
                    : 0;
            }
            while (index < pattern.Length && pattern[index] is 'l' or 'h' or 'q' or 'z' or 't' or 'j') index++;
            if (index >= pattern.Length)
            {
                builder.Append(pattern, start, pattern.Length - start);
                break;
            }
            var conversion = pattern[index++];
            var argumentIndex = position.HasValue ? position.Value - 1 : sequential++;
            var argument = argumentIndex >= 0 && argumentIndex < arguments.Length ? arguments[argumentIndex] : null;
            builder.Append(conversion switch
            {
                '@' => Convert.ToString(argument, culture) ?? "(null)",
                'd' or 'i' or 'u' => Convert.ToInt64(argument ?? 0, culture).ToString(culture),
                'f' or 'F' => Convert.ToDouble(argument ?? 0, culture).ToString("F" + (precision ?? 6).ToString(CultureInfo.InvariantCulture), culture),
                'g' => Convert.ToDouble(argument ?? 0, culture).ToString("G", culture),
                's' => Convert.ToString(argument, culture) ?? "",
                _ => pattern[start..index],
            });
        }
        return builder.ToString();
    }
}

/// <summary>CLDR plural categories for the two interface languages.</summary>
public static class PluralRules
{
    public static string Category(string languageCode, long count)
    {
        var n = Math.Abs(count);
        if (languageCode == "ru")
        {
            var mod10 = n % 10;
            var mod100 = n % 100;
            if (mod10 == 1 && mod100 != 11) return "one";
            if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return "few";
            return "many";
        }
        return n == 1 ? "one" : "other";
    }
}
