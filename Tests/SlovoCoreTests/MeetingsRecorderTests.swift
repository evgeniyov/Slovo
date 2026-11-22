import CoreAudio
import Foundation
import XCTest
@testable import SlovoCore

/// `MeetingRecorder.markMoment` on a recording session without capture hardware, with a
/// host clock the test moves.
@MainActor
final class MeetingsRecorderTests: XCTestCase {
    private var temporary: URL!
    private var store: MeetingStore!
    private var suiteName = ""
    private var defaults: UserDefaults!

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoMeetingsRecorderTests-\(UUID().uuidString)", isDirectory: true)
        let directories = AppDirectories(base: temporary)
        try directories.createFolders()
        store = MeetingStore(directories: directories)
        suiteName = "slovo.tests.meetings.recorder.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suiteName)
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    func testAppCaptureRespectsDisabledSystemAudio() async {
        let settings = AppSettings(defaults: defaults)
        settings.recordScreen = true
        settings.screenCaptureTarget = .application(bundleID: "test.call")
        settings.recordSystemAudio = false
        let recorder = MeetingRecorder(store: store, settings: settings)
        var systemStarts = 0
        var appAudioRequests: [Bool] = []

        _ = await recorder.startAdditionalTracks(startSystem: {
            systemStarts += 1
            return nil
        }, startScreen: { captureAppAudio in
            appAudioRequests.append(captureAppAudio)
            return nil
        })

        XCTAssertEqual(systemStarts, 0)
        XCTAssertEqual(appAudioRequests, [false], "screen-only recording must never open an audio writer")
        XCTAssertEqual(recorder.systemAudioStatus, .off)
    }

    func testFailedAppCaptureNeverFallsBackToWholeComputerAudio() async {
        let settings = AppSettings(defaults: defaults)
        settings.recordScreen = true
        settings.screenCaptureTarget = .application(bundleID: "test.call")
        settings.recordSystemAudio = true
        let recorder = MeetingRecorder(store: store, settings: settings)
        var systemStarts = 0
        var appAudioRequests: [Bool] = []

        let tracks = await recorder.startAdditionalTracks(startSystem: {
            systemStarts += 1
            return nil
        }, startScreen: { captureAppAudio in
            appAudioRequests.append(captureAppAudio)
            return nil // Denied permission or the selected app no longer running.
        })

        XCTAssertEqual(systemStarts, 0, "a failed scoped capture must not record unrelated apps")
        XCTAssertEqual(appAudioRequests, [true])
        XCTAssertNil(tracks.0)
        XCTAssertNil(tracks.1)
        guard case .unavailable = recorder.systemAudioStatus else {
            return XCTFail("the missing audio track must be visible")
        }
    }

    func testWholeDisplayAndAudioOnlyMeetingsStillStartSystemAudio() async {
        let settings = AppSettings(defaults: defaults)
        settings.recordSystemAudio = true
        for screenEnabled in [false, true] {
            settings.recordScreen = screenEnabled
            // An old app selection must have no effect while screen recording is off.
            settings.screenCaptureTarget = screenEnabled ? .display : .application(bundleID: "test.call")
            let recorder = MeetingRecorder(store: store, settings: settings)
            var systemStarts = 0
            var screenRequests: [Bool] = []
            _ = await recorder.startAdditionalTracks(startSystem: {
                systemStarts += 1
                return nil
            }, startScreen: { captureAppAudio in
                screenRequests.append(captureAppAudio)
                return nil
            })
            XCTAssertEqual(systemStarts, 1)
            XCTAssertEqual(screenRequests, screenEnabled ? [false] : [])
        }
    }

