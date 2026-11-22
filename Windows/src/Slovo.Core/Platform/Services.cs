using Slovo.Core.Model;

namespace Slovo.Core.Platform;

// Services depend on the operating system only through these interfaces
// (the Windows counterparts of Sources/SlovoCore/Model/ServiceProtocols.swift).
// Slovo.Platform.Windows implements them; tests use fakes; on other systems the app
// runs with inert stand-ins so the interface can be developed on a Mac.
//
// Threading: unless a member says otherwise, call these from the UI thread. Events
// documented as "any thread" must be marshalled by the subscriber.

/// <summary>Microphone capture for dictation: 16 kHz mono float, kept in memory.</summary>
public interface IAudioRecorder
{
    bool IsRecording { get; }
    /// <summary>Smoothed RMS level from 0 to 1, about 30 times per second, on any thread.</summary>
    event Action<float>? LevelChanged;
    /// <summary>Starts capturing from the device with this id, or the default when null or gone. Throws SlovoException.</summary>
    void Start(string? deviceUid);
    /// <summary>Stops and returns everything captured.</summary>
    RecordedAudio Stop();
    /// <summary>Stops and throws the audio away.</summary>
    void Cancel();
    /// <summary>Name of the microphone the last recording used, for "Nothing heard".</summary>
    string? LastDeviceName { get; }
}

/// <summary>Audio input devices.</summary>
public interface IAudioDevices
{
    IReadOnlyList<AudioInputDevice> InputDevices();
    /// <summary>Raised when devices are added or removed or the default changes, on any thread.</summary>
    event Action? DevicesChanged;
}

/// <summary>The global dictation key listener (a low-level keyboard hook on Windows).</summary>
public interface IHotkeyMonitor
{
    bool IsRunning { get; }
    /// <summary>Whether the dictation key is held right now, for "press to test" indicators.</summary>
    bool IsKeyDown { get; }
    /// <summary>Raised on the UI thread.</summary>
    event Action<DictationTrigger>? Triggered;
    /// <summary>Raised on the UI thread.</summary>
    event Action<bool>? KeyStateChanged;
    bool Start(HotkeyConfiguration configuration);
    void Update(HotkeyConfiguration configuration);
    /// <summary>Tells the state machine what the controller is doing (a recording started from the Flow bar, or null once stopped).</summary>
    void SyncRecording(DictationMode? mode);
    void Stop();
}

/// <summary>System-wide shortcuts such as "paste last transcript" (RegisterHotKey on Windows).</summary>
public interface IGlobalShortcuts
{
    /// <summary>Registers a shortcut; the action runs on the UI thread. Null when the system refused it.</summary>
    IDisposable? Register(HotkeyShortcut shortcut, Action action);
}

/// <summary>Puts text into the focused field.</summary>
public interface ITextInserter
{
    Task<InsertionResult> InsertAsync(string text);
    /// <summary>Copies without pasting, marked so clipboard history and cloud sync skip it.</summary>
    void CopyToClipboard(string text);
}

/// <summary>The app that receives dictated text.</summary>
public interface IForegroundApp
{
    /// <summary>The foreground app other than Slovo itself.</summary>
    AppIdentity? CurrentApp();
}

/// <summary>Whether keyboard focus is in something that accepts text.</summary>
public interface IFocusedInput
{
    /// <summary>True for a text field, false for something else, null when it cannot be told.</summary>
    bool? IsTextInputFocused();
}

/// <summary>Permission status and the settings that change it.</summary>
public interface IPermissions
{
    PermissionStatus Status(PermissionKind kind);
    Task<PermissionStatus> RequestAsync(PermissionKind kind);
    /// <summary>Opens the matching page of Windows Settings.</summary>
    void OpenSystemSettings(PermissionKind kind);
    /// <summary>True when input would not reach the foreground window (it runs elevated, or the secure desktop is up).</summary>
    bool IsSecureInputActive { get; }
}

public interface ISoundPlayer
{
    void Play(SoundCue cue);
}

/// <summary>Starting Slovo when the user signs in.</summary>
public interface IAutostart
{
    bool IsEnabled { get; }
    /// <summary>Returns false when the system refused the change.</summary>
    bool SetEnabled(bool enabled);
}

