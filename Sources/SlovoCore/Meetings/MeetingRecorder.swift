import AppKit
import AVFoundation
import Combine
import CoreAudio
import CoreGraphics
import Darwin
import Foundation
import os

/// Records a meeting: the microphone through its own capture session into `mic.caf`
/// and, when enabled, system audio into `system.caf`. Dictation is blocked meanwhile.
///
/// Both files grow on disk as the meeting goes on and share one timeline, so a
/// segment's start time means the same moment in both tracks.
@MainActor
public final class MeetingRecorder: ObservableObject, MeetingActivityProviding {
    @Published public private(set) var state: MeetingRecorderState = .idle
    @Published public private(set) var microphoneLevel: Float = 0
    @Published public private(set) var systemLevel: Float = 0
    @Published public private(set) var systemAudioStatus: SystemAudioStatus = .off
    @Published public private(set) var screenRecordingStatus: ScreenRecordingStatus = .off
    /// The app a screen recording is scoped to, for the recording panel; nil for the
    /// whole display or when nothing is capturing.
    @Published public private(set) var screenCaptureAppName: String?
    /// Whether the microphone is still being recorded. A meeting outlives a lost
    /// microphone, so this degrades rather than ending the recording.
    @Published public private(set) var microphoneStatus: MicrophoneStatus = .capturing
    /// Whether the microphone is being recorded or gated to silence. A recording starts
    /// as `AppSettings.recordMicrophone` says and can be switched while it lasts; the
    /// capture itself runs either way, so switching back is instant.
    @Published public private(set) var isMicrophoneOn = true
    /// The switch made last, for the Flow bar's short confirmation. Nil until the
    /// microphone is switched during a recording.
    @Published public private(set) var lastMicrophoneSwitch: MicrophoneSwitch?
    /// Seconds since the recording started, refreshed four times per second.
    @Published public private(set) var elapsed: TimeInterval = 0
    /// The moment marked last, for the Flow bar's short confirmation.
    @Published public private(set) var lastMarkedMoment: MeetingHighlight?
    /// The message of a recording that ended by itself, so the app can tell the owner
    /// even with no window open. Nil until something fails after a start.
    @Published public private(set) var lastFailure: String?
    /// The meeting a recording that ended by itself left behind, saved and published after
    /// `lastFailure`, so the app transcribes it like one the owner stopped.
    @Published public private(set) var autoStoppedMeeting: MeetingRecord?

    /// Two marks closer than this on the audio timeline count as one press.
    static let momentDeduplicationSeconds: TimeInterval = 2
    /// Below this, the two clocks disagree only by the moment that passes between reading
    /// one and the other; a sleep worth padding is seconds at the very least.
    static let minimumSleepSeconds: TimeInterval = 0.5
    /// A recording is refused below this much free space: the two tracks take about
    /// 230 MB an hour, and a disk that fills mid-meeting costs the recording.
    public static let minimumFreeBytes: Int64 = 2 * 1024 * 1024 * 1024
    /// A recording ends by itself, saved, once the disk has less than this left: about a
    /// quarter of an hour of both tracks, enough to close the files and save the meeting,
    /// where a disk that fills completely fails every write at once.
    public static let minimumFreeBytesToContinue: Int64 = 100 * 1024 * 1024

    /// The Core Audio host clock, which both tracks' timelines are measured on; tests
    /// replace it to mark moments at chosen positions.
    var hostTime: () -> UInt64 = { AudioGetCurrentHostTime() }
    /// Seconds since boot with sleep counted in. `uptime` counts only the awake part, so
    /// how far these two have drifted apart is exactly what the Mac has slept; tests
    /// replace it.
    var continuousUptime: () -> TimeInterval = { MachContinuousClock.seconds() }
    /// Free space on the meetings volume, re-read while recording; tests replace it.
    var availableSpace: () -> Int64?

    private let store: MeetingStoring
    private let settings: AppSettings
    private let uptime: UptimeProviding
    /// Carries a lost microphone out of the window: the panel that says so is the only
    /// place it appears, and the window is closed for the whole meeting.
    private let sounds: SoundPlaying?
    private var session: RecordingSession?
    /// The last moment marked in the current recording, for de-duplication.
    private var sessionMoment: MeetingHighlight?
    private var starting = false
    /// Track events that arrive while `start()` is still opening the other tracks, before a
    /// session exists to apply them to. `start()` runs them once it has one, so a failure
    /// in those first moments is acted on instead of dropped.
    private var eventsDuringStart: [@MainActor () async -> Void] = []
    private var ticker: Task<Void, Never>?
    /// The wake observer, held for the life of a recording.
    private var wakeObserver: NSObjectProtocol?
    /// How far the two clocks had already drifted apart when the recording started, so
    /// only this meeting's own sleeps are padded.
    private var sleptBeforeStart: TimeInterval = 0
    private let logger = SlovoLog.logger("MeetingRecorder")

    public init(store: MeetingStoring, settings: AppSettings, uptime: UptimeProviding = SystemUptime(),
                sounds: SoundPlaying? = nil) {
        self.store = store
        self.settings = settings
        self.uptime = uptime
        self.sounds = sounds
        let meetingsFolder = store.meetingsFolder
        availableSpace = { MeetingRecorder.availableBytes(at: meetingsFolder) }
    }

    /// True while recording and while the files are being closed after stop.
    public var isMeetingRecording: Bool {
        switch state {
        case .recording, .stopping: return true
        case .idle, .failed: return false
        }
    }

    /// The id of the meeting being recorded.
    public var currentMeetingID: UUID? {
        session?.meetingID
    }

    /// The tracks sound is going to right now; nil unless a meeting is recording.
    public var recordingTracks: MeetingRecordingTracks? {
        guard case .recording = state, session != nil else { return nil }
        return MeetingRecordingTracks(microphoneOn: isMicrophoneOn, microphoneStatus: microphoneStatus,
                                      systemAudioStatus: systemAudioStatus)
    }

