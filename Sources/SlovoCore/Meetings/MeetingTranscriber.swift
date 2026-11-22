import Combine
import Foundation
import os

/// How one processing run of a meeting should go.
public struct MeetingProcessingOptions: Equatable, Sendable {
    public enum Mode: Equatable, Sendable {
        /// Speaker separation, then speech recognition of every clip.
        case full
        /// Speaker separation over the existing text; no speech engine is loaded.
        case speakersOnly
    }

    public var mode: Mode
    public var separateSpeakers: Bool
    /// nil or 0 = automatic; otherwise 2–8.
    public var expectedSpeakers: Int?
    /// nil = the meetings engine from settings.
    public var engineID: EngineID?

    public init(mode: Mode = .full, separateSpeakers: Bool = true, expectedSpeakers: Int? = nil,
                engineID: EngineID? = nil) {
        self.mode = mode
        self.separateSpeakers = separateSpeakers
        self.expectedSpeakers = expectedSpeakers
        self.engineID = engineID
    }

    /// A full run with the Meetings settings: separation on or off and the expected number
    /// of people.
    public static func fromSettings(_ settings: AppSettings) -> MeetingProcessingOptions {
        let expected = settings.expectedSpeakers
        return MeetingProcessingOptions(mode: .full, separateSpeakers: settings.speakerSeparation,
                                        expectedSpeakers: expected == 0 ? nil : expected, engineID: nil)
    }
}

/// Processes recorded meetings at utility priority: separates speakers on each track,
/// then transcribes one clip per speaker turn, with progress and cancellation.
///
/// Separation comes first because speech engines return only text: deciding the speaker
/// of each clip before recognition works with every engine. Tracks are read from disk
/// block by block; only the clip being transcribed is in memory, written to a short
/// temporary WAV for the engine.
///
/// A run changes the stored transcript only when it succeeds. Until then the store keeps
/// the previous segments, speakers and separation result, so a failed or cancelled
/// re-run leaves the previous transcript in place, and names, merges and moments the user
/// changes while it runs are kept.
@MainActor
public final class MeetingTranscriber: ObservableObject {
    /// Progress of meetings being processed, by meeting id.
    @Published public private(set) var progress: [UUID: TranscriptionProgress] = [:]

    /// Share of the progress bar given to speaker separation in a full run.
    static let separationProgressWeight = 0.2

    private let store: MeetingStoring
    private let engines: EngineProviding
    private let segmenter: SpeechSegmenter
    private let settings: AppSettings?
    private let diarization: DiarizationProviding?
    private var tasks: [UUID: Task<MeetingRecord, Error>] = [:]
    private var runs: [UUID: RunState] = [:]
    private let logger = SlovoLog.logger("MeetingTranscriber")

    /// Without settings or a diarization provider: no speaker separation.
    public init(store: MeetingStoring, engines: EngineProviding, segmenter: SpeechSegmenter = SpeechSegmenter()) {
        self.store = store
        self.engines = engines
        self.segmenter = segmenter
        settings = nil
        diarization = nil
    }

    /// With settings, "Keep meeting audio: delete after transcription" removes the audio
    /// as soon as the transcript is saved instead of at the next retention pass.
    public init(store: MeetingStoring, engines: EngineProviding, settings: AppSettings,
                segmenter: SpeechSegmenter = SpeechSegmenter()) {
        self.store = store
        self.engines = engines
        self.segmenter = segmenter
        self.settings = settings
        diarization = nil
    }

    /// With a diarization provider, runs separate speakers when the settings ask for it and
    /// the speaker model is ready.
    public init(store: MeetingStoring, engines: EngineProviding, settings: AppSettings,
                diarization: DiarizationProviding?, segmenter: SpeechSegmenter = SpeechSegmenter()) {
        self.store = store
        self.engines = engines
        self.segmenter = segmenter
        self.settings = settings
        self.diarization = diarization
    }

    public func isTranscribing(_ meetingID: UUID) -> Bool {
        progress[meetingID] != nil
    }

