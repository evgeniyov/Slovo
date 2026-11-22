import AVFoundation
import Foundation
import XCTest
@testable import SlovoCore

@MainActor
final class PublicReviewRegressionTests: XCTestCase {
    private var directories: AppDirectories!
    private var settings: AppSettings!
    private var suite = ""

    override func setUp() async throws {
        suite = "slovo.tests.public-review.\(UUID())"
        settings = AppSettings(defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        settings.registerDefaults()
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(suite, isDirectory: true)
        directories = AppDirectories(base: base)
        try directories.createFolders()
    }

    override func tearDown() async throws {
        settings.defaults.removePersistentDomain(forName: suite)
        try? FileManager.default.removeItem(at: directories.base)
    }

    func testFailedHistoryDeletionKeepsTheAudioReferenceAndCanBeRetried() async throws {
        let history = HistoryStore(directories: directories)
        let entry = makeEntry("keep", audio: "day/locked.wav")
        let audio = try writeAudio(to: "day/locked.wav")
        history.add(entry)
        history.add(makeEntry("remove"))
        try history.flush()

        try setLocked(true, at: audio)
        defer { try? setLocked(false, at: audio) }

        history.removeAudio(for: entry.id)
        XCTAssertEqual(history.audioURL(for: entry), audio)
        XCTAssertEqual(history.entries.first(where: { $0.id == entry.id })?.audioFileName, entry.audioFileName)
        XCTAssertFalse(history.deleteEntry(id: entry.id))

        XCTAssertFalse(history.clearHistory())
        XCTAssertEqual(history.entries, [entry])
        XCTAssertEqual(HistoryStore(directories: directories).entries, [entry])
        XCTAssertTrue(history.hasHistoryToDelete)

        try setLocked(false, at: audio)
        XCTAssertTrue(history.clearHistory())
        XCTAssertTrue(HistoryStore(directories: directories).entries.isEmpty)
        XCTAssertFalse(history.hasHistoryToDelete)
    }

    func testClearHistoryReportsSaveFailureAndCanPersistTheDeletionOnRetry() async throws {
        let history = HistoryStore(directories: directories)
        let entry = makeEntry("saved dictation")
        history.add(entry)
        try history.flush()

        let file = directories.historyFile
        try setLocked(true, at: file)
        defer {
            if FileManager.default.fileExists(atPath: file.path) {
                try? setLocked(false, at: file)
            }
        }

        XCTAssertFalse(history.clearHistory())
        XCTAssertTrue(history.hasHistoryToDelete)
        XCTAssertEqual(HistoryStore(directories: directories).entries, [entry])

        try setLocked(false, at: file)
        XCTAssertTrue(history.clearHistory())
        XCTAssertFalse(history.hasHistoryToDelete)
        XCTAssertTrue(HistoryStore(directories: directories).entries.isEmpty)
    }

    func testClearHistoryCanRetryAnOrphanWithoutHistoryEntries() async throws {
        let history = HistoryStore(directories: directories)
        let orphan = try writeAudio(to: "orphan.wav")
        try setLocked(true, at: orphan)
        defer { try? setLocked(false, at: orphan) }

        XCTAssertTrue(history.entries.isEmpty)
        XCTAssertFalse(history.clearHistory())
        XCTAssertTrue(history.hasHistoryToDelete)
        try setLocked(false, at: orphan)
        XCTAssertTrue(history.clearHistory())
        XCTAssertFalse(history.hasHistoryToDelete)
    }

    func testExplicitFlushNotifiesOnceAndRetriesWithoutAnotherChange() async throws {
        let blocked = directories.base.appendingPathComponent("blocked")
        let url = blocked.appendingPathComponent("test-history.json")
        try Data().write(to: blocked)
        let storage = StoreFile<[String]>(url: url, retryDelay: .milliseconds(25))
        let messages = DictationFakes.Locked<[String]>([])
        let observer = NotificationCenter.default.addObserver(forName: StoreFileEvents.saveFailed, object: nil,
                                                              queue: nil) { notice in
            if notice.userInfo?["file"] as? String == url.lastPathComponent,
               let message = notice.userInfo?["message"] as? String {
                messages.value.append(message)
            }
        }
        defer { NotificationCenter.default.removeObserver(observer) }

        storage.scheduleSave(["latest"])
        XCTAssertThrowsError(try storage.flush())
        XCTAssertThrowsError(try storage.flush())
        XCTAssertEqual(messages.value.count, 1)
        try FileManager.default.removeItem(at: blocked)
        let recovered = await DictationFakes.waitUntil { FileManager.default.fileExists(atPath: url.path) }
        XCTAssertTrue(recovered)
        XCTAssertEqual(try JSONDecoder().decode([String].self, from: Data(contentsOf: url)), ["latest"])
    }

    func testPasteLastUsesTheRemainingHistoryAfterDeleteRetentionAndClear() async throws {
        let now = Date()
        let history = HistoryStore(directories: directories)
        let inserter = DictationFakes.Inserter()
        let controller = makeController(history: history, inserter: inserter, recorder: DictationFakes.Recorder())
        let latest = makeEntry("newest", date: now)
        history.add(makeEntry("expired", date: now.addingTimeInterval(-2 * 86_400)))
        history.add(makeEntry("previous", date: now.addingTimeInterval(-60)))
        history.add(latest)

        XCTAssertTrue(history.deleteEntry(id: latest.id))
        XCTAssertEqual(controller.lastTranscript, "previous")
        await controller.pasteLastTranscript()
        XCTAssertEqual(inserter.inserted, ["previous"])

        settings.keepHistory = .hours24
        let cleaner = RetentionCleaner(history: history, meetings: MeetingStore(directories: directories), settings: settings)
        XCTAssertEqual(cleaner.run(now: now.addingTimeInterval(86_400)).removedEntries, 2)
        XCTAssertNil(controller.lastTranscript)
        await controller.pasteLastTranscript()
        XCTAssertEqual(inserter.inserted, ["previous"])

        history.add(makeEntry("another"))
        XCTAssertTrue(history.clearHistory())
        XCTAssertNil(controller.lastTranscript)
        await controller.pasteLastTranscript()
        XCTAssertEqual(inserter.inserted, ["previous"])
    }

    func testRetentionPreviewCountsHistoryAudioOnceAndRunCountsOnlySuccessfulRemovals() async throws {
        let now = Date()
        let history = HistoryStore(directories: directories)
        let cleaner = RetentionCleaner(history: history, meetings: MeetingStore(directories: directories), settings: settings)
        let historyOnly = makeEntry("history only", date: now.addingTimeInterval(-2 * 86_400), audio: "day/short.wav")
        let both = makeEntry("both", date: now.addingTimeInterval(-8 * 86_400), audio: "day/long.wav")
        let first = try writeAudio(to: "day/short.wav")
        let second = try writeAudio(to: "day/long.wav")
        history.add(historyOnly)
        history.add(both)
        settings.keepHistory = .hours24
        settings.keepDictationAudio = .days7

        XCTAssertEqual(cleaner.preview(now: now), RetentionPreview(entries: 2, dictationAudio: 2, audioBytes: 6))
        try setLocked(true, at: second)
        defer { try? setLocked(false, at: second) }

        XCTAssertEqual(cleaner.run(now: now), RetentionReport(removedEntries: 1, removedDictationAudio: 1))
        XCTAssertEqual(history.entries, [both])
        XCTAssertFalse(FileManager.default.fileExists(atPath: first.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: second.path))
        XCTAssertEqual(cleaner.preview(now: now), RetentionPreview(entries: 1, dictationAudio: 1, audioBytes: 3))

        try setLocked(false, at: second)
        XCTAssertEqual(cleaner.run(now: now), RetentionReport(removedEntries: 1, removedDictationAudio: 1))
        XCTAssertTrue(cleaner.preview(now: now).isEmpty)
    }

    func testRetentionKeepsFailedMeetingAudioEligibleForRetry() async throws {
        let now = Date()
        let history = HistoryStore(directories: directories)
        let meetings = MeetingStore(directories: directories)
        var meeting = try meetings.create(title: "Meeting")
        meeting.createdAt = now.addingTimeInterval(-2 * 86_400)
        meeting.status = .transcribed
        let mic = meetings.microphoneAudioURL(for: meeting.id)
        try Data([1, 2, 3]).write(to: mic)
        meetings.update(meeting)
        settings.keepMeetingAudio = .hours24
        let cleaner = RetentionCleaner(history: history, meetings: meetings, settings: settings)

        try setLocked(true, at: mic)
        defer { try? setLocked(false, at: mic) }
        XCTAssertEqual(cleaner.preview(now: now), RetentionPreview(meetingAudio: 1, audioBytes: 3))
        XCTAssertEqual(cleaner.run(now: now).removedMeetingAudio, 0)
        XCTAssertTrue(try XCTUnwrap(meetings.meetings.first).hasAudio)
        XCTAssertEqual(cleaner.preview(now: now).meetingAudio, 1)

        try setLocked(false, at: mic)
        XCTAssertEqual(cleaner.run(now: now).removedMeetingAudio, 1)
        XCTAssertFalse(try XCTUnwrap(meetings.meetings.first).hasAudio)
        XCTAssertTrue(cleaner.preview(now: now).isEmpty)
    }

    func testPartialCaptureKeepsTheTranscriptAndWarns() async throws {
        let history = HistoryStore(directories: directories)
        let inserter = DictationFakes.Inserter()
        let recorder = PartialRecorder()
        let controller = makeController(history: history, inserter: inserter, recorder: recorder)
        controller.startHandsFree()
        controller.finish()
        await controller.waitForProcessing()

        XCTAssertEqual(inserter.inserted, ["часть фразы"])
        XCTAssertEqual(history.entries.first?.text, "часть фразы")
        let warning = CoreStrings.string("dictation.error.partialRecording", table: .dictation)
        XCTAssertEqual(controller.state, .message(.failure(.recordingFailed(warning))))
        controller.stop()
    }

    func testEmptyCaptureConversionAndGapHaveDifferentEffects() async throws {
        let converter = try XCTUnwrap(CaptureConverter(targetRate: 16_000))
        let format = try XCTUnwrap(AVAudioFormat(standardFormatWithSampleRate: 48_000, channels: 1))
        let empty = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 1))
        let capture = CaptureBuffer()
        capture.append(try converter.convert(empty))
        XCTAssertTrue(capture.allowsIncrementalDecode)
        XCTAssertFalse(capture.hasGap)

