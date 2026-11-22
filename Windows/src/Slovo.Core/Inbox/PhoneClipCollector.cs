using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Slovo.Core.Dictation;
using Slovo.Core.Logging;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.Core.Inbox;

/// <summary>
/// Collects the clips a phone recorded over the USB cable and leaves them in the inbox, where
/// <see cref="InboxImporter"/> turns each one into a meeting (port of the macOS
/// <c>PhoneClipCollector</c>; <c>Scripts/pull_phone_clips.ps1</c> does the same by hand).
///
/// Nothing listens on the network: this asks adb what is plugged in, copies the pair of files a
/// clip is made of, and deletes from the phone only what arrived whole. A computer without adb
/// is an ordinary computer rather than a broken one, so that is said once and then left alone.
///
/// The phone's side of the bargain is that a clip appears in the folder only once it is
/// complete, with its sidecar already beside it. This side does not take that on trust: audio
/// with no sidecar is left where it is until it has stopped changing for a minute, which is far
/// longer than publishing a clip takes.
///
/// Threading: used from the UI thread (<see cref="IMainThread"/>), like the importer. adb runs
/// on the thread pool, one command at a time for the whole app, and a pass comes back to the
/// UI thread after each command. <see cref="Start"/> collects at once and then
/// <see cref="PollSeconds"/> after each pass ends; passes never overlap.
/// </summary>
public sealed class PhoneClipCollector : IDisposable
{
    /// <summary>Where the phone app writes: MediaStore's shared <c>Download/Slovo</c>.</summary>
    public const string PhoneFolder = "/sdcard/Download/Slovo";
    /// <summary>How often the cable is looked at; a pull then takes as long as it takes.</summary>
    public const double PollSeconds = 15;
    /// <summary>
    /// A file on its way from the phone is written under this prefix and renamed once whole;
    /// the leading dot keeps the importer, which skips hidden files, away from it.
    /// </summary>
    public const string PartialPrefix = ".partial-";
    /// <summary>
    /// How long audio with no sidecar has to sit still before it is taken for what it looks
    /// like. Publishing a clip takes a moment, so a minute is the difference between a
    /// recording another app left in the folder and one of this phone's clips caught between
    /// its audio appearing and its sidecar landing.
    /// </summary>
    public const double SidecarGraceSeconds = 60;
    /// <summary>
    /// A command that has not answered by then is stuck — a locked phone, a cable half out —
    /// and is killed, so the next poll starts from a clean adb.
    /// </summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(120);
    /// <summary>
    /// How long what a finished command printed may take to arrive. An adb server the command
    /// started can inherit its output and hold it open long after the command itself ended;
    /// what had arrived by then is the answer.
    /// </summary>
    internal static readonly TimeSpan OutputDrainTime = TimeSpan.FromSeconds(3);
    /// <summary>
    /// The longest file name: 255 UTF-16 code units on NTFS, 255 bytes of UTF-8 on the file
    /// systems of a Mac or Linux, where the app is developed.
    /// </summary>
    const int MaximumNameLength = 255;

    /// <summary>
    /// adb runs here, one command at a time: a pull of a long clip must not race the next
    /// poll, and none of this waiting belongs on the UI thread. One queue for the whole app,
    /// as on the Mac.
    /// </summary>
    static readonly SemaphoreSlim AdbQueue = new(1, 1);

    static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    /// <summary>What Swift's <c>Character.isNewline</c> splits on.</summary>
    static readonly char[] LineBreaks = ['\n', '\r', '\v', '\f', '\u0085', '\u2028', '\u2029'];
    /// <summary>Anything that leaves the folder or ends the quoting around a name, in a phone's shell or on a command line.</summary>
    static readonly char[] RefusedCharacters = ['/', '\'', '"', '\\', '`', '$'];
    /// <summary>What Windows does not allow in a file name, besides what is refused everywhere.</summary>
    static readonly char[] RefusedOnWindows = [':', '*', '?', '<', '>', '|'];
    /// <summary>Names Windows gives to devices, with or without an extension: <c>NUL.m4a</c> is not a file.</summary>
    static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    static readonly Logger Logger = Log.For("PhoneClips");

    /// <summary>A clip on the phone: the audio, and the sidecar next to it when it has one.</summary>
    internal sealed record PhoneClip(string Audio, string? Sidecar);