    /// Audio present, not recording or processing; for speakersOnly also formatVersion ≥ 2,
    /// at least one segment, a supported system and a ready model.
    public func canProcess(_ meeting: MeetingRecord, mode: MeetingProcessingOptions.Mode) -> Bool {
        guard meeting.hasAudio, meeting.status != .recording, meeting.status != .transcribing,
              !isTranscribing(meeting.id), tasks[meeting.id] == nil else {
            return false
        }
        guard mode == .speakersOnly else { return true }
        // Segments of the first format are whole merged turns that can hold several
        // voices, so only clip-sized segments can be given to speakers again.
        guard meeting.formatVersion >= 2, !meeting.segments.isEmpty, let diarization,
              diarization.isSupportedOnThisSystem, case .ready = diarization.modelStatus else {
            return false
        }
        return true
    }

    /// Processes a meeting with the Meetings settings, or without speaker separation when
    /// the transcriber has no settings.
    ///
    /// Throws `CancellationError` after `cancel(meetingID:)`. Cancelling the calling task
    /// does not stop processing, which can take minutes; use `cancel(meetingID:)`.
    public func transcribe(meetingID: UUID) async throws -> MeetingRecord {
        let options = settings.map { MeetingProcessingOptions.fromSettings($0) }
            ?? MeetingProcessingOptions(separateSpeakers: false)
        return try await transcribe(meetingID: meetingID, options: options)
    }

    /// Runs with explicit options, for "Process again…".
    ///
    /// On failure or cancellation a meeting that was already transcribed keeps its
    /// previous transcript and the error is rethrown; any other meeting becomes `.failed`.
    public func transcribe(meetingID: UUID, options: MeetingProcessingOptions) async throws -> MeetingRecord {
        guard tasks[meetingID] == nil else { throw MeetingErrors.alreadyTranscribing() }
        guard var meeting = store.meetings.first(where: { $0.id == meetingID }) else { throw MeetingErrors.notFound() }
        guard meeting.status != .recording else { throw MeetingErrors.stillRecording() }
        guard meeting.hasAudio else { throw MeetingErrors.noAudio() }
        if options.mode == .speakersOnly, !canProcess(meeting, mode: .speakersOnly) {
            throw MeetingErrors.speakersOnlyUnavailable()
        }

        let original = meeting
        // `.transcribing` also keeps retention from removing the audio mid-run.
        // Keep any previous empty-result error in case this retry is interrupted.
        meeting.status = .transcribing
        store.update(meeting)
        let run = RunState(mode: options.mode)
        runs[meetingID] = run
        progress[meetingID] = TranscriptionProgress(completedSegments: 0, totalSegments: 0, phase: .preparing,
                                                    overallFraction: 0)

        let task = Task(priority: .utility) { () async throws -> MeetingRecord in
            try await self.run(meetingID: meetingID, options: options, token: run.token)
        }
        tasks[meetingID] = task

        do {
            let result = try await task.value
            finish(meetingID)
            return result
        } catch {
            finish(meetingID)
            recoverAfterFailure(meetingID, original: original, mode: options.mode, error: error)
            throw error
        }
    }

    public func cancel(meetingID: UUID) {
        tasks[meetingID]?.cancel()
    }

    // MARK: - Run state

    private struct RunState {
        let token = UUID()
        let mode: MeetingProcessingOptions.Mode
        /// Tracks handed to the diarizer, in order.
        var separationTracks: [MeetingTrack] = []
        var separationFractions: [MeetingTrack: Double] = [:]

        /// The mean over the tracks being separated.
        var separationFraction: Double {
            guard !separationTracks.isEmpty else { return 0 }
            let sum = separationTracks.reduce(0) { $0 + min(1, max(0, separationFractions[$1] ?? 0)) }
            return sum / Double(separationTracks.count)
        }
    }

    private struct AudioTrack: Sendable {
        let track: MeetingTrack
        let url: URL
    }

    private struct TrackAnalysis: Sendable {
        let track: MeetingTrack
        let url: URL
        let envelope: EnergyEnvelope
        let speech: [SpeechSegment]

        var duration: TimeInterval {
            Double(envelope.sampleCount) / Double(envelope.sampleRate)
        }

        var speechSeconds: TimeInterval {
            speech.reduce(0) { $0 + ($1.end - $1.start) }
        }
    }

    private struct Separation {
        var record: MeetingDiarization
        /// Normalized turns of each track the diarizer processed.
        var turns: [MeetingTrack: [SpeakerTurn]] = [:]
        var failure: Error?

        /// Tracks that came back with at least one turn.
        var separatedTracks: Set<MeetingTrack> {
            Set(turns.filter { !$0.value.isEmpty }.keys)
        }
    }