    func testMarkMomentIsNilUnlessRecording() {
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults))
        XCTAssertNil(recorder.markMoment())
        XCTAssertNil(recorder.markMoment(note: "важно"))
        XCTAssertNil(recorder.lastMarkedMoment)
    }

    func testMomentTimeComesFromTheTracksHostTimeOrigin() throws {
        let meeting = try store.create(title: "Коворк")
        // Uptime stands still, so a time taken from uptime would be zero.
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let clock = HostClock()
        recorder.hostTime = { clock.now() }
        try recorder.startSessionWithoutCapture(meetingID: meeting.id, originHostTime: clock.origin)
        XCTAssertTrue(recorder.isMeetingRecording)

        clock.seconds = 12.5
        let moment = try XCTUnwrap(recorder.markMoment(note: "  Решение\nпринято "))

        XCTAssertEqual(moment.time, 12.5, accuracy: 0.001)
        XCTAssertEqual(moment.note, "Решение принято")
        XCTAssertEqual(moment.source, .recording)
        XCTAssertEqual(recorder.lastMarkedMoment, moment)
        XCTAssertEqual(store.meeting(id: meeting.id)?.highlights, [moment])
        XCTAssertNotNil(recorder.stopImmediately())
    }

    func testRecordingIsRefusedWithTooLittleFreeSpace() throws {
        XCTAssertNil(MeetingRecorder.lowSpaceError(availableBytes: nil), "an unreadable volume never refuses")
        XCTAssertNil(MeetingRecorder.lowSpaceError(availableBytes: MeetingRecorder.minimumFreeBytes))
        XCTAssertNil(MeetingRecorder.lowSpaceError(availableBytes: 40 * 1024 * 1024 * 1024))

        let refusal = try XCTUnwrap(MeetingRecorder.lowSpaceError(availableBytes: 300 * 1024 * 1024))
        XCTAssertNotEqual(refusal.message, "meetings.error.lowDiskSpace", "the message must be localized")
        XCTAssertFalse(refusal.message.isEmpty)
    }

    func testASleepPadsTheTrackAndKeepsMarkedMomentsTrue() throws {
        let meeting = try store.create(title: "Коворк")
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let clock = HostClock()
        recorder.hostTime = { clock.now() }
        let writer = try recorder.startSessionWithoutCapture(meetingID: meeting.id,
                                                             originHostTime: clock.origin).microphone

        clock.seconds = 12.5
        let before = try XCTUnwrap(recorder.markMoment(note: "до сна"))
        XCTAssertEqual(before.time, 12.5, accuracy: 0.001)

        // Thirty seconds of sleep, which neither the host clock nor uptime counted.
        recorder.padForSleep(seconds: 30)
        XCTAssertEqual(Double(writer.frameCount), 30 * 16_000, accuracy: 20, "the slept interval is on the track")

        // The host clock resumes where it stopped, so a mark right after the wake belongs
        // thirty seconds further along than that clock alone would say.
        let afterWake = try XCTUnwrap(recorder.markMoment(note: "после сна"))
        XCTAssertEqual(afterWake.time, 42.5, accuracy: 0.001)
        clock.seconds = 20
        let later = try XCTUnwrap(recorder.markMoment())
        XCTAssertEqual(later.time, 50, accuracy: 0.001)

        let stopped = try XCTUnwrap(recorder.stopImmediately())
        XCTAssertEqual(stopped.duration, 30, accuracy: 0.001, "the sleep counts towards the length")
        XCTAssertEqual(stopped.highlights.count, 3)
    }

    func testAWriteFailureStopsTheRecordingAndIsPublished() async throws {
        let meeting = try store.create(title: "Коворк")
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let writer = try recorder.startSessionWithoutCapture(meetingID: meeting.id,
                                                             originHostTime: AudioGetCurrentHostTime()).microphone
        writer.writeBuffer = { _, _ in throw SlovoError("the disk is full") }

        writer.append([Float](repeating: 0.2, count: 16_000))

        let reported = await DictationFakes.waitUntil { recorder.lastFailure != nil }
        XCTAssertTrue(reported, "a track that cannot be written must end the recording")
        guard case let .failed(message) = recorder.state else {
            return XCTFail("the recorder must report the failure, got \(recorder.state)")
        }
        XCTAssertTrue(message.contains("the disk is full"), message)
        XCTAssertFalse(recorder.isMeetingRecording)
        XCTAssertEqual(store.meeting(id: meeting.id)?.status, .recorded, "what was captured is still saved")
    }

    func testMarksWithinTwoSecondsCountAsOne() throws {
        let meeting = try store.create(title: "Коворк")
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let clock = HostClock()
        recorder.hostTime = { clock.now() }
        try recorder.startSessionWithoutCapture(meetingID: meeting.id, originHostTime: clock.origin)

        clock.seconds = 12.5
        let first = try XCTUnwrap(recorder.markMoment())
        clock.seconds = 13.9
        XCTAssertEqual(recorder.markMoment(note: "повтор"), first)
        clock.seconds = 15
        let second = try XCTUnwrap(recorder.markMoment())
        XCTAssertNotEqual(second.id, first.id)
        XCTAssertEqual(second.time, 15, accuracy: 0.001)
        clock.seconds = 16.9
        XCTAssertEqual(recorder.markMoment(), second)

        XCTAssertEqual(store.meeting(id: meeting.id)?.highlights.map(\.id), [first.id, second.id])
        XCTAssertEqual(recorder.lastMarkedMoment, second)

        let stopped = try XCTUnwrap(recorder.stopImmediately())
        XCTAssertEqual(stopped.status, .recorded)
        XCTAssertEqual(stopped.highlights.map(\.id), [first.id, second.id])
        XCTAssertEqual(recorder.state, .idle)
        XCTAssertNil(recorder.markMoment())

        // A new recording starts without the previous one's last moment.
        let next = try store.create(title: "Созвон")
        try recorder.startSessionWithoutCapture(meetingID: next.id, originHostTime: clock.origin)
        let fresh = try XCTUnwrap(recorder.markMoment())
        XCTAssertEqual(fresh.time, 16.9, accuracy: 0.001)
        XCTAssertEqual(store.meeting(id: next.id)?.highlights, [fresh])
        XCTAssertNotNil(recorder.stopImmediately())
    }

    /// A Bluetooth handoff macOS takes minutes over, or an interface left unplugged, must
    /// cost the microphone track and nothing else: the meeting, the system track and the
    /// timer all go on, and the microphone rejoins by itself when a device returns.
    func testALostMicrophoneKeepsTheMeetingAndTheSystemTrackRecording() throws {
        let meeting = try store.create(title: "Коворк")
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let clock = HostClock()
        recorder.hostTime = { clock.now() }
        let writers = try recorder.startSessionWithoutCapture(meetingID: meeting.id, originHostTime: clock.origin,
                                                              systemTrack: true)
        let system = try XCTUnwrap(writers.system)

        recorder.microphoneLost(MeetingErrors.noMicrophone())

        XCTAssertEqual(recorder.microphoneStatus, .lost)
        XCTAssertTrue(recorder.isMeetingRecording, "an outage on one track must not end the meeting")
        XCTAssertNil(recorder.lastFailure)
        XCTAssertEqual(recorder.microphoneLevel, 0)
        // System audio keeps being written for the whole outage.
        system.append([Float](repeating: 0.2, count: 16_000), hostTime: clock.now())
        XCTAssertEqual(system.frameCount, 16_000)

        recorder.microphoneBack()
        XCTAssertEqual(recorder.microphoneStatus, .capturing)
        clock.seconds = 30
        let moment = try XCTUnwrap(recorder.markMoment())
        XCTAssertEqual(moment.time, 30, accuracy: 0.001, "the returning audio keeps its place on the timeline")
        XCTAssertNotNil(recorder.stopImmediately())
    }

    /// The pad may not hang on `willSleep`, which macOS enqueues on the main queue without
    /// waiting for it, nor on `didWake`: the clocks alone say what happened.
    func testAWakeWithNoNotificationStillPadsTheTracks() throws {
        let meeting = try store.create(title: "Коворк")
        let clocks = SleepingClocks(awake: 1_000, slept: 5_000)
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults), uptime: clocks)
        recorder.continuousUptime = { clocks.continuousUptime() }
        let clock = HostClock()
        recorder.hostTime = { clock.now() }
        let writers = try recorder.startSessionWithoutCapture(meetingID: meeting.id, originHostTime: clock.origin)

        clock.seconds = 10
        clocks.stayAwake(for: 10)
        // Two minutes of sleep, and not a single notification about it.
        clocks.sleep(for: 120)
        recorder.checkForSleep()

        XCTAssertEqual(Double(writers.microphone.frameCount), 120 * 16_000, accuracy: 20,
                       "the slept interval is on the track")
        let moment = try XCTUnwrap(recorder.markMoment())
        XCTAssertEqual(moment.time, 130, accuracy: 0.001)
        // Reading the clocks again without another sleep pads nothing.
        recorder.checkForSleep()
        XCTAssertEqual(Double(writers.microphone.frameCount), 120 * 16_000, accuracy: 20)

        let stopped = try XCTUnwrap(recorder.stopImmediately())
        XCTAssertEqual(stopped.duration, 130, accuracy: 0.001)
    }

    /// System audio is the optional track: at an in-person meeting it records silence and
    /// is deleted at the end anyway, so losing it must not cost the microphone.
    func testASystemTrackWriteFailureLeavesTheMicrophoneRecording() async throws {
        var meeting = try store.create(title: "Коворк")
        meeting.hasSystemAudio = true
        store.update(meeting)
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let writers = try recorder.startSessionWithoutCapture(meetingID: meeting.id,
                                                              originHostTime: AudioGetCurrentHostTime(),
                                                              systemTrack: true)
        let system = try XCTUnwrap(writers.system)
        system.writeBuffer = { _, _ in throw SlovoError("the disk is full") }

        system.append([Float](repeating: 0.2, count: 16_000))

        let degraded = await DictationFakes.waitUntil {
            if case .unavailable = recorder.systemAudioStatus { return true }
            return false
        }
        XCTAssertTrue(degraded, "a failed system track must show as unavailable")
        XCTAssertTrue(recorder.isMeetingRecording, "the microphone track was never in trouble")
        XCTAssertNil(recorder.lastFailure)
        XCTAssertEqual(recorder.microphoneStatus, .capturing)
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.systemAudioURL(for: meeting.id).path),
                       "the partial system track is dropped")
        XCTAssertEqual(store.meeting(id: meeting.id)?.hasSystemAudio, false)

        writers.microphone.append([Float](repeating: 0.2, count: 16_000))
        XCTAssertEqual(writers.microphone.frameCount, 16_000, "the microphone keeps recording")
        XCTAssertNotNil(recorder.stopImmediately())
    }

    /// What the system track recorded before a failed write is the other side of the call up
    /// to that moment: it stays on disk and in the meeting.
    func testASystemTrackThatFailsLaterKeepsWhatItRecorded() async throws {
        var meeting = try store.create(title: "Созвон")
        meeting.hasSystemAudio = true
        store.update(meeting)
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let writers = try recorder.startSessionWithoutCapture(meetingID: meeting.id,
                                                              originHostTime: AudioGetCurrentHostTime(),
                                                              systemTrack: true)
        let system = try XCTUnwrap(writers.system)
        system.append([Float](repeating: 0.3, count: 16_000))
        XCTAssertEqual(system.frameCount, 16_000)

        system.writeBuffer = { _, _ in throw SlovoError("the disk is full") }
        system.append([Float](repeating: 0.3, count: 16_000))

        let degraded = await DictationFakes.waitUntil {
            if case .unavailable = recorder.systemAudioStatus { return true }
            return false
        }
        XCTAssertTrue(degraded)
        XCTAssertTrue(recorder.isMeetingRecording)
        let stopped = try XCTUnwrap(recorder.stopImmediately())
        XCTAssertTrue(stopped.hasSystemAudio, "the partial track is kept")
        // Closing is idempotent and waits for the recorder's own close, wherever it is.
        system.finish()
        XCTAssertTrue(FileManager.default.fileExists(atPath: store.systemAudioURL(for: meeting.id).path))
        let samples = try MeetingTrackReader(url: store.systemAudioURL(for: meeting.id)).read(32_000)
        XCTAssertEqual(samples.count, 16_000)
        XCTAssertEqual(MeetingAudioMath.rms(samples), 0.3, accuracy: 0.001)
    }

    /// A disk that is nearly full ends the recording before the writes start failing:
    /// what was recorded is saved, the failure is published, and the meeting is handed on
    /// to be transcribed like one the owner stopped.
    func testANearlyFullDiskEndsTheRecordingSavedAndHandsItOn() async throws {
        let meeting = try store.create(title: "Коворк")
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let writers = try recorder.startSessionWithoutCapture(meetingID: meeting.id,
                                                              originHostTime: AudioGetCurrentHostTime())
        writers.microphone.append([Float](repeating: 0.2, count: 16_000))
        recorder.availableSpace = { 40 * 1024 * 1024 }

        recorder.checkRecordingHealth()

        let ended = await DictationFakes.waitUntil { recorder.autoStoppedMeeting != nil }
        XCTAssertTrue(ended)
        XCTAssertEqual(recorder.autoStoppedMeeting?.id, meeting.id)
        XCTAssertEqual(recorder.autoStoppedMeeting?.status, .recorded)
        XCTAssertFalse(recorder.isMeetingRecording)
        XCTAssertNotNil(recorder.lastFailure)
        XCTAssertEqual(store.meeting(id: meeting.id)?.status, .recorded)
        XCTAssertEqual(try MeetingTrackReader(url: store.microphoneAudioURL(for: meeting.id)).read(32_000).count, 16_000)
    }

    /// A sleep is padded with silence of its own length, queued on the writers rather than
    /// written from the main actor; a pad the disk cannot hold ends the recording instead.
    func testASleepTheDiskCannotHoldEndsTheRecordingInsteadOfPadding() async throws {
        XCTAssertTrue(MeetingRecorder.sleepPadFits(seconds: 3600, tracks: 2, availableBytes: 10 * 1024 * 1024 * 1024))
        XCTAssertFalse(MeetingRecorder.sleepPadFits(seconds: 3600, tracks: 1, availableBytes: 150 * 1024 * 1024))
        XCTAssertTrue(MeetingRecorder.sleepPadFits(seconds: 0, tracks: 2, availableBytes: MeetingRecorder.minimumFreeBytesToContinue))

        let meeting = try store.create(title: "Коворк")
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let writers = try recorder.startSessionWithoutCapture(meetingID: meeting.id,
                                                              originHostTime: AudioGetCurrentHostTime())
        recorder.availableSpace = { 150 * 1024 * 1024 }

        recorder.padForSleep(seconds: 3600)

        XCTAssertEqual(writers.microphone.frameCount, 0, "no hour of silence on a disk that cannot hold it")
        let ended = await DictationFakes.waitUntil { recorder.autoStoppedMeeting != nil }
        XCTAssertTrue(ended)
        XCTAssertFalse(recorder.isMeetingRecording)
        let stopped = try XCTUnwrap(store.meeting(id: meeting.id))
        XCTAssertEqual(stopped.duration, 0, accuracy: 0.001, "the unpadded sleep is not counted in the length")
    }

    /// Both tracks can go, one after the other. The meeting must not outlive the second
    /// one: a session with no writers left counts time and claims a recording over two
    /// files that stopped growing.
    func testTheMicrophoneThenTheSystemTrackFailingEndsTheMeeting() async throws {
        var meeting = try store.create(title: "Коворк")
        meeting.hasSystemAudio = true
        store.update(meeting)
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let writers = try recorder.startSessionWithoutCapture(meetingID: meeting.id,
                                                              originHostTime: AudioGetCurrentHostTime(),
                                                              systemTrack: true)
        let system = try XCTUnwrap(writers.system)

        writers.microphone.writeBuffer = { _, _ in throw SlovoError("the disk is full") }
        writers.microphone.append([Float](repeating: 0.2, count: 16_000))

        let degraded = await DictationFakes.waitUntil { recorder.microphoneStatus == .lost }
        XCTAssertTrue(degraded, "system audio alone still keeps the meeting")
        XCTAssertTrue(recorder.isMeetingRecording)
        XCTAssertNil(recorder.lastFailure)

        system.writeBuffer = { _, _ in throw SlovoError("the disk is full") }
        system.append([Float](repeating: 0.2, count: 16_000))

        let ended = await DictationFakes.waitUntil { recorder.lastFailure != nil }
        XCTAssertTrue(ended, "with both tracks gone there is nothing left to record")
        XCTAssertFalse(recorder.isMeetingRecording)
        XCTAssertNil(recorder.markMoment(), "the session is closed, not left running")
        XCTAssertEqual(store.meeting(id: meeting.id)?.status, .recorded, "what was captured is still saved")
        XCTAssertEqual(store.meeting(id: meeting.id)?.hasSystemAudio, false)
    }

    /// And in the other order: the system track going first must not leave the
    /// microphone's own failure with a session to keep alive.
    func testTheSystemThenTheMicrophoneTrackFailingEndsTheMeeting() async throws {
        var meeting = try store.create(title: "Коворк")
        meeting.hasSystemAudio = true
        store.update(meeting)
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let writers = try recorder.startSessionWithoutCapture(meetingID: meeting.id,
                                                              originHostTime: AudioGetCurrentHostTime(),
                                                              systemTrack: true)
        let system = try XCTUnwrap(writers.system)

        system.writeBuffer = { _, _ in throw SlovoError("the disk is full") }
        system.append([Float](repeating: 0.2, count: 16_000))

        let degraded = await DictationFakes.waitUntil {
            if case .unavailable = recorder.systemAudioStatus { return true }
            return false
        }
        XCTAssertTrue(degraded)
        XCTAssertTrue(recorder.isMeetingRecording, "the microphone was never in trouble")

        writers.microphone.writeBuffer = { _, _ in throw SlovoError("the disk is full") }
        writers.microphone.append([Float](repeating: 0.2, count: 16_000))

        let ended = await DictationFakes.waitUntil { recorder.lastFailure != nil }
        XCTAssertTrue(ended, "with both tracks gone there is nothing left to record")
        XCTAssertFalse(recorder.isMeetingRecording)
        XCTAssertNil(recorder.markMoment())
        XCTAssertEqual(store.meeting(id: meeting.id)?.status, .recorded, "what was captured is still saved")
    }

    /// A device that only went away is not a closed track: its writer is open and the
    /// capture keeps trying, so the system track failing during the outage leaves a
    /// meeting that is still recording and picks the microphone back up.
    func testASystemTrackFailureDuringALostMicrophoneKeepsTheMeeting() async throws {
        var meeting = try store.create(title: "Коворк")
        meeting.hasSystemAudio = true
        store.update(meeting)
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000))
        let writers = try recorder.startSessionWithoutCapture(meetingID: meeting.id,
                                                              originHostTime: AudioGetCurrentHostTime(),
                                                              systemTrack: true)
        let system = try XCTUnwrap(writers.system)

        recorder.microphoneLost(MeetingErrors.noMicrophone())
        system.writeBuffer = { _, _ in throw SlovoError("the disk is full") }
        system.append([Float](repeating: 0.2, count: 16_000))

        let degraded = await DictationFakes.waitUntil {
            if case .unavailable = recorder.systemAudioStatus { return true }
            return false
        }
        XCTAssertTrue(degraded)
        XCTAssertTrue(recorder.isMeetingRecording, "a recoverable outage keeps the meeting")
        XCTAssertNil(recorder.lastFailure)

        recorder.microphoneBack()
        XCTAssertEqual(recorder.microphoneStatus, .capturing)
        writers.microphone.append([Float](repeating: 0.2, count: 16_000))
        XCTAssertEqual(writers.microphone.frameCount, 16_000, "the track was never closed")

        let stopped = try XCTUnwrap(recorder.stopImmediately())
        XCTAssertEqual(stopped.status, .recorded)
    }

    /// The panel is the only place a lost microphone is written, and the window holding it
    /// is closed for the whole meeting, so the outage is heard. Once: the capture retries
    /// until the microphone returns, and a sound per attempt would fill the room.
    func testALostMicrophoneIsHeardOnceOnTheTransition() throws {
        let meeting = try store.create(title: "Коворк")
        let sounds = DictationFakes.Sounds()
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000), sounds: sounds)
        try recorder.startSessionWithoutCapture(meetingID: meeting.id, originHostTime: AudioGetCurrentHostTime())
        XCTAssertEqual(sounds.played, [], "a meeting that starts is not an outage")

        recorder.microphoneLost(MeetingErrors.noMicrophone())
        recorder.microphoneLost(MeetingErrors.noMicrophone())
        XCTAssertEqual(sounds.played, [.cancel], "every later retry is the same outage")

        recorder.microphoneBack()
        XCTAssertEqual(sounds.played, [.cancel], "a microphone that comes back is silent")
        recorder.microphoneLost(MeetingErrors.noMicrophone())
        XCTAssertEqual(sounds.played, [.cancel, .cancel], "losing it again is a new outage")

        XCTAssertNotNil(recorder.stopImmediately())
        XCTAssertEqual(recorder.microphoneStatus, .capturing)
        XCTAssertEqual(sounds.played, [.cancel, .cancel], "stopping clears the status without a sound")
    }

    /// «Record the microphone in meetings» decides how each recording starts, and a
    /// recording that starts with it off never has the room on its microphone track.
    func testTheMicrophoneStartsAsTheSettingSays() throws {
        let settings = AppSettings(defaults: defaults)
        XCTAssertTrue(settings.recordMicrophone, "on unless it was turned off")
        let recorder = MeetingRecorder(store: store, settings: settings, uptime: FrozenUptime(value: 1_000))
        XCTAssertNil(recorder.recordingTracks)

        let first = try store.create(title: "Созвон")
        let on = try recorder.startSessionWithoutCapture(meetingID: first.id, originHostTime: AudioGetCurrentHostTime(),
                                                         systemTrack: true)
        XCTAssertTrue(recorder.isMicrophoneOn)
        XCTAssertFalse(on.microphone.isMuted)
        XCTAssertEqual(recorder.recordingTracks, .microphoneAndSystem)
        XCTAssertNotNil(recorder.stopImmediately())
        XCTAssertNil(recorder.recordingTracks)

        settings.recordMicrophone = false
        let second = try store.create(title: "Созвон из переговорки")
        let off = try recorder.startSessionWithoutCapture(meetingID: second.id, originHostTime: AudioGetCurrentHostTime(),
                                                          systemTrack: true)
        XCTAssertFalse(recorder.isMicrophoneOn)
        XCTAssertTrue(off.microphone.isMuted)
        XCTAssertEqual(recorder.recordingTracks, .systemOnly)
        XCTAssertNil(recorder.lastMicrophoneSwitch, "starting is not a switch")
        off.microphone.append([Float](repeating: 0.3, count: 16_000))
        XCTAssertEqual(off.microphone.frameCount, 16_000)

        // Switching during the recording changes that recording, not the setting.
        XCTAssertTrue(recorder.setMicrophoneOn(true))
        XCTAssertFalse(settings.recordMicrophone)
        XCTAssertNotNil(recorder.stopImmediately())
        let samples = try MeetingTrackReader(url: store.microphoneAudioURL(for: second.id)).read(32_000)
        XCTAssertEqual(samples.count, 16_000)
        XCTAssertEqual(MeetingAudioMath.rms(samples), 0)

        // Without system audio and without the microphone, nothing has sound going to it.
        let third = try store.create(title: "Тишина")
        try recorder.startSessionWithoutCapture(meetingID: third.id, originHostTime: AudioGetCurrentHostTime())
        XCTAssertFalse(recorder.isMicrophoneOn, "the next recording starts from the setting again")
        XCTAssertEqual(recorder.recordingTracks, .neither)
        XCTAssertNotNil(recorder.stopImmediately())
    }

    /// Off, on and off again in one call: the capture never stops, the microphone track
    /// gets silence of exactly the length it would have got sound, both tracks stay on one
    /// timeline, moments keep their place, and each switch is heard and published.
    func testSwitchingTheMicrophoneMidRecordingKeepsTheTimeline() throws {
        let meeting = try store.create(title: "Созвон")
        let sounds = DictationFakes.Sounds()
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults),
                                       uptime: FrozenUptime(value: 1_000), sounds: sounds)
        let clock = HostClock()
        recorder.hostTime = { clock.now() }
        let writers = try recorder.startSessionWithoutCapture(meetingID: meeting.id, originHostTime: clock.origin,
                                                              systemTrack: true)
        let microphone = writers.microphone
        let system = try XCTUnwrap(writers.system)
        let speech = [Float](repeating: 0.25, count: 16_000)
        let second = AudioConvertNanosToHostTime(1_000_000_000)
        func deliver(atSecond index: UInt64) {
            microphone.append(speech, hostTime: clock.origin + index * second)
            system.append(speech, hostTime: clock.origin + index * second)
        }

        deliver(atSecond: 0)
        recorder.receiveMicrophoneLevel(0.8)
        XCTAssertGreaterThan(recorder.microphoneLevel, 0)

        XCTAssertTrue(recorder.toggleMicrophone())
        XCTAssertFalse(recorder.isMicrophoneOn)
        XCTAssertEqual(recorder.microphoneLevel, 0)
        recorder.receiveMicrophoneLevel(0.8)
        XCTAssertEqual(recorder.microphoneLevel, 0, "the meter shows nothing while the track gets nothing")
        XCTAssertEqual(recorder.recordingTracks, .systemOnly)
        XCTAssertEqual(recorder.lastMicrophoneSwitch?.isOn, false)
        XCTAssertFalse(recorder.setMicrophoneOn(false), "switching to the state it is in does nothing")
        XCTAssertEqual(sounds.played, [.stop])

        // The capture keeps delivering; one buffer is late, as after a device switch.
        deliver(atSecond: 1)
        deliver(atSecond: 3)
        clock.seconds = 4
        let marked = try XCTUnwrap(recorder.markMoment())
        XCTAssertEqual(marked.time, 4, accuracy: 0.001, "moments keep their place while the microphone is off")

        XCTAssertTrue(recorder.setMicrophoneOn(true))
        let switchedOn = try XCTUnwrap(recorder.lastMicrophoneSwitch)
        XCTAssertTrue(switchedOn.isOn)
        XCTAssertEqual(recorder.recordingTracks, .microphoneAndSystem)
        deliver(atSecond: 4)

        XCTAssertTrue(recorder.toggleMicrophone())
        XCTAssertNotEqual(recorder.lastMicrophoneSwitch, switchedOn)
        XCTAssertEqual(recorder.lastMicrophoneSwitch?.isOn, false)
        XCTAssertEqual(sounds.played, [.stop, .start, .stop])
        XCTAssertTrue(recorder.isMeetingRecording, "switching never touches the recording itself")

        XCTAssertEqual(microphone.frameCount, system.frameCount, "both tracks keep one timeline")
        XCTAssertEqual(Double(microphone.frameCount), 5 * 16_000, accuracy: 20)
        XCTAssertNotNil(recorder.stopImmediately())
        XCTAssertFalse(recorder.toggleMicrophone(), "there is nothing to switch without a recording")
        XCTAssertEqual(sounds.played, [.stop, .start, .stop])

        let samples = try MeetingTrackReader(url: store.microphoneAudioURL(for: meeting.id)).read(5 * 16_000)
        XCTAssertEqual(MeetingAudioMath.rms(samples[500..<15_500]), 0.25, accuracy: 0.001)
        XCTAssertEqual(MeetingAudioMath.rms(samples[16_000..<64_000]), 0, accuracy: 1e-6, "the switched-off stretch")
        XCTAssertEqual(MeetingAudioMath.rms(samples[64_500..<79_500]), 0.25, accuracy: 0.001,
                       "speech after switching back is where it was said")
    }

    func testTracksFollowTheMicrophoneSwitchAndBothStatuses() {
        typealias Tracks = MeetingRecordingTracks
        XCTAssertEqual(Tracks(microphoneOn: true, microphoneStatus: .capturing, systemAudioStatus: .capturing),
                       .microphoneAndSystem)
        XCTAssertEqual(Tracks(microphoneOn: true, microphoneStatus: .capturing, systemAudioStatus: .silentPermissionLikelyDenied),
                       .microphoneAndSystem, "a call nobody has spoken in yet is still being recorded")
        XCTAssertEqual(Tracks(microphoneOn: true, microphoneStatus: .capturing, systemAudioStatus: .off), .microphoneOnly)
        XCTAssertEqual(Tracks(microphoneOn: true, microphoneStatus: .capturing, systemAudioStatus: .unavailable("нет")),
                       .microphoneOnly)
        XCTAssertEqual(Tracks(microphoneOn: false, microphoneStatus: .capturing, systemAudioStatus: .capturing), .systemOnly)
        XCTAssertEqual(Tracks(microphoneOn: true, microphoneStatus: .lost, systemAudioStatus: .capturing), .systemOnly)
        XCTAssertEqual(Tracks(microphoneOn: false, microphoneStatus: .capturing, systemAudioStatus: .off), .neither)
        XCTAssertEqual(Tracks(microphoneOn: true, microphoneStatus: .lost, systemAudioStatus: .unavailable("нет")), .neither)
    }

    /// The premise the padding rests on, checked against this Mac rather than assumed.
    func testTheContinuousClockCountsSleepAndTheAwakeClockDoesNot() {
        let uptime = SystemUptime()
        // Sample the awake clock first: with no sleep the continuous clock may only be
        // ahead by the time taken to make the second read.
        let awake = uptime.uptime()
        XCTAssertGreaterThanOrEqual(MachContinuousClock.seconds(), awake,
                                    "the continuous clock counts everything the awake one does, plus sleep")

        // While the Mac is awake the two must advance together, or a meeting would be
        // padded for a sleep that never happened.
        let awakeBefore = uptime.uptime()
        let continuousBefore = MachContinuousClock.seconds()
        Thread.sleep(forTimeInterval: 0.05)
        let drift = (MachContinuousClock.seconds() - continuousBefore) - (uptime.uptime() - awakeBefore)
        XCTAssertEqual(drift, 0, accuracy: 0.01)
        XCTAssertLessThan(drift, MeetingRecorder.minimumSleepSeconds)
    }
}

