using Slovo.Core.Audio;
using Slovo.Core.Dictation;
using Slovo.Core.Json;
using Slovo.Core.Logging;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.Core.Inbox;

/// <summary>
/// Audio dropped into <c>Inbox/</c> becomes a meeting: a clip recorded on a phone, a voice
/// message, a file copied in by hand (port of the macOS <c>InboxImporter</c>). What fills the
/// folder — a sync tool, Explorer, <c>Scripts/pull_phone_clips.ps1</c> — is deliberately
/// outside this type, so the import path outlives any transport.
///
/// A clip that arrives with the <c>.json</c> sidecar a phone writes keeps that phone's time,
/// name and marked moments; audio that arrives alone is imported exactly as before.
///
/// Threading: used from the UI thread (<see cref="IMainThread"/>), where the store is read
/// and written. Decoding and writing audio run on the thread pool. <see cref="Start"/> looks
/// at the folder at once, then every <see cref="PollSeconds"/> after a sweep ends, and
/// <see cref="SettleSeconds"/> after the folder reports a change; sweeps never overlap.
/// </summary>
public sealed class InboxImporter : IDisposable
{
    /// <summary>
    /// What the importer takes: what the macOS app takes, plus what Media Foundation opens on
    /// Windows. WAV and CAF are decoded here; the rest goes through <see cref="IAudioDecoder"/>,
    /// and a file neither can read is remembered as refused. Also the file picker's allowed
    /// types for a manual import, so the two ways of adding a file never disagree.
    /// </summary>
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "m4a", "mp3", "wav", "caf", "aac", "aif", "aiff", "mp4", "mov", "wma", "flac", "3gp", "m4b",
    };

    /// <summary>A file has to stop growing before it is taken, so a half-copied clip is left alone.</summary>
    public const double SettleSeconds = 3;
    /// <summary>Shorter than this is a stray tap, not speech.</summary>
    public const double MinimumSeconds = 0.5;
    public const double PollSeconds = 5;
    /// <summary>
    /// How long a sidecar waits for its audio. The phone writes the sidecar after the clip and
    /// a transport copies it last, so after this long the audio is not coming at all.
    /// </summary>
    public const double StrandedSidecarSeconds = 600;
    /// <summary>
    /// Imported files move here rather than being deleted: the folder is often the shared end
    /// of a sync, and destroying what arrived is not this code's decision to make.
    /// </summary>
    public const string ImportedFolderName = "Imported";
    /// <summary>
    /// A clip being turned into a meeting waits under <c>.importing-&lt;meeting id&gt;-&lt;name&gt;</c>:
    /// hidden, so no sweep takes it again, and naming its meeting, so an import cut off by a
    /// crash or a quit finishes that meeting at the next sweep instead of starting a second one.
    /// </summary>
    public const string ClaimPrefix = ".importing-";

    static readonly Logger Logger = Log.For("Inbox");

    readonly AppPaths paths;
    readonly IMeetingStore store;
    readonly AppSettings settings;
    readonly IMainThread mainThread;
    readonly Func<Guid, MeetingProcessingOptions, Task> transcribe;
    readonly IAudioDecoder? decoder;
    readonly TimeProvider time;
    /// <summary>
    /// Files that failed once, so a broken clip cannot spin the sweep forever. Keyed by name,
    /// size and date, so a file that changes afterwards is tried again.
    /// </summary>
    readonly HashSet<FileIdentity> refused = [];
    /// <summary>The size and date each waiting file had at the last look, and since when.</summary>
    readonly Dictionary<string, Sighting> sightings = new(StringComparer.Ordinal);

    bool running;
    /// <summary>Set by <see cref="Stop"/>, so a sweep in progress ends after its current clip.</summary>
    bool stopped;
    bool sweeping;
    bool sweepAgain;
    InboxFolderWatcher? watcher;
    ITimer? timer;
    DateTimeOffset? timerDue;

    /// <param name="transcribe">
    /// Hands an imported meeting to transcription, as a recording that just stopped is; the
    /// app wires <see cref="MeetingTranscriber.TranscribeAsync(Guid, MeetingProcessingOptions)"/>.
    /// A sweep waits for it, one clip at a time: a long file holds the engine, and nothing here
    /// is urgent. Its failures are the transcriber's to report and do not stop the sweep.
    /// </param>
    /// <param name="decoder">Decodes what the core cannot (m4a, mp3, …); null takes only WAV and CAF.</param>
    public InboxImporter(AppPaths paths, IMeetingStore store, AppSettings settings, IMainThread mainThread,
                         Func<Guid, MeetingProcessingOptions, Task> transcribe, IAudioDecoder? decoder = null, TimeProvider? time = null)
    {
        this.paths = paths;
        this.store = store;
        this.settings = settings;
        this.mainThread = mainThread;
        this.transcribe = transcribe;
        this.decoder = decoder;
        this.time = time ?? TimeProvider.System;
    }

    public string InboxFolder => paths.InboxFolder;

    public bool IsRunning => running;

    // MARK: - Watching

    /// <summary>
    /// Sweeps now, then again <see cref="PollSeconds"/> after each sweep and soon after the
    /// folder changes. Sweeps do nothing while <see cref="AppSettings.InboxEnabled"/> is off;
    /// turning it on sweeps at once.
    /// </summary>
    public void Start()
    {
        if (running)
        {
            return;
        }
        running = true;
        stopped = false;
        settings.Store.Changed += SettingChanged;
        watcher = new InboxFolderWatcher(paths.InboxFolder, () => mainThread.Post(FolderChanged));
        RequestSweep();
    }

    /// <summary>Stops watching; a sweep already running finishes its current clip and ends.</summary>
    public void Stop()
    {
        if (!running)
        {
            return;
        }
        running = false;
        stopped = true;
        settings.Store.Changed -= SettingChanged;
        watcher?.Dispose();
        watcher = null;
        timer?.Dispose();
        timer = null;
        timerDue = null;
    }

    public void Dispose() => Stop();

    /// <summary>Sweeps as soon as no sweep is running (for "look now" and tests); does nothing when stopped.</summary>
    public void RequestSweep()
    {
        if (!running)
        {
            return;
        }
        if (sweeping)
        {
            sweepAgain = true;
            return;
        }
        _ = RunSweepsAsync();
    }

    async Task RunSweepsAsync()
    {
        sweeping = true;
        try
        {
            do
            {
                sweepAgain = false;
                await SweepAsync(time.GetUtcNow()).ResumeOn(mainThread);
            }
            while (sweepAgain && running);
        }
        catch (Exception error)
        {
            Logger.Error("A sweep of the inbox failed", error);
        }
        finally
        {
            sweeping = false;
        }
        if (running)
        {
            ScheduleSweep(TimeSpan.FromSeconds(PollSeconds));
        }
    }

    /// <summary>A change is looked at once the file has had <see cref="SettleSeconds"/> to stop changing.</summary>
    void FolderChanged()
    {
        if (running)
        {
            ScheduleSweep(TimeSpan.FromSeconds(SettleSeconds + 0.5));
        }
    }

    void SettingChanged(string key)
    {
        if (key == SettingsKey.InboxEnabled)
        {
            mainThread.Post(RequestSweep);
        }
    }

    /// <summary>Arms the one timer for <paramref name="delay"/> from now, unless it already fires sooner.</summary>
    void ScheduleSweep(TimeSpan delay)
    {
        var due = time.GetUtcNow() + delay;
        if (timerDue is { } current && current <= due)
        {
            return;
        }
        timerDue = due;
        timer?.Dispose();
        timer = time.CreateTimer(_ => mainThread.Post(TimerFired), null, delay, Timeout.InfiniteTimeSpan);
    }

    void TimerFired()
    {
        if (timerDue is { } due && due > time.GetUtcNow())
        {
            return;
        }
        timerDue = null;
        timer?.Dispose();
        timer = null;
        RequestSweep();
    }

    // MARK: - Sweeping

    /// <summary>
    /// Finishes imports an earlier run started, imports every clip that has settled, then
    /// gives up on sidecars whose audio never came. One clip at a time.
    /// </summary>
    internal async Task SweepAsync(DateTimeOffset now)
    {
        if (!settings.InboxEnabled)
        {
            return;
        }
        // Imports an earlier run started and never finished come first, so their clips are
        // settled before anything new is taken.
        foreach (var claimed in ClaimedClips())
        {
            if (stopped)
            {
                return;
            }
            try
            {
                await ResumeImportAsync(claimed, now).ResumeOn(mainThread);
            }
            catch (Exception error)
            {
                Refuse(claimed.Path);
                Logger.Error($"Finishing the import of {claimed.Name} failed", error);
            }
        }
        foreach (var path in ReadyFiles(now))
        {
            if (stopped)
            {
                return;
            }
            try
            {
                await ImportClipAsync(path, now).ResumeOn(mainThread);
            }
            catch (Exception error)
            {
                Refuse(path);
                Logger.Error($"Importing {Path.GetFileName(path)} failed", error);
            }
        }
        DropStrandedSidecars(now);
    }

    /// <summary>What a refusal is remembered by: the same name with a new size or date is a new file.</summary>
    internal readonly record struct FileIdentity(string Name, long Size, DateTime ModifiedUtc);

    /// <summary>A clip left under its claim name, with the meeting it was becoming.</summary>
    /// <param name="Name">The clip's own name, which it goes back to or on to <c>Imported/</c> under.</param>
    internal sealed record ClaimedClip(string Path, Guid MeetingId, string Name);

    readonly record struct Sighting(long Size, DateTime ModifiedUtc, DateTimeOffset StableSince);

    /// <summary>Remembers the file now at <paramref name="path"/>, if any, as one not to try again while it is unchanged.</summary>
    void Refuse(string path)
    {
        if (Identity(path) is { } identity)
        {
            refused.Add(identity);
        }
    }

    /// <summary>The name, size and modification date of the file at <paramref name="path"/>, read afresh.</summary>
    internal static FileIdentity? Identity(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? Identity(info) : null;
    }

    static FileIdentity Identity(FileInfo info) => new(info.Name, info.Length, info.LastWriteTimeUtc);

    static DateTimeOffset Modified(FileInfo info) => new(info.LastWriteTimeUtc, TimeSpan.Zero);

    /// <summary>
    /// Audio files that have finished arriving, oldest first: modified at least
    /// <see cref="SettleSeconds"/> ago, the same size and date as at the last look for that
    /// long when they changed in between, and not held open by whatever is writing them.
    ///
    /// Hidden files are skipped (a leading dot counts as hidden, as on the Mac): a copy still
    /// arriving as <c>.&lt;name&gt;.partial</c>, a clip already claimed by an import, and
    /// whatever a sync tool keeps to itself.
    /// </summary>
    internal IReadOnlyList<string> ReadyFiles(DateTimeOffset now)
    {
        var settle = TimeSpan.FromSeconds(SettleSeconds);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ready = new List<(string Path, DateTimeOffset Modified, string Name)>();
        foreach (var file in VisibleFiles())
        {
            if (!IsAudioName(file.Name) || refused.Contains(Identity(file)))
            {
                continue;
            }
            seen.Add(file.Name);
            var modified = Modified(file);
            // The file's own date says when it stopped changing; a change seen since the last
            // look (a copy that keeps the source's old date while it grows) starts the wait over.
            var stableSince = sightings.TryGetValue(file.Name, out var last)
                ? (last.Size == file.Length && last.ModifiedUtc == file.LastWriteTimeUtc ? last.StableSince : now)
                : modified;
            sightings[file.Name] = new Sighting(file.Length, file.LastWriteTimeUtc, stableSince);
            if (now - modified < settle || now - stableSince < settle || IsBeingWritten(file.FullName))
            {
                continue;
            }
            ready.Add((file.FullName, modified, file.Name));
        }
        foreach (var gone in sightings.Keys.Where(name => !seen.Contains(name)).ToList())
        {
            sightings.Remove(gone);
        }
        return ready.OrderBy(r => r.Modified).ThenBy(r => r.Name, StringComparer.Ordinal).Select(r => r.Path).ToList();
    }

    /// <summary>
    /// True while another program holds the file open for writing (a copy in progress): on
    /// Windows the open below fails with a sharing violation then. Elsewhere locks are
    /// advisory and this mostly says false; the settle rules still apply.
    /// </summary>
    static bool IsBeingWritten(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Unreadable is not busy: the import fails and remembers the file.
            return false;
        }
    }

    /// <summary>Sidecars left waiting by audio that never arrived, oldest first.</summary>
    internal IReadOnlyList<string> StrandedSidecars(DateTimeOffset now)
    {
        var items = VisibleFiles();
        // A clip under its claim name still owns its sidecar.
        var audioNames = items.Select(f => f.Name).Concat(ClaimedClips(includingRefused: true).Select(c => c.Name));
        var audioStems = audioNames.Where(IsAudioName).Select(Stem).ToHashSet(StringComparer.Ordinal);
        return items
            .Where(f => string.Equals(Extension(f.Name), InboxSidecar.FileExtension, StringComparison.OrdinalIgnoreCase)
                        && !audioStems.Contains(Stem(f.Name))
                        && !refused.Contains(Identity(f))
                        && (now - Modified(f)).TotalSeconds >= StrandedSidecarSeconds)
            .OrderBy(Modified)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => f.FullName)
            .ToList();
    }

    /// <summary>Clips left under their claim name by an import that never finished, in name order.</summary>
    internal IReadOnlyList<ClaimedClip> ClaimedClips(bool includingRefused = false)
    {
        var result = new List<ClaimedClip>();
        foreach (var file in Files())
        {
            if (ParseClaimName(file.Name) is not { } claim || (!includingRefused && refused.Contains(Identity(file))))
            {
                continue;
            }
            result.Add(new ClaimedClip(file.FullName, claim.MeetingId, claim.Name));
        }
        return result.OrderBy(c => Path.GetFileName(c.Path), StringComparer.Ordinal).ToList();
    }

    /// <summary><c>.importing-&lt;meeting id&gt;-&lt;name&gt;</c> for a clip becoming that meeting.</summary>
    public static string ClaimName(Guid meetingId, string name) => ClaimPrefix + meetingId.ToString("D").ToUpperInvariant() + "-" + name;

    /// <summary>The meeting id and the clip's own name in a claim name; null for any other name.</summary>
    public static (Guid MeetingId, string Name)? ParseClaimName(string fileName)
    {
        if (!fileName.StartsWith(ClaimPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = fileName[ClaimPrefix.Length..];
        const int idLength = 36;
        if (rest.Length <= idLength + 1 || rest[idLength] != '-'
            || !Guid.TryParseExact(rest[..idLength], "D", out var id))
        {
            return null;
        }
        var name = rest[(idLength + 1)..];
        return IsAudioName(name) ? (id, name) : null;
    }

    /// <summary>
    /// How a clip is processed. A clip the phone calls a meeting is separated into speakers
    /// exactly as the user's settings ask, like one recorded here; a note keeps speakers off,
    /// because separation would cost a model load and a large memory peak to label one voice
    /// "Speaker 1".
    /// </summary>
    public static MeetingProcessingOptions ProcessingOptions(InboxSidecar.SidecarKind kind, AppSettings settings) => kind switch
    {
        InboxSidecar.SidecarKind.Meeting => MeetingProcessingOptions.FromSettings(settings),
        _ => new MeetingProcessingOptions(MeetingProcessingMode.Full, SeparateSpeakers: false),
    };

    /// <summary>
    /// The moments marked on the phone, on the clip's own timeline. They are the same kind of
    /// mark the shortcut makes here, so they show with the same star. A moment past the end of
    /// the decoded audio is kept at the end rather than dropped: the phone and the computer can
    /// measure a clip a little differently, and the mark still belongs to it.
    /// </summary>
    public static IReadOnlyList<MeetingHighlight> Highlights(InboxSidecar sidecar, double duration, DateTimeOffset recordedAt) =>
        sidecar.Moments.Select(moment =>
        {
            var at = Math.Min(moment.Time, duration);
            return new MeetingHighlight
            {
                Time = at,
                Note = moment.Note,
                Source = HighlightSource.Recording,
                CreatedAt = Storable(recordedAt.AddSeconds(at)),
            };
        }).ToList();

    // MARK: - Importing

    /// <summary>What an import needs from a clip and its sidecar, read before anything is created.</summary>
    sealed record PreparedClip(float[] Samples, double Duration, InboxSidecar? Sidecar, string SidecarPath, DateTimeOffset RecordedAt);

    /// <summary>Reads the audio at <paramref name="path"/> and the sidecar that belongs to a clip named <paramref name="name"/>.</summary>
    async Task<PreparedClip> PrepareAsync(string path, string name)
    {
        var info = new FileInfo(path);
        var arrived = info.Exists ? Modified(info) : time.GetUtcNow();
        var sidecarPath = InboxSidecar.PathFor(Path.Combine(paths.InboxFolder, name));
        var sidecar = InboxSidecar.Read(sidecarPath);
        var audioDecoder = decoder;
        var samples = await Task.Run(() => LoadSamples(path, audioDecoder)).ResumeOn(mainThread);
        return new PreparedClip(samples, (double)samples.Length / MeetingAudioFormat.SampleRate, sidecar, sidecarPath,
                                sidecar?.RecordedAt ?? arrived);
    }

    async Task ImportClipAsync(string path, DateTimeOffset now)
    {
        var name = Path.GetFileName(path);
        var clip = await PrepareAsync(path, name).ResumeOn(mainThread);
        if (clip.Duration < MinimumSeconds)
        {
            MoveToImported(path);
            MoveSidecarAside(clip.SidecarPath);
            return;
        }

        var created = store.Create(clip.Sidecar?.Title ?? MeetingTitles.DefaultTitle(clip.RecordedAt));
        // From here on the clip carries its meeting's id: a crash or a quit before it reaches
        // Imported/ finishes this meeting at the next sweep instead of making a second one.
        var claimed = Path.Combine(paths.InboxFolder, ClaimName(created.Id, name));
        try
        {
            File.Move(path, claimed);
        }
        catch
        {
            store.Delete(created.Id);
            throw;
        }
        sightings.Remove(name);
        MeetingRecord meeting;
        try
        {
            meeting = await FillAsync(created, clip, now).ResumeOn(mainThread);
        }
        catch
        {
            // The meeting is gone again, so the clip goes back under its own name, where the
            // failure is remembered.
            try
            {
                Unclaim(claimed, name);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logger.Error($"Returning {name} to the inbox failed", error);
            }
            throw;
        }
        Finish(meeting, claimed, name, clip);
        await TranscribeAsync(meeting, clip.Sidecar?.Kind).ResumeOn(mainThread);
    }

    /// <summary>
    /// Finishes an import an earlier run started: the meeting the clip names gets its audio and
    /// fields again from the clip, which is still whole, and the clip moves on. A meeting that
    /// is gone, or was never saved, sends the clip back to be imported afresh.
    /// </summary>
    async Task ResumeImportAsync(ClaimedClip claim, DateTimeOffset now)
    {
        if (store.Meeting(claim.MeetingId) is not { } existing)
        {
            Unclaim(claim.Path, claim.Name);
            Logger.Info($"{claim.Name} returns to the inbox: its meeting was never saved");
            return;
        }
        // A meeting already transcribing or transcribed was taken further since; only the files
        // are left to put away.
        if (existing.Status is not (MeetingStatus.Recording or MeetingStatus.Recorded))
        {
            MoveToImported(claim.Path, claim.Name);
            MoveSidecarAside(InboxSidecar.PathFor(Path.Combine(paths.InboxFolder, claim.Name)));
            return;
        }
        var clip = await PrepareAsync(claim.Path, claim.Name).ResumeOn(mainThread);
        var meeting = await FillAsync(existing, clip, now).ResumeOn(mainThread);
        Finish(meeting, claim.Path, claim.Name, clip);
        Logger.Info($"Finished importing {claim.Name}, which a previous run had started");
        await TranscribeAsync(meeting, clip.Sidecar?.Kind).ResumeOn(mainThread);
    }

    /// <summary>Writes the clip's audio into the meeting and sets what the clip and its sidecar say.</summary>
    async Task<MeetingRecord> FillAsync(MeetingRecord created, PreparedClip clip, DateTimeOffset storedAt)
    {
        var meeting = await FillAsync(created, clip.Samples, clip.Duration, clip.RecordedAt, storedAt).ResumeOn(mainThread);
        if (clip.Sidecar is { } sidecar)
        {
            meeting = meeting with
            {
                Source = new MeetingSource { Origin = MeetingOrigin.Phone, Device = sidecar.Device },
                Highlights = Highlights(sidecar, clip.Duration, clip.RecordedAt),
            };
            store.Update(meeting);
        }
        return meeting;
    }

    /// <summary>
    /// Moves the claimed clip and its sidecar on to <c>Imported/</c>. A clip that cannot be
    /// moved stays claimed, so the next sweep finishes this meeting rather than making
    /// another, and is remembered so that sweep is not every five seconds.
    /// </summary>
    void Finish(MeetingRecord meeting, string claimed, string name, PreparedClip clip)
    {
        try
        {
            MoveToImported(claimed, name);
        }
        catch
        {
            Refuse(claimed);
            throw;
        }
        MoveSidecarAside(clip.SidecarPath);
        Logger.Info($"Imported {name} as a {(int)clip.Duration} s meeting ({meeting.Id})");
    }

    async Task TranscribeAsync(MeetingRecord meeting, InboxSidecar.SidecarKind? kind)
    {
        var options = ProcessingOptions(kind ?? InboxSidecar.SidecarKind.Note, settings);
        try
        {
            await transcribe(meeting.Id, options).ResumeOn(mainThread);
        }
        catch (Exception error)
        {
            // The transcriber has already marked the meeting; the import itself succeeded.
            Logger.Info($"Transcribing an imported meeting did not finish: {error.Message}");
        }
    }

    /// <summary>Gives a claimed clip its own name back, or a free variant of it.</summary>
    void Unclaim(string claimed, string name) => File.Move(claimed, FreePath(paths.InboxFolder, name));

    /// <summary>
    /// A file the user picked by hand, from anywhere on disk — not the Inbox folder, and not
    /// gated behind <see cref="AppSettings.InboxEnabled"/>, which only governs the automatic
    /// folder sweep. The file is read in place and never moved or deleted: unlike a clip
    /// dropped into the transient Inbox, this is the user's own copy, sitting wherever they
    /// keep it. It only creates the meeting and its audio track, so the caller starts
    /// transcription the same way it would for a fresh recording. Throws SlovoException.
    /// </summary>
    public async Task<MeetingRecord> ImportPickedFileAsync(string path)
    {
        var now = time.GetUtcNow();
        var info = new FileInfo(path);
        var recordedAt = info.Exists ? Modified(info) : now;
        var audioDecoder = decoder;
        var samples = await Task.Run(() => LoadSamples(path, audioDecoder)).ResumeOn(mainThread);
        var duration = (double)samples.Length / MeetingAudioFormat.SampleRate;
        if (duration < MinimumSeconds)
        {
            throw MeetingErrors.FileTooShort();
        }
        var meeting = await FillAsync(store.Create(Title(path, recordedAt)), samples, duration, recordedAt, now).ResumeOn(mainThread);
        Logger.Info($"Imported {Path.GetFileName(path)} as a {(int)duration} s meeting");
        return meeting;
    }

    /// <summary>
    /// The file's own name, since that is the one thing a manual pick says about its content;
    /// a name left empty falls back to the usual date-based title.
    /// </summary>
    public static string Title(string path, DateTimeOffset recordedAt)
    {
        var stem = Path.GetFileNameWithoutExtension(path).Trim();
        return stem.Length == 0 ? MeetingTitles.DefaultTitle(recordedAt) : stem;
    }

    /// <summary>
    /// Writes the audio track of a created meeting and the fields an imported clip sets; the
    /// meeting is removed again when its track cannot be written. Shared by the inbox sweep
    /// and a manual pick, which differ only in where the title and timestamps come from.
    /// </summary>
    async Task<MeetingRecord> FillAsync(MeetingRecord created, float[] samples, double duration, DateTimeOffset recordedAt,
                                        DateTimeOffset storedAt)
    {
        var track = store.MicrophoneAudioPath(created.Id);
        try
        {
            await Task.Run(() => WriteTrack(samples, track)).ResumeOn(mainThread);
        }
        catch
        {
            store.Delete(created.Id);
            throw;
        }
        // Dates at the precision meeting.json keeps, so the record in memory equals the one read back.
        recordedAt = Storable(recordedAt);
        storedAt = Storable(storedAt);
        var meeting = (store.Meeting(created.Id) ?? created) with
        {
            // One instant both names the meeting and places it in the list: the source's own
            // time when it has one, and otherwise the moment the audio was last written. The
            // title is made from that same instant, and a title that disagreed with the sort
            // order would leave the list looking shuffled.
            CreatedAt = recordedAt,
            // Retention counts the audio's age from here instead of from CreatedAt: a clip
            // recorded elsewhere last week becomes this computer's to keep only now, and counted
            // from the source's time its audio could be deleted before anyone had heard it.
            AudioStoredAt = storedAt,
            Duration = duration,
            HasAudio = true,
            Status = MeetingStatus.Recorded,
        };
        store.Update(meeting);
        return meeting;
    }

    /// <summary>
    /// Moves aside a sidecar whose clip never came, and says so once. Waiting forever would
    /// leave the folder filling with metadata for audio that does not exist.
    /// </summary>
    void DropStrandedSidecars(DateTimeOffset now)
    {
        foreach (var path in StrandedSidecars(now))
        {
            try
            {
                MoveToImported(path);
                Logger.Info($"{Path.GetFileName(path)} waited for its audio, which never arrived");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Refuse(path);
                Logger.Error($"Moving {Path.GetFileName(path)} aside failed", error);
            }
        }
    }

    /// <summary>
    /// A sidecar leaves the folder with its clip, read or not: left behind, it would later look
    /// like metadata whose audio never arrived.
    /// </summary>
    void MoveSidecarAside(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            MoveToImported(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Moving {Path.GetFileName(path)} aside failed", error);
        }
    }

    void MoveToImported(string path, string? name = null)
    {
        var folder = Path.Combine(paths.InboxFolder, ImportedFolderName);
        Directory.CreateDirectory(folder);
        File.Move(path, FreePath(folder, name ?? Path.GetFileName(path)));
    }

    /// <summary><paramref name="name"/> in <paramref name="folder"/>, or <c>&lt;stem&gt;-2.&lt;ext&gt;</c>, <c>&lt;stem&gt;-3.&lt;ext&gt;</c> … when that is taken.</summary>
    static string FreePath(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        var stem = Stem(name);
        var extension = Extension(name);
        for (var attempt = 2; File.Exists(target) || Directory.Exists(target); attempt++)
        {
            target = Path.Combine(folder, extension.Length == 0 ? $"{stem}-{attempt}" : $"{stem}-{attempt}.{extension}");
        }
        return target;
    }

    /// <summary>Regular files of the inbox, hidden ones included; empty when the folder cannot be read.</summary>
    List<FileInfo> Files()
    {
        try
        {
            return new DirectoryInfo(paths.InboxFolder).EnumerateFiles()
                .Where(file => file.LinkTarget is null)
                .ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Files that are not hidden: neither named with a leading dot nor marked hidden.</summary>
    List<FileInfo> VisibleFiles() =>
        Files().Where(file => !file.Name.StartsWith('.') && (file.Attributes & FileAttributes.Hidden) == 0).ToList();

    static DateTimeOffset Storable(DateTimeOffset date) => AppleDate.FromReferenceSeconds(AppleDate.ToReferenceSeconds(date));

    static bool IsAudioName(string name) => AudioExtensions.Contains(Extension(name));

    /// <summary>The extension without its dot, as the Mac's <c>pathExtension</c>.</summary>
    static string Extension(string name) => Path.GetExtension(name).TrimStart('.');

    static string Stem(string name) => Path.GetFileNameWithoutExtension(name);

    // MARK: - Audio

    /// <summary>
    /// 16 kHz mono samples of an audio file. WAV and CAF are read here (as meeting tracks are);
    /// anything else, and a WAV the core cannot parse, goes through the platform decoder.
    /// Throws SlovoException.
    /// </summary>
    internal static float[] LoadSamples(string path, IAudioDecoder? decoder, CancellationToken cancellation = default)
    {
        var extension = Extension(path).ToLowerInvariant();
        var isCaf = CafFile.IsCaf(path);
        if (isCaf || extension is "wav" or "caf")
        {
            try
            {
                return MeetingClips.ReadAll(path, 0, cancellation);
            }
            catch (SlovoException) when (!isCaf && decoder?.CanDecode(path) == true)
            {
                // A compressed WAV, or a file named .wav that holds something else.
            }
        }
        if (decoder?.CanDecode(path) == true)
        {
            return decoder.DecodeToMono16k(path, cancellation);
        }
        throw MeetingErrors.UnsupportedAudioFormat();
    }

    /// <summary>
    /// Writes 16 kHz mono samples as the same CAF a recorded meeting uses, so everything
    /// downstream — transcription, clipping, playback — cannot tell the two apart. Throws SlovoException.
    /// </summary>
    public static void WriteTrack(ReadOnlySpan<float> samples, string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw MeetingErrors.FileFailed(error);
        }
        using var writer = CafWriter.Create(path);
        writer.Append(samples);
        writer.Close();
    }
}
