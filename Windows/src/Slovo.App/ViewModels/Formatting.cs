using System.Globalization;
using Slovo.App.Localization;

namespace Slovo.App.ViewModels;

/// <summary>Durations, clock times and dates the way the macOS app writes them.</summary>
public static class Formatting
{
    /// <summary>"0:07", "12:40", "1:02:03".</summary>
    public static string Clock(double seconds)
    {
        var total = double.IsFinite(seconds) ? (long)Math.Max(0, Math.Floor(seconds)) : 0;
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{secs:00}");
    }

    /// <summary>"[01:05]" for transcript lines (minutes keep two digits, hours appear when needed).</summary>
    public static string Timestamp(double seconds)
    {
        var total = double.IsFinite(seconds) ? (long)Math.Max(0, Math.Floor(seconds)) : 0;
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{secs:00}");
    }

    public static string Time(DateTimeOffset date) =>
        date.ToLocalTime().ToString("t", Loc.Instance.Culture);

    public static string Date(DateTimeOffset date) =>
        date.ToLocalTime().ToString("D", Loc.Instance.Culture);

    /// <summary>
    /// "September 15, 2026" / «15 сентября 2026 г.»: the culture's long date without the
    /// weekday, as the macOS longDate (date style .long) writes it.
    /// </summary>
    public static string LongDate(DateTimeOffset date)
    {
        var culture = Loc.Instance.Culture;
        var pattern = culture.DateTimeFormat.LongDatePattern;
        foreach (var weekday in new[] { "dddd, ", "dddd ", ", dddd", " dddd", "dddd" })
        {
            pattern = pattern.Replace(weekday, "", StringComparison.Ordinal);
        }
        return date.ToLocalTime().ToString(pattern.Trim(), culture);
    }

    public static string ShortDate(DateTimeOffset date) =>
        date.ToLocalTime().ToString("d MMM yyyy", Loc.Instance.Culture);

    /// <summary>"Today, 10:52", "Yesterday, 18:04" or the date with the time.</summary>
    public static string RelativeDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        var time = Time(date);
        if (local.Date == today)
        {
            return Loc.Instance.Format("date.todayAt", time);
        }
        if (local.Date == today.AddDays(-1))
        {
            return Loc.Instance.Format("date.yesterdayAt", time);
        }
        return ShortDate(date) + ", " + time;
    }

    /// <summary>"Today", "Yesterday" or the long date, for history day headers.</summary>
    public static string DayTitle(DateTime day)
    {
        var today = DateTime.Now.Date;
        if (day == today) return Loc.Instance.Get("date.today");
        if (day == today.AddDays(-1)) return Loc.Instance.Get("date.yesterday");
        return day.ToString(day.Year == today.Year ? "M" : "D", Loc.Instance.Culture);
    }

    /// <summary>"12 480" / "12,480".</summary>
    /// <summary>"3h 12m" / "3 ч 12 мин", or minutes alone under an hour — what the macOS app's abbreviated duration formatter gives.</summary>
    public static string Duration(double seconds)
    {
        var total = double.IsFinite(seconds) ? (long)Math.Round(Math.Max(0, seconds), MidpointRounding.AwayFromZero) : 0;
        var russian = Loc.Instance.Culture.TwoLetterISOLanguageName == "ru";
        var minutes = total >= 3600 ? total % 3600 / 60 : total / 60;
        var minuteText = russian ? $"{minutes} мин" : $"{minutes}m";
        return total >= 3600 ? (russian ? $"{total / 3600} ч " : $"{total / 3600}h ") + minuteText : minuteText;
    }

    public static string Number(long value) => value.ToString("N0", Loc.Instance.Culture);

    /// <summary>"243 MB" / "243 МБ", or "1,2 GB" from a gigabyte on.</summary>
    public static string Megabytes(long bytes)
    {
        var culture = Loc.Instance.Culture;
        var russian = Loc.Instance.LanguageCode == "ru";
        if (bytes >= 1_000_000_000)
        {
            return (bytes / 1_000_000_000.0).ToString("0.#", culture) + (russian ? " ГБ" : " GB");
        }
        return Math.Max(1, (long)Math.Round(bytes / 1_000_000.0)).ToString("N0", culture) + (russian ? " МБ" : " MB");
    }
}

/// <summary>Case-insensitive search that treats ё as е, as Russian readers expect.</summary>
public static class Search
{
    public static string Key(string text) => text.ToLowerInvariant().Replace('ё', 'е');

    public static bool Matches(string query, params string?[] fields)
    {
        var needle = Key(query.Trim());
        return needle.Length == 0 || fields.Any(field => field is not null && Key(field).Contains(needle, StringComparison.Ordinal));
    }
}
