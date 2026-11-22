using System.Runtime.ExceptionServices;
using Slovo.Core.Audio;
using Slovo.Core.Dictation;
using Slovo.Core.Json;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.Core.Meetings;

public enum MeetingProcessingMode
{
    /// <summary>Speaker separation, then speech recognition of every clip.</summary>
    Full,
    /// <summary>Speaker separation over the existing text; no speech engine is loaded.</summary>
    SpeakersOnly,
}

/// <summary>How one processing run of a meeting should go (port of the macOS <c>MeetingProcessingOptions</c>).</summary>
/// <param name="ExpectedSpeakers">Null or 0 = automatic; otherwise 2–8.</param>
/// <param name="EngineId">Null = the meetings engine from settings.</param>
public sealed record MeetingProcessingOptions(
    MeetingProcessingMode Mode = MeetingProcessingMode.Full,
    bool SeparateSpeakers = true,
    int? ExpectedSpeakers = null,
    EngineId? EngineId = null)
{
    /// <summary>A full run with the Meetings settings: separation on or off and the expected number of people.</summary>
    public static MeetingProcessingOptions FromSettings(AppSettings settings)
    {
        var expected = settings.ExpectedSpeakers;
        return new MeetingProcessingOptions(MeetingProcessingMode.Full, settings.SpeakerSeparation, expected == 0 ? null : expected);
    }
}

/// <summary>
/// Processes recorded meetings off the UI thread: separates speakers on each track, then
/// transcribes one clip per speaker turn, with progress and cancellation (port of the macOS
/// <c>MeetingTranscriber</c>).
///
/// Separation comes first because speech engines return only text: deciding the speaker of
/// each clip before recognition works with every engine. Tracks are read from disk block by
/// block; only the clip being transcribed is in memory (the whole track while the diarizer
/// works on it), handed to the engine as samples.
///
/// A run changes the stored transcript only when it succeeds. Until then the store keeps the
/// previous segments, speakers and separation result, so a failed or cancelled re-run leaves
/// the previous transcript in place, and names, merges and moments the user changes while it
/// runs are kept.
///
/// Threading: call from the UI thread (<see cref="IMainThread"/>), where the store is read
/// and written and <see cref="Progress"/> changes. Reading audio, planning clips, the
/// diarizer and the engine run on the thread pool; every step returns to the UI thread.
/// </summary>
public sealed class MeetingTranscriber : Observable
{
    /// <summary>Share of the progress bar given to speaker separation in a full run.</summary>
    public const double SeparationProgressWeight = 0.2;

    static readonly Logger Logger = Log.For("MeetingTranscriber");

    readonly IMeetingStore store;
    readonly IEngineProvider engines;
    readonly IMainThread mainThread;
    readonly AppSettings? settings;
    readonly IDiarizationProvider? diarization;
    readonly SpeechSegmenter segmenter;
    readonly TimeProvider time;
    readonly Dictionary<Guid, RunState> runs = [];
    IReadOnlyDictionary<Guid, TranscriptionProgress> progress = new Dictionary<Guid, TranscriptionProgress>();

