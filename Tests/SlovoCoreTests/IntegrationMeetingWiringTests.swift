import Combine
import CoreAudio
import Foundation
import XCTest
@testable import SlovoCore

/// `MeetingWiring` over the real recorder, store and speaker model service: the
/// mark-moment shortcut exists only while a meeting records and follows its setting, the
/// speaker model is released and a load in flight finishes before its files are deleted,
/// and the meeting pipeline separates speakers through the service the app hands it.
@MainActor
final class IntegrationMeetingWiringTests: XCTestCase {
    private var suiteName = ""
    private var defaults: UserDefaults!
    private var settings: AppSettings!
    private var temporary: URL!
    private var store: MeetingStore!

    override func setUp() async throws {
        suiteName = "slovo.tests.integration.meetings.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        settings = AppSettings(defaults: defaults)
        settings.registerDefaults()
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoIntegrationMeetingTests-\(UUID().uuidString)", isDirectory: true)
        let directories = AppDirectories(base: temporary)
        try directories.createFolders()
        store = MeetingStore(directories: directories)
        store.transcriptLanguage = { .ru }
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    // MARK: - Mark-moment shortcut

    func testHighlightShortcutIsRegisteredOnlyWhileAMeetingRecords() async throws {
        let recorder = MeetingRecorder(store: store, settings: settings)
        let registrar = Registrar()
        let wiring = MeetingWiring(settings: settings, recorder: recorder, shortcut: registrar,
                                   diarization: FakeSpeakerModel(log: EventLog()))
        wiring.start()
        defer { wiring.stop() }
        XCTAssertEqual(registrar.registrations, [])
        XCTAssertFalse(wiring.isShortcutRegistered)

        let meeting = try store.create(title: "Коворк")
        try recorder.startSessionWithoutCapture(meetingID: meeting.id, originHostTime: AudioGetCurrentHostTime())
        XCTAssertEqual(registrar.registrations, [.markMomentDefault])
        XCTAssertEqual(registrar.shortcut, .markMomentDefault)
        XCTAssertTrue(wiring.isShortcutRegistered)

        // Pressing the shortcut marks a moment in the meeting being recorded.
        registrar.fire()
        let marked = try XCTUnwrap(recorder.lastMarkedMoment)
        XCTAssertEqual(marked.source, .recording)
        XCTAssertEqual(store.meeting(id: meeting.id)?.highlights, [marked])

        // A new choice in Settings replaces the registration during the recording.
        let changed = HotkeyShortcut.markMomentChoices[1]
        settings.highlightShortcut = changed
        let followed = await DictationFakes.waitUntil { registrar.registrations.last == changed }
        XCTAssertTrue(followed)
        XCTAssertEqual(registrar.shortcut, changed)

        XCTAssertNotNil(recorder.stopImmediately())
        XCTAssertNil(registrar.shortcut)
        XCTAssertFalse(wiring.isShortcutRegistered)

        // Between recordings a settings change registers nothing.
        let later = HotkeyShortcut.markMomentChoices[2]
        settings.highlightShortcut = later
        XCTAssertFalse(wiring.applySettings())
        XCTAssertNil(registrar.shortcut)
        XCTAssertEqual(registrar.registrations, [.markMomentDefault, changed])

        // The next recording takes the setting as it is then.
        let next = try store.create(title: "Созвон")
        try recorder.startSessionWithoutCapture(meetingID: next.id, originHostTime: AudioGetCurrentHostTime())
        XCTAssertEqual(registrar.shortcut, later)
        XCTAssertNotNil(recorder.stopImmediately())
        XCTAssertNil(registrar.shortcut)

        // Once the wiring stops, a recording no longer registers anything.
        wiring.stop()
        let last = try store.create(title: "Ретро")
        try recorder.startSessionWithoutCapture(meetingID: last.id, originHostTime: AudioGetCurrentHostTime())
        XCTAssertNil(registrar.shortcut)
        XCTAssertEqual(registrar.registrations.count, 3)
        XCTAssertNotNil(recorder.stopImmediately())
    }

    func testRefusedShortcutIsReportedUntilARegistrationSucceeds() throws {
        let recorder = MeetingRecorder(store: store, settings: settings)
        let registrar = Registrar()
        registrar.refuses = true
        let wiring = MeetingWiring(settings: settings, recorder: recorder, shortcut: registrar,
                                   diarization: FakeSpeakerModel(log: EventLog()))
        var reported: [HotkeyShortcut?] = []
        let subscription = wiring.$refusedShortcut.dropFirst().sink { reported.append($0) }
        defer { subscription.cancel() }
        wiring.start()
        defer { wiring.stop() }

        let meeting = try store.create(title: "Коворк")
        try recorder.startSessionWithoutCapture(meetingID: meeting.id, originHostTime: AudioGetCurrentHostTime())
        XCTAssertFalse(wiring.isShortcutRegistered)
        XCTAssertEqual(wiring.refusedShortcut, .markMomentDefault)
        XCTAssertEqual(reported, [.markMomentDefault])

        // Unrelated settings do not retry a refused shortcut.
        settings.soundsEnabled = false
        XCTAssertFalse(wiring.applySettings())
        XCTAssertEqual(registrar.registrations.count, 1)

        // The warning stays after the recording, while that shortcut is still chosen.
        XCTAssertNotNil(recorder.stopImmediately())
        XCTAssertEqual(wiring.refusedShortcut, .markMomentDefault)

        registrar.refuses = false
        let accepted = HotkeyShortcut.markMomentChoices[1]
        settings.highlightShortcut = accepted
        let next = try store.create(title: "Созвон")
        try recorder.startSessionWithoutCapture(meetingID: next.id, originHostTime: AudioGetCurrentHostTime())
        XCTAssertTrue(wiring.isShortcutRegistered)
        XCTAssertEqual(registrar.shortcut, accepted)
        XCTAssertNil(wiring.refusedShortcut)
        XCTAssertEqual(reported, [.markMomentDefault, nil])
        XCTAssertNotNil(recorder.stopImmediately())
    }

    // MARK: - Microphone shortcut

    func testMicrophoneShortcutIsRegisteredOnlyWhileAMeetingRecords() async throws {
        XCTAssertEqual(HotkeyShortcut.microphoneToggleDefault, HotkeyShortcut(keyCode: 32, modifiers: [.control, .command]))
        XCTAssertEqual(settings.microphoneShortcut, .microphoneToggleDefault)
        for taken in HotkeyShortcut.markMomentChoices + [.pasteLastDefault] {
            XCTAssertFalse(HotkeyShortcut.microphoneToggleChoices.contains(taken), "\(taken) belongs to another action")
        }

        let recorder = MeetingRecorder(store: store, settings: settings)
        let marks = Registrar()
        let microphone = Registrar()
        let wiring = MeetingWiring(settings: settings, recorder: recorder, shortcut: marks, microphoneShortcut: microphone,
                                   diarization: FakeSpeakerModel(log: EventLog()))
        wiring.start()
        defer { wiring.stop() }
        XCTAssertEqual(microphone.registrations, [])
        XCTAssertFalse(wiring.isMicrophoneShortcutRegistered)

        let meeting = try store.create(title: "Созвон")
        try recorder.startSessionWithoutCapture(meetingID: meeting.id, originHostTime: AudioGetCurrentHostTime())
        XCTAssertEqual(microphone.registrations, [.microphoneToggleDefault])
        XCTAssertEqual(marks.registrations, [.markMomentDefault])
        XCTAssertTrue(wiring.isMicrophoneShortcutRegistered)

        // Each press switches the microphone of the meeting being recorded, and only that.
        microphone.fire()
        XCTAssertFalse(recorder.isMicrophoneOn)
        XCTAssertNil(recorder.lastMarkedMoment, "the microphone shortcut marks nothing")
        microphone.fire()
        XCTAssertTrue(recorder.isMicrophoneOn)
        marks.fire()
        XCTAssertNotNil(recorder.lastMarkedMoment)
        XCTAssertTrue(recorder.isMicrophoneOn, "marking a moment leaves the microphone alone")
        XCTAssertTrue(settings.recordMicrophone, "the shortcut changes the recording, not the setting")

        let changed = HotkeyShortcut.microphoneToggleChoices[1]
        settings.microphoneShortcut = changed
        let followed = await DictationFakes.waitUntil { microphone.registrations.last == changed }
        XCTAssertTrue(followed)
        XCTAssertEqual(marks.registrations, [.markMomentDefault], "the other shortcut is left as it was")

        XCTAssertNotNil(recorder.stopImmediately())
        XCTAssertNil(microphone.shortcut)
        XCTAssertNil(marks.shortcut)
        XCTAssertFalse(wiring.isMicrophoneShortcutRegistered)

        // A refusal is reported for this shortcut alone and leaves marking working.
        microphone.refuses = true
        let refused = HotkeyShortcut.microphoneToggleChoices[2]
        settings.microphoneShortcut = refused
        XCTAssertFalse(wiring.applySettings())
        XCTAssertNil(wiring.refusedMicrophoneShortcut, "nothing is attempted between recordings")
        let next = try store.create(title: "Ещё созвон")
        try recorder.startSessionWithoutCapture(meetingID: next.id, originHostTime: AudioGetCurrentHostTime())
        XCTAssertFalse(wiring.isMicrophoneShortcutRegistered)
        XCTAssertEqual(wiring.refusedMicrophoneShortcut, refused)
        XCTAssertNil(wiring.refusedShortcut)
        XCTAssertTrue(wiring.isShortcutRegistered)
        XCTAssertNotNil(recorder.stopImmediately())
        XCTAssertEqual(wiring.refusedMicrophoneShortcut, refused, "the warning stays while that shortcut is chosen")
    }

    // MARK: - Speaker model deletion

    func testDeletingTheSpeakerModelReleasesWaitsThenDeletes() async throws {
        let log = EventLog()
        let model = FakeSpeakerModel(log: log)
        let wiring = MeetingWiring(settings: settings, shortcut: Registrar(), diarization: model,
                                   isRecording: Just(false).eraseToAnyPublisher(), markMoment: {})

        try await wiring.deleteDiarizationModel()
        XCTAssertEqual(log.events, ["release", "wait", "delete"])

        model.deleteError = SlovoError("busy")
        // XCTAssertThrowsError takes a synchronous autoclosure, so the async call is checked by hand.
        do {
            try await wiring.deleteDiarizationModel()
            XCTFail("A refused delete must be reported")
        } catch {
            XCTAssertEqual(error.localizedDescription, "busy")
        }
        XCTAssertEqual(log.events, ["release", "wait", "delete", "release", "wait", "delete"])
    }

    func testSpeakerModelFilesStayUntilALoadInFlightEnds() async throws {
        let modelsRoot = temporary.appendingPathComponent("Models", isDirectory: true)
        try DiarizationFixtures.installModel(in: modelsRoot.appendingPathComponent("diarization", isDirectory: true))
        let gate = LoadGate()
        let service = DiarizationService(modelsRoot: modelsRoot, downloader: RefusingDownloader()) { _ in
            await gate.wait()
            return TurnsDiarizer(turns: [])
        }
        guard case .ready = service.status else {
            return XCTFail("the installed model must read as ready, got \(service.status)")
        }
        let wiring = MeetingWiring(settings: settings, shortcut: Registrar(), diarization: service,
                                   isRecording: Just(false).eraseToAnyPublisher(), markMoment: {})

        let loading = Task { try await service.diarizer() }
        await DiarizationFixtures.waitUntil { service.loadState == .loading }
        let deleting = Task { try await wiring.deleteDiarizationModel() }
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(FileManager.default.fileExists(atPath: service.modelFolder.path),
                      "files must stay while the load reads them")

        gate.open()
        try await deleting.value
        _ = try? await loading.value
        XCTAssertFalse(FileManager.default.fileExists(atPath: service.modelFolder.path))
        XCTAssertEqual(service.status, .notDownloaded(downloadBytes: DiarizationService.downloadBytes))
        XCTAssertEqual(service.loadState, .unloaded)
    }

    // MARK: - Meeting pipeline

    /// The app builds `MeetingTranscriber(store:engines:settings:diarization:)` with its
    /// `DiarizationService`; this runs the same composition over a fake model load.
    func testMeetingPipelineSeparatesSpeakersThroughTheDiarizationService() async throws {
        let modelsRoot = temporary.appendingPathComponent("Models", isDirectory: true)
        try DiarizationFixtures.installModel(in: modelsRoot.appendingPathComponent("diarization", isDirectory: true))
        let diarizer = TurnsDiarizer(turns: [SpeakerTurn(start: 0.9, end: 7.1, cluster: 0),
                                             SpeakerTurn(start: 8.9, end: 15.1, cluster: 1)])
        let loads = EventLog()
        let service = DiarizationService(modelsRoot: modelsRoot, downloader: RefusingDownloader()) { folder in
            loads.events.append(folder.lastPathComponent)
            return diarizer
        }
        let engines = DictationFakes.Engines()
        engines.engines[.parakeet] = DictationFakes.Engine(id: EngineID.parakeet.rawValue, text: "слово")
        let transcriber = MeetingTranscriber(store: store, engines: engines, settings: settings, diarization: service)

        var meeting = try store.create(title: "Коворк")
        let microphone = silence(1) + tone(6) + silence(2) + tone(6) + silence(1)
        let writer = try MeetingTrackWriter(url: store.microphoneAudioURL(for: meeting.id))
        writer.append(microphone)
        writer.finish()
        meeting.status = .recorded
        meeting.duration = Double(microphone.count) / 16_000
        store.update(meeting)

        let result = try await transcriber.transcribe(meetingID: meeting.id)

        XCTAssertEqual(loads.events, ["diarization"], "the service loads from its own model folder")
        XCTAssertEqual(diarizer.files, ["mic.caf"])
        XCTAssertEqual(result.diarization?.state, .applied)
        XCTAssertEqual(result.diarization?.model, DiarizationService.modelIdentifier)
        XCTAssertEqual(result.speakers.map { TranscriptMerger.speakerLabel($0, language: .ru) }, ["Спикер 1", "Спикер 2"])
        XCTAssertEqual(Set(result.segments.map { result.speakerID(for: $0) }), Set(result.speakers.map(\.id)))
        // Core ML state is not kept between rare meetings.
        XCTAssertEqual(service.loadState, .unloaded)
        let transcript = try String(contentsOf: store.transcriptURL(for: meeting.id), encoding: .utf8)
        XCTAssertTrue(transcript.contains("Спикер 1: слово"), transcript)
        XCTAssertTrue(transcript.contains("Спикер 2: слово"), transcript)
    }

    // MARK: - Helpers

    private func tone(_ seconds: Double) -> [Float] {
        (0..<Int(seconds * 16_000)).map { 0.2 * Float(sin(2 * Double.pi * 200 * Double($0) / 16_000)) }
    }

    private func silence(_ seconds: Double) -> [Float] {
        [Float](repeating: 0, count: Int(seconds * 16_000))
    }

    @MainActor
    final class Registrar: GlobalShortcutRegistering {
        var shortcut: HotkeyShortcut?
        var refuses = false
        private(set) var registrations: [HotkeyShortcut] = []
        private var action: (@MainActor () -> Void)?

        func register(_ shortcut: HotkeyShortcut, action: @escaping @MainActor () -> Void) -> Bool {
            registrations.append(shortcut)
            guard !refuses else {
                self.shortcut = nil
                self.action = nil
                return false
            }
            self.shortcut = shortcut
            self.action = action
            return true
        }

        func unregister() {
            shortcut = nil
            action = nil
        }

        func fire() {
            action?()
        }
    }

    @MainActor
    final class EventLog {
        var events: [String] = []
    }

    @MainActor
    final class FakeSpeakerModel: DiarizationProviding, DiarizationModelManaging {
        let log: EventLog
        var deleteError: Error?
        var status: ModelStatus = .ready(diskBytes: 1)
        var lastError: String?

        init(log: EventLog) {
            self.log = log
        }

        var modelStatus: ModelStatus {
            status
        }

        var isSupportedOnThisSystem: Bool {
            true
        }

        func diarizer() async throws -> any SpeakerDiarizing {
            throw SlovoError("Not used by these tests")
        }

        func release() {
            log.events.append("release")
        }

        func waitForLoad() async {
            log.events.append("wait")
        }

        func refresh() {}

        func startDownload() {}

        func cancelDownload() {}

        func delete() throws {
            log.events.append("delete")
            if let deleteError {
                throw deleteError
            }
            status = .notDownloaded(downloadBytes: DiarizationService.downloadBytes)
        }
    }

    /// Holds a model load until the test opens it.
    @MainActor
    final class LoadGate {
        private var isOpen = false
        private var waiters: [CheckedContinuation<Void, Never>] = []

        func wait() async {
            guard !isOpen else { return }
            await withCheckedContinuation { waiters.append($0) }
        }

        func open() {
            isOpen = true
            let pending = waiters
            waiters = []
            pending.forEach { $0.resume() }
        }
    }

    /// Nothing may be downloaded in tests.
    struct RefusingDownloader: DiarizationModelDownloading {
        func download(to folder: URL, progress: @escaping @Sendable (Double) -> Void) async throws {
            throw SlovoError("Downloads are not allowed in tests")
        }
    }

    /// Returns fixed turns for every file and records which files it was given.
    final class TurnsDiarizer: SpeakerDiarizing, @unchecked Sendable {
        private let lock = NSLock()
        private let turns: [SpeakerTurn]
        private var received: [String] = []

        init(turns: [SpeakerTurn]) {
            self.turns = turns
        }

        var files: [String] {
            lock.withLock { received }
        }

        func diarize(fileAt url: URL, hint: SpeakerCountHint,
                     progress: @escaping @Sendable (Double) -> Void) async throws -> [SpeakerTurn] {
            lock.withLock { received.append(url.lastPathComponent) }
            progress(1)
            return turns
        }
    }
}
