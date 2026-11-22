import Foundation
import XCTest
@testable import SlovoCore

/// `MeetingTranscriber` with a fake engine and a fake diarizer on synthetic tone tracks:
/// clips and lines, speakers, phases and progress, the transcript file, cancellation,
/// failures, re-runs, speakers-only runs, the speaker count hint and retention. Also the
/// idle behaviour of `MeetingRecorder`, which needs no hardware.
@MainActor
final class MeetingsTranscriberTests: XCTestCase {
    private var temporary: URL!
    private var store: MeetingStore!
    private var suiteName = ""
    private var defaults: UserDefaults!

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoMeetingsTranscriberTests-\(UUID().uuidString)", isDirectory: true)
        let directories = AppDirectories(base: temporary)
        try directories.createFolders()
        store = MeetingStore(directories: directories)
        store.transcriptLanguage = { .ru }
        suiteName = "slovo.tests.meetings.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suiteName)
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    // MARK: - Without speaker separation

    func testTranscribesBothTracksAndWritesTheTranscript() async throws {
        let meeting = try makeRecordedMeeting(
            microphone: silence(1) + tone(2, amplitude: 0.2) + silence(6) + tone(2, amplitude: 0.2) + silence(2),
            system: silence(4) + tone(2, amplitude: 0.5) + silence(7))
        let engines = FakeEngines()
        let transcriber = MeetingTranscriber(store: store, engines: engines)

        let result = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(result.status, .transcribed)
        XCTAssertEqual(result.engineID, .parakeet)
        XCTAssertEqual(result.formatVersion, 2)
        XCTAssertNil(result.errorMessage)
        XCTAssertEqual(result.segments.map(\.speaker), [.me, .others, .me])
        XCTAssertEqual(result.segments.map(\.text), ["quiet words", "loud words", "quiet words"])
        XCTAssertEqual(result.segments.map { TranscriptMerger.timestamp($0.start) }, ["00:00", "00:03", "00:08"])
        XCTAssertTrue(result.segments.allSatisfy { $0.speakerID == nil })
        XCTAssertEqual(result.speakers, [])
        XCTAssertEqual(result.diarization?.state, .off)
        XCTAssertEqual(engines.fake.callCount, 3)
        XCTAssertEqual(engines.markedUsed, 3)
        XCTAssertFalse(transcriber.isTranscribing(meeting.id))
        XCTAssertEqual(transcriber.progress, [:])
        XCTAssertEqual(store.meeting(id: meeting.id), result)
        XCTAssertTrue(result.hasAudio)

        let transcript = try String(contentsOf: store.transcriptURL(for: meeting.id), encoding: .utf8)
        XCTAssertTrue(transcript.hasPrefix("# Standup\n"), transcript)
        XCTAssertTrue(transcript.contains("[00:00] Я: quiet words\n\n[00:03] Собеседники: loud words\n\n[00:08] Я: quiet words"),
                      transcript)
    }

    func testSegmentsStayOnePerClipAndJoinOnlyIntoDisplayLines() async throws {
        let meeting = try makeRecordedMeeting(
            microphone: silence(1) + tone(2, amplitude: 0.2) + silence(3) + tone(2, amplitude: 0.2),
            system: silence(8))
        let transcriber = MeetingTranscriber(store: store, engines: FakeEngines())

        let result = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(result.segments.count, 2)
        XCTAssertEqual(result.segments.map(\.text), ["quiet words", "quiet words"])
        let lines = TranscriptMerger.lines(for: result)
        XCTAssertEqual(lines.map(\.text), ["quiet words quiet words"])
        XCTAssertEqual(lines.first?.segmentIndices, [0, 1])
        XCTAssertEqual(lines.first?.speakerID, "me")
    }

    func testProgressCountsSegmentsOfBothTracks() async throws {
        let meeting = try makeRecordedMeeting(microphone: tone(2, amplitude: 0.2) + silence(2) + tone(2, amplitude: 0.2),
                                              system: silence(3) + tone(2, amplitude: 0.5))
        let engines = FakeEngines()
        engines.fake.delay = .milliseconds(300)
        let transcriber = MeetingTranscriber(store: store, engines: engines)
        var seen: [TranscriptionProgress] = []
        let observation = transcriber.$progress.sink { progress in
            if let value = progress[meeting.id], seen.last != value {
                seen.append(value)
            }
        }
        defer { observation.cancel() }

        _ = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(seen.first, TranscriptionProgress(completedSegments: 0, totalSegments: 0, phase: .preparing,
                                                         overallFraction: 0))
        XCTAssertEqual(seen.last, TranscriptionProgress(completedSegments: 3, totalSegments: 3))
        XCTAssertTrue(seen.contains(TranscriptionProgress(completedSegments: 1, totalSegments: 3)))
    }