    /// <summary>
    /// What the phone said about its folder: its own clock, and the modification time of each
    /// clip that was asked about.
    /// </summary>
    internal sealed record PhoneTimes(DateTimeOffset? Now, IReadOnlyDictionary<string, DateTimeOffset> Modified)
    {
        public static PhoneTimes None { get; } = new(null, new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
    }

    /// <summary>What adb printed, and whether it worked.</summary>
    internal readonly record struct CommandResult(int Status, string Output)
    {
        /// <summary>A command that never ran: it could not start, or the collector was stopped first.</summary>
        public static CommandResult NotRun => new(-1, "");

        public bool Succeeded => Status == 0;
    }

    /// <summary>
    /// Runs one command to its end and returns what it printed. Called on the adb queue, off the
    /// UI thread; <paramref name="cancellation"/> fires when the collector stops.
    /// </summary>
    internal delegate CommandResult CommandRunner(string executable, IReadOnlyList<string> arguments, CancellationToken cancellation);

    readonly AppPaths paths;
    readonly AppSettings settings;
    readonly IMainThread mainThread;
    readonly IScheduler scheduler;
    readonly Func<string?> findAdb;
    readonly CommandRunner runner;
    /// <summary>Cancelled by <see cref="Stop"/>, so a command in flight is killed instead of outliving the app.</summary>
    CancellationTokenSource stopping = new();
    bool running;
    /// <summary>Set by <see cref="Stop"/>, so a pass in progress ends after its current clip.</summary>
    bool stopped;
    bool collecting;
    IDisposable? nextPoll;
    /// <summary>
    /// Said once. Without it a computer that has no adb would repeat the same line every poll,
    /// and never installing adb is a choice, not a fault.
    /// </summary>
    bool saidAdbIsMissing;
    /// <summary>
    /// Phones already told about, so a folder listing the poll cannot date is said once rather
    /// than every fifteen seconds.
    /// </summary>
    readonly HashSet<string> saidTimesAreUnreadable = new(StringComparer.Ordinal);

    public PhoneClipCollector(AppPaths paths, AppSettings settings, IMainThread mainThread)
        : this(paths, settings, mainThread, new MainThreadScheduler(mainThread), FindAdb, RunProcess)
    {
    }

    /// <summary>
    /// Where adb is and how a command runs are given rather than assumed, so a test can put a
    /// stand-in for the cable — and for the phone behind it — in their place.
    /// </summary>
    internal PhoneClipCollector(AppPaths paths, AppSettings settings, IMainThread mainThread, IScheduler scheduler,
                                Func<string?> findAdb, CommandRunner runner)
    {
        this.paths = paths;
        this.settings = settings;
        this.mainThread = mainThread;
        this.scheduler = scheduler;
        this.findAdb = findAdb;
        this.runner = runner;
    }

    public bool IsRunning => running;

    /// <summary>Whether this run has said that adb is missing; for tests.</summary>
    internal bool SaidAdbIsMissing => saidAdbIsMissing;

    /// <summary>The phones this run has said cannot date their clips; for tests.</summary>
    internal IReadOnlySet<string> SaidTimesAreUnreadable => saidTimesAreUnreadable;

    // MARK: - Polling

    /// <summary>Polls while the app runs; each pass does nothing unless the inbox is switched on.</summary>
    public void Start()
    {
        if (running)
        {
            return;
        }
        running = true;
        stopped = false;
        if (stopping.IsCancellationRequested)
        {
            stopping = new CancellationTokenSource();
        }
        Poll();
    }

    /// <summary>Stops polling and kills the command in flight; the pass it belonged to ends there.</summary>
    public void Stop()
    {
        if (!running)
        {
            return;
        }
        running = false;
        stopped = true;
        nextPoll?.Dispose();
        nextPoll = null;
        stopping.Cancel();
    }

    public void Dispose() => Stop();

    /// <summary>A pass now, unless one is still running; that one schedules the next when it ends.</summary>
    void Poll()
    {
        nextPoll = null;
        if (running && !collecting)
        {
            _ = PollAsync();
        }
    }