    /// The title a new meeting gets: "Meeting" and the date in the interface language.
    public static func defaultTitle(for date: Date, language: AppLanguage? = nil) -> String {
        MeetingTitles.defaultTitle(for: date, language: language)
    }

    /// Creates the meeting and starts both tracks. A system audio failure does not
    /// stop the recording; it shows in `systemAudioStatus`.
    public func start() async throws -> MeetingRecord {
        guard session == nil, !starting, !isMeetingRecording else {
            throw MeetingErrors.alreadyRecording()
        }
        starting = true
        eventsDuringStart = []
        defer {
            starting = false
            eventsDuringStart = []
        }

        do {
            try await Self.ensureMicrophoneAccess()
        } catch {
            state = .failed(error.localizedDescription)
            throw error
        }

        // Refused before anything is created: the files are the point of a meeting, and a
        // disk that fills halfway through costs the part that matters most.
        if let lowSpace = Self.lowSpaceError(availableBytes: availableSpace()) {
            state = .failed(lowSpace.message)
            throw lowSpace
        }
        lastFailure = nil

        var meeting: MeetingRecord
        do {
            meeting = try store.create(title: MeetingTitles.defaultTitle(for: Date(), language: settings.appLanguage))
        } catch {
            state = .failed(error.localizedDescription)
            throw error
        }

        let origin = AudioGetCurrentHostTime()
        let microphoneOn = settings.recordMicrophone
        let microphone: MeetingMicrophoneCapture
        let microphoneWriter: MeetingTrackWriter
        do {
            microphoneWriter = try MeetingTrackWriter(url: store.microphoneAudioURL(for: meeting.id), originHostTime: origin)
            // Gated before capture starts, so a meeting that begins with the microphone off
            // never has a first buffer of the room on it.
            microphoneWriter.isMuted = !microphoneOn
            microphone = MeetingMicrophoneCapture(writer: microphoneWriter, deviceUID: settings.microphoneUID)
        } catch {
            store.delete(id: meeting.id)
            state = .failed(error.localizedDescription)
            throw error
        }
        endRecordingOnMicrophoneWriteFailure(microphoneWriter)
        microphone.onLevel = { [weak self] level in
            Task { @MainActor in self?.receiveMicrophoneLevel(level) }
        }
        microphone.onMicrophoneLost = { [weak self] error in
            Task { @MainActor in self?.microphoneLost(error) }
        }
        microphone.onMicrophoneBack = { [weak self] in
            Task { @MainActor in self?.microphoneBack() }
        }
        do {
            try await Self.offMainActor { try microphone.start() }
        } catch {
            microphoneWriter.finish()
            store.delete(id: meeting.id)
            state = .failed(error.localizedDescription)
            throw error
        }

        // A screen recording scoped to one app brings its own audio in place of
        // `system.caf`'s usual system-wide tap; starting both would defeat the point of
        // scoping down to just that app, so the tap is skipped in that case.
        let (system, screen) = await startAdditionalTracks(
            startSystem: { await self.startSystemAudio(meetingID: meeting.id, origin: origin) },
            startScreen: { captureAppAudio in
                await self.startScreenRecording(meetingID: meeting.id, origin: origin,
                                                captureAppAudio: captureAppAudio)
            })
        meeting.hasSystemAudio = system != nil || screen?.audioWriter != nil
        meeting.hasScreenRecording = screen != nil
        store.update(meeting)

        let startedAt = uptime.uptime()
        // Idle system sleep is a minute on battery, and a sleeping Mac records nothing.
        // `beginActivity` holds the same PreventUserIdleSystemSleep assertion that an
        // `IOPMAssertionCreateWithName` call would take, so one of the two is enough.
        // Neither can stop a lid close, which is why a sleep that happens regardless is
        // padded on wake rather than assumed away.
        let activity = ProcessInfo.processInfo.beginActivity(options: [.userInitiated, .idleSystemSleepDisabled],
                                                             reason: "Recording a meeting")
        session = RecordingSession(meetingID: meeting.id, startedAt: startedAt, originHostTime: origin,
                                   microphone: microphone, microphoneWriter: microphoneWriter, systemTap: system?.tap,
                                   systemWriter: system?.writer ?? screen?.audioWriter, screenCapture: screen?.capture,
                                   screenWriter: screen?.videoWriter, activity: activity)
        sessionMoment = nil
        elapsed = 0
        microphoneStatus = .capturing
        isMicrophoneOn = microphoneOn
        sleptBeforeStart = continuousUptime() - uptime.uptime()
        state = .recording(meetingID: meeting.id, startedAt: startedAt)
        startWakeWatch()
        startTicker(startedAt: startedAt)
        logger.info("Meeting recording started")
        let events = eventsDuringStart
        eventsDuringStart = []
        for event in events {
            await event()
        }
        return store.meetings.first(where: { $0.id == meeting.id }) ?? meeting
    }

    /// Stops both tracks and marks the meeting `.recorded`; nil when nothing was recording.
    public func stop() async -> MeetingRecord? {
        // A sleep that ended since the last reading still belongs on the tracks and in the
        // length this meeting is saved with.
        checkForSleep()
        guard let session else { return nil }
        self.session = nil
        state = .stopping
        let duration = max(0, uptime.uptime() - session.startedAt) + session.sleptSeconds
        let result = await Self.offMainActor { session.finish() }
        return finalize(session, result: result, duration: duration)
    }