/// <summary>Opening files, folders and links with the system.</summary>
public interface IShell
{
    void OpenFolder(string path);
    /// <summary>Opens the folder with the file selected.</summary>
    void RevealFile(string path);
    void OpenUrl(string url);
    /// <summary>
    /// Opens a media file Slovo wrote (a meeting's screen recording) in the app the system has
    /// for it; anything else is only shown in its folder.
    /// </summary>
    void OpenFile(string path) => RevealFile(path);
}

/// <summary>
/// A continuous 16 kHz mono float stream for meeting recording: the microphone, or the
/// system's output (WASAPI loopback, without Slovo's own sounds).
/// </summary>
public interface IAudioStream : IDisposable
{
    /// <summary>A name for the interface, such as the microphone's.</summary>
    string? DeviceName { get; }
    /// <summary>
    /// Starts delivering samples on a background thread. The callback gets 16 kHz mono
    /// float samples and the monotonic time (<see cref="IUptime"/> seconds) of the
    /// first sample when the source knows it. Throws SlovoException when the stream
    /// cannot start.
    /// </summary>
    void Start(Action<float[], double?> onSamples);
    void Stop();
    /// <summary>
    /// Raised on any thread when the stream stopped by itself (device removed) and could not
    /// be reopened for a while; it keeps retrying. The message is localized.
    /// </summary>
    event Action<string>? Failed;
    /// <summary>Raised on any thread when the stream is delivering again after <see cref="Failed"/>.</summary>
    event Action? Resumed;
}

public interface IMeetingAudioSources
{
    /// <summary>The microphone with this id, or the default input when null or gone.</summary>
    IAudioStream Microphone(string? deviceUid);
    /// <summary>Everything the computer plays except Slovo itself; null when the system cannot capture it.</summary>
    IAudioStream? SystemAudio();

    /// <summary>
    /// One app's own sound, with that of the processes it started, for a screen recording
    /// scoped to that app (<see cref="ScreenCaptureTarget.Application"/>); null where the system
    /// cannot capture a single app. Start throws SlovoException when the app is not running.
    /// </summary>
    IAudioStream? ApplicationAudio(string bundleId) => null;
}

/// <summary>
/// Receives one captured picture while the platform still holds it: 32-bit BGRA rows from
/// top to bottom, <paramref name="stride"/> bytes apart. The span is valid only during the call.
/// </summary>
public delegate void BgraFrameHandler(ReadOnlySpan<byte> pixels, int width, int height, int stride);

/// <summary>What <see cref="IScreenCaptureStream.ReadFrame"/> found.</summary>
public enum ScreenFrameResult
{
    /// <summary>Nothing new since the previous call: the picture has not changed.</summary>
    Unchanged,
    /// <summary>A new picture was handed to the handler.</summary>
    Frame,
    /// <summary>The capture ended by itself (the window closed, the display went away, the graphics device was reset).</summary>
    Lost,
}

/// <summary>What happened to the session a screen recording runs in.</summary>
public enum ScreenSessionEvent
{
    /// <summary>The screen was locked: nothing on it is the owner's to record until unlocked.</summary>
    Locked,
    Unlocked,
    /// <summary>The computer woke from sleep; a capture from before may not have survived it.</summary>
    Resumed,
}

/// <summary>
/// Screen capture for meeting recordings (ScreenCaptureKit on macOS, Windows.Graphics.Capture
/// and Media Foundation on Windows). The meeting logic around it — timeline, pacing, scaling,
/// the file — is <see cref="Meetings.MeetingScreenCapture"/> in the core.
/// </summary>
public interface IScreenCapture
{
    /// <summary>Null when this computer can record the screen; otherwise why not, localized.</summary>
    string? UnavailableReason { get; }

    /// <summary>The pixel size of the main display, which a recording's video is sized from; null when there is none.</summary>
    (int Width, int Height)? MainDisplaySize();

    /// <summary>Running apps with a window that can be recorded, Slovo excepted, sorted by name.</summary>
    IReadOnlyList<AppIdentity> CapturableApps();