    async Task PollAsync()
    {
        collecting = true;
        try
        {
            await CollectAsync().ResumeOn(mainThread);
        }
        catch (Exception error)
        {
            Logger.Error("Collecting clips from the phone failed", error);
        }
        finally
        {
            collecting = false;
        }
        if (running)
        {
            nextPoll = scheduler.Schedule(PollSeconds, Poll);
        }
    }

    // MARK: - Collecting

    /// <summary>One pass: the cable, then every phone that answered on it.</summary>
    internal async Task CollectAsync()
    {
        if (!settings.InboxEnabled)
        {
            return;
        }
        if (findAdb() is not { } adb)
        {
            if (!saidAdbIsMissing)
            {
                saidAdbIsMissing = true;
                Logger.Info("adb is not installed, so nothing is collected over the cable");
            }
            return;
        }
        saidAdbIsMissing = false;

        var devices = await RunAsync(adb, ["devices"]).ResumeOn(mainThread);
        // A phone that is only plugged in, unauthorized or still waking up is not a failure:
        // the next poll finds it once its owner has allowed USB debugging.
        if (!devices.Succeeded)
        {
            return;
        }
        // Every phone that answered, not the first of them: a second phone on the cable carries
        // its own clips, and they would otherwise wait for the first to be unplugged.
        foreach (var serial in AuthorizedSerials(devices.Output))
        {
            if (stopped)
            {
                return;
            }
            await CollectAsync(serial, adb).ResumeOn(mainThread);
        }
    }

    /// <summary>
    /// One phone: what is in its folder, how old the clips that came without a sidecar are, and
    /// then every clip that is complete.
    /// </summary>
    async Task CollectAsync(string serial, string adb)
    {
        var listing = await RunAsync(adb, ["-s", serial, "shell", "ls", "-1", PhoneFolder]).ResumeOn(mainThread);
        if (!listing.Succeeded)
        {
            return;
        }
        var clips = Clips(listing.Output);
        var waiting = clips.Where(clip => clip.Sidecar is null).Select(clip => clip.Audio).ToList();
        var times = waiting.Count == 0 ? PhoneTimes.None : await ReadPhoneTimesAsync(waiting, serial, adb).ResumeOn(mainThread);
        NoteUnreadableTimes(times, waiting, serial);
        foreach (var clip in Collectable(clips, times))
        {
            if (stopped)
            {
                return;
            }
            await CollectAsync(clip, serial, adb).ResumeOn(mainThread);
        }
    }

    /// <summary>
    /// Says once that a phone answered with nothing a clip's age can be read from — a
    /// <c>stat</c> that does not take these flags, a shell that swallowed the command. Every clip
    /// that arrived without a sidecar then waits for a pass that never comes, which is the safe
    /// thing to do and, left unsaid, looks exactly like a phone with nothing to hand over. A
    /// phone that answers properly is forgotten again, so a cable that starts failing later is
    /// not silent because of what happened before it.
    /// </summary>
    void NoteUnreadableTimes(PhoneTimes times, IReadOnlyList<string> asked, string serial)
    {
        if (asked.Count == 0)
        {
            return;
        }
        if (TimesAreReadable(times, asked))
        {
            saidTimesAreUnreadable.Remove(serial);
            return;
        }
        if (saidTimesAreUnreadable.Add(serial))
        {
            Logger.Info($"{serial} reported no clock or clip times, so its clips without a sidecar keep waiting; its stat may not take the flags used");
        }
    }