    /// Marks an important moment at the recording's current position on the tracks' own
    /// clock and saves it with the meeting. Two marks within 2 s count as one, and the
    /// second call returns the first highlight. Nil unless recording.
    ///
    /// The position comes from the host-time origin both track writers share, not from
    /// uptime minus the start: capture starts a moment after `start()` begins, and uptime
    /// would place the mark that much later than the audio.
    @discardableResult
    public func markMoment(note: String? = nil) -> MeetingHighlight? {
        // A mark made in the first moments after a wake must not wait for the ticker to
        // notice the sleep, or it would sit that much earlier on the track.
        checkForSleep()
        guard case .recording = state, let session else { return nil }
        // The host clock stands still while the Mac sleeps, so the silence the tracks were
        // padded with is added back; without it a mark made after a sleep would sit
        // earlier on the track than the moment it names.
        let frame = MeetingTrackWriter.frame(at: hostTime(), origin: session.originHostTime)
        let time = Double(frame) / Double(MeetingAudioFormat.sampleRate) + session.sleptSeconds
        if let previous = sessionMoment, abs(time - previous.time) < Self.momentDeduplicationSeconds {
            return previous
        }
        guard var meeting = store.meetings.first(where: { $0.id == session.meetingID }) else { return nil }
        let highlight = MeetingHighlight(time: time, note: MeetingEdits.sanitizedNote(note), source: .recording,
                                         createdAt: Date())
        meeting.highlights.append(highlight)
        store.update(meeting)
        sessionMoment = highlight
        lastMarkedMoment = highlight
        return highlight
    }

    /// Starts or stops recording the microphone in the current meeting, for this meeting
    /// only. The microphone keeps being captured and its track keeps the timeline: while
    /// off, the track receives silence of exactly the length it would have received sound.
    /// Heard and shown, because it is switched without looking at Slovo. Returns whether
    /// anything changed.
    @discardableResult
    public func setMicrophoneOn(_ on: Bool) -> Bool {
        guard case .recording = state, let session, on != isMicrophoneOn else { return false }
        session.microphoneWriter.isMuted = !on
        isMicrophoneOn = on
        if !on {
            microphoneLevel = 0
        }
        sounds?.play(on ? .start : .stop)
        lastMicrophoneSwitch = MicrophoneSwitch(isOn: on)
        logger.notice("Meeting microphone switched \(on ? "on" : "off", privacy: .public)")
        return true
    }

    /// Flips the microphone of the current meeting; false unless recording.
    @discardableResult
    public func toggleMicrophone() -> Bool {
        setMicrophoneOn(!isMicrophoneOn)
    }

    /// Starts a recording session over file writers without opening a microphone or a
    /// system audio tap, so moment marking, sleep padding and track failures can be tested
    /// without hardware or permissions. `systemTrack` adds the second writer, for the
    /// failures that must degrade one track and leave the other recording. Stop it with
    /// `stopImmediately()`.
    @discardableResult
    func startSessionWithoutCapture(meetingID: UUID, originHostTime: UInt64,
                                    systemTrack: Bool = false) throws -> (microphone: MeetingTrackWriter,
                                                                          system: MeetingTrackWriter?) {
        guard session == nil, !starting, !isMeetingRecording else { throw MeetingErrors.alreadyRecording() }
        let writer = try MeetingTrackWriter(url: store.microphoneAudioURL(for: meetingID), originHostTime: originHostTime)
        let microphoneOn = settings.recordMicrophone
        writer.isMuted = !microphoneOn
        endRecordingOnMicrophoneWriteFailure(writer)
        var system: MeetingTrackWriter?
        if systemTrack {
            let systemWriter = try MeetingTrackWriter(url: store.systemAudioURL(for: meetingID),
                                                      originHostTime: originHostTime)
            dropSystemTrackOnWriteFailure(systemWriter)
            system = systemWriter
        }
        let startedAt = uptime.uptime()
        session = RecordingSession(meetingID: meetingID, startedAt: startedAt, originHostTime: originHostTime,
                                   microphone: MeetingMicrophoneCapture(writer: writer, deviceUID: nil),
                                   microphoneWriter: writer, systemTap: nil, systemWriter: system, activity: nil)
        sessionMoment = nil
        microphoneStatus = .capturing
        isMicrophoneOn = microphoneOn
        if systemTrack {
            systemAudioStatus = .capturing
        }
        sleptBeforeStart = continuousUptime() - uptime.uptime()
        state = .recording(meetingID: meetingID, startedAt: startedAt)
        return (writer, system)
    }

    /// Stops and closes the files without waiting on another thread, for app termination.
    @discardableResult
    public func stopImmediately() -> MeetingRecord? {
        checkForSleep()
        guard let session else { return nil }
        self.session = nil
        state = .stopping
        let duration = max(0, uptime.uptime() - session.startedAt) + session.sleptSeconds
        return finalize(session, result: session.finish(), duration: duration)
    }

    // MARK: - Private

    typealias SystemCapture = (tap: SystemAudioTap, writer: MeetingTrackWriter)
    typealias ScreenCapture = (capture: MeetingScreenCapture, videoWriter: MeetingScreenWriter,
                              audioWriter: MeetingTrackWriter?)

    /// Keeps the requested audio boundary even when screen capture fails. The starters
    /// are injected so privacy-sensitive routing can be checked without capture hardware.
    func startAdditionalTracks(startSystem: () async -> SystemCapture?,
                               startScreen: (_ captureAppAudio: Bool) async -> ScreenCapture?) async
        -> (SystemCapture?, ScreenCapture?) {
        let screenEnabled = settings.recordScreen
        let appScoped = screenEnabled && settings.screenCaptureTarget.storedBundleID != nil
        let audioEnabled = settings.recordSystemAudio
        systemAudioStatus = .off
        let system = audioEnabled && !appScoped ? await startSystem() : nil
        let screen = screenEnabled ? await startScreen(appScoped && audioEnabled) : nil
        if appScoped && audioEnabled && screen?.audioWriter == nil {
            // Recording the whole computer would expose unrelated apps' audio. Keep the
            // microphone and report the failed scoped track instead of broadening access.
            if case let .unavailable(message) = screenRecordingStatus {
                systemAudioStatus = .unavailable(message)
            } else {
                systemAudioStatus = .unavailable(MeetingErrors.appAudioEnded().message)
            }
        }
        return (system, screen)
    }