    func testCancelStopsTranscriptionAndMarksTheMeeting() async throws {
        let meeting = try makeRecordedMeeting(microphone: tone(2, amplitude: 0.2) + silence(3) + tone(2, amplitude: 0.2),
                                              system: nil)
        let engines = FakeEngines()
        engines.fake.delay = .seconds(30)
        let transcriber = MeetingTranscriber(store: store, engines: engines)

        let task = Task { try await transcriber.transcribe(meetingID: meeting.id) }
        try await waitUntil { engines.fake.callCount > 0 }
        XCTAssertTrue(transcriber.isTranscribing(meeting.id))
        XCTAssertEqual(store.meeting(id: meeting.id)?.status, .transcribing)

        let started = ContinuousClock.now
        transcriber.cancel(meetingID: meeting.id)
        do {
            _ = try await task.value
            XCTFail("Cancelled transcription returned a result")
        } catch is CancellationError {
        }

        XCTAssertLessThan(ContinuousClock.now - started, .seconds(5))
        XCTAssertEqual(engines.fake.callCount, 1)
        XCTAssertFalse(transcriber.isTranscribing(meeting.id))
        let failed = try XCTUnwrap(store.meeting(id: meeting.id))
        XCTAssertEqual(failed.status, .failed)
        XCTAssertEqual(failed.errorMessage, CoreStrings.string("meetings.transcription.cancelled", table: .meetings))
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.transcriptURL(for: meeting.id).path))

        // A cancelled meeting can be transcribed again.
        engines.fake.delay = nil
        let retried = try await transcriber.transcribe(meetingID: meeting.id)
        XCTAssertEqual(retried.status, .transcribed)
    }

    func testEngineFailureMarksTheMeetingFailed() async throws {
        let meeting = try makeRecordedMeeting(microphone: tone(2, amplitude: 0.2), system: nil)
        let engines = FakeEngines()
        engines.loadError = SlovoError("Parakeet v3 is not downloaded")
        let transcriber = MeetingTranscriber(store: store, engines: engines)

        do {
            _ = try await transcriber.transcribe(meetingID: meeting.id)
            XCTFail("Expected the engine error")
        } catch let error as SlovoError {
            XCTAssertEqual(error.message, "Parakeet v3 is not downloaded")
        }

        let failed = try XCTUnwrap(store.meeting(id: meeting.id))
        XCTAssertEqual(failed.status, .failed)
        XCTAssertEqual(failed.errorMessage, "Parakeet v3 is not downloaded")
        XCTAssertFalse(transcriber.isTranscribing(meeting.id))
    }

    func testSilentMeetingGetsAnEmptyTranscript() async throws {
        let meeting = try makeRecordedMeeting(microphone: silence(5), system: silence(5))
        let engines = FakeEngines()
        let settings = AppSettings(defaults: defaults)
        settings.keepMeetingAudio = .afterTranscription
        let transcriber = MeetingTranscriber(store: store, engines: engines, settings: settings)

        let result = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(result.status, .transcribed)
        XCTAssertEqual(result.segments, [])
        XCTAssertTrue(result.hasAudio)
        XCTAssertTrue(RetentionCleaner.expiredMeetingAudioIDs([result], keep: .afterTranscription, now: Date()).isEmpty)
        XCTAssertEqual(engines.fake.callCount, 0)
        let transcript = try String(contentsOf: store.transcriptURL(for: meeting.id), encoding: .utf8)
        XCTAssertTrue(transcript.hasSuffix(CoreStrings.string("meetings.transcript.empty", table: .meetings, language: .ru) + "\n"))
    }

    func testRefusesMeetingsThatCannotBeTranscribed() async throws {
        let transcriber = MeetingTranscriber(store: store, engines: FakeEngines())

        await assertThrows(MeetingErrors.notFound()) {
            _ = try await transcriber.transcribe(meetingID: UUID())
        }

        let recording = try store.create(title: "Live")
        await assertThrows(MeetingErrors.stillRecording()) {
            _ = try await transcriber.transcribe(meetingID: recording.id)
        }

        var noAudio = try makeRecordedMeeting(microphone: tone(1, amplitude: 0.2), system: nil)
        store.deleteAudio(id: noAudio.id)
        noAudio = try XCTUnwrap(store.meeting(id: noAudio.id))
        await assertThrows(MeetingErrors.noAudio()) {
            _ = try await transcriber.transcribe(meetingID: noAudio.id)
        }
        XCTAssertEqual(store.meeting(id: noAudio.id)?.status, .recorded)
        XCTAssertFalse(transcriber.canProcess(noAudio, mode: .full))
        XCTAssertFalse(transcriber.canProcess(recording, mode: .full))
    }

    func testDeleteAfterTranscriptionRemovesAudioOnceTheTranscriptIsSaved() async throws {
        let settings = AppSettings(defaults: defaults)
        settings.keepMeetingAudio = .afterTranscription
        let meeting = try makeRecordedMeeting(microphone: tone(2, amplitude: 0.2), system: tone(2, amplitude: 0.5))
        let transcriber = MeetingTranscriber(store: store, engines: FakeEngines(), settings: settings)

        let result = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(result.status, .transcribed)
        XCTAssertFalse(result.hasAudio)
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.microphoneAudioURL(for: meeting.id).path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.systemAudioURL(for: meeting.id).path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: store.transcriptURL(for: meeting.id).path))
    }

    func testEmptyRerunKeepsThePreviousTranscriptAndProtectsAudioFromRetention() async throws {
        let settings = AppSettings(defaults: defaults)
        settings.keepMeetingAudio = .forever
        let meeting = try makeRecordedMeeting(microphone: tone(2, amplitude: 0.2), system: nil)
        let engines = FakeEngines()
        let transcriber = MeetingTranscriber(store: store, engines: engines, settings: settings)
        let original = try await transcriber.transcribe(meetingID: meeting.id)
        let transcriptURL = store.transcriptURL(for: meeting.id)
        let audioURL = store.microphoneAudioURL(for: meeting.id)
        let transcript = try Data(contentsOf: transcriptURL)
        let audio = try Data(contentsOf: audioURL)

        settings.keepMeetingAudio = .afterTranscription
        engines.fake.response = " \n "
        do {
            _ = try await transcriber.transcribe(meetingID: meeting.id)
            XCTFail("An empty rerun succeeded")
        } catch is MeetingErrors.EmptyTranscription {
        }

        let reloaded = MeetingStore(directories: store.directories)
        let saved = try XCTUnwrap(reloaded.meeting(id: meeting.id))
        XCTAssertEqual(saved.status, .transcribed)
        XCTAssertEqual(saved.segments, original.segments)
        XCTAssertEqual(saved.engineID, original.engineID)
        XCTAssertTrue(saved.hasAudio)
        XCTAssertEqual(try Data(contentsOf: transcriptURL), transcript)
        XCTAssertEqual(try Data(contentsOf: audioURL), audio)
        XCTAssertTrue(RetentionCleaner.expiredMeetingAudioIDs([saved], keep: .afterTranscription, now: Date()).isEmpty)

        engines.fake.response = nil
        let retried = try await transcriber.transcribe(meetingID: meeting.id)
        XCTAssertNil(retried.errorMessage)
        XCTAssertFalse(retried.hasAudio)
    }

    // MARK: - Speaker separation

    func testSeparatedSpeakersGetNumberedLinesInTheTranscript() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns]
        let engines = FakeEngines()
        let transcriber = makeTranscriber(engines: engines, diarization: diarization)

        let result = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(result.status, .transcribed)
        XCTAssertEqual(result.formatVersion, 2)
        XCTAssertEqual(result.speakers, [
            MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0),
            MeetingSpeaker(id: "s2", track: .microphone, number: 2, colorIndex: 1)
        ])
        XCTAssertEqual(result.segments.map(\.speakerID), ["s1", "s2"])
        XCTAssertEqual(result.segments.map(\.speaker), [.me, .me])
        let first = try XCTUnwrap(result.segments.first)
        let last = try XCTUnwrap(result.segments.last)
        XCTAssertEqual(first.start, 0.75, accuracy: 1e-6)
        XCTAssertEqual(first.end, 7.25, accuracy: 1e-6)
        XCTAssertEqual(last.start, 8.75, accuracy: 1e-6)
        XCTAssertEqual(last.end, 15.25, accuracy: 1e-6)

        let lines = TranscriptMerger.lines(for: result).map { TranscriptMerger.text(of: $0, in: result, language: .ru) }
        XCTAssertEqual(lines, ["[00:00] Спикер 1: quiet words", "[00:08] Спикер 2: quiet words"])

        let diarizationRecord = try XCTUnwrap(result.diarization)
        XCTAssertEqual(diarizationRecord.state, .applied)
        XCTAssertEqual(diarizationRecord.speakersPerTrack, ["microphone": 2])
        XCTAssertEqual(diarizationRecord.model, DiarizationService.modelIdentifier)
        XCTAssertEqual(diarizationRecord.configuration, DiarizationTuning.standard.description)
        XCTAssertNil(diarizationRecord.requestedSpeakers)
        XCTAssertNotNil(diarizationRecord.processedAt)

        XCTAssertEqual(diarization.fake.hints, ["mic.caf": .automatic])
        XCTAssertEqual(diarization.diarizerRequests, 1)
        XCTAssertEqual(diarization.releases, 1)
        XCTAssertEqual(engines.fake.callCount, 2)

        let transcript = try String(contentsOf: store.transcriptURL(for: meeting.id), encoding: .utf8)
        XCTAssertTrue(transcript.contains("\nУчастники: Спикер 1, Спикер 2\n"), transcript)
        XCTAssertTrue(transcript.contains("[00:00] Спикер 1: quiet words\n\n[00:08] Спикер 2: quiet words\n"), transcript)
    }

    func testCallGivesSystemSpeakersTheNextNumbersAndMarksTheOnlyMicrophoneVoiceAsMe() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: callSystem())
        let diarization = FakeDiarization()
        diarization.fake.turns = [
            "mic.caf": [SpeakerTurn(start: 0.9, end: 15.1, cluster: 0)],
            "system.caf": [SpeakerTurn(start: 2.9, end: 9.1, cluster: 0)]
        ]
        let transcriber = makeTranscriber(engines: FakeEngines(), diarization: diarization)

        let result = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(result.speakers, [
            MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0, isMe: true),
            MeetingSpeaker(id: "s2", track: .system, number: 2, colorIndex: 1)
        ])
        XCTAssertEqual(result.segments.map(\.speakerID), ["s1", "s2"])
        XCTAssertEqual(result.segments.map(\.text), ["quiet words", "loud words"])
        XCTAssertEqual(result.diarization?.speakersPerTrack, ["microphone": 1, "system": 1])
        let labels = TranscriptMerger.lines(for: result).map { TranscriptMerger.label(of: $0, in: result, language: .ru) }
        XCTAssertEqual(labels, ["Я", "Спикер 2"])
    }

    /// A call recorded with the microphone off the whole time has a microphone track of
    /// silence. It gives no lines and no «Я», and the diarizer is never shown it, so a
    /// model that imagines a voice in silence has no chance to.
    func testACallWithTheMicrophoneOffGivesNoMicrophoneLinesAndNoMe() async throws {
        var meeting = try store.create(title: "Созвон")
        let muted = try MeetingTrackWriter(url: store.microphoneAudioURL(for: meeting.id))
        muted.isMuted = true
        muted.append(twoVoices())
        XCTAssertEqual(muted.finish(), Int64(twoVoices().count))
        try write(callSystem(), to: store.systemAudioURL(for: meeting.id))
        meeting.status = .recorded
        meeting.hasSystemAudio = true
        meeting.duration = 16
        store.update(meeting)

        let diarization = FakeDiarization()
        diarization.fake.turns = [
            "mic.caf": [SpeakerTurn(start: 0.9, end: 15.1, cluster: 0)],
            "system.caf": [SpeakerTurn(start: 2.9, end: 9.1, cluster: 0)]
        ]
        let engines = FakeEngines()
        let separated = try await makeTranscriber(engines: engines, diarization: diarization)
            .transcribe(meetingID: meeting.id)

        XCTAssertEqual(diarization.fake.callCount, 1)
        XCTAssertEqual(Array(diarization.fake.hints.keys), ["system.caf"])
        XCTAssertEqual(separated.segments.map(\.speaker), [.others])
        XCTAssertEqual(separated.speakers, [MeetingSpeaker(id: "s1", track: .system, number: 1, colorIndex: 0)])
        XCTAssertFalse(separated.effectiveSpeakers.contains { TranscriptMerger.isMe($0, in: separated) })
        XCTAssertEqual(TranscriptMerger.lines(for: separated).map { TranscriptMerger.label(of: $0, in: separated, language: .ru) },
                       ["Спикер 1"])
        XCTAssertEqual(engines.fake.callCount, 1, "no clip is cut from the silent track")

        // Without speaker separation the silent track is just as absent, and the voice the
        // first run found on system audio keeps its number.
        let plain = try await makeTranscriber(engines: FakeEngines(), diarization: FakeDiarization(), separate: false)
            .transcribe(meetingID: meeting.id)
        XCTAssertEqual(plain.segments.map(\.speaker), [.others])
        XCTAssertEqual(plain.effectiveSpeakers.map(\.id), ["s1"])
        XCTAssertFalse(plain.effectiveSpeakers.contains { TranscriptMerger.isMe($0, in: plain) })
        let transcript = try String(contentsOf: store.transcriptURL(for: meeting.id), encoding: .utf8)
        XCTAssertFalse(transcript.contains("Я:"), transcript)
    }

    func testPhasesFollowTheRunAndProgressReachesOne() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: callSystem())
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns, "system.caf": [SpeakerTurn(start: 2.9, end: 9.1, cluster: 0)]]
        // The pause after the diarizer's half-way report lets that report reach the main
        // actor while the track is still being separated.
        diarization.fake.delay = .milliseconds(100)
        let engines = FakeEngines()
        engines.fake.delay = .milliseconds(50)
        let transcriber = makeTranscriber(engines: engines, diarization: diarization)
        var seen: [TranscriptionProgress] = []
        let observation = transcriber.$progress.sink { progress in
            if let value = progress[meeting.id], seen.last != value {
                seen.append(value)
            }
        }
        defer { observation.cancel() }

        _ = try await transcriber.transcribe(meetingID: meeting.id)

        var kinds: [String] = []
        for kind in seen.map({ Self.kind(of: $0.phase) }) where kinds.last != kind {
            kinds.append(kind)
        }
        XCTAssertEqual(kinds, ["preparing", "loading", "separating-microphone", "separating-system", "transcribing"])
        XCTAssertEqual(try XCTUnwrap(seen.last).overallFraction, 1, accuracy: 1e-9)
        XCTAssertEqual(try XCTUnwrap(seen.last).completedSegments, 3)
        for (earlier, later) in zip(seen, seen.dropFirst()) {
            XCTAssertLessThanOrEqual(earlier.overallFraction, later.overallFraction + 1e-9, "\(earlier) → \(later)")
        }
        // Separation takes the first fifth of the bar in a full run.
        let firstTranscribing = try XCTUnwrap(seen.first { $0.phase == .transcribing })
        XCTAssertEqual(firstTranscribing.overallFraction, 0.2, accuracy: 1e-9)
        // Half of the first of two tracks is a quarter of separation, a twentieth of the bar.
        XCTAssertTrue(seen.contains {
            $0.phase == .separatingSpeakers(.microphone, fraction: 0.5) && abs($0.overallFraction - 0.05) < 1e-9
        }, "\(seen)")
    }

    func testCancelDuringSeparationLeavesNoTranscript() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns]
        diarization.fake.delay = .seconds(30)
        let engines = FakeEngines()
        let transcriber = makeTranscriber(engines: engines, diarization: diarization)

        let task = Task { try await transcriber.transcribe(meetingID: meeting.id) }
        try await waitUntil { diarization.fake.callCount > 0 }
        transcriber.cancel(meetingID: meeting.id)
        do {
            _ = try await task.value
            XCTFail("Cancelled separation returned a result")
        } catch is CancellationError {
        }

        let failed = try XCTUnwrap(store.meeting(id: meeting.id))
        XCTAssertEqual(failed.status, .failed)
        XCTAssertEqual(failed.errorMessage, CoreStrings.string("meetings.transcription.cancelled", table: .meetings))
        XCTAssertEqual(failed.segments, [])
        XCTAssertEqual(engines.requests, 0)
        XCTAssertEqual(diarization.releases, 1)
        XCTAssertFalse(transcriber.isTranscribing(meeting.id))
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.transcriptURL(for: meeting.id).path))
    }

    func testCancelWhileTheSpeakerModelWaitsForTheFluidAudioQueueEndsTheRunAtOnce() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let modelsRoot = temporary.appendingPathComponent("Models", isDirectory: true)
        try DiarizationFixtures.installModel(in: modelsRoot.appendingPathComponent("diarization", isDirectory: true))
        // A model download holds the queue that the speaker model load needs.
        let queue = FluidAudioJobQueue()
        let gate = DictationFakes.Gate()
        let holder = Task { try await queue.run { await gate.wait() } }
        // Frees the queue later anyway, so a run that ignores the cancellation fails the
        // timing below instead of hanging the test.
        let fallbackOpener = Task {
            try await Task.sleep(nanoseconds: 5_000_000_000)
            gate.open()
        }
        defer { fallbackOpener.cancel() }
        let service = DiarizationService(modelsRoot: modelsRoot, downloader: DiarizationServiceTests.Downloader(),
                                         factory: { _ in try await queue.run { DiarizationServiceTests.Diarizer() } },
                                         systemSupported: true)
        let settings = AppSettings(defaults: defaults)
        settings.speakerSeparation = true
        let engines = FakeEngines()
        let transcriber = MeetingTranscriber(store: store, engines: engines, settings: settings, diarization: service)

        let run = Task { try await transcriber.transcribe(meetingID: meeting.id) }
        await DiarizationFixtures.waitUntil { await queue.waitingCount == 1 }
        XCTAssertEqual(transcriber.progress[meeting.id]?.phase, .loadingSpeakerModel)
        let cancelledAt = ContinuousClock.now
        transcriber.cancel(meetingID: meeting.id)
        do {
            _ = try await run.value
            XCTFail("A cancelled run returned a result")
        } catch is CancellationError {
        }

        XCTAssertLessThan(ContinuousClock.now - cancelledAt, .seconds(2), "the run waited for the queue")
        XCTAssertEqual(store.meeting(id: meeting.id)?.status, .failed)
        XCTAssertEqual(engines.requests, 0)
        XCTAssertEqual(service.loadState, .unloaded)
        gate.open()
        try await holder.value
    }

    func testCancelDuringSeparationOfAReRunRestoresThePreviousTranscript() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns]
        let transcriber = makeTranscriber(engines: FakeEngines(), diarization: diarization)
        let previous = try await transcriber.transcribe(meetingID: meeting.id)
        let previousDocument = try Data(contentsOf: store.transcriptURL(for: meeting.id))

        diarization.fake.delay = .seconds(30)
        let task = Task { try await transcriber.transcribe(meetingID: meeting.id) }
        try await waitUntil { diarization.fake.callCount > 1 }
        XCTAssertEqual(store.meeting(id: meeting.id)?.status, .transcribing)
        transcriber.cancel(meetingID: meeting.id)
        do {
            _ = try await task.value
            XCTFail("Cancelled re-run returned a result")
        } catch is CancellationError {
        }

        XCTAssertEqual(store.meeting(id: meeting.id), previous)
        XCTAssertEqual(try Data(contentsOf: store.transcriptURL(for: meeting.id)), previousDocument)
        try store.flush()
        XCTAssertEqual(MeetingStore(directories: AppDirectories(base: temporary)).meeting(id: meeting.id), previous)
    }

    func testWithoutTheModelSpeakersStayMeAndOthers() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: callSystem())
        let diarization = FakeDiarization()
        diarization.modelStatus = .notDownloaded(downloadBytes: DiarizationService.downloadBytes)
        let transcriber = makeTranscriber(engines: FakeEngines(), diarization: diarization, expectedSpeakers: 3)

        let result = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(result.status, .transcribed)
        XCTAssertEqual(result.speakers, [])
        XCTAssertEqual(result.effectiveSpeakers.map(\.id), ["me", "others"])
        XCTAssertTrue(result.segments.allSatisfy { $0.speakerID == nil })
        XCTAssertEqual(result.segments.count, 3)
        XCTAssertEqual(result.diarization?.state, .modelMissing)
        XCTAssertEqual(result.diarization?.requestedSpeakers, 3)
        XCTAssertNil(result.diarization?.model)
        XCTAssertEqual(diarization.diarizerRequests, 0)
        XCTAssertEqual(diarization.releases, 0)
        XCTAssertFalse(transcriber.canProcess(result, mode: .speakersOnly))
        XCTAssertTrue(transcriber.canProcess(result, mode: .full))
    }

    func testSeparationOffOrUnsupportedIsRecorded() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns]

        let off = try await makeTranscriber(engines: FakeEngines(), diarization: diarization, separate: false)
            .transcribe(meetingID: meeting.id)
        XCTAssertEqual(off.diarization?.state, .off)
        XCTAssertEqual(off.speakers, [])

        diarization.isSupportedOnThisSystem = false
        let unsupported = try await makeTranscriber(engines: FakeEngines(), diarization: diarization)
            .transcribe(meetingID: meeting.id)
        XCTAssertEqual(unsupported.diarization?.state, .unsupportedSystem)
        XCTAssertEqual(unsupported.diarization?.message,
                       CoreStrings.string("meetings.diarization.unsupportedSystem", table: .meetings))
        XCTAssertEqual(unsupported.speakers, [])
        XCTAssertEqual(diarization.diarizerRequests, 0)
    }

    /// A full re-run that does not separate (turned off, no model, or a failure) keeps the
    /// voices an earlier run found, with the names and «Я» the user gave them: each new
    /// segment goes to the voice whose earlier lines it overlaps.
    func testAFullReRunWithoutSeparationKeepsTheNamedSpeakers() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns]
        let first = try await makeTranscriber(engines: FakeEngines(), diarization: diarization)
            .transcribe(meetingID: meeting.id)
        XCTAssertEqual(first.segments.map(\.speakerID), ["s1", "s2"])
        store.update(MeetingEdits.markAsMe("s1", in: MeetingEdits.renameSpeaker("s2", to: "Аня", in: first)))

        let plain = try await makeTranscriber(engines: FakeEngines(), diarization: diarization, separate: false)
            .transcribe(meetingID: meeting.id)

        XCTAssertEqual(plain.diarization?.state, .off)
        XCTAssertEqual(plain.segments.map(\.speakerID), ["s1", "s2"])
        XCTAssertEqual(plain.speakers.map(\.id), ["s1", "s2"])
        XCTAssertEqual(plain.speakers.map(\.displayName), [nil, "Аня"])
        XCTAssertEqual(plain.speakers.map(\.isMe), [true, false])
        XCTAssertEqual(TranscriptMerger.lines(for: plain).map { TranscriptMerger.text(of: $0, in: plain, language: .ru) },
                       ["[00:00] Я: quiet words", "[00:08] Аня: quiet words"])

        // A run whose separation fails keeps them the same way.
        diarization.fake.error = SlovoError("Segmentation crashed")
        let failed = try await makeTranscriber(engines: FakeEngines(), diarization: diarization)
            .transcribe(meetingID: meeting.id)
        XCTAssertEqual(failed.diarization?.state, .failed)
        XCTAssertEqual(failed.segments.map(\.speakerID), ["s1", "s2"])
        XCTAssertEqual(failed.speakers.map(\.displayName), [nil, "Аня"])
        XCTAssertEqual(failed.highestSpeakerNumber, 2)
    }

    func testDiarizerErrorStillCompletesWithFallbackSpeakers() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: callSystem())
        let diarization = FakeDiarization()
        diarization.fake.error = SlovoError("Segmentation crashed")
        let engines = FakeEngines()
        let transcriber = makeTranscriber(engines: engines, diarization: diarization)

        let result = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(result.status, .transcribed)
        XCTAssertEqual(result.speakers, [])
        XCTAssertEqual(result.effectiveSpeakers.map(\.id), ["me", "others"])
        XCTAssertEqual(result.segments.count, 3)
        XCTAssertEqual(result.diarization?.state, .failed)
        XCTAssertEqual(result.diarization?.message,
                       CoreStrings.format("meetings.diarization.failed", table: .meetings, "Segmentation crashed"))
        // Separation stops at the first failure.
        XCTAssertEqual(diarization.fake.callCount, 1)
        XCTAssertEqual(diarization.releases, 1)
        XCTAssertEqual(engines.fake.callCount, 3)

        diarization.fake.error = nil
        diarization.loadError = SlovoError("The speaker model is damaged")
        let loadFailed = try await transcriber.transcribe(meetingID: meeting.id)
        XCTAssertEqual(loadFailed.status, .transcribed)
        XCTAssertEqual(loadFailed.diarization?.state, .failed)
        XCTAssertEqual(loadFailed.diarization?.message,
                       CoreStrings.format("meetings.diarization.failed", table: .meetings, "The speaker model is damaged"))
    }

    func testSeparationFailureMessageReadsAsTwoSentences() {
        // The stored message wraps a speaker model error, which is already a sentence.
        let expected: [(AppLanguage, String)] = [
            (.en, "Speakers were not separated. Could not load the speaker model: Core ML"),
            (.ru, "Спикеры не разделены. Не удалось загрузить модель спикеров: Core ML")
        ]
        for (language, message) in expected {
            let reason = CoreStrings.format("diarization.error.loadFailed", table: .diarization, language: language, "Core ML")
            XCTAssertEqual(CoreStrings.format("meetings.diarization.failed", table: .meetings, language: language, reason),
                           message)
        }
    }

    func testFailedFullReRunRestoresThePreviousTranscript() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns]
        let engines = FakeEngines()
        let transcriber = makeTranscriber(engines: engines, diarization: diarization)
        let previous = try await transcriber.transcribe(meetingID: meeting.id)
        let previousDocument = try Data(contentsOf: store.transcriptURL(for: meeting.id))

        engines.loadError = SlovoError("GigaAM is not downloaded")
        await assertThrows(SlovoError("GigaAM is not downloaded")) {
            _ = try await transcriber.transcribe(meetingID: meeting.id,
                                                 options: MeetingProcessingOptions(expectedSpeakers: 3, engineID: .gigaamCTC))
        }

        XCTAssertEqual(store.meeting(id: meeting.id), previous)
        XCTAssertEqual(try Data(contentsOf: store.transcriptURL(for: meeting.id)), previousDocument)
        XCTAssertFalse(transcriber.isTranscribing(meeting.id))
    }

    func testRenameTitleAndMomentMadeDuringProcessingSurvive() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns]
        let engines = FakeEngines()
        let transcriber = makeTranscriber(engines: engines, diarization: diarization)
        _ = try await transcriber.transcribe(meetingID: meeting.id)

        engines.fake.delay = .milliseconds(300)
        let task = Task { try await transcriber.transcribe(meetingID: meeting.id) }
        try await waitUntil { engines.fake.callCount > 2 }
        let processing = try XCTUnwrap(store.meeting(id: meeting.id))
        var edited = MeetingEdits.renameSpeaker("s2", to: "Аня", in: processing)
        edited.title = "Коворк"
        let moment = MeetingHighlight(time: 9, note: "дедлайн", source: .recording)
        edited.highlights.append(moment)
        store.update(edited)

        let result = try await task.value

        XCTAssertEqual(result.status, .transcribed)
        XCTAssertEqual(result.title, "Коворк")
        XCTAssertEqual(result.highlights, [moment])
        XCTAssertEqual(result.speakers.map(\.id), ["s1", "s2"])
        XCTAssertEqual(result.speaker(id: "s2")?.displayName, "Аня")
        let transcript = try String(contentsOf: store.transcriptURL(for: meeting.id), encoding: .utf8)
        XCTAssertTrue(transcript.hasPrefix("# Коворк\n"), transcript)
        XCTAssertTrue(transcript.contains("[00:08] ★ Аня: quiet words"), transcript)
    }

    func testReRunWithMorePeopleKeepsMatchedNamesAndNumbersNewVoicesAbove() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns]
        let transcriber = makeTranscriber(engines: FakeEngines(), diarization: diarization)
        let first = try await transcriber.transcribe(meetingID: meeting.id)
        store.update(MeetingEdits.renameSpeaker("s2", to: "Аня", in: first))

        diarization.fake.turns = ["mic.caf": [
            SpeakerTurn(start: 0.9, end: 7.1, cluster: 0),
            SpeakerTurn(start: 8.9, end: 12, cluster: 1),
            SpeakerTurn(start: 12, end: 15.1, cluster: 2)
        ]]
        let result = try await transcriber.transcribe(meetingID: meeting.id,
                                                      options: MeetingProcessingOptions(expectedSpeakers: 3))

        XCTAssertEqual(result.speakers.map(\.id), ["s1", "s2", "s3"])
        XCTAssertEqual(result.speakers.map(\.displayName), [nil, "Аня", nil])
        XCTAssertEqual(result.speakers.map(\.number), [1, 2, 3])
        XCTAssertEqual(result.segments.map(\.speakerID), ["s1", "s2", "s3"])
        XCTAssertEqual(result.diarization?.requestedSpeakers, 3)
        XCTAssertEqual(diarization.fake.hints["mic.caf"], SpeakerCountHint(exactly: 3))
    }

    func testSpeakersOnlyReassignsExistingTextWithoutTheEngine() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns]
        let engines = FakeEngines()
        let transcriber = makeTranscriber(engines: engines, diarization: diarization)
        let first = try await transcriber.transcribe(meetingID: meeting.id)
        XCTAssertTrue(transcriber.canProcess(first, mode: .speakersOnly))
        let requests = engines.requests
        let calls = engines.fake.callCount

        diarization.fake.turns = ["mic.caf": [SpeakerTurn(start: 0.9, end: 15.1, cluster: 0)]]
        var seen: [TranscriptionProgress] = []
        let observation = transcriber.$progress.sink { progress in
            if let value = progress[meeting.id], seen.last != value {
                seen.append(value)
            }
        }
        defer { observation.cancel() }
        let result = try await transcriber.transcribe(meetingID: meeting.id,
                                                      options: MeetingProcessingOptions(mode: .speakersOnly))

        XCTAssertEqual(engines.requests, requests)
        XCTAssertEqual(engines.fake.callCount, calls)
        XCTAssertEqual(result.status, .transcribed)
        XCTAssertEqual(result.segments.map(\.text), first.segments.map(\.text))
        XCTAssertEqual(result.segments.map(\.start), first.segments.map(\.start))
        XCTAssertEqual(result.segments.map(\.end), first.segments.map(\.end))
        XCTAssertEqual(result.segments.map(\.speakerID), ["s1", "s1"])
        XCTAssertEqual(result.speakers.map(\.id), ["s1"])
        XCTAssertEqual(result.engineID, first.engineID)
        XCTAssertEqual(result.diarization?.state, .applied)
        XCTAssertEqual(result.diarization?.speakersPerTrack, ["microphone": 1])
        XCTAssertFalse(seen.contains { $0.phase == .transcribing })
        XCTAssertEqual(try XCTUnwrap(seen.last).overallFraction, 1, accuracy: 1e-9)
        XCTAssertEqual(diarization.releases, 2)
    }

    func testSpeakersOnlyKeepsTheSpeakersOfATrackThatGivesNoTurns() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: callSystem())
        let diarization = FakeDiarization()
        let systemTurns = [SpeakerTurn(start: 2.9, end: 9.1, cluster: 0)]
        diarization.fake.turns = ["mic.caf": twoVoiceTurns, "system.caf": systemTurns]
        let transcriber = makeTranscriber(engines: FakeEngines(), diarization: diarization)
        let first = try await transcriber.transcribe(meetingID: meeting.id)
        XCTAssertEqual(first.speakers.map(\.id), ["s1", "s2", "s3"])
        var edited = MeetingEdits.renameSpeaker("s2", to: "Аня", in: first)
        edited = MeetingEdits.renameSpeaker("s3", to: "Олег", in: edited)
        store.update(edited)

        // The diarizer finds no speech on the microphone this time.
        diarization.fake.turns = ["mic.caf": [], "system.caf": systemTurns]
        let oneTrack = try await transcriber.transcribe(meetingID: meeting.id,
                                                        options: MeetingProcessingOptions(mode: .speakersOnly))

        XCTAssertEqual(oneTrack.speakers, edited.speakers)
        XCTAssertEqual(oneTrack.segments, edited.segments)
        XCTAssertEqual(oneTrack.diarization?.state, .applied)
        XCTAssertEqual(oneTrack.diarization?.speakersPerTrack, ["microphone": 2, "system": 1])

        // Nothing on either track leaves every speaker as it was.
        diarization.fake.turns = [:]
        let noTrack = try await transcriber.transcribe(meetingID: meeting.id,
                                                       options: MeetingProcessingOptions(mode: .speakersOnly))

        XCTAssertEqual(noTrack.speakers, edited.speakers)
        XCTAssertEqual(noTrack.segments, edited.segments)
        XCTAssertEqual(noTrack.diarization?.speakersPerTrack, ["microphone": 2, "system": 1])
        let transcript = try String(contentsOf: store.transcriptURL(for: meeting.id), encoding: .utf8)
        XCTAssertTrue(transcript.contains("Аня") && transcript.contains("Олег"), transcript)
    }

    func testNumbersRemovedByMergesStayUsedAcrossReRuns() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": [
            SpeakerTurn(start: 0.9, end: 7.1, cluster: 0),
            SpeakerTurn(start: 8.9, end: 12, cluster: 1),
            SpeakerTurn(start: 12, end: 15.1, cluster: 2)
        ]]
        let transcriber = makeTranscriber(engines: FakeEngines(), diarization: diarization)
        let options = MeetingProcessingOptions(expectedSpeakers: 3)

        let first = try await transcriber.transcribe(meetingID: meeting.id, options: options)
        XCTAssertEqual(first.speakers.map(\.id), ["s1", "s2", "s3"])
        XCTAssertEqual(first.highestSpeakerNumber, 3)
        store.update(try MeetingEdits.mergeSpeaker("s3", into: "s2", in: first))

        // The two halves of the second voice tie for «Спикер 2», so which one gets it is left open.
        let second = try await transcriber.transcribe(meetingID: meeting.id, options: options)
        XCTAssertEqual(Set(second.speakers.map(\.id)), ["s1", "s2", "s4"])
        store.update(try MeetingEdits.mergeSpeaker("s4", into: "s1", in: second))

        let third = try await transcriber.transcribe(meetingID: meeting.id, options: options)
        XCTAssertEqual(Set(third.speakers.map(\.id)), ["s1", "s2", "s5"])
        XCTAssertEqual(third.highestSpeakerNumber, 5)
        try store.flush()
        XCTAssertEqual(MeetingStore(directories: AppDirectories(base: temporary)).meeting(id: meeting.id)?.highestSpeakerNumber, 5)
    }

    func testSpeakersOnlyIsRefusedWhenItCannotRun() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        let transcriber = makeTranscriber(engines: FakeEngines(), diarization: diarization)
        let recorded = try XCTUnwrap(store.meeting(id: meeting.id))

        // No segments yet.
        XCTAssertFalse(transcriber.canProcess(recorded, mode: .speakersOnly))
        await assertThrows(MeetingErrors.speakersOnlyUnavailable()) {
            _ = try await transcriber.transcribe(meetingID: meeting.id, options: MeetingProcessingOptions(mode: .speakersOnly))
        }
        XCTAssertEqual(store.meeting(id: meeting.id)?.status, .recorded)

        var firstFormat = recorded
        firstFormat.status = .transcribed
        firstFormat.formatVersion = 1
        firstFormat.segments = [TranscriptSegment(start: 1, end: 15, speaker: .me, text: "Длинный общий кусок")]
        store.update(firstFormat)
        XCTAssertFalse(transcriber.canProcess(firstFormat, mode: .speakersOnly))

        var current = firstFormat
        current.formatVersion = 2
        store.update(current)
        XCTAssertTrue(transcriber.canProcess(current, mode: .speakersOnly))
        diarization.modelStatus = .damaged(reason: "weights missing")
        XCTAssertFalse(transcriber.canProcess(current, mode: .speakersOnly))
        diarization.modelStatus = .ready(diskBytes: 1)
        diarization.isSupportedOnThisSystem = false
        XCTAssertFalse(transcriber.canProcess(current, mode: .speakersOnly))
        XCTAssertFalse(MeetingTranscriber(store: store, engines: FakeEngines()).canProcess(current, mode: .speakersOnly))
    }

    func testSpeakersOnlyFailureRestoresTheRecordAndThrows() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns]
        let transcriber = makeTranscriber(engines: FakeEngines(), diarization: diarization)
        let previous = try await transcriber.transcribe(meetingID: meeting.id)

        diarization.fake.error = SlovoError("Embedding failed")
        await assertThrows(SlovoError("Embedding failed")) {
            _ = try await transcriber.transcribe(meetingID: meeting.id, options: MeetingProcessingOptions(mode: .speakersOnly))
        }
        XCTAssertEqual(store.meeting(id: meeting.id), previous)
    }

    func testSpeakerCountHintFollowsTheKindOfMeeting() async throws {
        XCTAssertEqual(MeetingTranscriber.hint(for: .microphone, expectedSpeakers: nil, hasSystemTrack: true), .automatic)
        XCTAssertEqual(MeetingTranscriber.hint(for: .microphone, expectedSpeakers: 4, hasSystemTrack: false),
                       SpeakerCountHint(exactly: 4))
        XCTAssertEqual(MeetingTranscriber.hint(for: .microphone, expectedSpeakers: 3, hasSystemTrack: true),
                       SpeakerCountHint(maximum: 3))
        XCTAssertEqual(MeetingTranscriber.hint(for: .system, expectedSpeakers: 3, hasSystemTrack: true),
                       SpeakerCountHint(maximum: 2))
        XCTAssertEqual(MeetingTranscriber.hint(for: .system, expectedSpeakers: 2, hasSystemTrack: true),
                       SpeakerCountHint(maximum: 1))
        XCTAssertNil(MeetingTranscriber.requestedSpeakers(0))
        XCTAssertNil(MeetingTranscriber.requestedSpeakers(9))
        XCTAssertEqual(MeetingTranscriber.requestedSpeakers(5), 5)

        let call = try makeRecordedMeeting(microphone: twoVoices(), system: callSystem())
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns, "system.caf": [SpeakerTurn(start: 2.9, end: 9.1, cluster: 0)]]
        let transcriber = makeTranscriber(engines: FakeEngines(), diarization: diarization, expectedSpeakers: 3)

        _ = try await transcriber.transcribe(meetingID: call.id)
        XCTAssertEqual(diarization.fake.hints, ["mic.caf": SpeakerCountHint(maximum: 3), "system.caf": SpeakerCountHint(maximum: 2)])

        _ = try await transcriber.transcribe(meetingID: call.id, options: MeetingProcessingOptions(expectedSpeakers: 2))
        XCTAssertEqual(diarization.fake.hints, ["mic.caf": SpeakerCountHint(maximum: 2), "system.caf": SpeakerCountHint(maximum: 1)])

        _ = try await transcriber.transcribe(meetingID: call.id, options: MeetingProcessingOptions(expectedSpeakers: 0))
        XCTAssertEqual(diarization.fake.hints, ["mic.caf": .automatic, "system.caf": .automatic])

        let inPerson = try makeRecordedMeeting(microphone: twoVoices(), system: nil)
        let recorded = try await makeTranscriber(engines: FakeEngines(), diarization: diarization, expectedSpeakers: 4)
            .transcribe(meetingID: inPerson.id)
        XCTAssertEqual(diarization.fake.hints["mic.caf"], SpeakerCountHint(exactly: 4))
        XCTAssertEqual(recorded.diarization?.requestedSpeakers, 4)
    }

    func testTrackWithTooLittleSpeechKeepsItsFallbackSpeaker() async throws {
        let meeting = try makeRecordedMeeting(microphone: twoVoices(), system: silence(3) + tone(3, amplitude: 0.5) + silence(10))
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns, "system.caf": [SpeakerTurn(start: 2.9, end: 6.1, cluster: 0)]]
        let transcriber = makeTranscriber(engines: FakeEngines(), diarization: diarization)

        let result = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(Set(diarization.fake.hints.keys), ["mic.caf"])
        XCTAssertEqual(result.effectiveSpeakers.map(\.id), ["s1", "s2", "others"])
        XCTAssertEqual(result.diarization?.speakersPerTrack, ["microphone": 2])
        let system = try XCTUnwrap(result.segments.first { $0.speaker == .others })
        XCTAssertNil(system.speakerID)
    }

    func testAudioIsRemovedOnlyAfterTheTranscriptIsSaved() async throws {
        let spy = SpyStore(base: store)
        let settings = AppSettings(defaults: defaults)
        settings.keepMeetingAudio = .afterTranscription
        var meeting = try makeRecordedMeeting(microphone: twoVoices(), system: callSystem())
        meeting.hasScreenRecording = true
        store.update(meeting)
        try Data([0]).write(to: store.screenRecordingURL(for: meeting.id))
        let diarization = FakeDiarization()
        diarization.fake.turns = ["mic.caf": twoVoiceTurns, "system.caf": [SpeakerTurn(start: 2.9, end: 9.1, cluster: 0)]]
        let engines = FakeEngines()
        engines.fake.requiredFile = store.microphoneAudioURL(for: meeting.id)
        let transcriber = MeetingTranscriber(store: spy, engines: engines, settings: settings, diarization: diarization)

        let result = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(engines.fake.callCount, 3)
        XCTAssertEqual(engines.fake.callsWithoutFile, 0, "audio was removed before every clip was transcribed")
        XCTAssertFalse(result.hasAudio)
        let transcribed = try XCTUnwrap(spy.events.lastIndex(of: "update transcribed"))
        let deletion = try XCTUnwrap(spy.events.firstIndex { $0.hasPrefix("removeAudio") })
        XCTAssertLessThan(transcribed, deletion)
        XCTAssertTrue(spy.events[transcribed..<deletion].contains("flush"), "\(spy.events)")
        XCTAssertEqual(spy.events[deletion], "removeAudio transcript=true saved=transcribed")
        XCTAssertFalse(spy.events.contains("deleteAudio"), "the screen recording is not audio")
        XCTAssertTrue(FileManager.default.fileExists(atPath: store.transcriptURL(for: meeting.id).path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.microphoneAudioURL(for: meeting.id).path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.systemAudioURL(for: meeting.id).path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: store.screenRecordingURL(for: meeting.id).path))
        XCTAssertTrue(result.hasScreenRecording)
    }

    // MARK: - Recorder

    func testIdleRecorderIsNotRecording() async throws {
        let recorder = MeetingRecorder(store: store, settings: AppSettings(defaults: defaults))
        XCTAssertFalse(recorder.isMeetingRecording)
        XCTAssertEqual(recorder.state, .idle)
        XCTAssertEqual(recorder.systemAudioStatus, .off)
        let stopped = await recorder.stop()
        XCTAssertNil(stopped)
        XCTAssertNil(recorder.stopImmediately())
        XCTAssertEqual(MeetingRecorder.smoothed(0, toward: 1), 0.6, accuracy: 1e-6)
        XCTAssertEqual(MeetingRecorder.smoothed(1, toward: 0), 0.75, accuracy: 1e-6)
    }

    func testDefaultTitleIsLocalized() {
        let date = Date(timeIntervalSince1970: 1_789_000_000)
        XCTAssertTrue(MeetingRecorder.defaultTitle(for: date, language: .en).hasPrefix("Meeting "))
        XCTAssertTrue(MeetingRecorder.defaultTitle(for: date, language: .ru).hasPrefix("Встреча "))
    }

    // MARK: - Helpers

    /// Two six-second voices on the microphone: 1–7 s and 9–15 s of a 16 s track.
    private func twoVoices() -> [Float] {
        silence(1) + tone(6, amplitude: 0.2) + silence(2) + tone(6, amplitude: 0.2) + silence(1)
    }

    private let twoVoiceTurns = [SpeakerTurn(start: 0.9, end: 7.1, cluster: 0), SpeakerTurn(start: 8.9, end: 15.1, cluster: 1)]

    /// One remote voice, 3–9 s of a 16 s system track.
    private func callSystem() -> [Float] {
        silence(3) + tone(6, amplitude: 0.5) + silence(7)
    }

    private func makeTranscriber(engines: FakeEngines, diarization: FakeDiarization, separate: Bool = true,
                                 expectedSpeakers: Int = 0) -> MeetingTranscriber {
        let settings = AppSettings(defaults: defaults)
        settings.speakerSeparation = separate
        settings.expectedSpeakers = expectedSpeakers
        return MeetingTranscriber(store: store, engines: engines, settings: settings, diarization: diarization)
    }

    private func makeRecordedMeeting(microphone: [Float], system: [Float]?) throws -> MeetingRecord {
        var meeting = try store.create(title: "Standup")
        try write(microphone, to: store.microphoneAudioURL(for: meeting.id))
        if let system {
            try write(system, to: store.systemAudioURL(for: meeting.id))
        }
        meeting.status = .recorded
        meeting.hasSystemAudio = system != nil
        meeting.duration = Double(microphone.count) / 16_000
        store.update(meeting)
        return meeting
    }

    private func write(_ samples: [Float], to url: URL) throws {
        let writer = try MeetingTrackWriter(url: url)
        writer.append(samples)
        writer.finish()
    }

    private func tone(_ seconds: Double, amplitude: Float) -> [Float] {
        (0..<Int(seconds * 16_000)).map { amplitude * Float(sin(2 * Double.pi * 200 * Double($0) / 16_000)) }
    }

    private func silence(_ seconds: Double) -> [Float] {
        [Float](repeating: 0, count: Int(seconds * 16_000))
    }

    private func waitUntil(_ condition: () -> Bool) async throws {
        for _ in 0..<250 where !condition() {
            try await Task.sleep(for: .milliseconds(20))
        }
    }

    private static func kind(of phase: MeetingProcessingPhase) -> String {
        switch phase {
        case .preparing: return "preparing"
        case .loadingSpeakerModel: return "loading"
        case .separatingSpeakers(let track, _): return "separating-\(track.rawValue)"
        case .transcribing: return "transcribing"
        }
    }

    private func assertThrows(_ expected: SlovoError, file: StaticString = #filePath, line: UInt = #line,
                              _ body: () async throws -> Void) async {
        do {
            try await body()
            XCTFail("Expected \(expected.message)", file: file, line: line)
        } catch {
            XCTAssertEqual(error as? SlovoError, expected, file: file, line: line)
        }
    }
}