    async Task CollectAsync(PhoneClip clip, string serial, string adb)
    {
        var inbox = paths.InboxFolder;
        try
        {
            Directory.CreateDirectory(inbox);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Could not open the inbox folder", error);
            return;
        }
        // Already here: still in the inbox, being imported, or moved on to Imported once it
        // became a meeting. A clip the phone would not let go of comes round again on the next
        // pass, and without the other places to look it would become a second meeting. The
        // phone keeps its copy either way, because a matching name is not enough to decide that
        // this computer has the clip.
        if (IsAlreadyHere(clip.Audio, inbox))
        {
            return;
        }

        // The sidecar is copied first. An import starts from the audio, so a clip is never picked
        // up while the metadata that belongs to it is still being copied.
        if (clip.Sidecar is { } sidecar && !await PullAsync(sidecar, serial, adb, inbox).ResumeOn(mainThread))
        {
            return;
        }
        if (!await PullAsync(clip.Audio, serial, adb, inbox).ResumeOn(mainThread))
        {
            // The sidecar arrived and its audio did not: it goes, since nothing here belongs to
            // it, and the pair stays on the phone for a later pass.
            if (clip.Sidecar is { } arrived)
            {
                TryDelete(Path.Combine(inbox, arrived));
            }
            return;
        }
        Logger.Info($"Collected {clip.Audio} from the phone");

        // Deleted from the phone only now, with both files here and neither empty. The audio goes
        // first so the sidecar outlives it: while the pair is whole the phone's own rule still
        // holds, so a delete that fails in between leaves a clip the next pass pairs again, not
        // audio the grace period would take for a plain note.
        if (!await RemoveAsync(clip.Audio, serial, adb).ResumeOn(mainThread))
        {
            return;
        }
        if (clip.Sidecar is { } paired)
        {
            await RemoveAsync(paired, serial, adb).ResumeOn(mainThread);
        }
    }