    private func startSystemAudio(meetingID: UUID, origin: UInt64) async -> (tap: SystemAudioTap, writer: MeetingTrackWriter)? {
        guard settings.recordSystemAudio else {
            systemAudioStatus = .off
            return nil
        }
        guard SystemAudioTap.isSupported else {
            systemAudioStatus = .unavailable(MeetingErrors.systemAudioUnsupported().message)
            return nil
        }

        let url = store.systemAudioURL(for: meetingID)
        let tap = SystemAudioTap()
        tap.onLevel = { [weak self] level in
            Task { @MainActor in self?.receiveSystemLevel(level) }
        }
        tap.onSilence = { [weak self] in
            Task { @MainActor in self?.systemAudioWentSilent() }
        }
        tap.onSignal = { [weak self] in
            Task { @MainActor in self?.systemAudioResumed() }
        }
        let tapID = ObjectIdentifier(tap)
        tap.onStopped = { [weak self] error in
            Task { @MainActor in self?.systemAudioInterrupted(error, tapID: tapID) }
        }
        tap.onResumed = { [weak self] in
            Task { @MainActor in self?.systemAudioReconnected(tapID: tapID) }
        }
        do {
            let writer = try MeetingTrackWriter(url: url, originHostTime: origin)
            dropSystemTrackOnWriteFailure(writer)
            do {
                try await Self.offMainActor { try tap.start(writer: writer) }
            } catch {
                writer.finish()
                try? FileManager.default.removeItem(at: url)
                throw error
            }
            systemAudioStatus = .capturing
            return (tap, writer)
        } catch {
            logger.error("System audio unavailable: \(error.localizedDescription, privacy: .public)")
            systemAudioStatus = .unavailable(error.localizedDescription)
            return nil
        }
    }

    /// The whole display's video rides on the existing `SystemAudioTap`-fed `system.caf`
    /// (started separately, in `start()`); one app's video instead brings its own audio,
    /// written to that same file in place of a tap, so `audioWriter` is only ever set
    /// for `.application`.
    private func startScreenRecording(meetingID: UUID, origin: UInt64, captureAppAudio: Bool)
        async -> (capture: MeetingScreenCapture, videoWriter: MeetingScreenWriter, audioWriter: MeetingTrackWriter?)? {
        guard settings.recordScreen else {
            screenRecordingStatus = .off
            screenCaptureAppName = nil
            return nil
        }
        guard MeetingScreenCapture.isSupported else {
            screenRecordingStatus = .unavailable(MeetingErrors.screenRecordingUnavailable().message)
            return nil
        }
        guard CGPreflightScreenCaptureAccess() else {
            screenRecordingStatus = .unavailable(MeetingErrors.screenRecordingDenied().message)
            return nil
        }

        let target = settings.screenCaptureTarget
        let videoURL = store.screenRecordingURL(for: meetingID)
        var audioWriter: MeetingTrackWriter?
        if case let .application(bundleID) = target {
            screenCaptureAppName = NSWorkspace.shared.runningApplications
                .first { $0.bundleIdentifier == bundleID }?.localizedName ?? bundleID
        } else {
            screenCaptureAppName = nil
        }
        if captureAppAudio {
            do {
                let writer = try MeetingTrackWriter(url: store.systemAudioURL(for: meetingID), originHostTime: origin)
                // The write-failure path is identical to a system-wide tap's: end this
                // track with what it holds, keep the meeting going, and say so through the
                // same status the "System audio" meter already reads.
                dropSystemTrackOnWriteFailure(writer)
                audioWriter = writer
            } catch {
                screenRecordingStatus = .unavailable(error.localizedDescription)
                systemAudioStatus = .unavailable(error.localizedDescription)
                screenCaptureAppName = nil
                return nil
            }
        }

        let capture = MeetingScreenCapture()
        wireScreenTrack(capture)
        if audioWriter != nil {
            capture.onLevel = { [weak self] level in
                Task { @MainActor in self?.receiveSystemLevel(level) }
            }
        }
        let captureID = ObjectIdentifier(capture)
        let videoFailed: (Error) -> Void = { [weak self] error in
            Task { @MainActor in await self?.screenVideoFailed(error, captureID: captureID) }
        }
        var createdVideoWriter: MeetingScreenWriter?
        do {
            try await Self.offMainActor {
                try capture.start(target: target, audioWriter: audioWriter) { width, height in
                    let writer = try MeetingScreenWriter(url: videoURL, originHostTime: origin, width: width, height: height,
                                                         onWriteFailure: videoFailed)
                    createdVideoWriter = writer
                    return writer
                }
            }
            guard let createdVideoWriter else {
                throw MeetingErrors.screenRecordingUnavailable()
            }
            screenRecordingStatus = .capturing
            if audioWriter != nil {
                systemAudioStatus = .capturing
            }
            return (capture, createdVideoWriter, audioWriter)
        } catch {
            logger.error("Screen recording unavailable: \(error.localizedDescription, privacy: .public)")
            try? FileManager.default.removeItem(at: videoURL)
            if let audioWriter {
                audioWriter.finish()
                try? FileManager.default.removeItem(at: store.systemAudioURL(for: meetingID))
                systemAudioStatus = .unavailable(error.localizedDescription)
            }
            screenRecordingStatus = .unavailable(error.localizedDescription)
            screenCaptureAppName = nil
            return nil
        }
    }

    private func wireScreenTrack(_ capture: MeetingScreenCapture) {
        let captureID = ObjectIdentifier(capture)
        capture.onLost = { [weak self] _ in
            Task { @MainActor in await self?.retireScreenTrack(status: .lost, captureID: captureID) }
        }
        capture.onPauseChanged = { [weak self] paused in
            Task { @MainActor in self?.screenCapturePaused(paused, captureID: captureID) }
        }
    }