@MainActor
private final class FakeEngines: EngineProviding {
    let fake = FakeEngine()
    var loadError: Error?
    private(set) var markedUsed = 0
    private(set) var requests = 0

    func engineID(for purpose: EnginePurpose) -> EngineID {
        purpose == .meetings ? .parakeet : .gigaamRNNT
    }

    func loadState(of id: EngineID) -> EngineLoadState {
        .ready
    }

    func engine(for id: EngineID) async throws -> any TranscriptionEngine {
        requests += 1
        if let loadError {
            throw loadError
        }
        return fake
    }

    func preloadForCurrentSettings() {}

    func markUsed(_ id: EngineID) {
        markedUsed += 1
    }
}

/// Answers "loud words" for clips of the system track (amplitude 0.5) and "quiet
/// words" for the microphone track (0.2), after an optional cancellable delay.
private final class FakeEngine: TranscriptionEngine, @unchecked Sendable {
    let id = "fake"
    private let lock = NSLock()
    private var calls = 0
    private var missing = 0
    private var storedDelay: Duration?
    private var storedResponse: String?
    private var storedRequiredFile: URL?

    var delay: Duration? {
        get { lock.withLock { storedDelay } }
        set { lock.withLock { storedDelay = newValue } }
    }

    var response: String? {
        get { lock.withLock { storedResponse } }
        set { lock.withLock { storedResponse = newValue } }
    }