    /// <summary>
    /// Whether a clip of this name is in the inbox, is being imported there under its claim
    /// name, or has already been imported out of it.
    /// </summary>
    internal static bool IsAlreadyHere(string name, string inbox)
    {
        if (File.Exists(Path.Combine(inbox, name))
            || File.Exists(Path.Combine(inbox, InboxImporter.ImportedFolderName, name)))
        {
            return true;
        }
        try
        {
            return Directory.EnumerateFiles(inbox, InboxImporter.ClaimPrefix + "*")
                .Any(path => InboxImporter.ParseClaimName(Path.GetFileName(path)) is { } claim
                             && string.Equals(claim.Name, name, StringComparison.Ordinal));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True once the phone no longer has the file. <c>rm -f</c> stays quiet about a file that
    /// was already gone and speaks only of a real refusal — a read-only volume, a cable pulled
    /// mid-command — which is worth hearing: the clip stays where it is, and the check for a clip
    /// already here is then all that keeps it from arriving twice.
    /// </summary>
    async Task<bool> RemoveAsync(string name, string serial, string adb)
    {
        var result = await RunAsync(adb, ["-s", serial, "shell", "rm", "-f", $"'{PhoneFolder}/{name}'"]).ResumeOn(mainThread);
        if (!result.Succeeded)
        {
            Logger.Error($"{name} stays on the phone: deleting it there failed");
        }
        return result.Succeeded;
    }

    /// <summary>
    /// The phone's clock and the modification times of the named clips, in one command. Both
    /// ends of the comparison are read from the phone, because a computer whose clock runs ahead
    /// of it would make a clip written a second ago look a minute old.
    /// </summary>
    async Task<PhoneTimes> ReadPhoneTimesAsync(IReadOnlyList<string> names, string serial, string adb)
    {
        // adb joins these into one line for the phone's shell without quoting anything, so the
        // format string and every path carry their own quotes.
        List<string> arguments = ["-s", serial, "shell", "date", "+%s", ";", "stat", "-c", "'%Y %n'"];
        arguments.AddRange(names.Select(name => $"'{PhoneFolder}/{name}'"));
        var result = await RunAsync(adb, arguments).ResumeOn(mainThread);
        return ParsePhoneTimes(result.Output);
    }

    /// <summary>
    /// True once the file is here under its own name and not empty.
    ///
    /// adb writes into a hidden <c>.partial-&lt;name&gt;</c> that the importer never looks at,
    /// and the file takes its real name only once the copy is complete: a pull that stalls for a
    /// few seconds would otherwise leave a half-copied clip that has stopped changing, which is
    /// exactly what the importer takes for a finished one. A failed pull leaves nothing behind.
    /// </summary>
    async Task<bool> PullAsync(string name, string serial, string adb, string folder)
    {
        var target = Path.Combine(folder, name);
        var partial = Path.Combine(folder, PartialName(name));
        TryDelete(partial);
        var result = await RunAsync(adb, ["-s", serial, "pull", $"{PhoneFolder}/{name}", partial]).ResumeOn(mainThread);
        // The move replaces a stale file of the same name in one step, which a sidecar left by an
        // earlier failed pass may be.
        if (result.Succeeded && Size(partial) > 0 && TryMove(partial, target))
        {
            return true;
        }
        TryDelete(partial);
        Logger.Error($"Pulling {name} failed; it stays on the phone");
        return false;
    }

    /// <summary>The hidden name a file is copied under until it has arrived whole.</summary>
    internal static string PartialName(string name) => PartialPrefix + name;

    static long Size(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    // MARK: - Reading what adb printed

    /// <summary>
    /// The serials of phones ready to talk to. <c>adb devices</c> prints a header and then one
    /// <c>&lt;serial&gt; &lt;state&gt;</c> line each; only "device" is usable, while
    /// "unauthorized" is a phone whose owner has not tapped Allow and "offline" one that is still
    /// waking up.
    /// </summary>
    internal static IReadOnlyList<string> AuthorizedSerials(string output) =>
        Lines(output)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 2 && fields[1] == "device")
            .Select(fields => fields[0])
            .ToList();

    /// <summary>
    /// The clips in a folder listing, each with the sidecar that shares its name. A sidecar with
    /// no audio beside it is left on the phone: the clip is what makes a meeting, and a sidecar
    /// pulled alone would only sit in the inbox waiting.
    /// </summary>
    internal static IReadOnlyList<PhoneClip> Clips(string listing)
    {
        var names = Lines(listing).Select(line => line.Trim()).Where(name => IsSafeName(name)).ToList();
        var sidecarsByStem = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names.Where(name => string.Equals(Extension(name), InboxSidecar.FileExtension, StringComparison.OrdinalIgnoreCase)))
        {
            sidecarsByStem[Stem(name)] = name;
        }
        return names
            .Where(name => InboxImporter.AudioExtensions.Contains(Extension(name)))
            .Order(StringComparer.Ordinal)
            .Select(name => new PhoneClip(name, sidecarsByStem.GetValueOrDefault(Stem(name))))
            .ToList();
    }

    /// <summary>
    /// The clips that may be taken now.
    ///
    /// A clip with its sidecar beside it is finished, because that is the order the phone
    /// publishes them in. Audio on its own is either a recording some other app left in the
    /// folder or one of this phone's clips caught between its two files appearing, and only age
    /// tells the two apart — so it waits out the grace period first. Audio whose age the phone
    /// did not report waits for a later pass instead of being guessed at: guessing wrong costs a
    /// meeting every moment that was marked during it.
    /// </summary>
    internal static IReadOnlyList<PhoneClip> Collectable(IReadOnlyList<PhoneClip> clips, PhoneTimes times) =>
        clips.Where(clip => clip.Sidecar is not null
                            || (times.Now is { } now
                                && times.Modified.TryGetValue(clip.Audio, out var modified)
                                && (now - modified).TotalSeconds >= SidecarGraceSeconds))
            .ToList();

    /// <summary>
    /// What <c>date +%s; stat -c '%Y %n'</c> printed: the phone's clock alone on a line, then
    /// one <c>&lt;epoch&gt; &lt;path&gt;</c> line per file. A name can hold spaces, so a line is
    /// split once, and a line that does not begin with a number is stat naming a file that has
    /// just gone, or adb talking about its daemon, and is skipped.
    /// </summary>
    internal static PhoneTimes ParsePhoneTimes(string output)
    {
        DateTimeOffset? now = null;
        var modified = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            var text = line.Trim();
            var space = text.IndexOf(' ');
            if (space < 0)
            {
                if (now is null && Epoch(text) is { } clock)
                {
                    now = clock;
                }
                continue;
            }
            if (Epoch(text[..space]) is not { } date)
            {
                continue;
            }
            var name = LastPathComponent(text[(space + 1)..]);
            if (name.Length > 0)
            {
                modified[name] = date;
            }
        }
        return new PhoneTimes(now, modified);
    }

    /// <summary>
    /// Whether the phone gave both ends of the comparison an age needs: its own clock, and a
    /// time for at least one of the clips asked about. One clip missing from the answer is
    /// <c>stat</c> naming a file that has just gone, not a phone that cannot be read.
    /// </summary>
    internal static bool TimesAreReadable(PhoneTimes times, IReadOnlyList<string> asked) =>
        times.Now is not null && asked.Any(times.Modified.ContainsKey);