    /// <param name="settings">
    /// Null: runs without speaker separation and keep the audio. With settings, the Meetings
    /// settings choose separation and the number of people, and "Keep meeting audio: delete
    /// after transcription" removes the audio as soon as the transcript is saved.
    /// </param>
    /// <param name="diarization">Null: no speaker separation.</param>
    public MeetingTranscriber(IMeetingStore store, IEngineProvider engines, IMainThread mainThread, AppSettings? settings = null,
                              IDiarizationProvider? diarization = null, SpeechSegmenter? segmenter = null, TimeProvider? time = null)
    {
        this.store = store;
        this.engines = engines;
        this.mainThread = mainThread;
        this.settings = settings;
        this.diarization = diarization;
        this.segmenter = segmenter ?? new SpeechSegmenter();
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Progress of meetings being processed, by meeting id; a new dictionary on every change.</summary>
    public IReadOnlyDictionary<Guid, TranscriptionProgress> Progress => progress;

    public bool IsTranscribing(Guid meetingId) => progress.ContainsKey(meetingId);

    /// <summary>
    /// Audio present, not recording or processing; for <see cref="MeetingProcessingMode.SpeakersOnly"/>
    /// also format 2 or later, at least one segment, a supported system and a ready speaker model.
    /// </summary>
    public bool CanProcess(MeetingRecord meeting, MeetingProcessingMode mode)
    {
        if (!meeting.HasAudio || meeting.Status is MeetingStatus.Recording or MeetingStatus.Transcribing
            || IsTranscribing(meeting.Id) || runs.ContainsKey(meeting.Id))
        {
            return false;
        }
        if (mode != MeetingProcessingMode.SpeakersOnly)
        {
            return true;
        }
        // Segments of the first format are whole merged turns that can hold several voices,
        // so only clip-sized segments can be given to speakers again.
        return meeting.FormatVersion >= 2 && meeting.Segments.Count > 0 && diarization is not null
               && diarization.IsSupportedOnThisSystem && diarization.ModelStatus.IsUsable;
    }

    /// <summary>
    /// Processes a meeting with the Meetings settings, or without speaker separation when the
    /// transcriber has no settings. Throws OperationCanceledException after
    /// <see cref="Cancel"/>; processing can take minutes and only <see cref="Cancel"/> stops it.
    /// </summary>
    public Task<MeetingRecord> TranscribeAsync(Guid meetingId)
    {
        var options = settings is null
            ? new MeetingProcessingOptions(SeparateSpeakers: false)
            : MeetingProcessingOptions.FromSettings(settings);
        return TranscribeAsync(meetingId, options);
    }

    /// <summary>
    /// Runs with explicit options, for "Process again…". On failure or cancellation a meeting
    /// that was already transcribed keeps its previous transcript and the error is rethrown;
    /// any other meeting becomes Failed. Throws SlovoException, EngineUnavailableException,
    /// OperationCanceledException.
    /// </summary>
    public async Task<MeetingRecord> TranscribeAsync(Guid meetingId, MeetingProcessingOptions options)
    {
        if (runs.ContainsKey(meetingId))
        {
            throw MeetingErrors.AlreadyTranscribing();
        }
        var meeting = store.Meeting(meetingId) ?? throw MeetingErrors.NotFound();
        if (meeting.Status == MeetingStatus.Recording)
        {
            throw MeetingErrors.StillRecording();
        }
        if (!meeting.HasAudio)
        {
            throw MeetingErrors.NoAudio();
        }
        if (options.Mode == MeetingProcessingMode.SpeakersOnly && !CanProcess(meeting, MeetingProcessingMode.SpeakersOnly))
        {
            throw MeetingErrors.SpeakersOnlyUnavailable();
        }

        var original = meeting;
        // Transcribing also keeps retention from removing the audio mid-run.
        store.Update(meeting with { Status = MeetingStatus.Transcribing, ErrorMessage = null });
        var run = new RunState(options.Mode);
        runs[meetingId] = run;
        SetProgress(meetingId, new TranscriptionProgress(0, 0, MeetingProcessingPhase.Preparing, 0));

        try
        {
            var result = await RunAsync(meetingId, options, run).ResumeOn(mainThread);
            Finish(meetingId, run);
            return result;
        }
        catch (Exception error)
        {
            Finish(meetingId, run);
            // An engine or diarizer interrupted by cancellation may report it as its own error.
            var cancelled = error is OperationCanceledException || run.Token.IsCancellationRequested;
            RecoverAfterFailure(meetingId, original, options.Mode, cancelled, error);
            if (cancelled && error is not OperationCanceledException)
            {
                throw new OperationCanceledException(run.Token);
            }
            throw;
        }
    }

    /// <summary>Stops a run; its TranscribeAsync throws OperationCanceledException.</summary>
    public void Cancel(Guid meetingId)
    {
        if (runs.TryGetValue(meetingId, out var run))
        {
            run.Cancel();
        }
    }

    // MARK: - Run state

    sealed class RunState
    {
        readonly CancellationTokenSource cancellation = new();

        public RunState(MeetingProcessingMode mode)
        {
            Mode = mode;
            Token = cancellation.Token;
        }

        public MeetingProcessingMode Mode { get; }
        /// <summary>Taken once, so it can still be read after <see cref="Dispose"/>.</summary>
        public CancellationToken Token { get; }
        /// <summary>Tracks handed to the diarizer, in order.</summary>
        public IReadOnlyList<MeetingTrack> SeparationTracks { get; set; } = [];
        public Dictionary<MeetingTrack, double> SeparationFractions { get; } = [];

        /// <summary>The mean over the tracks being separated.</summary>
        public double SeparationFraction => SeparationTracks.Count == 0
            ? 0
            : SeparationTracks.Sum(t => Math.Min(1, Math.Max(0, SeparationFractions.GetValueOrDefault(t)))) / SeparationTracks.Count;

        public void Cancel() => cancellation.Cancel();

        public void Dispose() => cancellation.Dispose();
    }

    sealed record AudioTrack(MeetingTrack Track, string Path);

    sealed record TrackAnalysis(MeetingTrack Track, string Path, EnergyEnvelope Envelope, IReadOnlyList<SpeechSegment> Speech)
    {
        public double Duration => (double)Envelope.SampleCount / Envelope.SampleRate;

        public double SpeechSeconds => Speech.Sum(s => s.End - s.Start);
    }

    sealed class Separation(MeetingDiarization record)
    {
        public MeetingDiarization Record { get; set; } = record;
        /// <summary>Normalized turns of each track the diarizer processed.</summary>
        public Dictionary<MeetingTrack, IReadOnlyList<SpeakerTurn>> Turns { get; } = [];
        public Exception? Failure { get; set; }

        /// <summary>Tracks that came back with at least one turn.</summary>
        public HashSet<MeetingTrack> SeparatedTracks => Turns.Where(p => p.Value.Count > 0).Select(p => p.Key).ToHashSet();
    }

    readonly record struct TrackClip(double Start, double End, SampleRange Samples, int? Cluster);

    sealed record ClipPlan(MeetingTrack Track, string Path, IReadOnlyList<TrackClip> Clips);

    readonly record struct ProducedSegment(MeetingTrack Track, double Start, double End, string Text, int? Cluster);

    // MARK: - Pipeline

    async Task<MeetingRecord> RunAsync(Guid meetingId, MeetingProcessingOptions options, RunState run)
    {
        var token = run.Token;
        var tracks = AudioTracks(meetingId);
        if (tracks.Count == 0)
        {
            throw MeetingErrors.NoAudio();
        }
        var hasSystemTrack = tracks.Any(t => t.Track == MeetingTrack.System);

        // One pass over each file gives both the speech ranges and the envelope that long
        // turns are split on.
        var speechSegmenter = segmenter;
        var analyses = await Task.Run(() => tracks.Select(track =>
        {
            token.ThrowIfCancellationRequested();
            var envelope = SpeechSegmenter.Envelope(track.Path);
            return new TrackAnalysis(track.Track, track.Path, envelope, speechSegmenter.Segments(envelope));
        }).ToList(), token).ResumeOn(mainThread);
        token.ThrowIfCancellationRequested();

        var separation = await SeparateSpeakersAsync(meetingId, run, analyses, hasSystemTrack, options).ResumeOn(mainThread);
        token.ThrowIfCancellationRequested();

        return options.Mode == MeetingProcessingMode.SpeakersOnly
            ? FinishSpeakersOnly(meetingId, run, separation, hasSystemTrack)
            : await TranscribeClipsAsync(meetingId, run, analyses, separation, hasSystemTrack, options).ResumeOn(mainThread);
    }

    /// <summary>
    /// Diarizes each track with enough speech, microphone first. Any failure other than
    /// cancellation is recorded and ends separation; the tracks already separated keep their
    /// turns and the rest keep fallback speakers, so separation never fails a full run. The
    /// loaded model is released before the speech engine is requested.
    /// </summary>
    async Task<Separation> SeparateSpeakersAsync(Guid meetingId, RunState run, IReadOnlyList<TrackAnalysis> analyses, bool hasSystemTrack,
                                                 MeetingProcessingOptions options)
    {
        var token = run.Token;
        var requested = RequestedSpeakers(options.ExpectedSpeakers);
        // At the precision meeting.json keeps, so the record in memory equals the one read back.
        var processedAt = AppleDate.FromReferenceSeconds(AppleDate.ToReferenceSeconds(time.GetUtcNow()));
        if (!(options.Mode == MeetingProcessingMode.SpeakersOnly || options.SeparateSpeakers) || diarization is null)
        {
            return new Separation(new MeetingDiarization { State = DiarizationState.Off, RequestedSpeakers = requested, ProcessedAt = processedAt });
        }
        if (!diarization.IsSupportedOnThisSystem)
        {
            return new Separation(new MeetingDiarization
            {
                State = DiarizationState.UnsupportedSystem,
                RequestedSpeakers = requested,
                Message = CoreStrings.Get("meetings.diarization.unsupportedSystem"),
                ProcessedAt = processedAt,
            });
        }
        if (!diarization.ModelStatus.IsUsable)
        {
            return new Separation(new MeetingDiarization { State = DiarizationState.ModelMissing, RequestedSpeakers = requested, ProcessedAt = processedAt });
        }

        var separation = new Separation(new MeetingDiarization
        {
            State = DiarizationState.Applied,
            RequestedSpeakers = requested,
            ModelName = diarization.ModelName,
            Configuration = diarization.Configuration,
            ProcessedAt = processedAt,
        });
        // Too little speech gives the clustering nothing to compare; such a track keeps its
        // fallback speaker.
        var candidates = analyses.Where(a => a.SpeechSeconds >= DiarizationTuning.MinimumTrackSpeechSeconds).ToList();
        if (candidates.Count == 0)
        {
            return separation;
        }
        run.SeparationTracks = candidates.Select(a => a.Track).ToList();

        PublishSeparation(meetingId, run, MeetingProcessingPhase.LoadingSpeakerModel);
        try
        {
            var diarizer = await diarization.DiarizerAsync(token).ResumeOn(mainThread);
            foreach (var analysis in candidates)
            {
                token.ThrowIfCancellationRequested();
                var track = analysis.Track;
                PublishSeparation(meetingId, run, MeetingProcessingPhase.SeparatingSpeakers(track, 0));
                var hint = Hint(track, requested, hasSystemTrack);
                var expected = analysis.Envelope.SampleCount;
                var samples = await Task.Run(() => MeetingClips.ReadAll(analysis.Path, expected, token), token).ResumeOn(mainThread);
                var reporter = new MainThreadProgress(mainThread, fraction => ReceiveSeparationProgress(meetingId, run, track, fraction));
                // Started on the thread pool, so a diarizer that does work before its first
                // await never holds the UI thread.
                var raw = await Task.Run(() => diarizer.DiarizeAsync(samples, hint, reporter, token), token).ResumeOn(mainThread);
                token.ThrowIfCancellationRequested();
                separation.Turns[track] = SpeakerTurns.Normalize(raw, analysis.Duration, hint);
                run.SeparationFractions[track] = 1;
                PublishSeparation(meetingId, run, MeetingProcessingPhase.SeparatingSpeakers(track, 1));
            }
        }
        catch (Exception error) when (error is OperationCanceledException || token.IsCancellationRequested)
        {
            // A diarizer interrupted by cancellation may report it as its own error.
            throw new OperationCanceledException(token);
        }
        catch (Exception error)
        {
            separation.Failure = error;
            separation.Record = separation.Record with
            {
                State = DiarizationState.Failed,
                Message = CoreStrings.Format("meetings.diarization.failed", error.Message),
            };
            Logger.Error("Speaker separation failed", error);
        }
        finally
        {
            diarization.Release();
        }
        return separation;
    }

    async Task<MeetingRecord> TranscribeClipsAsync(Guid meetingId, RunState run, IReadOnlyList<TrackAnalysis> analyses, Separation separation,
                                                   bool hasSystemTrack, MeetingProcessingOptions options)
    {
        var token = run.Token;
        var speechSegmenter = segmenter;
        var turns = separation.Turns;
        var plans = await Task.Run(() => analyses.Select(analysis =>
        {
            IReadOnlyList<TrackClip> clips;
            if (turns.TryGetValue(analysis.Track, out var trackTurns) && trackTurns.Count > 0)
            {
                clips = ClipPlanner.Plan(trackTurns, analysis.Speech.Select(s => new TimeRange(s.Start, s.End)).ToList(), analysis.Duration,
                                         analysis.Envelope, speechSegmenter, speechSegmenter.MaxSeconds)
                    .Select(c => new TrackClip(c.Range.Lower, c.Range.Upper, MeetingClips.SampleRange(c.Range), c.Cluster))
                    .ToList();
            }
            else
            {
                clips = analysis.Speech.Select(s => new TrackClip(s.Start, s.End, s.Samples, null)).ToList();
            }
            return new ClipPlan(analysis.Track, analysis.Path, clips);
        }).ToList(), token).ResumeOn(mainThread);
        token.ThrowIfCancellationRequested();

        var engineId = options.EngineId ?? engines.EngineFor(EnginePurpose.Meetings);
        var engine = await engines.EngineAsync(engineId, token).ResumeOn(mainThread);
        token.ThrowIfCancellationRequested();

        var total = plans.Sum(p => p.Clips.Count);
        PublishClips(meetingId, run, 0, total);

        var produced = new List<ProducedSegment>();
        var completed = 0;
        foreach (var plan in plans)
        {
            var reader = await Task.Run(() => MeetingTrackReader.Open(plan.Path), token).ResumeOn(mainThread);
            try
            {
                foreach (var clip in plan.Clips)
                {
                    token.ThrowIfCancellationRequested();
                    if (clip.Samples.Count > 0)
                    {
                        var samples = await Task.Run(() => reader.Samples(clip.Samples.Lower, clip.Samples.Upper), token).ResumeOn(mainThread);
                        var text = await Task.Run(() => engine.TranscribeAsync(samples, token), token).ResumeOn(mainThread);
                        engines.MarkUsed(engineId);
                        token.ThrowIfCancellationRequested();

                        var cleaned = TranscriptMerger.NormalizedText(text);
                        if (cleaned.Length > 0)
                        {
                            produced.Add(new ProducedSegment(plan.Track, clip.Start, clip.End, cleaned, clip.Cluster));
                        }
                    }
                    completed++;
                    PublishClips(meetingId, run, completed, total);
                }
            }
            finally
            {
                reader.Dispose();
            }
        }

        // The record is read again, so a title change, a rename or a moment marked while the
        // run went on survives, and speakers are matched against the names as they are now.
        var meeting = store.Meeting(meetingId) ?? throw MeetingErrors.NotFound();
        var separated = separation.SeparatedTracks;
        var clusters = new Dictionary<MeetingTrack, Dictionary<int, List<TimeRange>>>();
        foreach (var segment in produced)
        {
            if (segment.Cluster is not { } cluster || !separated.Contains(segment.Track) || !(segment.End > segment.Start))
            {
                continue;
            }
            AddRange(clusters, segment.Track, cluster, new TimeRange(segment.Start, segment.End));
        }
        // Speakers are built from clips that produced text, so a voice that said nothing
        // recognizable does not become an empty speaker.
        var registry = SpeakerRegistry.Build(
            ClustersOf(clusters, separated, MeetingTrack.Microphone),
            ClustersOf(clusters, separated, MeetingTrack.System),
            hasSystemTrack, meeting);

        // A track this run did not separate keeps the voices an earlier run found on it, with
        // the names the user gave them, each new segment going to the voice whose earlier
        // lines it overlaps.
        var carriedIds = new Dictionary<int, string?>();
        var carriedSpeakers = new List<MeetingSpeaker>();
        foreach (var track in MeetingTracks.All.Where(t => !separated.Contains(t)))
        {
            var offsets = Enumerable.Range(0, produced.Count).Where(i => produced[i].Track == track).ToList();
            if (offsets.Count == 0)
            {
                continue;
            }
            var newSegments = offsets.Select(i => new TranscriptSegment
            {
                Start = produced[i].Start,
                End = produced[i].End,
                Speaker = track.ToSpeaker(),
                Text = produced[i].Text,
            }).ToList();
            if (SpeakerRegistry.CarryOver(track, newSegments, meeting) is not { } carried)
            {
                continue;
            }
            for (var index = 0; index < offsets.Count && index < carried.SpeakerIds.Count; index++)
            {
                carriedIds[offsets[index]] = carried.SpeakerIds[index];
            }
            carriedSpeakers.AddRange(carried.Speakers);
        }

        var segments = produced
            .Select((segment, offset) => (segment, offset))
            .OrderBy(p => p.segment.Start)
            .ThenBy(p => p.segment.Track == MeetingTrack.Microphone ? 0 : 1)
            .ThenBy(p => p.offset)
            .Select(p =>
            {
                string? speakerId;
                if (carriedIds.TryGetValue(p.offset, out var carried))
                {
                    speakerId = carried;
                }
                else
                {
                    speakerId = p.segment.Cluster is { } cluster && registry.IdForCluster.TryGetValue(p.segment.Track, out var ids)
                                && ids.TryGetValue(cluster, out var id)
                        ? id
                        : null;
                }
                return new TranscriptSegment
                {
                    Start = p.segment.Start,
                    End = p.segment.End,
                    Speaker = p.segment.Track.ToSpeaker(),
                    Text = p.segment.Text,
                    SpeakerId = speakerId,
                };
            })
            .ToList();

        var combined = registry.Speakers.ToList();
        foreach (var speaker in carriedSpeakers)
        {
            if (!combined.Any(s => s.Id == speaker.Id))
            {
                combined.Add(speaker);
            }
        }
        // The registry may pick «Я» automatically on a separated track while a carried speaker
        // holds the user's own choice; that choice wins.
        var speakers = KeepChosenMe(MeetingTracks.All.SelectMany(track => combined.Where(s => s.Track == track)).ToList(), meeting);

        var diarizationRecord = separation.Record with { SpeakersPerTrack = SpeakerCounts(registry.Speakers, separated) };
        meeting = meeting with
        {
            HighestSpeakerNumber = SpeakerRegistry.HighestNumber(meeting, speakers),
            Speakers = speakers,
            Segments = segments,
            Diarization = diarizationRecord,
            EngineId = engineId,
            FormatVersion = MeetingRecord.CurrentFormatVersion,
            Status = MeetingStatus.Transcribed,
            ErrorMessage = null,
        };
        store.Update(meeting);

        // The audio goes only once meeting.json and transcript.md are on disk, so a failed
        // save never leaves a meeting with neither a transcript nor its recording. A screen
        // recording stays: the transcript replaces the audio, not the picture.
        if (Save("transcribed") && settings?.KeepMeetingAudio == RetentionPeriod.AfterTranscription)
        {
            store.RemoveAudioTracks(meetingId);
            Save("audio removed");
        }
        Logger.Info($"Transcribed a meeting: {total} clips, {segments.Count} segments, {speakers.Count} speakers");
        return store.Meeting(meetingId) ?? meeting;
    }

    /// <summary>
    /// Gives the existing segments to the voices of a new separation without touching text.
    /// Here separation is the whole job, so its failure fails the run.
    ///
    /// A track that gives no turns this time, because it has too little speech or the
    /// diarizer found none, keeps its speakers and line assignments as they were: this mode
    /// exists to keep the user's work, and nothing new is known about that track.
    /// </summary>
    MeetingRecord FinishSpeakersOnly(Guid meetingId, RunState run, Separation separation, bool hasSystemTrack)
    {
        if (separation.Failure is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
        var meeting = store.Meeting(meetingId) ?? throw MeetingErrors.NotFound();

        var separated = separation.SeparatedTracks;
        var clusters = new Dictionary<MeetingTrack, Dictionary<int, List<TimeRange>>>();
        var clusterOfSegment = new Dictionary<int, int>();
        foreach (var track in MeetingTracks.All.Where(separated.Contains))
        {
            var indices = Enumerable.Range(0, meeting.Segments.Count)
                .Where(i => MeetingTracks.FromSpeaker(meeting.Segments[i].Speaker) == track)
                .ToList();
            var assigned = ClipPlanner.Assign(indices.Select(i => meeting.Segments[i]).ToList(), separation.Turns.GetValueOrDefault(track) ?? []);
            for (var position = 0; position < indices.Count && position < assigned.Count; position++)
            {
                var index = indices[position];
                var segment = meeting.Segments[index];
                if (assigned[position] is not { } cluster || !double.IsFinite(segment.Start) || !double.IsFinite(segment.End))
                {
                    continue;
                }
                clusterOfSegment[index] = cluster;
                AddRange(clusters, track, cluster, new TimeRange(segment.Start, Math.Max(segment.Start, segment.End)));
            }
        }

        var registry = SpeakerRegistry.Build(
            ClustersOf(clusters, separated, MeetingTrack.Microphone),
            ClustersOf(clusters, separated, MeetingTrack.System),
            hasSystemTrack, meeting);
        var segments = meeting.Segments.Select((segment, index) =>
        {
            var track = MeetingTracks.FromSpeaker(segment.Speaker);
            if (!separated.Contains(track))
            {
                return segment;
            }
            var speakerId = clusterOfSegment.TryGetValue(index, out var cluster)
                            && registry.IdForCluster.TryGetValue(track, out var ids) && ids.TryGetValue(cluster, out var id)
                ? id
                : null;
            return segment with { SpeakerId = speakerId };
        }).ToList();

        // The registry sees only separated tracks, so it may pick «Я» automatically while a
        // kept track holds the user's own choice; that choice wins.
        var speakers = KeepChosenMe(MeetingTracks.All
            .SelectMany(track => (separated.Contains(track) ? registry.Speakers : meeting.Speakers).Where(s => s.Track == track))
            .ToList(), meeting);
        var perTrack = SpeakerCounts(registry.Speakers, separated);
        foreach (var track in MeetingTracks.All.Where(t => !separated.Contains(t)))
        {
            var key = EnumText.Raw(track);
            if (meeting.Diarization?.SpeakersPerTrack.TryGetValue(key, out var kept) == true)
            {
                perTrack[key] = kept;
            }
        }
        meeting = meeting with
        {
            HighestSpeakerNumber = SpeakerRegistry.HighestNumber(meeting, speakers),
            Speakers = speakers,
            Segments = segments,
            Diarization = separation.Record with { SpeakersPerTrack = perTrack },
            Status = MeetingStatus.Transcribed,
            ErrorMessage = null,
        };
        store.Update(meeting);
        Save("speakers recalculated");

        var lastTrack = run.SeparationTracks.Count > 0 ? run.SeparationTracks[^1] : MeetingTrack.Microphone;
        SetProgress(meetingId, new TranscriptionProgress(0, 0, MeetingProcessingPhase.SeparatingSpeakers(lastTrack, 1), 1));
        Logger.Info($"Recalculated speakers: {speakers.Count} speakers");
        return store.Meeting(meetingId) ?? meeting;
    }

    // MARK: - Progress

    void PublishSeparation(Guid meetingId, RunState run, MeetingProcessingPhase phase)
    {
        if (!IsCurrent(meetingId, run))
        {
            return;
        }
        var fraction = run.SeparationFraction;
        var overall = run.Mode == MeetingProcessingMode.SpeakersOnly ? fraction : SeparationProgressWeight * fraction;
        SetProgress(meetingId, new TranscriptionProgress(0, 0, phase, overall));
    }

    void PublishClips(Guid meetingId, RunState run, int completed, int total)
    {
        if (!IsCurrent(meetingId, run))
        {
            return;
        }
        var clips = total > 0 ? (double)completed / total : 1;
        var overall = run.SeparationTracks.Count == 0 ? clips : SeparationProgressWeight + (1 - SeparationProgressWeight) * clips;
        SetProgress(meetingId, new TranscriptionProgress(completed, total, MeetingProcessingPhase.Transcribing, overall));
    }

    /// <summary>
    /// Diarizer progress arrives on any thread and can trail the run; it only moves the track
    /// that is still being separated in the same run forward.
    /// </summary>
    void ReceiveSeparationProgress(Guid meetingId, RunState run, MeetingTrack track, double fraction)
    {
        if (!IsCurrent(meetingId, run) || !double.IsFinite(fraction)
            || !progress.TryGetValue(meetingId, out var current)
            || current.Phase.Kind != MeetingProcessingPhaseKind.SeparatingSpeakers || current.Phase.Track != track)
        {
            return;
        }
        var value = Math.Min(1, Math.Max(run.SeparationFractions.GetValueOrDefault(track), fraction));
        run.SeparationFractions[track] = value;
        PublishSeparation(meetingId, run, MeetingProcessingPhase.SeparatingSpeakers(track, value));
    }

    bool IsCurrent(Guid meetingId, RunState run) => runs.TryGetValue(meetingId, out var current) && ReferenceEquals(current, run);

    void SetProgress(Guid meetingId, TranscriptionProgress? value)
    {
        var next = new Dictionary<Guid, TranscriptionProgress>(progress);
        if (value is null)
        {
            if (!next.Remove(meetingId))
            {
                return;
            }
        }
        else
        {
            if (progress.TryGetValue(meetingId, out var existing) && existing.Equals(value))
            {
                return;
            }
            next[meetingId] = value;
        }
        progress = next;
        OnPropertyChanged(nameof(Progress));
    }

    // MARK: - Helpers

    /// <summary>
    /// The speaker count hint for one track: an exact number in person, where the microphone
    /// hears everyone, and upper bounds in calls, where the people are split between the
    /// microphone and system audio.
    /// </summary>
    public static SpeakerCountHint Hint(MeetingTrack track, int? expectedSpeakers, bool hasSystemTrack)
    {
        if (expectedSpeakers is not { } expected)
        {
            return SpeakerCountHint.Automatic;
        }
        if (!hasSystemTrack)
        {
            return track == MeetingTrack.Microphone ? new SpeakerCountHint(Exactly: expected) : SpeakerCountHint.Automatic;
        }
        return track == MeetingTrack.Microphone
            ? new SpeakerCountHint(Maximum: expected)
            : new SpeakerCountHint(Maximum: Math.Max(1, expected - 1));
    }

    /// <summary>2–8, or null for automatic.</summary>
    public static int? RequestedSpeakers(int? value)
    {
        if (value is not { } number)
        {
            return null;
        }
        var normalized = AppSettings.NormalizedExpectedSpeakers(number);
        return normalized == 0 ? null : normalized;
    }

    static Dictionary<string, int> SpeakerCounts(IReadOnlyList<MeetingSpeaker> speakers, HashSet<MeetingTrack> separated)
    {
        var counts = new Dictionary<string, int>();
        foreach (var track in separated)
        {
            counts[EnumText.Raw(track)] = speakers.Count(s => s.Track == track && s.Number is not null);
        }
        return counts;
    }

    /// <summary>When more than one speaker ends up as «Я», the one the user chose before keeps it.</summary>
    static IReadOnlyList<MeetingSpeaker> KeepChosenMe(List<MeetingSpeaker> speakers, MeetingRecord meeting)
    {
        if (speakers.Count(s => s.IsMe) > 1 && meeting.Speakers.FirstOrDefault(s => s.IsMe)?.Id is { } chosen)
        {
            return speakers.Select(s => s with { IsMe = s.Id == chosen }).ToList();
        }
        return speakers;
    }

    static void AddRange(Dictionary<MeetingTrack, Dictionary<int, List<TimeRange>>> clusters, MeetingTrack track, int cluster, TimeRange range)
    {
        if (!clusters.TryGetValue(track, out var ofTrack))
        {
            clusters[track] = ofTrack = [];
        }
        if (!ofTrack.TryGetValue(cluster, out var ranges))
        {
            ofTrack[cluster] = ranges = [];
        }
        ranges.Add(range);
    }

    /// <summary>The clusters of a separated track (empty when none produced text); null for a track that was not separated.</summary>
    static IReadOnlyDictionary<int, IReadOnlyList<TimeRange>>? ClustersOf(Dictionary<MeetingTrack, Dictionary<int, List<TimeRange>>> clusters,
                                                                         HashSet<MeetingTrack> separated, MeetingTrack track)
    {
        if (!separated.Contains(track))
        {
            return null;
        }
        return clusters.TryGetValue(track, out var ofTrack)
            ? ofTrack.ToDictionary(p => p.Key, p => (IReadOnlyList<TimeRange>)p.Value)
            : new Dictionary<int, IReadOnlyList<TimeRange>>();
    }

    List<AudioTrack> AudioTracks(Guid meetingId)
    {
        var tracks = new List<AudioTrack>();
        var microphone = store.MicrophoneAudioPath(meetingId);
        if (File.Exists(microphone))
        {
            tracks.Add(new AudioTrack(MeetingTrack.Microphone, microphone));
        }
        var system = store.SystemAudioPath(meetingId);
        if (File.Exists(system))
        {
            tracks.Add(new AudioTrack(MeetingTrack.System, system));
        }
        return tracks;
    }

    void Finish(Guid meetingId, RunState run)
    {
        if (IsCurrent(meetingId, run))
        {
            runs.Remove(meetingId);
        }
        run.Dispose();
        SetProgress(meetingId, null);
    }

    /// <summary>
    /// A meeting that already had a transcript, and any speakers-only run, gets its previous
    /// status back: the run never replaced segments, speakers or the separation result in the
    /// store, so they are still the previous ones, together with anything the user renamed,
    /// merged or marked meanwhile. A first transcription is marked Failed.
    /// </summary>
    void RecoverAfterFailure(Guid meetingId, MeetingRecord original, MeetingProcessingMode mode, bool cancelled, Exception error)
    {
        if (store.Meeting(meetingId) is not { } meeting)
        {
            return;
        }
        if (mode == MeetingProcessingMode.SpeakersOnly || original.Status == MeetingStatus.Transcribed)
        {
            meeting = meeting with { Status = original.Status, ErrorMessage = original.ErrorMessage };
        }
        else
        {
            meeting = meeting with
            {
                Status = MeetingStatus.Failed,
                ErrorMessage = cancelled ? MeetingErrors.CancelledMessage() : error.Message,
            };
        }
        store.Update(meeting);
        Save("after a failed run");
        if (!cancelled)
        {
            Logger.Error("Meeting processing failed", error);
        }
    }

    bool Save(string context)
    {
        try
        {
            store.Flush();
            return true;
        }
        catch (SlovoException error)
        {
            Logger.Error($"Saving the meeting failed ({context})", error);
            return false;
        }
    }

    /// <summary>Hands diarizer progress, reported on any thread, to the UI thread.</summary>
    sealed class MainThreadProgress(IMainThread mainThread, Action<double> report) : IProgress<double>
    {
        public void Report(double value) => mainThread.Post(() => report(value));
    }
}