    private struct TrackClip: Sendable {
        let start: TimeInterval
        let end: TimeInterval
        let samples: Range<Int>
        let cluster: Int?
    }

    private struct ClipPlan: Sendable {
        let track: MeetingTrack
        let url: URL
        let clips: [TrackClip]
    }

    private struct ProducedSegment {
        let track: MeetingTrack
        let start: TimeInterval
        let end: TimeInterval
        let text: String
        let cluster: Int?
    }

    // MARK: - Pipeline

    private func run(meetingID: UUID, options: MeetingProcessingOptions, token: UUID) async throws -> MeetingRecord {
        let tracks = audioTracks(for: meetingID)
        guard !tracks.isEmpty else { throw MeetingErrors.noAudio() }
        let hasSystemTrack = tracks.contains { $0.track == .system }

        // One pass over each file gives both the speech ranges and the envelope that long
        // turns are split on.
        let segmenter = self.segmenter
        let analyses = try await Self.utility {
            try tracks.map { track -> TrackAnalysis in
                let envelope = try segmenter.envelope(inFileAt: track.url)
                return TrackAnalysis(track: track.track, url: track.url, envelope: envelope,
                                     speech: segmenter.segments(for: envelope))
            }
        }
        try Task.checkCancellation()

        let separation = try await separateSpeakers(meetingID: meetingID, token: token, analyses: analyses,
                                                    hasSystemTrack: hasSystemTrack, options: options)
        try Task.checkCancellation()

        switch options.mode {
        case .speakersOnly:
            return try finishSpeakersOnly(meetingID: meetingID, separation: separation, hasSystemTrack: hasSystemTrack)
        case .full:
            return try await transcribeClips(meetingID: meetingID, analyses: analyses, separation: separation,
                                             hasSystemTrack: hasSystemTrack, options: options)
        }
    }

    /// Diarizes each track with enough speech, microphone first. Any failure other than
    /// cancellation is recorded and ends separation; the tracks already separated keep
    /// their turns and the rest keep fallback speakers, so separation never fails a full
    /// run. The loaded model is released before the speech engine is requested.
    private func separateSpeakers(meetingID: UUID, token: UUID, analyses: [TrackAnalysis], hasSystemTrack: Bool,
                                  options: MeetingProcessingOptions) async throws -> Separation {
        let requested = Self.requestedSpeakers(options.expectedSpeakers)
        let processedAt = Date()
        guard options.mode == .speakersOnly || options.separateSpeakers, let diarization else {
            return Separation(record: MeetingDiarization(state: .off, requestedSpeakers: requested,
                                                         processedAt: processedAt))
        }
        guard diarization.isSupportedOnThisSystem else {
            let message = CoreStrings.string("meetings.diarization.unsupportedSystem", table: .meetings)
            return Separation(record: MeetingDiarization(state: .unsupportedSystem, requestedSpeakers: requested,
                                                         message: message, processedAt: processedAt))
        }
        guard case .ready = diarization.modelStatus else {
            return Separation(record: MeetingDiarization(state: .modelMissing, requestedSpeakers: requested,
                                                         processedAt: processedAt))
        }

        var separation = Separation(record: MeetingDiarization(
            state: .applied, requestedSpeakers: requested, model: DiarizationService.modelIdentifier,
            configuration: DiarizationTuning.standard.description, processedAt: processedAt))
        // Too little speech gives the clustering nothing to compare; such a track keeps its
        // fallback speaker.
        let candidates = analyses.filter { $0.speechSeconds >= DiarizationTuning.minimumTrackSpeechSeconds }
        guard !candidates.isEmpty else { return separation }
        runs[meetingID]?.separationTracks = candidates.map(\.track)

        publishSeparation(meetingID, phase: .loadingSpeakerModel)
        defer { diarization.release() }
        do {
            let diarizer = try await diarization.diarizer()
            for analysis in candidates {
                try Task.checkCancellation()
                let track = analysis.track
                publishSeparation(meetingID, phase: .separatingSpeakers(track, fraction: 0))
                let hint = Self.hint(for: track, expectedSpeakers: requested, hasSystemTrack: hasSystemTrack)
                let raw = try await diarizer.diarize(fileAt: analysis.url, hint: hint) { fraction in
                    Task { @MainActor in
                        self.receiveSeparationProgress(meetingID, token: token, track: track, fraction: fraction)
                    }
                }
                try Task.checkCancellation()
                separation.turns[track] = SpeakerTurns.normalize(raw, trackDuration: analysis.duration, hint: hint)
                runs[meetingID]?.separationFractions[track] = 1
                publishSeparation(meetingID, phase: .separatingSpeakers(track, fraction: 1))
            }
        } catch {
            // A diarizer interrupted by cancellation may report it as its own error.
            if error is CancellationError || Task.isCancelled {
                throw CancellationError()
            }
            separation.failure = error
            separation.record.state = .failed
            separation.record.message = CoreStrings.format("meetings.diarization.failed", table: .meetings,
                                                           error.localizedDescription)
            logger.error("Speaker separation failed: \(error.localizedDescription, privacy: .public)")
        }
        return separation
    }