    /// Stops screen capture for the rest of this meeting, whatever the reason: a lost
    /// stream, a revoked permission, a restart that failed. The meeting itself keeps
    /// recording on its microphone regardless. In app mode the same stream also supplies
    /// `system.caf`'s audio, so that track ends with it.
    ///
    /// What the track recorded until now is kept: the fragmented video and the CAF file are
    /// readable up to the moment they stopped, and in app mode that file is the other side
    /// of the call. Only a file that never received anything is removed.
    ///
    /// `captureID` names the capture a callback came from, so a late one from an earlier
    /// capture cannot end a newer one; nil means whatever capture the session has.
    private func retireScreenTrack(status: ScreenRecordingStatus, captureID: ObjectIdentifier?) async {
        guard let session else {
            if let captureID {
                deferDuringStart { [weak self] in await self?.retireScreenTrack(status: status, captureID: captureID) }
            }
            return
        }
        guard session.screenCapture != nil || session.screenWriter != nil else { return }
        if let captureID {
            guard let current = session.screenCapture, ObjectIdentifier(current) == captureID else { return }
        }
        let capture = session.screenCapture
        let videoWriter = session.screenWriter
        session.screenCapture = nil
        session.screenWriter = nil
        let appAudioWriter = session.systemTap == nil ? session.systemWriter : nil
        if appAudioWriter != nil {
            session.systemWriter = nil
        }
        // Said before waiting for the capture to close, so a stop that lands meanwhile
        // ends on the statuses it sets itself rather than on these.
        screenRecordingStatus = status
        screenCaptureAppName = nil
        if appAudioWriter != nil {
            systemLevel = 0
            systemAudioStatus = .unavailable(MeetingErrors.appAudioEnded().message)
        }
        let kept = await Self.offMainActor { () -> (video: Bool, appAudio: Bool) in
            capture?.stop()
            let video = videoWriter?.finish() ?? false
            appAudioWriter?.finish()
            return (video, appAudioWriter?.hasSound ?? false)
        }
        let dropsVideo = videoWriter != nil && !kept.video
        let dropsAppAudio = appAudioWriter != nil && !kept.appAudio
        if dropsVideo {
            try? FileManager.default.removeItem(at: store.screenRecordingURL(for: session.meetingID))
        }
        if dropsAppAudio {
            try? FileManager.default.removeItem(at: store.systemAudioURL(for: session.meetingID))
        }
        if var meeting = store.meetings.first(where: { $0.id == session.meetingID }) {
            if dropsVideo {
                meeting.hasScreenRecording = false
            }
            if dropsAppAudio {
                meeting.hasSystemAudio = false
            }
            store.update(meeting)
        }
        logger.error("The screen recording track stopped; what it recorded is kept")
    }

    /// The video can no longer be written: a write failed, or the disk has no room left for
    /// it. In app mode the same stream still carries the app's audio, which keeps
    /// recording, so only the video is let go; otherwise the capture ends with it.
    private func endScreenVideo(status: ScreenRecordingStatus) async {
        guard let session, let videoWriter = session.screenWriter else { return }
        guard let capture = session.screenCapture, session.systemTap == nil, session.systemWriter != nil else {
            await retireScreenTrack(status: status, captureID: nil)
            return
        }
        session.screenWriter = nil
        screenRecordingStatus = status
        let wrote = await Self.offMainActor { () -> Bool in
            capture.detachVideo()
            return videoWriter.finish()
        }
        if !wrote {
            try? FileManager.default.removeItem(at: store.screenRecordingURL(for: session.meetingID))
            if var meeting = store.meetings.first(where: { $0.id == session.meetingID }) {
                meeting.hasScreenRecording = false
                store.update(meeting)
            }
        }
        logger.error("The screen video stopped; the app's audio keeps recording")
    }

    private func screenVideoFailed(_ error: Error, captureID: ObjectIdentifier) async {
        guard let session else {
            deferDuringStart { [weak self] in await self?.screenVideoFailed(error, captureID: captureID) }
            return
        }
        guard let capture = session.screenCapture, ObjectIdentifier(capture) == captureID,
              session.screenWriter != nil else { return }
        await endScreenVideo(status: .unavailable(MeetingErrors.fileFailed(error).message))
    }

    /// A locked screen pauses capture and unlocking resumes it; the panel says so.
    private func screenCapturePaused(_ paused: Bool, captureID: ObjectIdentifier) {
        guard let capture = session?.screenCapture, ObjectIdentifier(capture) == captureID else { return }
        if paused, screenRecordingStatus == .capturing {
            screenRecordingStatus = .pausedScreenLocked
        } else if !paused, screenRecordingStatus == .pausedScreenLocked {
            screenRecordingStatus = .capturing
        }
    }

    /// Checked every 30 s, so neither depends on a notification arriving, the same caution
    /// the wake observer's own doc comment gives for sleep. A disk that is nearly full ends
    /// the recording, saved, before the writes start failing; a disk short of room for
    /// video lets the video go; a revoked permission ends the screen track.
    func checkRecordingHealth() {
        guard let session, !session.isEnding else { return }
        let available = availableSpace()
        if let available, available < Self.minimumFreeBytesToContinue {
            session.isEnding = true
            logger.error("The disk is nearly full; ending the meeting recording")
            Task { await self.captureFailed(MeetingErrors.lowDiskSpaceWhileRecording(available), in: session) }
            return
        }
        guard session.screenWriter != nil, screenRecordingStatus == .capturing else { return }
        if !CGPreflightScreenCaptureAccess() {
            Task { await self.retireScreenTrack(status: .lost, captureID: nil) }
            return
        }
        if let available, available < Self.minimumFreeBytes {
            Task { await self.endScreenVideo(status: .unavailable(MeetingErrors.lowDiskSpace(available).message)) }
        }
    }

