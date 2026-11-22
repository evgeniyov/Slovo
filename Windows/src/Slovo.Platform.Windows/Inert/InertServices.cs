using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Platform.Windows.Inert;

// Stand-ins used when the Windows app runs on another system (a Mac) for interface work:
// nothing records, listens to keys or pastes, and each says so plainly instead of failing
// in a way that looks like a bug.

/// <summary>A recorder that refuses to start with a clear message.</summary>
public sealed class InertAudioRecorder : IAudioRecorder
{
    public bool IsRecording => false;
    public string? LastDeviceName => null;

    public event Action<float>? LevelChanged
    {
        add { }
        remove { }
    }

    public void Start(string? deviceUid) => throw new SlovoException(CoreStrings.Get("audio.error.unavailableHere"));
    public RecordedAudio Stop() => RecordedAudio.Empty;
    public void Cancel()
    {
    }
}

public sealed class InertAudioDevices : IAudioDevices
{
    public IReadOnlyList<AudioInputDevice> InputDevices() => [];

    public event Action? DevicesChanged
    {
        add { }
        remove { }
    }
}

/// <summary>A dictation key that never fires; Start reports that it is not running.</summary>
public sealed class InertHotkeyMonitor : IHotkeyMonitor
{
    public bool IsRunning => false;
    public bool IsKeyDown => false;

    public event Action<DictationTrigger>? Triggered
    {
        add { }
        remove { }
    }

    public event Action<bool>? KeyStateChanged
    {
        add { }
        remove { }
    }

    public bool Start(HotkeyConfiguration configuration) => false;
    public void Update(HotkeyConfiguration configuration)
    {
    }

    public void SyncRecording(DictationMode? mode)
    {
    }

    public void Stop()
    {
    }
}

/// <summary>Accepts every shortcut and never fires it, so settings screens show no "taken" error.</summary>
public sealed class InertGlobalShortcuts : IGlobalShortcuts
{
    public IDisposable? Register(HotkeyShortcut shortcut, Action action) => new Registration();

    sealed class Registration : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>Keeps the text in memory and reports that pasting failed, so the "Copied" state shows.</summary>
public sealed class InertTextInserter : ITextInserter
{
    public string? LastCopied { get; private set; }

    public Task<InsertionResult> InsertAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(InsertionResult.EmptyResult);
        }
        LastCopied = text;
        return Task.FromResult<InsertionResult>(new InsertionResult.Copied(CopyReason.PasteFailed));
    }

    public void CopyToClipboard(string text) => LastCopied = text;
}

public sealed class InertForegroundApp : IForegroundApp
{
    public AppIdentity? CurrentApp() => null;
}

public sealed class InertFocusedInput : IFocusedInput
{
    public bool? IsTextInputFocused() => null;
}

/// <summary>Everything granted, so onboarding and settings can be worked on.</summary>
public sealed class InertPermissions : IPermissions
{
    public PermissionStatus Status(PermissionKind kind) => PermissionStatus.Granted;
    public Task<PermissionStatus> RequestAsync(PermissionKind kind) => Task.FromResult(PermissionStatus.Granted);
    public void OpenSystemSettings(PermissionKind kind)
    {
    }

    public bool IsSecureInputActive => false;
}

public sealed class InertSoundPlayer : ISoundPlayer
{
    public void Play(SoundCue cue)
    {
    }
}

public sealed class InertAutostart : IAutostart
{
    public bool IsEnabled => false;
    public bool SetEnabled(bool enabled) => false;
}

/// <summary>A microphone stream that cannot start.</summary>
public sealed class InertAudioStream : IAudioStream
{
    public string? DeviceName => null;

    public event Action<string>? Failed
    {
        add { }
        remove { }
    }

    public event Action? Resumed
    {
        add { }
        remove { }
    }

    public void Start(Action<float[], double?> onSamples) => throw new SlovoException(CoreStrings.Get("audio.error.unavailableHere"));
    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>A microphone that cannot start and no system audio.</summary>
public sealed class InertMeetingAudioSources : IMeetingAudioSources
{
    public IAudioStream Microphone(string? deviceUid) => new InertAudioStream();
    public IAudioStream? SystemAudio() => null;
}

public sealed class InertAudioDecoder : IAudioDecoder
{
    public bool CanDecode(string path) => false;
    public float[] DecodeToMono16k(string path, CancellationToken cancellation) =>
        throw new SlovoException(CoreStrings.Get("audio.error.unavailableHere"));
}

/// <summary>Screen recording that says it works only on Windows; no apps to pick from.</summary>
public sealed class InertScreenCapture : IScreenCapture
{
    public string? UnavailableReason => CoreStrings.Get("audio.error.unavailableHere");
    public (int Width, int Height)? MainDisplaySize() => null;
    public IReadOnlyList<AppIdentity> CapturableApps() => [];
    public IScreenCaptureStream Open(ScreenCaptureTarget target) => throw new SlovoException(CoreStrings.Get("audio.error.unavailableHere"));
    public IVideoEncoder CreateEncoder(int width, int height) => throw new SlovoException(CoreStrings.Get("audio.error.unavailableHere"));

    public event Action<ScreenSessionEvent>? SessionChanged
    {
        add { }
        remove { }
    }
}