    /// <summary>
    /// Names travel into a shell command on the phone, onto the command line that starts adb and
    /// into a path on this computer, so only a plain file name that still fits the file system's
    /// limit while it is copied is taken: nothing that leaves the folder, and nothing that could
    /// end the quoting around it. A leading dot is refused in its own right: MediaStore parks a
    /// clip that is still being written under <c>.pending-&lt;n&gt;-&lt;name&gt;</c>, so the dot
    /// marks exactly the file that must never be pulled, and <c>.</c> and <c>..</c> are not
    /// files at all.
    /// </summary>
    internal static bool IsSafeName(string name) => IsSafeName(name, OperatingSystem.IsWindows());

    /// <summary>
    /// <see cref="IsSafeName(string)"/> by the rules of Windows (<paramref name="windows"/>) or
    /// of the Mac. Windows also refuses what it cannot store — <c>: * ? &lt; &gt; |</c>, a name
    /// ending in a dot or a space, which it would quietly store under another name, and device
    /// names such as <c>NUL.m4a</c> — and counts a name's length in UTF-16 code units, as NTFS
    /// does, rather than in bytes of UTF-8.
    /// </summary>
    internal static bool IsSafeName(string name, bool windows)
    {
        // A name has to leave room for the prefix it is copied under.
        var length = windows ? name.Length : Encoding.UTF8.GetByteCount(name);
        if (name.Length == 0 || length > MaximumNameLength - PartialPrefix.Length || name.StartsWith('.'))
        {
            return false;
        }
        if (name.IndexOfAny(RefusedCharacters) >= 0 || name.Any(character => character < ' '))
        {
            return false;
        }
        if (!windows)
        {
            return true;
        }
        if (name.IndexOfAny(RefusedOnWindows) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
        {
            return false;
        }
        var dot = name.IndexOf('.');
        return !WindowsDeviceNames.Contains((dot < 0 ? name : name[..dot]).TrimEnd(' '));
    }

    /// <summary>The extension without its dot; empty for none, and for a name that only begins with a dot.</summary>
    static string Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[(dot + 1)..] : "";
    }

    static string Stem(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>The last component of a path on the phone, where only <c>/</c> separates.</summary>
    static string LastPathComponent(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    static string[] Lines(string text) => text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Seconds since 1970 as a date; null for anything else.</summary>
    static DateTimeOffset? Epoch(string text)
    {
        const double earliest = -62_135_596_800;
        const double latest = 253_402_300_799;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds < earliest || seconds > latest)
        {
            return null;
        }
        return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
    }

    // MARK: - Finding adb

    /// <summary>adb on this computer, or null when none is installed.</summary>
    static string? FindAdb() => FindAdb(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, IsExecutableFile);

    /// <summary>The first of <see cref="AdbCandidates"/> that <paramref name="isExecutable"/> accepts.</summary>
    internal static string? FindAdb(bool windows, Func<string, string?> environment, Func<string, bool> isExecutable) =>
        AdbCandidates(windows, environment).FirstOrDefault(isExecutable);

    /// <summary>
    /// Where adb is looked for, in order. On Windows: the adb named by <c>%ADB%</c>, then
    /// <c>adb.exe</c> in each folder of PATH, then where Android Studio installs it
    /// (<c>%LOCALAPPDATA%\Android\Sdk\platform-tools</c>) — the order of
    /// <c>Scripts/pull_phone_clips.ps1</c>. Elsewhere, as on the Mac: where Android Studio and
    /// Homebrew put it, then anything on PATH; an app started from the Dock has a short PATH, so
    /// the two known places are what usually answers.
    /// </summary>
    internal static IEnumerable<string> AdbCandidates(bool windows, Func<string, string?> environment)
    {
        if (windows)
        {
            if (Unquoted(environment("ADB")) is { Length: > 0 } chosen)
            {
                yield return chosen;
            }
            foreach (var folder in (environment("PATH") ?? "").Split(';'))
            {
                if (Unquoted(folder) is { Length: > 0 } unquoted)
                {
                    yield return Join(unquoted, "adb.exe", '\\');
                }
            }
            if (Unquoted(environment("LOCALAPPDATA")) is { Length: > 0 } local)
            {
                yield return Join(local, @"Android\Sdk\platform-tools\adb.exe", '\\');
            }
            yield break;
        }
        if (environment("HOME") is { Length: > 0 } home)
        {
            yield return Join(home, "Library/Android/sdk/platform-tools/adb", '/');
        }
        yield return "/opt/homebrew/bin/adb";
        foreach (var folder in (environment("PATH") ?? "").Split(':'))
        {
            if (folder.Length > 0)
            {
                yield return Join(folder, "adb", '/');
            }
        }
    }

    /// <summary>A Windows path as a variable holds it, which may be in quotes.</summary>
    static string? Unquoted(string? value) => value?.Trim().Trim('"').Trim();

    static string Join(string folder, string name, char separator) => folder.TrimEnd('/', separator) + separator + name;

    /// <summary>A file that exists and, on the Mac and Linux, that someone may execute.</summary>
    internal static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            if (OperatingSystem.IsWindows())
            {
                return true;
            }
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // MARK: - Running adb

    /// <summary>Runs one adb command on the queue, with the runner this collector was given.</summary>
    Task<CommandResult> RunAsync(string adb, IReadOnlyList<string> arguments)
    {
        var cancellation = stopping.Token;
        return OnAdbQueueAsync(() => runner(adb, arguments, cancellation), cancellation);
    }

    /// <summary>Runs a process on the adb queue, off the UI thread, and returns what it printed.</summary>
    internal static Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan? timeout = null,
                                                 CancellationToken cancellation = default) =>
        OnAdbQueueAsync(() => Run(executable, arguments, timeout ?? CommandTimeout, cancellation), cancellation);

