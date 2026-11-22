using System.Globalization;
using System.Text;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.Core.Meetings;

/// <summary>
/// The string table a meeting document is written in. Swift passes
/// <c>language ?? AppLanguage.saved()</c>; here a null language means the language Slovo
/// shows now (<see cref="CoreStrings.CurrentCode"/>), which the app keeps in step with the
/// saved setting.
/// </summary>
internal static class MeetingLanguage
{
    public static string Code(AppLanguage? language) =>
        language is { } chosen ? CoreStrings.ResolveCode(chosen) : CoreStrings.CurrentCode;

    public static StringTable Table(string code) => CoreStrings.TableFor(code);

    public static StringTable Table(AppLanguage? language) => Table(Code(language));
}

/// <summary>
/// Dates as the macOS app writes them into meeting documents and default titles:
/// <c>DateFormatter</c> with <c>dateStyle</c> .long or .medium and <c>timeStyle</c> .short,
/// in English or Russian. The patterns are the ones current macOS (ICU/CLDR 46) uses, spelled
/// out here because .NET's own culture patterns differ from Apple's ("dddd, MMMM d, yyyy"),
/// and a document written on Windows must read exactly like one written on a Mac:
/// <list type="bullet">
/// <item>en, long: <c>MMMM d, y 'at' h:mm\u202Fa</c> → "September 15, 2026 at 11:02 AM"</item>
/// <item>ru, long: <c>d MMMM y\u202F'г'. 'в' HH:mm</c> → "15 сентября 2026 г. в 11:02"</item>
/// <item>en, medium: <c>MMM d, y 'at' h:mm\u202Fa</c> → "Sep 15, 2026 at 11:02 AM"</item>
/// <item>ru, medium: <c>d MMM y\u202F'г'., HH:mm</c> → "15 сент. 2026 г., 11:02"</item>
/// </list>
/// The space before AM/PM and before «г.» is U+202F NARROW NO-BREAK SPACE, as on macOS.
/// </summary>
public static class MeetingDates
{
    const char NarrowSpace = '\u202F';

    static readonly string[] EnglishMonths =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    static readonly string[] EnglishShortMonths =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    // Genitive forms: «15 сентября».
    static readonly string[] RussianMonths =
        ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];

    static readonly string[] RussianShortMonths =
        ["янв.", "февр.", "марта", "апр.", "мая", "июня", "июля", "авг.", "сент.", "окт.", "нояб.", "дек."];

    /// <summary><c>dateStyle = .long</c>, <c>timeStyle = .short</c>; the time zone defaults to the computer's.</summary>
    public static string LongDateShortTime(DateTimeOffset date, AppLanguage? language = null, TimeZoneInfo? timeZone = null) =>
        Format(date, MeetingLanguage.Code(language), timeZone, longMonth: true);

    /// <summary><c>dateStyle = .medium</c>, <c>timeStyle = .short</c>; the time zone defaults to the computer's.</summary>
    public static string MediumDateShortTime(DateTimeOffset date, AppLanguage? language = null, TimeZoneInfo? timeZone = null) =>
        Format(date, MeetingLanguage.Code(language), timeZone, longMonth: false);

    internal static string LongDateShortTime(DateTimeOffset date, string code, TimeZoneInfo? timeZone) =>
        Format(date, code, timeZone, longMonth: true);

    static string Format(DateTimeOffset date, string code, TimeZoneInfo? timeZone, bool longMonth)
    {
        var local = TimeZoneInfo.ConvertTime(date, timeZone ?? TimeZoneInfo.Local);
        var month = local.Month - 1;
        var builder = new StringBuilder(40);
        if (code == "ru")
        {
            builder.Append(local.Day.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(longMonth ? RussianMonths[month] : RussianShortMonths[month]).Append(' ')
                .Append(local.Year.ToString(CultureInfo.InvariantCulture)).Append(NarrowSpace).Append("г.")
                .Append(longMonth ? " в " : ", ")
                .Append(local.Hour.ToString("00", CultureInfo.InvariantCulture)).Append(':')
                .Append(local.Minute.ToString("00", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
        var hour = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        builder.Append(longMonth ? EnglishMonths[month] : EnglishShortMonths[month]).Append(' ')
            .Append(local.Day.ToString(CultureInfo.InvariantCulture)).Append(", ")
            .Append(local.Year.ToString(CultureInfo.InvariantCulture)).Append(" at ")
            .Append(hour.ToString(CultureInfo.InvariantCulture)).Append(':')
            .Append(local.Minute.ToString("00", CultureInfo.InvariantCulture)).Append(NarrowSpace)
            .Append(local.Hour < 12 ? "AM" : "PM");
        return builder.ToString();
    }
}