    private func finalize(_ session: RecordingSession, result: RecordingSession.Result,
                          duration: TimeInterval) -> MeetingRecord? {
        ticker?.cancel()
        ticker = nil
        stopWakeWatch()
        // Exactly once: every stop, failure and termination path ends up here.
        session.endActivity()
        microphoneLevel = 0
        systemLevel = 0
        systemAudioStatus = .off
        screenRecordingStatus = .off
        screenCaptureAppName = nil
        microphoneStatus = .capturing
        elapsed = duration
        defer {
            if case .stopping = state {
                state = .idle
            }
        }

        guard var meeting = store.meetings.first(where: { $0.id == session.meetingID }) else { return nil }
        meeting.status = .recorded
        meeting.duration = duration
        meeting.hasAudio = true
        if session.systemWriter != nil, !result.heardSystemAudio {
            // Only zeros: nothing to transcribe, and the file would only take space.
            try? FileManager.default.removeItem(at: store.systemAudioURL(for: meeting.id))
            meeting.hasSystemAudio = false
        }
        if session.screenWriter != nil, !result.wroteScreenRecording {
            try? FileManager.default.removeItem(at: store.screenRecordingURL(for: meeting.id))
            meeting.hasScreenRecording = false
        }
        store.update(meeting)
        do {
            try store.flush()
        } catch {
            logger.error("Saving the stopped meeting failed: \(error.localizedDescription, privacy: .public)")
        }
        logger.info("Meeting recording stopped after \(duration, privacy: .public) s")
        return meeting
    }

    /// The microphone track can no longer be written. While system audio is still
    /// recording the meeting goes on as a one-track recording; with nothing left to write
    /// there is no recording to keep, and it ends rather than leaving the timer running
    /// over a file that stopped growing.
    private func endRecordingOnMicrophoneWriteFailure(_ writer: MeetingTrackWriter) {
        let writerID = ObjectIdentifier(writer)
        writer.onWriteFailure = { [weak self] error in
            Task { @MainActor in await self?.microphoneTrackFailed(error, writerID: writerID) }
        }
    }

    /// System audio is the optional track: at an in-person meeting it records nothing but
    /// silence and is deleted at the end anyway, so a failed write to it must not take the
    /// microphone — the track the meeting is actually about — down with it.
    private func dropSystemTrackOnWriteFailure(_ writer: MeetingTrackWriter) {
        let writerID = ObjectIdentifier(writer)
        writer.onWriteFailure = { [weak self] error in
            Task { @MainActor in await self?.systemTrackFailed(error, writerID: writerID) }
        }
    }

    /// Queues an event that found no session because `start()` is still opening tracks.
    private func deferDuringStart(_ event: @escaping @MainActor () async -> Void) {
        guard starting else { return }
        eventsDuringStart.append(event)
    }

    private func microphoneTrackFailed(_ error: Error, writerID: ObjectIdentifier) async {
        guard let session else {
            deferDuringStart { [weak self] in await self?.microphoneTrackFailed(error, writerID: writerID) }
            return
        }
        guard ObjectIdentifier(session.microphoneWriter) == writerID, !session.microphoneTrackEnded else { return }
        // The track is closed for the rest of the meeting, which `microphoneStatus == .lost`
        // does not mean: that one clears when a device comes back.
        session.microphoneTrackEnded = true
        guard session.systemWriter != nil else {
            await captureFailed(MeetingErrors.fileFailed(error), in: session)
            return
        }
        // Said before waiting for the capture to stop, so a stop that lands meanwhile ends
        // on the status it sets itself.
        microphoneLevel = 0
        setMicrophoneStatus(.lost)
        let microphone = session.microphone
        await Self.offMainActor { microphone.stop() }
        logger.error("The microphone track stopped; system audio keeps recording: \(error.localizedDescription, privacy: .public)")
    }

    /// Stops the tap and leaves the microphone recording. What the file received before
    /// the failure is kept: in a call it is the other side of the conversation up to that
    /// moment. Only a file that never held a sound is removed.
    private func systemTrackFailed(_ error: Error, writerID: ObjectIdentifier) async {
        guard let session else {
            deferDuringStart { [weak self] in await self?.systemTrackFailed(error, writerID: writerID) }
            return
        }
        guard let writer = session.systemWriter, ObjectIdentifier(writer) == writerID else { return }
        let tap = session.systemTap
        session.systemTap = nil
        session.systemWriter = nil
        systemLevel = 0
        systemAudioStatus = .unavailable(MeetingErrors.fileFailed(error).message)
        let hasSound = await Self.offMainActor { () -> Bool in
            tap?.stop()
            writer.finish()
            return writer.hasSound
        }
        if !hasSound {
            try? FileManager.default.removeItem(at: store.systemAudioURL(for: session.meetingID))
            if var meeting = store.meetings.first(where: { $0.id == session.meetingID }) {
                meeting.hasSystemAudio = false
                store.update(meeting)
            }
        }
        logger.error("The system audio track stopped: \(error.localizedDescription, privacy: .public)")
        // The same end condition the microphone track is held to, read from the other side:
        // a meeting ends when nothing is left to write. Without this the session would
        // outlive both tracks, counting time and showing a recording over files that
        // stopped growing.
        if session.microphoneTrackEnded {
            await captureFailed(MeetingErrors.fileFailed(error), in: session)
        }
    }

    /// The tap could not be rebuilt after an output device change; it keeps retrying.
    private func systemAudioInterrupted(_ error: Error, tapID: ObjectIdentifier) {
        guard let session else {
            deferDuringStart { [weak self] in self?.systemAudioInterrupted(error, tapID: tapID) }
            return
        }
        guard let tap = session.systemTap, ObjectIdentifier(tap) == tapID else { return }
        systemLevel = 0
        systemAudioStatus = .unavailable(MeetingErrors.systemAudioInterrupted(error).message)
        logger.error("System audio stopped arriving: \(error.localizedDescription, privacy: .public)")
    }

    private func systemAudioReconnected(tapID: ObjectIdentifier) {
        guard let tap = session?.systemTap, ObjectIdentifier(tap) == tapID,
              case .unavailable = systemAudioStatus else { return }
        systemAudioStatus = .capturing
        logger.notice("System audio is back")
    }

    /// No microphone could be opened for the whole restart budget. The meeting keeps
    /// recording — the writer fills the outage with silence and system audio is untouched
    /// — and the capture keeps trying, so this clears by itself when a device comes back.
    func microphoneLost(_ error: Error) {
        guard session != nil, microphoneStatus != .lost else { return }
        microphoneLevel = 0
        setMicrophoneStatus(.lost)
        logger.error("The microphone is gone; the meeting keeps recording: \(error.localizedDescription, privacy: .public)")
    }