    /// <summary>Waits for the adb queue, then runs <paramref name="command"/> on the thread pool; a command stopped while it waited never runs.</summary>
    static async Task<CommandResult> OnAdbQueueAsync(Func<CommandResult> command, CancellationToken cancellation)
    {
        try
        {
            await AdbQueue.WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.NotRun;
        }
        try
        {
            return await Task.Run(command, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            AdbQueue.Release();
        }
    }

    static CommandResult RunProcess(string executable, IReadOnlyList<string> arguments, CancellationToken cancellation) =>
        Run(executable, arguments, CommandTimeout, cancellation);

    /// <summary>
    /// Runs a process to its end, or kills it after <paramref name="timeout"/> or when
    /// <paramref name="cancellation"/> fires, and returns its exit code with what it printed.
    /// Blocks; call it off the UI thread.
    ///
    /// Output and errors are read as one stream, line by line in the order they arrive: adb
    /// writes "daemon started" and other asides to its error output, and the readers above skip
    /// lines they do not know. Both are decoded as UTF-8, which is what adb passes on from the
    /// phone, whatever the console's code page. No window is shown for the console program.
    /// </summary>
    internal static CommandResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
                                      CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested)
        {
            return CommandResult.NotRun;
        }
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        // The adb server otherwise browses mDNS on every interface for phones with wireless
        // debugging, though clips only ever come over USB (and on a Mac, macOS then asks whether
        // the app may find devices on the local network). The setting applies to a server this
        // command starts; one already running keeps its own.
        start.Environment["ADB_MDNS"] = "0";
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return CommandResult.NotRun;
        }
        try
        {
            // Nothing to read, as from the null device: a remote shell gets its end of input.
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        var output = new StringBuilder();
        var gate = new Lock();
        Task[] readers = [ReadLines(process.StandardOutput, output, gate), ReadLines(process.StandardError, output, gate)];
        bool exited;
        using (cancellation.Register(() => Kill(process)))
        {
            exited = process.WaitForExit(timeout);
            if (!exited)
            {
                Kill(process);
                process.WaitForExit(OutputDrainTime);
            }
        }
        Task.WaitAll(readers, OutputDrainTime);
        string text;
        lock (gate)
        {
            text = output.ToString();
        }
        return new CommandResult(exited && !cancellation.IsCancellationRequested ? process.ExitCode : -1, text);
    }

    /// <summary>
    /// Reads a stream to its end on a thread of its own, appending whole lines to the shared
    /// output, so lines from the two streams never mix and a full pipe never stops the process.
    /// </summary>
    static Task ReadLines(StreamReader reader, StringBuilder output, Lock gate) =>
        Task.Factory.StartNew(() =>
        {
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    lock (gate)
                    {
                        output.Append(line).Append('\n');
                    }
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    static void Kill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }
}