    /// A file that must still exist whenever a clip is transcribed.
    var requiredFile: URL? {
        get { lock.withLock { storedRequiredFile } }
        set { lock.withLock { storedRequiredFile = newValue } }
    }

    var callCount: Int {
        lock.withLock { calls }
    }

    var callsWithoutFile: Int {
        lock.withLock { missing }
    }

    func prepare() async throws {}

    func transcribe(fileURL: URL) async throws -> String {
        let required = lock.withLock { () -> URL? in
            calls += 1
            return storedRequiredFile
        }
        if let required, !FileManager.default.fileExists(atPath: required.path) {
            lock.withLock { missing += 1 }
        }
        if let delay {
            try await Task.sleep(for: delay)
        }
        if let response { return response }
        let samples = try AudioLoader.samples(from: fileURL)
        let peak = samples.map(abs).max() ?? 0
        return peak > 0.3 ? "loud words" : "quiet words"
    }
}

@MainActor
private final class FakeDiarization: DiarizationProviding {
    let fake = FakeDiarizer()
    var modelStatus: ModelStatus = .ready(diskBytes: DiarizationService.downloadBytes)
    var isSupportedOnThisSystem = true
    var loadError: Error?
    private(set) var diarizerRequests = 0
    private(set) var releases = 0

    func diarizer() async throws -> any SpeakerDiarizing {
        diarizerRequests += 1
        if let loadError {
            throw loadError
        }
        return fake
    }