    func microphoneBack() {
        guard session != nil, microphoneStatus == .lost else { return }
        setMicrophoneStatus(.capturing)
        logger.notice("The microphone is back")
    }

    /// The single place the status changes while a meeting records, so a microphone that
    /// vanishes is heard exactly once per outage: the capture retries for as long as the
    /// meeting lasts, and a sound on every attempt would fill the room the meeting is in.
    /// A device that comes back and goes again is a new outage and is heard again.
    private func setMicrophoneStatus(_ status: MicrophoneStatus) {
        guard microphoneStatus != status else { return }
        microphoneStatus = status
        if status == .lost {
            sounds?.play(.cancel)
        }
    }

    /// Nothing is left to record. What was captured is finalized and saved first, and only
    /// then is the failure published, so the app can report a recording that is already safe
    /// and transcribe it. `failed` is the session the failure belongs to: one that ended
    /// meanwhile is not taken for a newer one.
    private func captureFailed(_ error: Error, in failed: RecordingSession) async {
        guard let session, session === failed else { return }
        let stopped = await stop()
        state = .failed(error.localizedDescription)
        lastFailure = error.localizedDescription
        if let stopped {
            autoStoppedMeeting = stopped
        }
    }

    func receiveMicrophoneLevel(_ level: Float) {
        guard session != nil else { return }
        // Capture goes on while the microphone is off; the meter must not suggest that
        // anything of it reaches the track.
        microphoneLevel = isMicrophoneOn ? Self.smoothed(microphoneLevel, toward: level) : 0
    }

    private func receiveSystemLevel(_ level: Float) {
        guard session != nil else { return }
        systemLevel = Self.smoothed(systemLevel, toward: level)
    }

    private func systemAudioWentSilent() {
        guard session != nil, systemAudioStatus == .capturing else { return }
        systemAudioStatus = .silentPermissionLikelyDenied
    }

    private func systemAudioResumed() {
        guard session != nil, systemAudioStatus == .silentPermissionLikelyDenied else { return }
        systemAudioStatus = .capturing
    }

    private func startTicker(startedAt: TimeInterval) {
        ticker?.cancel()
        var tick = 0
        ticker = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                self.checkForSleep()
                self.elapsed = max(0, self.uptime.uptime() - startedAt) + (self.session?.sleptSeconds ?? 0)
                tick += 1
                // Every 30 s: cheap enough to poll, and neither check has to depend on a
                // notification arriving, the same caution the wake observer's own doc
                // comment gives for sleep.
                if tick % 120 == 0 {
                    self.checkRecordingHealth()
                }
                try? await Task.sleep(for: .milliseconds(250))
            }
        }
    }

    // MARK: - Sleep

    /// A sleep stops capture and both clocks the tracks are measured on. How long it
    /// lasted is read from the clocks themselves, in `checkForSleep`; this notification
    /// only asks for that reading sooner than the ticker would. macOS does not wait for an
    /// app's main queue to drain before freezing the process, so nothing that has to be
    /// right may depend on a notification arriving at all.
    private func startWakeWatch() {
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.handleWake() }
        }
    }

    /// A sleep does not just stop the clocks; `SCStream` does not reliably survive one
    /// either, so the screen track is rebuilt rather than merely padded like audio is.
    private func handleWake() {
        checkForSleep()
        guard let capture = session?.screenCapture else { return }
        Task { await Self.offMainActor { capture.restart() } }
    }

    private func stopWakeWatch() {
        guard let wakeObserver else { return }
        NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver)
        self.wakeObserver = nil
    }

    /// Pads the tracks for whatever the clocks say the Mac has slept since the last
    /// reading. Called from the ticker four times a second, on a wake, and before any
    /// position is read, so a notification that never arrives costs nothing.
    func checkForSleep() {
        guard let session, !session.isEnding else { return }
        let slept = continuousUptime() - uptime.uptime() - sleptBeforeStart - session.sleptSeconds
        guard slept >= Self.minimumSleepSeconds else { return }
        padForSleep(seconds: slept)
    }

    /// Fills both tracks with silence for a slept interval. `mach_absolute_time`,
    /// `ProcessInfo.systemUptime` and the Core Audio host clock all stand still while the
    /// Mac sleeps, while `mach_continuous_time` keeps counting. Without this the tracks
    /// would butt-join the two sides of the sleep, so the files would look continuous
    /// while minutes were missing and every later timestamp, marked moments included,
    /// would be early by the slept time.
    ///
    /// The silence is queued on each writer rather than waited for: a lid closed overnight
    /// is hours of it, and writing that from the main actor froze the app on wake. It must
    /// fit on the disk with room to spare; when it does not, the recording ends here with
    /// everything before the sleep saved, rather than filling the disk with silence.
    func padForSleep(seconds: TimeInterval) {
        guard let session, !session.isEnding, seconds > 0 else { return }
        let padded = min(seconds, MeetingTrackWriter.maximumSleepPad)
        let tracks = session.systemWriter == nil ? 1 : 2
        if let available = availableSpace(),
           !Self.sleepPadFits(seconds: padded, tracks: tracks, availableBytes: available) {
            session.isEnding = true
            logger.error("The Mac slept for \(seconds, privacy: .public) s and the disk cannot hold the pad; ending the recording")
            Task { await self.captureFailed(MeetingErrors.lowDiskSpaceWhileRecording(available), in: session) }
            return
        }
        session.microphoneWriter.enqueueSilence(seconds: padded)
        session.systemWriter?.enqueueSilence(seconds: padded)
        session.sleptSeconds += padded
        logger.notice("The Mac slept for \(seconds, privacy: .public) s; the tracks were padded by \(padded, privacy: .public) s")
    }

    /// Whether `seconds` of silence on `tracks` tracks leaves at least
    /// `minimumFreeBytesToContinue` on a disk with `availableBytes` free.
    static func sleepPadFits(seconds: TimeInterval, tracks: Int, availableBytes: Int64) -> Bool {
        let perTrack = Int64(min(max(0, seconds), MeetingTrackWriter.maximumSleepPad) * Double(MeetingTrackWriter.bytesPerSecond))
        return availableBytes - perTrack * Int64(max(1, tracks)) >= minimumFreeBytesToContinue
    }

    // MARK: - Disk space

    /// The error for too little room to record, or nil when there is enough. A capacity
    /// that cannot be read never refuses a recording.
    static func lowSpaceError(availableBytes: Int64?) -> SlovoError? {
        guard let availableBytes, availableBytes < minimumFreeBytes else { return nil }
        return MeetingErrors.lowDiskSpace(availableBytes)
    }

    /// What the meetings folder's volume has left for files worth keeping.
    nonisolated static func availableBytes(at folder: URL) -> Int64? {
        let values = try? folder.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        return values?.volumeAvailableCapacityForImportantUsage
    }

    /// Rises quickly and falls slowly, so the meter reads like speech rather than flicker.
    static func smoothed(_ current: Float, toward target: Float) -> Float {
        let factor: Float = target > current ? 0.6 : 0.25
        return current + (target - current) * factor
    }

    private static func ensureMicrophoneAccess() async throws {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            return
        case .notDetermined:
            guard await AVCaptureDevice.requestAccess(for: .audio) else {
                throw MeetingErrors.microphoneDenied()
            }
        case .denied, .restricted:
            throw MeetingErrors.microphoneDenied()
        @unknown default:
            throw MeetingErrors.microphoneDenied()
        }
    }

    /// Capture and Core Audio start-up can block for a noticeable moment while devices
    /// wake up, so it runs off the main thread.
    private static func offMainActor<T>(_ work: @escaping () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(with: Result(catching: work))
            }
        }
    }

    private static func offMainActor<T>(_ work: @escaping () -> T) async -> T {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(returning: work())
            }
        }
    }
}