private struct FrozenUptime: UptimeProviding {
    let value: TimeInterval

    func uptime() -> TimeInterval {
        value
    }
}

/// An awake clock that a sleep stops and a continuous one that it does not, the pair the
/// recorder reads the slept interval from.
private final class SleepingClocks: UptimeProviding, @unchecked Sendable {
    private let lock = NSLock()
    private var awake: TimeInterval
    private var slept: TimeInterval

    init(awake: TimeInterval, slept: TimeInterval) {
        self.awake = awake
        self.slept = slept
    }

    func uptime() -> TimeInterval {
        lock.withLock { awake }
    }

    func continuousUptime() -> TimeInterval {
        lock.withLock { awake + slept }
    }

    func stayAwake(for seconds: TimeInterval) {
        lock.withLock { awake += seconds }
    }

    /// Only the continuous clock moves, which is what a sleeping Mac looks like.
    func sleep(for seconds: TimeInterval) {
        lock.withLock { slept += seconds }
    }
}

/// Host times at a chosen number of seconds after a fixed origin.
private final class HostClock {
    let origin = AudioGetCurrentHostTime()
    var seconds: Double = 0

    func now() -> UInt64 {
        origin + AudioConvertNanosToHostTime(UInt64(seconds * 1_000_000_000))
    }
}