    func release() {
        releases += 1
    }

    func waitForLoad() async {}
}

/// Returns the turns set for a file name ("mic.caf", "system.caf") and records the hint
/// it was given, reporting progress half way and at the end.
private final class FakeDiarizer: SpeakerDiarizing, @unchecked Sendable {
    private let lock = NSLock()
    private var storedTurns: [String: [SpeakerTurn]] = [:]
    private var storedHints: [String: SpeakerCountHint] = [:]
    private var storedError: Error?
    private var storedDelay: Duration?
    private var calls = 0

    var turns: [String: [SpeakerTurn]] {
        get { lock.withLock { storedTurns } }
        set { lock.withLock { storedTurns = newValue } }
    }

    var error: Error? {
        get { lock.withLock { storedError } }
        set { lock.withLock { storedError = newValue } }
    }

    var delay: Duration? {
        get { lock.withLock { storedDelay } }
        set { lock.withLock { storedDelay = newValue } }
    }

    /// Hints of the latest run; a run starts over when the microphone track comes again.
    var hints: [String: SpeakerCountHint] {
        lock.withLock { storedHints }
    }

    var callCount: Int {
        lock.withLock { calls }
    }

    func diarize(fileAt url: URL, hint: SpeakerCountHint,
                 progress: @escaping @Sendable (Double) -> Void) async throws -> [SpeakerTurn] {
        let name = url.lastPathComponent
        let (turns, error, delay) = lock.withLock { () -> ([SpeakerTurn], Error?, Duration?) in
            calls += 1
            if name == "mic.caf" {
                storedHints = [:]
            }
            storedHints[name] = hint
            return (storedTurns[name] ?? [], storedError, storedDelay)
        }
        progress(0.5)
        if let delay {
            try await Task.sleep(for: delay)
        }
        if let error {
            throw error
        }
        progress(1)
        return turns
    }
}

