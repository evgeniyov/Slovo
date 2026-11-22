using System.Globalization;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.Core.Meetings;

/// <summary>
/// Localized errors of the meetings module (port of the macOS <c>MeetingErrors</c>). Every
/// message comes from <c>Meetings.strings</c>; lines that name macOS are replaced by the
/// Windows table in <c>Slovo.Core/Strings/&lt;language&gt;/Meetings.strings</c>.
/// </summary>
public static class MeetingErrors
{
    public static SlovoException AlreadyRecording() => Plain("meetings.error.alreadyRecording");
    public static SlovoException MicrophoneDenied() => Plain("meetings.error.microphoneDenied");
    public static SlovoException NoMicrophone() => Plain("meetings.error.noMicrophone");
    public static SlovoException MicrophoneFailed(Exception error) => Formatted("meetings.error.microphoneFailed", error);
    public static SlovoException SystemAudioUnsupported() => Plain("meetings.error.systemAudioUnsupported");
    public static SlovoException SystemAudioDenied() => Plain("meetings.error.systemAudioDenied");
    public static SlovoException ScreenRecordingDenied() => Plain("meetings.error.screenRecordingDenied");
    public static SlovoException ScreenRecordingUnavailable() => Plain("meetings.error.screenRecordingUnavailable");
    public static SlovoException ScreenRecordingAppNotRunning() => Plain("meetings.error.screenRecordingAppNotRunning");

    /// <summary>A system audio call failed; the function name and status stay untranslated for bug reports.</summary>
    public static SlovoException TapFailed(string function, int status) =>
        new(CoreStrings.Format("meetings.error.tapFailed", string.Create(CultureInfo.InvariantCulture, $"{function} ({status})")));

    public static SlovoException UnsupportedAudioFormat() => Plain("meetings.error.unsupportedFormat");

    /// <summary>The free space is spelled out, so the message says how far short the disk is.</summary>
    public static SlovoException LowDiskSpace(long availableBytes) =>
        new(CoreStrings.Format("meetings.error.lowDiskSpace", ByteCount(availableBytes)));

    /// <summary>A recording ended by itself before the disk filled; what it holds is saved.</summary>
    public static SlovoException LowDiskSpaceWhileRecording(long availableBytes) =>
        new(CoreStrings.Format("meetings.error.lowDiskSpaceWhileRecording", ByteCount(availableBytes)));

    /// <summary>System audio capture could not be rebuilt; it keeps retrying.</summary>
    public static SlovoException SystemAudioInterrupted(Exception error) => Formatted("meetings.error.systemAudioInterrupted", error);

    public static SlovoException AppAudioEnded() => Plain("meetings.error.appAudioEnded");
    public static SlovoException FileFailed(Exception error) => Formatted("meetings.error.fileFailed", error);
    public static SlovoException ReadFailed(Exception error) => Formatted("meetings.error.readFailed", error);
    public static SlovoException CreateFailed(Exception error) => Formatted("meetings.error.createFailed", error);
    public static SlovoException SaveFailed(Exception error) => Formatted("meetings.error.saveFailed", error);
    public static SlovoException NotFound() => Plain("meetings.error.notFound");
    public static SlovoException NoAudio() => Plain("meetings.error.noAudio");
    public static SlovoException StillRecording() => Plain("meetings.error.stillRecording");
    public static SlovoException AlreadyTranscribing() => Plain("meetings.error.alreadyTranscribing");
    public static SlovoException SpeakersOnlyUnavailable() => Plain("meetings.error.speakersOnlyUnavailable");
    public static SlovoException FileTooShort() => Plain("meetings.error.fileTooShort");
    public static SlovoException TranscriptFailed(Exception error) => Formatted("meetings.error.transcriptFailed", error);
    public static SlovoException MergeAcrossTracks() => Plain("meetings.error.mergeAcrossTracks");

    public static string CancelledMessage() => CoreStrings.Get("meetings.transcription.cancelled");
    public static string InterruptedMessage() => CoreStrings.Get("meetings.transcription.interrupted");
    public static string RecordDamagedMessage() => CoreStrings.Get("meetings.error.recordDamaged");

    /// <summary>
    /// A file size the way <c>ByteCountFormatter</c> spells it with <c>.file</c> (decimal
    /// units): whole kilobytes, megabytes with one decimal, larger units with two, trailing
    /// zeros dropped ("230 MB", "1.5 GB", "12.35 GB"), in the interface language.
    /// </summary>
    public static string ByteCount(long bytes)
    {
        var culture = CoreStrings.Table.Culture;
        var russian = CoreStrings.CurrentCode == "ru";
        string[] units = russian ? ["байт", "КБ", "МБ", "ГБ", "ТБ", "ПБ"] : ["bytes", "KB", "MB", "GB", "TB", "PB"];
        string[] patterns = ["0", "0", "0.#", "0.##", "0.##", "0.##"];
        bytes = Math.Max(0, bytes);
        if (bytes < 1000)
        {
            return bytes.ToString(culture) + " " + (bytes == 1 && !russian ? "byte" : units[0]);
        }
        var value = bytes / 1000.0;
        var unit = 1;
        // A value that rounds up to 1000 moves to the next unit: 999 999 bytes read "1 MB".
        while (unit < units.Length - 1 && (value >= 1000 || double.Parse(value.ToString(patterns[unit], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) >= 1000))
        {
            value /= 1000;
            unit++;
        }
        return value.ToString(patterns[unit], culture) + " " + units[unit];
    }

    static SlovoException Plain(string key) => new(CoreStrings.Get(key));

    static SlovoException Formatted(string key, Exception error) => new(CoreStrings.Format(key, error.Message), error);
}

/// <summary>Default meeting titles, shared by the store (recovered meetings) and the recorder.</summary>
public static class MeetingTitles
{
    /// <summary>«Встреча 15 сент. 2026 г., 11:02» / "Meeting Sep 15, 2026 at 11:02 AM".</summary>
    public static string DefaultTitle(DateTimeOffset date, AppLanguage? language = null, TimeZoneInfo? timeZone = null)
    {
        var code = MeetingLanguage.Code(language);
        return MeetingLanguage.Table(code).Format("meetings.defaultTitle", MeetingDates.MediumDateShortTime(date, CodeLanguage(code), timeZone));
    }

    static AppLanguage CodeLanguage(string code) => code == "ru" ? AppLanguage.Ru : AppLanguage.En;
}