    /// <summary>
    /// Opens a capture of the target. Throws SlovoException (the app is not running, capture was
    /// refused). The stream is used from the thread that opened it.
    /// </summary>
    IScreenCaptureStream Open(ScreenCaptureTarget target);

    /// <summary>
    /// An H.264 encoder for NV12 frames of this size, used from the thread that created it.
    /// Throws SlovoException when the system has none (Windows N editions without the Media Feature Pack).
    /// </summary>
    IVideoEncoder CreateEncoder(int width, int height);

    /// <summary>Screen lock, unlock and wake from sleep, raised on any thread while anyone listens.</summary>
    event Action<ScreenSessionEvent>? SessionChanged;
}

/// <summary>One open capture of a display or an app's window.</summary>
public interface IScreenCaptureStream : IDisposable
{
    /// <summary>
    /// Hands the newest picture since the previous call to <paramref name="handler"/> and
    /// returns <see cref="ScreenFrameResult.Frame"/>; or says that nothing changed, or that the
    /// capture ended (see <see cref="LostReason"/>). Throwing counts as lost.
    /// </summary>
    ScreenFrameResult ReadFrame(BgraFrameHandler handler);

    /// <summary>Why the capture ended, untranslated, for the log; null while it runs.</summary>
    string? LostReason { get; }
}

/// <summary>Encodes NV12 pictures into H.264.</summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>
    /// Encodes one NV12 picture (the luma plane, then interleaved chroma at half size) shown
    /// from <paramref name="time"/> seconds on, a key frame when asked. Returns the access
    /// units finished so far in decode order, as Annex B byte streams. Throws on failure.
    /// </summary>
    IReadOnlyList<EncodedVideoFrame> Encode(ReadOnlySpan<byte> nv12, double time, bool keyFrame);
}

/// <summary>One H.264 access unit (Annex B, start codes included) and the time its picture is shown from.</summary>
public sealed record EncodedVideoFrame(byte[] Data, double Time);

/// <summary>Decodes an audio file (m4a, mp3, …) the core cannot read itself into 16 kHz mono float.</summary>
public interface IAudioDecoder
{
    bool CanDecode(string path);
    /// <summary>Throws SlovoException when the file cannot be decoded.</summary>
    float[] DecodeToMono16k(string path, CancellationToken cancellation);
}

/// <summary>Random-access mono audio for playback: a dictation in memory or a meeting track on disk.</summary>
public interface IAudioSource
{
    double SampleRate { get; }
    /// <summary>Length in samples.</summary>
    long Length { get; }
    /// <summary>Reads samples from <paramref name="position"/>; returns how many were read (0 at the end).</summary>
    int Read(long position, Span<float> destination);
}

/// <summary>Samples already in memory.</summary>
public sealed class MemoryAudioSource(float[] samples, double sampleRate) : IAudioSource
{
    public double SampleRate { get; } = sampleRate;
    public long Length => samples.LongLength;

    public int Read(long position, Span<float> destination)
    {
        if (position < 0 || position >= samples.LongLength)
        {
            return 0;
        }
        var count = (int)Math.Min(destination.Length, samples.LongLength - position);
        samples.AsSpan((int)position, count).CopyTo(destination);
        return count;
    }
}

/// <summary>
/// Plays one or more tracks mixed together (a meeting's microphone and system audio start at
/// the same instant, so they play in step). Events are raised on any thread.
/// </summary>
public interface IAudioPlayer : IDisposable
{
    bool IsPlaying { get; }
    /// <summary>Seconds from the start of the tracks.</summary>
    double Position { get; }
    /// <summary>Seconds of the longest track.</summary>
    double Duration { get; }
    /// <summary>
    /// Playback speed, 1 as recorded (a meeting offers 0.75–2); speech keeps its pitch. Takes
    /// effect at once and stays for later <see cref="Play"/> calls.
    /// </summary>
    double Rate { get; set; }
    /// <summary>Playing started, paused, stopped or reached the end.</summary>
    event Action? StateChanged;
    /// <summary>Starts playing from a position. Throws SlovoException when the output cannot open.</summary>
    void Play(IReadOnlyList<IAudioSource> tracks, double startSeconds = 0);
    void Pause();
    void Resume();
    void Stop();
    void Seek(double seconds);
}