    private func transcribeClips(meetingID: UUID, analyses: [TrackAnalysis], separation: Separation,
                                 hasSystemTrack: Bool, options: MeetingProcessingOptions) async throws -> MeetingRecord {
        let segmenter = self.segmenter
        let turns = separation.turns
        let plans = try await Self.utility {
            analyses.map { analysis -> ClipPlan in
                let clips: [TrackClip]
                if let trackTurns = turns[analysis.track], !trackTurns.isEmpty {
                    clips = ClipPlanner.plan(turns: trackTurns, speech: analysis.speech.map { $0.start..<$0.end },
                                             trackDuration: analysis.duration, maxSeconds: segmenter.maxSeconds,
                                             envelope: analysis.envelope, segmenter: segmenter)
                        .map { TrackClip(start: $0.range.lowerBound, end: $0.range.upperBound,
                                         samples: Self.sampleRange(of: $0.range), cluster: $0.cluster) }
                } else {
                    clips = analysis.speech.map { TrackClip(start: $0.start, end: $0.end, samples: $0.samples, cluster: nil) }
                }
                return ClipPlan(track: analysis.track, url: analysis.url, clips: clips)
            }
        }
        try Task.checkCancellation()

        let engineID = options.engineID ?? engines.engineID(for: .meetings)
        let engine = try await engines.engine(for: engineID)
        try Task.checkCancellation()

        let total = plans.reduce(0) { $0 + $1.clips.count }
        publishClips(meetingID, completed: 0, total: total)

        let workFolder = FileManager.default.temporaryDirectory
            .appendingPathComponent("Slovo-Meeting-\(meetingID.uuidString)-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: workFolder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: workFolder) }

        var produced: [ProducedSegment] = []
        var completed = 0
        for plan in plans {
            let reader = try await Self.utility { try MeetingTrackReader(url: plan.url) }
            for (index, clip) in plan.clips.enumerated() {
                try Task.checkCancellation()
                if !clip.samples.isEmpty {
                    let clipURL = workFolder.appendingPathComponent("\(plan.track.rawValue)-\(index).wav")
                    try await Self.utility {
                        try MeetingClip.write(reader.samples(in: clip.samples), to: clipURL)
                    }
                    let text = try await engine.transcribe(fileURL: clipURL)
                    try? FileManager.default.removeItem(at: clipURL)
                    engines.markUsed(engineID)
                    try Task.checkCancellation()

                    let cleaned = TranscriptMerger.normalizedText(text)
                    if !cleaned.isEmpty {
                        produced.append(ProducedSegment(track: plan.track, start: clip.start, end: clip.end,
                                                        text: cleaned, cluster: clip.cluster))
                    }
                }
                completed += 1
                publishClips(meetingID, completed: completed, total: total)
            }
        }

        // The record is read again, so a title change, a rename or a moment marked while
        // the run went on survives, and speakers are matched against the names as they are now.
        guard var meeting = store.meetings.first(where: { $0.id == meetingID }) else { throw MeetingErrors.notFound() }
        if produced.isEmpty, total > 0 || !meeting.segments.isEmpty {
            throw MeetingErrors.EmptyTranscription()
        }
        let separated = separation.separatedTracks
        var clusters: [MeetingTrack: [Int: [Range<TimeInterval>]]] = [:]
        for segment in produced {
            guard let cluster = segment.cluster, separated.contains(segment.track), segment.end > segment.start else { continue }
            clusters[segment.track, default: [:]][cluster, default: []].append(segment.start..<segment.end)
        }
        // Speakers are built from clips that produced text, so a voice that said nothing
        // recognizable does not become an empty speaker.
        let registry = SpeakerRegistry.build(
            microphoneClusters: separated.contains(.microphone) ? clusters[.microphone] ?? [:] : nil,
            systemClusters: separated.contains(.system) ? clusters[.system] ?? [:] : nil,
            hasSystemTrack: hasSystemTrack, previous: meeting)

        // A track this run did not separate keeps the voices an earlier run found on it,
        // with the names the user gave them, each new segment going to the voice whose
        // earlier lines it overlaps.
        var carriedIDs: [Int: String?] = [:]
        var carriedSpeakers: [MeetingSpeaker] = []
        for track in MeetingTrack.allCases where !separated.contains(track) {
            let offsets = produced.indices.filter { produced[$0].track == track }
            guard !offsets.isEmpty else { continue }
            let newSegments = offsets.map {
                TranscriptSegment(start: produced[$0].start, end: produced[$0].end, speaker: track.speaker,
                                  text: produced[$0].text)
            }
            guard let carried = SpeakerRegistry.carryOver(track: track, segments: newSegments, previous: meeting) else {
                continue
            }
            for (offset, id) in zip(offsets, carried.speakerIDs) {
                carriedIDs[offset] = id
            }
            carriedSpeakers += carried.speakers
        }

        let segments = produced
            .enumerated()
            .sorted { left, right in
                if left.element.start != right.element.start { return left.element.start < right.element.start }
                if left.element.track != right.element.track { return left.element.track == .microphone }
                return left.offset < right.offset
            }
            .map { offset, segment -> TranscriptSegment in
                let speakerID: String?
                if let carried = carriedIDs[offset] {
                    speakerID = carried
                } else {
                    speakerID = segment.cluster.flatMap { registry.idForCluster[segment.track]?[$0] }
                }
                return TranscriptSegment(start: segment.start, end: segment.end, speaker: segment.track.speaker,
                                         text: segment.text, speakerID: speakerID)
            }

        var combined = registry.speakers
        for speaker in carriedSpeakers where !combined.contains(where: { $0.id == speaker.id }) {
            combined.append(speaker)
        }
        var speakers = MeetingTrack.allCases.flatMap { track in combined.filter { $0.track == track } }
        // The registry may pick «Я» automatically on a separated track while a carried
        // speaker holds the user's own choice; that choice wins.
        if speakers.filter(\.isMe).count > 1, let chosen = meeting.speakers.first(where: \.isMe)?.id {
            for index in speakers.indices {
                speakers[index].isMe = speakers[index].id == chosen
            }
        }

        var diarizationRecord = separation.record
        diarizationRecord.speakersPerTrack = Self.speakerCounts(registry.speakers, separated: separated)
        meeting.highestSpeakerNumber = SpeakerRegistry.highestNumber(keeping: meeting, with: speakers)
        meeting.speakers = speakers
        meeting.segments = segments
        meeting.diarization = diarizationRecord
        meeting.engineID = engineID
        meeting.formatVersion = MeetingRecord.currentFormatVersion
        meeting.status = .transcribed
        meeting.errorMessage = nil
        store.update(meeting)

        // The audio goes only once meeting.json and transcript.md are on disk, so a failed
        // save never leaves a meeting with neither a transcript nor its recording. The
        // screen recording stays: the transcript replaces the audio, not the picture.
        if save(context: "transcribed"), !segments.isEmpty, settings?.keepMeetingAudio == .afterTranscription {
            store.removeAudioTracks(id: meetingID)
            save(context: "audio removed")
        }
        logger.info("""
            Transcribed a meeting: \(total, privacy: .public) clips, \(segments.count, privacy: .public) segments, \
            \(speakers.count, privacy: .public) speakers
            """)
        return store.meetings.first(where: { $0.id == meetingID }) ?? meeting
    }

    /// Gives the existing segments to the voices of a new separation without touching text.
    /// Here separation is the whole job, so its failure fails the run.
    ///
    /// A track that gives no turns this time, because it has too little speech or the
    /// diarizer found none, keeps its speakers and line assignments as they were: this mode
    /// exists to keep the user's work, and nothing new is known about that track.
    private func finishSpeakersOnly(meetingID: UUID, separation: Separation, hasSystemTrack: Bool) throws -> MeetingRecord {
        if let failure = separation.failure {
            throw failure
        }
        guard var meeting = store.meetings.first(where: { $0.id == meetingID }) else { throw MeetingErrors.notFound() }

        let separated = separation.separatedTracks
        var clusters: [MeetingTrack: [Int: [Range<TimeInterval>]]] = [:]
        var clusterOfSegment: [Int: Int] = [:]
        for track in MeetingTrack.allCases where separated.contains(track) {
            let indices = meeting.segments.indices.filter { MeetingTrack(meeting.segments[$0].speaker) == track }
            let assigned = ClipPlanner.assign(segments: indices.map { meeting.segments[$0] },
                                              turns: separation.turns[track] ?? [])
            for (index, cluster) in zip(indices, assigned) {
                let segment = meeting.segments[index]
                guard let cluster, segment.start.isFinite, segment.end.isFinite else { continue }
                clusterOfSegment[index] = cluster
                clusters[track, default: [:]][cluster, default: []].append(segment.start..<max(segment.start, segment.end))
            }
        }

        let registry = SpeakerRegistry.build(
            microphoneClusters: separated.contains(.microphone) ? clusters[.microphone] ?? [:] : nil,
            systemClusters: separated.contains(.system) ? clusters[.system] ?? [:] : nil,
            hasSystemTrack: hasSystemTrack, previous: meeting)
        for index in meeting.segments.indices {
            let track = MeetingTrack(meeting.segments[index].speaker)
            guard separated.contains(track) else { continue }
            meeting.segments[index].speakerID = clusterOfSegment[index].flatMap { registry.idForCluster[track]?[$0] }
        }

        var speakers = MeetingTrack.allCases.flatMap { track in
            (separated.contains(track) ? registry.speakers : meeting.speakers).filter { $0.track == track }
        }
        // The registry sees only separated tracks, so it may pick «Я» automatically while a
        // kept track holds the user's own choice; that choice wins.
        if speakers.filter(\.isMe).count > 1, let chosen = meeting.speakers.first(where: \.isMe)?.id {
            for index in speakers.indices {
                speakers[index].isMe = speakers[index].id == chosen
            }
        }
        var diarizationRecord = separation.record
        diarizationRecord.speakersPerTrack = Self.speakerCounts(registry.speakers, separated: separated)
        for track in MeetingTrack.allCases where !separated.contains(track) {
            if let kept = meeting.diarization?.speakersPerTrack[track.rawValue] {
                diarizationRecord.speakersPerTrack[track.rawValue] = kept
            }
        }
        meeting.highestSpeakerNumber = SpeakerRegistry.highestNumber(keeping: meeting, with: speakers)
        meeting.speakers = speakers
        meeting.diarization = diarizationRecord
        meeting.status = .transcribed
        store.update(meeting)
        save(context: "speakers recalculated")

        progress[meetingID] = TranscriptionProgress(
            completedSegments: 0, totalSegments: 0,
            phase: .separatingSpeakers(runs[meetingID]?.separationTracks.last ?? .microphone, fraction: 1),
            overallFraction: 1)
        logger.info("Recalculated speakers: \(speakers.count, privacy: .public) speakers")
        return store.meetings.first(where: { $0.id == meetingID }) ?? meeting
    }

    // MARK: - Progress

    private func publishSeparation(_ meetingID: UUID, phase: MeetingProcessingPhase) {
        guard let run = runs[meetingID] else { return }
        let fraction = run.separationFraction
        let overall = run.mode == .speakersOnly ? fraction : Self.separationProgressWeight * fraction
        progress[meetingID] = TranscriptionProgress(completedSegments: 0, totalSegments: 0, phase: phase,
                                                    overallFraction: overall)
    }

    private func publishClips(_ meetingID: UUID, completed: Int, total: Int) {
        guard let run = runs[meetingID] else { return }
        let clips = total > 0 ? Double(completed) / Double(total) : 1
        let overall = run.separationTracks.isEmpty
            ? clips
            : Self.separationProgressWeight + (1 - Self.separationProgressWeight) * clips
        progress[meetingID] = TranscriptionProgress(completedSegments: completed, totalSegments: total,
                                                    phase: .transcribing, overallFraction: overall)
    }

    /// Diarizer progress arrives on any thread and can trail the run; it only moves the
    /// track that is still being separated in the same run forward.
    private func receiveSeparationProgress(_ meetingID: UUID, token: UUID, track: MeetingTrack, fraction: Double) {
        guard var run = runs[meetingID], run.token == token, fraction.isFinite,
              case .separatingSpeakers(let current, _)? = progress[meetingID]?.phase, current == track else {
            return
        }
        let value = min(1, max(run.separationFractions[track] ?? 0, fraction))
        run.separationFractions[track] = value
        runs[meetingID] = run
        publishSeparation(meetingID, phase: .separatingSpeakers(track, fraction: value))
    }

    // MARK: - Helpers

    /// The speaker count hint for one track: an exact number in person, where the
    /// microphone hears everyone, and upper bounds in calls, where the people are split
    /// between the microphone and system audio.
    static func hint(for track: MeetingTrack, expectedSpeakers: Int?, hasSystemTrack: Bool) -> SpeakerCountHint {
        guard let expected = expectedSpeakers else { return .automatic }
        guard hasSystemTrack else {
            return track == .microphone ? SpeakerCountHint(exactly: expected) : .automatic
        }
        switch track {
        case .microphone: return SpeakerCountHint(maximum: expected)
        case .system: return SpeakerCountHint(maximum: max(1, expected - 1))
        }
    }

    /// 2–8, or nil for automatic.
    static func requestedSpeakers(_ value: Int?) -> Int? {
        guard let value else { return nil }
        let normalized = AppSettings.normalizedExpectedSpeakers(value)
        return normalized == 0 ? nil : normalized
    }

    private static func speakerCounts(_ speakers: [MeetingSpeaker], separated: Set<MeetingTrack>) -> [String: Int] {
        var counts: [String: Int] = [:]
        for track in separated {
            counts[track.rawValue] = speakers.filter { $0.track == track && $0.number != nil }.count
        }
        return counts
    }

    private nonisolated static func sampleRange(of range: Range<TimeInterval>) -> Range<Int> {
        let rate = Double(MeetingAudioFormat.sampleRate)
        let lower = max(0, Int((range.lowerBound * rate).rounded()))
        let upper = max(lower, Int((range.upperBound * rate).rounded()))
        return lower..<upper
    }

    private func audioTracks(for meetingID: UUID) -> [AudioTrack] {
        let fileManager = FileManager.default
        var tracks: [AudioTrack] = []
        let microphone = store.microphoneAudioURL(for: meetingID)
        if fileManager.fileExists(atPath: microphone.path) {
            tracks.append(AudioTrack(track: .microphone, url: microphone))
        }
        let system = store.systemAudioURL(for: meetingID)
        if fileManager.fileExists(atPath: system.path) {
            tracks.append(AudioTrack(track: .system, url: system))
        }
        return tracks
    }

    private func finish(_ meetingID: UUID) {
        tasks[meetingID] = nil
        progress[meetingID] = nil
        runs[meetingID] = nil
    }

    /// A meeting that already had a transcript, and any speakers-only run, gets its
    /// previous status back: the run never replaced segments, speakers or the separation
    /// result in the store, so they are still the previous ones, together with anything the
    /// user renamed, merged or marked meanwhile. A first transcription is marked `.failed`.
    private func recoverAfterFailure(_ meetingID: UUID, original: MeetingRecord, mode: MeetingProcessingOptions.Mode,
                                     error: Error) {
        guard var meeting = store.meetings.first(where: { $0.id == meetingID }) else { return }
        let cancelled = error is CancellationError
        if mode == .speakersOnly || original.status == .transcribed {
            meeting.status = original.status
            meeting.errorMessage = error is MeetingErrors.EmptyTranscription
                ? error.localizedDescription : original.errorMessage
        } else {
            meeting.status = .failed
            meeting.errorMessage = cancelled ? MeetingErrors.cancelledMessage() : error.localizedDescription
        }
        store.update(meeting)
        save(context: "after a failed run")
        if !cancelled {
            logger.error("Meeting processing failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    @discardableResult
    private func save(context: String) -> Bool {
        do {
            try store.flush()
            return true
        } catch {
            logger.error("Saving the meeting failed (\(context, privacy: .public)): \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    /// Runs file work on a utility-priority task, away from the main actor.
    private static func utility<T: Sendable>(_ work: @escaping @Sendable () throws -> T) async throws -> T {
        let task = Task.detached(priority: .utility) {
            try Task.checkCancellation()
            return try work()
        }
        return try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            task.cancel()
        }
    }
}
