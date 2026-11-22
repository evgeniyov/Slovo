namespace Slovo.Core.Audio;

/// <summary>
/// The format of every meeting track on disk and of the samples the segmenter and the
/// engines see (port of the macOS <c>MeetingAudioFormat</c>): 16 kHz mono, stored as 16-bit
/// little-endian signed integer LPCM in a CAF file, which halves the disk use of Float32 and
/// loses nothing that matters for speech; CAF keeps the samples readable when the app dies
/// before the header is finalized.
/// </summary>
public static class MeetingAudioFormat
{
    public const int SampleRate = 16_000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int BytesPerFrame = BitsPerSample / 8 * Channels;
    /// <summary>Disk bytes one second of a track takes.</summary>
    public const long BytesPerSecond = (long)SampleRate * BytesPerFrame;

    /// <summary><c>Meetings/&lt;id&gt;/mic.caf</c>.</summary>
    public const string MicrophoneFileName = "mic.caf";
    /// <summary><c>Meetings/&lt;id&gt;/system.caf</c>.</summary>
    public const string SystemFileName = "system.caf";
    /// <summary><c>Meetings/&lt;id&gt;/screen.mov</c>, the optional screen video (a fragmented MP4 on Windows, a QuickTime movie on macOS).</summary>
    public const string ScreenFileName = "screen.mov";
}