        capture.append([0.25])
        capture.markGap()
        capture.append([0.5])
        XCTAssertTrue(capture.hasGap)
        XCTAssertFalse(capture.allowsIncrementalDecode)
        XCTAssertEqual(capture.takeSamples(), [0.25, 0.5])
    }

    func testDictionarySuggestionsChangeWithHistoryAndClearTheirExamples() async throws {
        let history = HistoryStore(directories: directories)
        let items = [DictionaryItem.term("GitHub")]
        history.add(makeEntry("с гитхабом сегодня"))
        history.add(makeEntry("с гитхабом завтра"))
        let initial = DictionarySuggestions.Input(history: history.entries, dictionary: items)
        XCTAssertTrue(initial.suggestions().isEmpty)

        let entryWithAudio = makeEntry("с гитхабом вчера", audio: "sample.wav")
        _ = try writeAudio(to: "sample.wav")
        history.add(entryWithAudio)
        let changed = DictionarySuggestions.Input(history: history.entries, dictionary: items)
        XCTAssertNotEqual(changed, initial)
        let suggestion = try XCTUnwrap(changed.suggestions().first { $0.from == "гитхабом" })
        XCTAssertEqual(suggestion.occurrences, 3)
        XCTAssertFalse(suggestion.examples.isEmpty)

        history.removeAudio(for: entryWithAudio.id)
        XCTAssertNil(history.entries.first(where: { $0.id == entryWithAudio.id })?.audioFileName)
        XCTAssertEqual(changed, DictionarySuggestions.Input(history: history.entries, dictionary: items))
        XCTAssertEqual(changed, DictionarySuggestions.Input(history: history.entries, dictionary: [.term("GitHub")]))

        let corrected = DictionarySuggestions.Input(history: history.entries,
            dictionary: items + [.replacement(from: "гитхабом", to: "GitHub")])
        XCTAssertNotEqual(corrected, changed)
        XCTAssertTrue(corrected.suggestions().isEmpty)

        XCTAssertTrue(history.clearHistory())
        let cleared = DictionarySuggestions.Input(history: history.entries, dictionary: items)
        XCTAssertNotEqual(cleared, changed)
        XCTAssertTrue(cleared.suggestions().isEmpty)
    }

    func testSentenceEndingInsideClosingPunctuationDoesNotProtectALatinNeighbor() async {
        let merger = MixedLanguageMerger(lexicon: .fixed([:]))
        for token in ["«Finder!»", "(Finder!)", "[«Finder!»]", "“Finder!”", "\"Finder!\""] {
            let original = "Открой \(token) V офисе."
            let corrected = "Открой \(token) В офисе."
            XCTAssertEqual(merger.merge(russian: original, helper: corrected).text, corrected)
        }
        XCTAssertEqual(merger.merge(russian: "Worker A.", helper: "Worker А.").text, "Worker A.")
        XCTAssertEqual(merger.merge(russian: "Открой «Finder» V офисе.", helper: "Открой «Finder» В офисе.").text,
                       "Открой «Finder» V офисе.")
    }

    private func makeEntry(_ text: String, date: Date = Date(), audio: String? = nil) -> HistoryEntry {
        HistoryEntry(date: date, text: text, rawText: text, engineID: .gigaamRNNT, languageMode: .mixed,
                     appBundleID: nil, appName: nil, category: .other, duration: 1,
                     wordCount: HistoryEntry.countWords(in: text), fixesCount: 0, audioFileName: audio)
    }

    private func writeAudio(to name: String) throws -> URL {
        let url = directories.recordingURL(relativePath: name)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data([1, 2, 3]).write(to: url)
        return url
    }

    private func setLocked(_ locked: Bool, at url: URL) throws {
        try FileManager.default.setAttributes([.immutable: locked], ofItemAtPath: url.path)
    }

    private func makeController(history: HistoryStore, inserter: DictationFakes.Inserter,
                                recorder: AudioRecording) -> DictationController {
        let engines = DictationFakes.Engines()
        engines.engines[.gigaamRNNT] = DictationFakes.Engine(id: EngineID.gigaamRNNT.rawValue, text: "часть фразы")
        engines.loadStates[.gigaamRNNT] = .ready
        let controller = DictationController(dependencies: .init(
            recorder: recorder, hotkeys: DictationFakes.Hotkeys(), inserter: inserter,
            frontmostApp: DictationFakes.FrontmostApp(), permissions: DictationFakes.Permissions(),
            engines: engines, pipeline: DictationFakes.Pipeline(), history: history,
            dictionary: DictationFakes.Dictionary(), snippets: DictationFakes.Snippets(), notes: DictationFakes.Notes(),
            categories: DictationFakes.Categories(), meetings: DictationFakes.Meetings(), sounds: nil,
            settings: settings, directories: directories))
        controller.messageLifetime = 60
        return controller
    }

    private final class PartialRecorder: CaptureGapReporting {
        var hasCaptureGap = false
        var isRecording = false
        var onLevel: ((Float) -> Void)?

        func start(deviceUID: String?) throws { isRecording = true }

        func stop() -> RecordedAudio {
            isRecording = false
            hasCaptureGap = true
            return DictationFakes.speech(seconds: 1)
        }

        func cancel() { isRecording = false }
    }
}
