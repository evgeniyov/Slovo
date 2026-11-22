import Foundation
import XCTest
@testable import SlovoCore

/// The inbox importer against a temporary folder: a clip that arrives with the phone's
/// sidecar and one that arrives alone, the moments marked on the phone, a sidecar whose
/// audio never comes, the options each kind of clip asks for, and the source a meeting
/// keeps once it is saved.
@MainActor
final class InboxImporterTests: XCTestCase {
    private var temporary: URL!
    private var directories: AppDirectories!
    private var store: MeetingStore!
    private var settings: AppSettings!
    private var suiteName = ""
    private var defaults: UserDefaults!

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoInboxTests-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)
        try directories.createFolders()
        store = MeetingStore(directories: directories)
        suiteName = "slovo.tests.inbox.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suiteName)
        settings = AppSettings(defaults: defaults)
        settings.inboxEnabled = true
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    // MARK: - Clips

    func testAClipWithoutASidecarIsNamedAndSortedByTheSameInstant() async throws {
        let now = Date()
        let dropped = now.addingTimeInterval(-600)
        try writeClip(named: "voice-message.caf", seconds: 2, modified: dropped)

        await makeImporter().sweep(now: now)

        let meeting = try XCTUnwrap(store.meetings.first)
        XCTAssertEqual(store.meetings.count, 1)
        // The store normalizes whitespace, and a medium date holds a narrow no-break space.
        XCTAssertEqual(meeting.title, TranscriptMerger.normalizedText(MeetingTitles.defaultTitle(for: dropped)))
        // The file's own date names the meeting, so it is also where the meeting sits in the
        // list: a title reading one time while the list means another is a bug, not a detail.
        XCTAssertEqual(meeting.createdAt.timeIntervalSince(dropped), 0, accuracy: 1)
        // The file's date can be older than its arrival — a copy that kept it — so retention
        // counts from the import instead.
        XCTAssertEqual(meeting.audioStoredAt, now)
        XCTAssertNil(meeting.source)
        XCTAssertEqual(meeting.highlights, [])
        XCTAssertEqual(meeting.duration, 2, accuracy: 0.05)
        XCTAssertTrue(meeting.hasAudio)
        XCTAssertFalse(FileManager.default.fileExists(atPath: inbox("voice-message.caf").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: imported("voice-message.caf").path))
    }

    func testTheSidecarGivesTheMeetingItsTimeTitleSourceAndMoments() async throws {
        let recordedAt = try XCTUnwrap(Self.date("2026-09-16T12:54:58+06:00"))
        try writeClip(named: "slovo-20260916-125458.caf", seconds: 6, modified: Date(timeIntervalSinceNow: -60))
        try writeSidecar(named: "slovo-20260916-125458.json", object: [
            "version": 1,
            "kind": "meeting",
            "recordedAt": "2026-09-16T12:54:58+06:00",
            "durationMs": 6_000,
            "device": "Demo Phone",
            "title": "Созвон с Аней",
            "highlights": [
                ["atMs": 1_500, "note": "дедлайн"],
                ["atMs": 4_200, "note": NSNull()],
                // Past the end of the clip, and one entry with no position at all.
                ["atMs": 99_000_000, "note": NSNull()],
                ["note": "no time"]
            ]
        ])

        await makeImporter().sweep()

        let meeting = try XCTUnwrap(store.meetings.first)
        XCTAssertEqual(meeting.title, "Созвон с Аней")
        XCTAssertEqual(meeting.createdAt, recordedAt)
        XCTAssertEqual(meeting.source, MeetingSource(origin: .phone, device: "Demo Phone"))
        XCTAssertEqual(meeting.highlights.map(\.time), [1.5, 4.2, meeting.duration])
        XCTAssertEqual(meeting.highlights.map(\.note), ["дедлайн", nil, nil])
        XCTAssertEqual(meeting.highlights.map(\.source), [.recording, .recording, .recording])
        // A moment was made at the moment it names, so it sorts with the marks made here.
        XCTAssertEqual(meeting.highlights.first?.createdAt, recordedAt.addingTimeInterval(1.5))
        XCTAssertTrue(FileManager.default.fileExists(atPath: imported("slovo-20260916-125458.json").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: imported("slovo-20260916-125458.caf").path))
    }

    func testAClipRecordedDaysAgoOnThePhoneDoesNotArriveWithItsAudioAlreadyExpired() async throws {
        let now = Date()
        let recordedAt = now.addingTimeInterval(-8 * 86_400)
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        try writeClip(named: "slovo-20260908-090000.caf", seconds: 2, modified: now.addingTimeInterval(-60))
        try writeSidecar(named: "slovo-20260908-090000.json", object: [
            "version": 1, "kind": "meeting", "recordedAt": formatter.string(from: recordedAt)
        ])

        await makeImporter().sweep(now: now)

        let meeting = try XCTUnwrap(store.meetings.first)
        // The meeting is shown and sorted by when the phone recorded it.
        XCTAssertEqual(meeting.createdAt.timeIntervalSince(recordedAt), 0, accuracy: 1)
        // Its audio, though, is a minute old here, and a week of keeping it starts now.
        XCTAssertEqual(meeting.audioStoredAt, now)
        XCTAssertEqual(RetentionCleaner.expiredMeetingAudioIDs([meeting], keep: .days7, now: now), [])
        XCTAssertEqual(RetentionCleaner.expiredMeetingAudioIDs([meeting], keep: .days7,
                                                               now: now.addingTimeInterval(8 * 86_400)), [meeting.id])
    }

    func testAMalformedSidecarCostsTheMetadataAndNotTheClip() async throws {
        let dropped = Date(timeIntervalSinceNow: -600)
        try writeClip(named: "slovo-20260916-131002.caf", seconds: 2, modified: dropped)
        try Data("{ \"kind\": \"meeting\", this is not JSON".utf8).write(to: inbox("slovo-20260916-131002.json"))

        await makeImporter().sweep()

        let meeting = try XCTUnwrap(store.meetings.first)
        XCTAssertEqual(meeting.title, TranscriptMerger.normalizedText(MeetingTitles.defaultTitle(for: dropped)))
        XCTAssertNil(meeting.source)
        XCTAssertEqual(meeting.highlights, [])
        // The unreadable sidecar leaves with its clip; left behind it would later look like
        // metadata waiting for audio.
        XCTAssertTrue(FileManager.default.fileExists(atPath: imported("slovo-20260916-131002.json").path))
    }

    func testASidecarWhoseAudioNeverArrivesIsGivenUpOn() async throws {
        try writeSidecar(named: "slovo-20260916-140000.json", object: ["version": 1, "kind": "note"],
                         modified: Date(timeIntervalSinceNow: -InboxImporter.strandedSidecarSeconds - 60))
        try writeSidecar(named: "slovo-20260916-141500.json", object: ["version": 1, "kind": "note"],
                         modified: Date())
        let importer = makeImporter()

        await importer.sweep()

        XCTAssertEqual(store.meetings, [])
        XCTAssertTrue(FileManager.default.fileExists(atPath: imported("slovo-20260916-140000.json").path))
        // The fresh one is still waiting for a clip that may be seconds away.
        XCTAssertTrue(FileManager.default.fileExists(atPath: inbox("slovo-20260916-141500.json").path))

        await importer.sweep()
        XCTAssertEqual(store.meetings, [])
    }

    // MARK: - Failures and interrupted imports

    func testAClipThatFailedIsTriedAgainOnceTheFileChanges() async throws {
        let name = "slovo-20260916-150000.caf"
        try Data("not audio at all".utf8).write(to: inbox(name))
        try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: -600)],
                                              ofItemAtPath: inbox(name).path)
        let importer = makeImporter()

        await importer.sweep()
        XCTAssertEqual(store.meetings, [])
        XCTAssertTrue(FileManager.default.fileExists(atPath: inbox(name).path), "a broken clip stays where it was")
        XCTAssertTrue(importer.readyFiles().isEmpty, "and is not tried again while it is unchanged")

        // The copy completes, or the user drops a good file under the same name: a new size
        // and date make it a new file, taken without a relaunch.
        try FileManager.default.removeItem(at: inbox(name))
        try writeClip(named: name, seconds: 2, modified: Date(timeIntervalSinceNow: -60))
        await importer.sweep()
        XCTAssertEqual(store.meetings.count, 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: imported(name).path))
    }

    func testAnImportLeavesNothingHiddenBehind() async throws {
        try writeClip(named: "voice-message.caf", seconds: 1, modified: Date(timeIntervalSinceNow: -60))
        await makeImporter().sweep()
        let left = try FileManager.default.contentsOfDirectory(atPath: directories.inboxFolder.path)
        XCTAssertEqual(left, [InboxImporter.importedFolderName])
    }

    /// The app quit or crashed after the meeting was created and before the clip reached
    /// `Imported/`: the next sweep finishes that meeting rather than making a second one.
    func testAnImportCutOffAfterItsMeetingWasCreatedFinishesThatMeeting() async throws {
        let created = try store.create(title: "Созвон с Аней")
        let trackURL = store.microphoneAudioURL(for: created.id)
        try MeetingClip.write([Float](repeating: 0, count: 16_000), to: trackURL)
        let name = "slovo-20260916-125458.caf"
        let claimed = InboxImporter.claimName(for: created.id, name: name)
        try writeClip(named: claimed, seconds: 3, modified: Date(timeIntervalSinceNow: -60))
        try writeSidecar(named: "slovo-20260916-125458.json", object: [
            "version": 1, "kind": "meeting", "device": "Demo Phone",
            "highlights": [["atMs": 1_000, "note": "дедлайн"]]
        ], modified: Date(timeIntervalSinceNow: -InboxImporter.strandedSidecarSeconds - 60))

        await makeImporter().sweep()

        XCTAssertEqual(store.meetings.count, 1, "the clip became the meeting it was already becoming")
        let meeting = try XCTUnwrap(store.meetings.first)
        XCTAssertEqual(meeting.id, created.id)
        XCTAssertTrue(meeting.hasAudio)
        XCTAssertEqual(meeting.duration, 3, accuracy: 0.05)
        XCTAssertEqual(MeetingClip.duration(of: trackURL), 3, accuracy: 0.05)
        XCTAssertEqual(meeting.status, .transcribed)
        XCTAssertEqual(meeting.source, MeetingSource(origin: .phone, device: "Demo Phone"))
        XCTAssertEqual(meeting.highlights.map(\.note), ["дедлайн"])
        XCTAssertTrue(FileManager.default.fileExists(atPath: imported(name).path), "the clip moved on under its own name")
        XCTAssertTrue(FileManager.default.fileExists(atPath: imported("slovo-20260916-125458.json").path),
                      "the sidecar went with it rather than being given up on")
        XCTAssertFalse(FileManager.default.fileExists(atPath: inbox(claimed).path))
    }

    func testAClaimWhoseMeetingWasNeverSavedIsImportedAfresh() async throws {
        let name = "voice-message.caf"
        let orphan = MCPFixture.uuid("0BADF00D-0000-4000-8000-000000000001")
        try writeClip(named: InboxImporter.claimName(for: orphan, name: name), seconds: 2,
                      modified: Date(timeIntervalSinceNow: -60))

        await makeImporter().sweep()

        XCTAssertEqual(store.meetings.count, 1)
        XCTAssertNotEqual(store.meetings.first?.id, orphan)
        XCTAssertTrue(FileManager.default.fileExists(atPath: imported(name).path))
    }

    func testAClaimedClipWhoseMeetingWentFurtherIsOnlyPutAway() async throws {
        var created = try store.create(title: "Уже расшифрована")
        created.status = .transcribed
        created.duration = 42
        store.update(created)
        let name = "voice-message.caf"
        try writeClip(named: InboxImporter.claimName(for: created.id, name: name), seconds: 2,
                      modified: Date(timeIntervalSinceNow: -60))

        await makeImporter().sweep()

        XCTAssertEqual(store.meetings.count, 1)
        XCTAssertEqual(store.meetings.first?.duration, 42, "a meeting taken further is not refilled")
        XCTAssertTrue(FileManager.default.fileExists(atPath: imported(name).path))
    }

    func testClaimNamesRoundTripAndOthersAreNotClaims() throws {
        let id = MCPFixture.uuid("3F2A9C1E-5B7D-4C1A-9E0F-2B6D8A4C7E11")
        let name = InboxImporter.claimName(for: id, name: "Запись 1.m4a")
        XCTAssertTrue(name.hasPrefix("."), "a claimed clip is hidden from the sweep")
        let parsed = try XCTUnwrap(InboxImporter.parseClaimName(name))
        XCTAssertEqual(parsed.0, id)
        XCTAssertEqual(parsed.1, "Запись 1.m4a")
        for other in ["Запись 1.m4a", ".importing-not-a-uuid-clip.m4a", ".importing-\(id.uuidString)-notes.txt",
                      ".importing-\(id.uuidString)", ".partial-clip.m4a"] {
            XCTAssertNil(InboxImporter.parseClaimName(other), other)
        }
    }

    // MARK: - The sidecar itself

    func testSidecarParsingFollowsTheContractAndDropsWhatItCannotRead() throws {
        try Data("{ not JSON at all".utf8).write(to: inbox("broken.json"))
        XCTAssertNil(InboxSidecar.read(from: inbox("broken.json")))
        XCTAssertNil(InboxSidecar.read(from: inbox("never-written.json")))

        let sidecar = InboxSidecar([
            "version": 1, "kind": "meeting", "recordedAt": "2026-09-16T12:54:58+06:00", "durationMs": 15_700,
            "device": "Demo Phone", "title": NSNull(),
            "highlights": [["atMs": 123_456, "note": NSNull()]]
        ])
        XCTAssertEqual(sidecar.kind, .meeting)
        XCTAssertEqual(sidecar.recordedAt, Self.date("2026-09-16T12:54:58+06:00"))
        XCTAssertEqual(sidecar.device, "Demo Phone")
        XCTAssertNil(sidecar.title)
        XCTAssertEqual(sidecar.moments, [InboxSidecar.Moment(time: 123.456, note: nil)])

        // What a newer phone build, or a write that was cut off, can leave behind.
        let odd = InboxSidecar([
            "version": 7, "kind": "podcast", "recordedAt": "yesterday",
            "device": String(repeating: "п", count: 200), "title": "  Планёрка\nв 10  ",
            "highlights": ["not an object", ["note": "no position"], ["atMs": -5], ["atMs": Double.nan]]
        ])
        XCTAssertEqual(odd.kind, .note)
        XCTAssertNil(odd.recordedAt)
        XCTAssertEqual(odd.device?.count, MeetingSource.maximumDeviceCharacters)
        XCTAssertEqual(odd.title, "Планёрка в 10")
        XCTAssertEqual(odd.moments, [])

        XCTAssertEqual(InboxSidecar.url(for: inbox("slovo-20260916-125458.m4a")).lastPathComponent,
                       "slovo-20260916-125458.json")
    }

    func testProcessingOptionsFollowTheKindOfClip() {
        settings.speakerSeparation = true
        settings.expectedSpeakers = 4
        XCTAssertEqual(InboxImporter.processingOptions(for: .meeting, settings: settings),
                       MeetingProcessingOptions(mode: .full, separateSpeakers: true, expectedSpeakers: 4))
        XCTAssertEqual(InboxImporter.processingOptions(for: .note, settings: settings),
                       MeetingProcessingOptions(mode: .full, separateSpeakers: false))

        settings.speakerSeparation = false
        XCTAssertFalse(InboxImporter.processingOptions(for: .meeting, settings: settings).separateSpeakers)
        XCTAssertFalse(InboxImporter.processingOptions(for: .note, settings: settings).separateSpeakers)
    }

    // MARK: - The stored source

    func testTheSourceOfAMeetingSurvivesASaveAndLoad() throws {
        var meeting = try store.create(title: "С телефона")
        meeting.source = MeetingSource(origin: .phone, device: "Demo Phone")
        store.update(meeting)
        try store.flush()

        let reloaded = try XCTUnwrap(MeetingStore(directories: directories).meeting(id: meeting.id))
        XCTAssertEqual(reloaded.source, MeetingSource(origin: .phone, device: "Demo Phone"))

        // Nothing is written for a meeting recorded here.
        let local = try JSONSerialization.jsonObject(with: JSONEncoder().encode(MeetingRecord(title: "Здесь")))
        XCTAssertNil((local as? [String: Any])?["source"])

        // An origin a later build writes leaves the record without a source rather than
        // naming the wrong one, and costs nothing else in the file.
        let encoded = try JSONEncoder().encode(MeetingRecord(title: "Со смартчасов",
                                                             source: MeetingSource(origin: .phone, device: "vivo")))
        let fromLater = String(decoding: encoded, as: UTF8.self).replacingOccurrences(of: "\"phone\"", with: "\"watch\"")
        let decoded = try JSONDecoder().decode(MeetingRecord.self, from: Data(fromLater.utf8))
        XCTAssertNil(decoded.source)
        XCTAssertEqual(decoded.title, "Со смартчасов")
    }

    // MARK: - Manual import

    func testAPickedFileIsNamedAfterItselfAndLeftWhereItWas() async throws {
        let modified = Date(timeIntervalSinceNow: -3600)
        let url = try writeManualFile(named: "Обсуждение БФЛ.caf", seconds: 2, modified: modified)

        let meeting = try await makeImporter().importPickedFile(at: url)

        XCTAssertEqual(store.meetings.count, 1)
        XCTAssertEqual(meeting.title, "Обсуждение БФЛ")
        XCTAssertEqual(meeting.createdAt.timeIntervalSince(modified), 0, accuracy: 1)
        XCTAssertEqual(meeting.duration, 2, accuracy: 0.05)
        XCTAssertTrue(meeting.hasAudio)
        // A manual pick is the user's own file, sitting wherever they keep it — unlike the
        // Inbox sweep, it is never moved or deleted.
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path))
    }

    func testAPickedFileTooShortToBeSpeechIsRefused() async throws {
        let url = try writeManualFile(named: "tap.caf", seconds: 0.1, modified: Date())

        do {
            _ = try await makeImporter().importPickedFile(at: url)
            XCTFail("The short recording was imported")
        } catch {
            XCTAssertEqual(error as? SlovoError, MeetingErrors.fileTooShort())
        }
        XCTAssertEqual(store.meetings.count, 0)
    }

    // MARK: - Helpers

    private func makeImporter() -> InboxImporter {
        InboxImporter(directories: directories, store: store,
                      transcriber: MeetingTranscriber(store: store, engines: FakeInboxEngines()),
                      settings: settings)
    }

    private func inbox(_ name: String) -> URL {
        directories.inboxFolder.appendingPathComponent(name)
    }

    private func imported(_ name: String) -> URL {
        directories.inboxFolder
            .appendingPathComponent(InboxImporter.importedFolderName, isDirectory: true)
            .appendingPathComponent(name)
    }

    /// A clip in the inbox: a quiet tone, dated far enough back for the importer to take it.
    private func writeClip(named name: String, seconds: Double, modified: Date) throws {
        let url = inbox(name)
        let samples = (0..<Int(seconds * AudioLoader.sampleRate)).map { frame in
            0.2 * Float(sin(2 * Double.pi * 200 * Double(frame) / AudioLoader.sampleRate))
        }
        try MeetingClip.write(samples, to: url)
        try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path)
    }

    /// A file the user picked from anywhere on disk — outside the Inbox folder, since a
    /// manual import never depends on it.
    private func writeManualFile(named name: String, seconds: Double, modified: Date) throws -> URL {
        let url = temporary.appendingPathComponent(name)
        let samples = (0..<Int(seconds * AudioLoader.sampleRate)).map { frame in
            0.2 * Float(sin(2 * Double.pi * 200 * Double(frame) / AudioLoader.sampleRate))
        }
        try MeetingClip.write(samples, to: url)
        try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path)
        return url
    }

    private func writeSidecar(named name: String, object: [String: Any], modified: Date = Date()) throws {
        let url = inbox(name)
        try JSONSerialization.data(withJSONObject: object).write(to: url)
        try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path)
    }

    private static func date(_ text: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: text)
    }
}

/// Answers with one line, so an import runs to the end without a speech model.
@MainActor
private final class FakeInboxEngines: EngineProviding {
    private let engine = FakeInboxEngine()

    func engineID(for purpose: EnginePurpose) -> EngineID { .parakeet }

    func loadState(of id: EngineID) -> EngineLoadState { .ready }

    func engine(for id: EngineID) async throws -> any TranscriptionEngine { engine }

    func preloadForCurrentSettings() {}

    func markUsed(_ id: EngineID) {}
}

private final class FakeInboxEngine: TranscriptionEngine, @unchecked Sendable {
    let id = "fake"

    func prepare() async throws {}

    func transcribe(fileURL: URL) async throws -> String { "слова" }
}
