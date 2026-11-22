using System.Globalization;

namespace Slovo.Core.Storage;

/// <summary>
/// Calendar days in one time zone, for grouping history, streaks and the recordings folders
/// (what Foundation's <c>Calendar</c> does for the macOS app). A day starts at the first
/// instant whose local date is that day: midnight, or the end of a daylight saving jump that
/// skipped midnight (America/Santiago, America/Havana), so days of 23 or 25 hours count as one
/// day each. Tests pass a fixed zone instead of the machine's.
/// </summary>
public sealed record DayCalendar(TimeZoneInfo TimeZone, DayOfWeek FirstDayOfWeek)
{
    /// <summary>The machine's time zone and the user's first day of the week.</summary>
    public static DayCalendar Current => new(TimeZoneInfo.Local, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);

    /// <summary>The calendar day an instant falls on.</summary>
    public DateOnly LocalDate(DateTimeOffset instant)
    {
        // Windows can report a day change a millisecond early. Use the UTC second
        // containing the instant so dates agree with StartOfDay at the boundary.
        var second = DateTimeOffset.FromUnixTimeSeconds(instant.ToUnixTimeSeconds());
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(second, TimeZone).DateTime);
    }

    /// <summary>The first instant of the day an instant falls on (<c>calendar.startOfDay(for:)</c>).</summary>
    public DateTimeOffset StartOfDay(DateTimeOffset instant) => StartOfDay(LocalDate(instant));

    /// <summary>The first instant of a calendar day, with the offset in effect then.</summary>
    public DateTimeOffset StartOfDay(DateOnly day)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        DateTimeOffset utc;
        if (TimeZone.IsInvalidTime(midnight))
        {
            utc = FirstInstantOf(day, midnight);
        }
        else if (TimeZone.IsAmbiguousTime(midnight))
        {
            // Clocks went back over midnight: the earlier of the two midnights starts the day.
            var offset = TimeZone.GetAmbiguousTimeOffsets(midnight).Max();
            utc = new DateTimeOffset(midnight - offset, TimeSpan.Zero);
        }
        else
        {
            utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, TimeZone), TimeSpan.Zero);
        }
        return TimeZoneInfo.ConvertTime(utc, TimeZone);
    }

    /// <summary>The start of the next calendar day: no instant of <paramref name="day"/> is at or after it.</summary>
    public DateTimeOffset EndOfDay(DateOnly day) => StartOfDay(day.AddDays(1));

    /// <summary>The first day of the week that holds <paramref name="day"/>.</summary>
    public DateOnly StartOfWeek(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek - (int)FirstDayOfWeek + 7) % 7));

    /// <summary>
    /// Midnight was skipped: the day starts where the clocks jumped. Local time only moves
    /// forward around a spring-forward change, so a binary search over the hours around
    /// midnight finds the first instant that is already on <paramref name="day"/>.
    /// </summary>
    DateTimeOffset FirstInstantOf(DateOnly day, DateTime midnight)
    {
        var standard = TimeZone.BaseUtcOffset;
        // Windows time zone rules can encode a midnight transition as 23:59:59.999.
        // Search at whole-second resolution to get the same boundary as IANA rules.
        var low = new DateTimeOffset(midnight - standard - TimeSpan.FromHours(4), TimeSpan.Zero).ToUnixTimeSeconds();
        var high = new DateTimeOffset(midnight - standard + TimeSpan.FromHours(4), TimeSpan.Zero).ToUnixTimeSeconds();
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (LocalDate(DateTimeOffset.FromUnixTimeSeconds(middle)) >= day)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }
        return DateTimeOffset.FromUnixTimeSeconds(low);
    }
}