/// Forwards to a real store and records the order of saves and audio removal.
@MainActor
private final class SpyStore: MeetingStoring {
    let base: MeetingStore
    private(set) var events: [String] = []

    init(base: MeetingStore) {
        self.base = base
    }

    var meetings: [MeetingRecord] {
        base.meetings
    }

    func create(title: String) throws -> MeetingRecord {
        try base.create(title: title)
    }

    func update(_ meeting: MeetingRecord) {
        if !meeting.hasAudio, base.meeting(id: meeting.id)?.hasAudio == true {
            let transcript = FileManager.default.fileExists(atPath: base.transcriptURL(for: meeting.id).path)
            let saved = (try? JSONDecoder().decode(MeetingRecord.self,
                                                   from: Data(contentsOf: base.recordURL(for: meeting.id))))?.status
            events.append("removeAudio transcript=\(transcript) saved=\(saved?.rawValue ?? "none")")
        } else {
            events.append("update \(meeting.status.rawValue)")
        }
        base.update(meeting)
    }

    func delete(id: UUID) {
        base.delete(id: id)
    }

    func deleteAudio(id: UUID) {
        events.append("deleteAudio")
        base.deleteAudio(id: id)
    }

    var meetingsFolder: URL {
        base.meetingsFolder
    }

    func folder(for id: UUID) -> URL {
        base.folder(for: id)
    }

    func microphoneAudioURL(for id: UUID) -> URL {
        base.microphoneAudioURL(for: id)
    }

    func systemAudioURL(for id: UUID) -> URL {
        base.systemAudioURL(for: id)
    }

    func screenRecordingURL(for id: UUID) -> URL {
        base.screenRecordingURL(for: id)
    }

    func transcriptURL(for id: UUID) -> URL {
        base.transcriptURL(for: id)
    }

    func flush() throws {
        events.append("flush")
        try base.flush()
    }
}