/// A microphone switch made during a recording. Each switch is a new value, so switching
/// off, on and off again is reported three times.
public struct MicrophoneSwitch: Equatable, Sendable {
    public let id = UUID()
    public let isOn: Bool

    public init(isOn: Bool) {
        self.isOn = isOn
    }
}

/// The capture objects of one recording.
private final class RecordingSession: @unchecked Sendable {
    struct Result {
        var microphoneFrames: Int64
        var systemFrames: Int64
        var heardSystemAudio: Bool
        var wroteScreenRecording: Bool
    }

    let meetingID: UUID
    let startedAt: TimeInterval
    /// The host time both track writers count frames from.
    let originHostTime: UInt64
    let microphone: MeetingMicrophoneCapture
    let microphoneWriter: MeetingTrackWriter
    /// Cleared when the optional system track fails: the meeting goes on without it.
    var systemTap: SystemAudioTap?
    var systemWriter: MeetingTrackWriter?
    /// Cleared when the optional screen track fails, is revoked, or is stopped: the
    /// meeting goes on without it, exactly like a lost system-audio track.
    var screenCapture: MeetingScreenCapture?
    var screenWriter: MeetingScreenWriter?
    /// Whether the microphone track is closed for good. A device that merely went away
    /// leaves it false: that writer is still open and the capture is still retrying.
    var microphoneTrackEnded = false
    /// Silence added to both tracks for sleeps, which neither the uptime nor the host
    /// clock counted; every position taken from those clocks adds it back.
    var sleptSeconds: TimeInterval = 0
    /// Set once the recording is on its way to end by itself (the disk is nearly full), so
    /// nothing more is padded or checked in the moment before it stops.
    var isEnding = false
    /// Keeps the Mac awake while the meeting records; ended once, when it stops.
    private var activity: NSObjectProtocol?

    init(meetingID: UUID, startedAt: TimeInterval, originHostTime: UInt64, microphone: MeetingMicrophoneCapture,
         microphoneWriter: MeetingTrackWriter, systemTap: SystemAudioTap?, systemWriter: MeetingTrackWriter?,
         screenCapture: MeetingScreenCapture? = nil, screenWriter: MeetingScreenWriter? = nil,
         activity: NSObjectProtocol?) {
        self.meetingID = meetingID
        self.startedAt = startedAt
        self.originHostTime = originHostTime
        self.microphone = microphone
        self.microphoneWriter = microphoneWriter
        self.systemTap = systemTap
        self.systemWriter = systemWriter
        self.screenCapture = screenCapture
        self.screenWriter = screenWriter
        self.activity = activity
    }

    /// Lets the Mac idle-sleep again. Safe to call more than once.
    func endActivity() {
        guard let activity else { return }
        self.activity = nil
        ProcessInfo.processInfo.endActivity(activity)
    }

    /// Stops capture first, then closes the files once queued writes are done. In app
    /// mode `systemTap` is nil and `screenCapture` is the source of `system.caf` instead.
    func finish() -> Result {
        microphone.stop()
        let heard = systemTap?.heardSignal ?? screenCapture?.heardAudioSignal ?? false
        systemTap?.stop()
        screenCapture?.stop()
        return Result(microphoneFrames: microphoneWriter.finish(),
                      systemFrames: systemWriter?.finish() ?? 0,
                      heardSystemAudio: heard,
                      wroteScreenRecording: screenWriter?.finish() ?? false)
    }
}

/// `mach_continuous_time` keeps counting while the Mac sleeps, which `mach_absolute_time`
/// and the `ProcessInfo.systemUptime` built on it do not; how far the two have drifted
/// apart is how long the machine has been away. Unlike the wall clock, neither can be
/// stepped by a time sync on wake.
enum MachContinuousClock {
    private static let timebase: mach_timebase_info_data_t = {
        var info = mach_timebase_info_data_t()
        mach_timebase_info(&info)
        return info
    }()

    /// Seconds since boot, sleep included.
    static func seconds() -> TimeInterval {
        // Converted in Double: the tick count times the numerator overflows UInt64 on a
        // machine that has been up for weeks.
        Double(mach_continuous_time()) * Double(timebase.numer) / Double(timebase.denom) / 1_000_000_000
    }
}
