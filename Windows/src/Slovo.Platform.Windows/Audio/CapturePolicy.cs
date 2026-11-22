namespace Slovo.Platform.Windows.Audio;

/// <summary>
/// When capture gives up, retries and switches devices (MicrophoneRecorder.swift and
/// MeetingMicrophoneCapture.swift), kept apart from the devices so the schedule can be
/// tested.
/// </summary>
public static class CapturePolicy
{
    /// <summary>Dictation: restarts after a device loss are tried this often before giving up.</summary>
    public const int DictationRestartAttempts = 5;
    /// <summary>Dictation: the wait between restart attempts.</summary>
    public static readonly TimeSpan DictationRestartInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Meetings: a lost device is retried this long before the owner is told it is gone. A
    /// Bluetooth handoff can leave the input missing for many seconds, which is not worth a
    /// warning; the retries themselves never stop while the recording runs.
    /// </summary>
    public const double RestartBudget = 30;
    /// <summary>
    /// The first wait between attempts; it grows towards <see cref="MaximumRetryDelay"/>, so
    /// a device that comes back at once is caught immediately and a long outage costs few
    /// attempts.
    /// </summary>
    public const double RestartRetryDelay = 0.3;
    public const double MaximumRetryDelay = 2;
    /// <summary>Device notifications come in bursts while hardware settles; switch once they stop.</summary>
    public const double SwitchDelay = 0.3;

    /// <summary>The wait before the next attempt, or null once another wait would run past the budget.</summary>
    public static double? NextRetryDelay(double elapsed, double delay)
    {
        if (elapsed + delay >= RestartBudget)
        {
            return null;
        }
        return Math.Min(delay * 1.5, MaximumRetryDelay);
    }

    /// <summary>
    /// The endpoint ids to try in order: the chosen microphone when it is connected, then the
    /// system default, then every other connected input, built-in ones last. A default that
    /// cannot be opened — a headset halfway through a handoff — would otherwise take every
    /// attempt the budget buys while a working microphone sat unused.
    /// </summary>
    public static IReadOnlyList<string> PreferredDeviceIds(string? chosen, Func<string, bool> isConnected, string? defaultId,
        IReadOnlyList<string>? connected = null, Func<string, bool>? isBuiltIn = null)
    {
        var ids = new List<string>();
        if (!string.IsNullOrEmpty(chosen) && isConnected(chosen))
        {
            ids.Add(chosen);
        }
        if (!string.IsNullOrEmpty(defaultId) && !ids.Contains(defaultId))
        {
            ids.Add(defaultId);
        }
        var rest = (connected ?? []).Where(id => !string.IsNullOrEmpty(id) && !ids.Contains(id)).Distinct().ToList();
        var builtIn = isBuiltIn ?? (_ => false);
        ids.AddRange(rest.Where(id => !builtIn(id)));
        ids.AddRange(rest.Where(builtIn));
        return ids;
    }
}
