import Combine
import Foundation

/// `Meetings/<id>/`: `meeting.json`, `mic.caf`, `system.caf`, `transcript.md`.
/// Owned by the meetings module.
///
/// Each meeting keeps its own JSON file, so a damaged file costs one meeting's
/// metadata instead of the whole list, and a meeting's audio stays next to it.
@MainActor
public final class MeetingStore: ObservableObject, MeetingStoring {
    public static let recordFileName = "meeting.json"
    public static let saveDelay: Duration = .milliseconds(500)

    @Published public private(set) var meetings: [MeetingRecord] = []

    public let directories: AppDirectories

    /// The language `transcript.md` is written in; the saved interface language unless a
    /// test pins one.
    var transcriptLanguage: () -> AppLanguage = { AppLanguage.saved() }

    private var dirty: Set<UUID> = []
    private var pendingSave: Task<Void, Never>?
    /// The `meeting.json` of each meeting as `refreshClaudeNotes` last read it.
    private var readRecords: [UUID: RecordStamp] = [:]
    /// The highlight ids of each meeting the last time this store and its file agreed on
    /// them: after a load, a save, or `refreshClaudeNotes`. `write` and `refreshClaudeNotes`
    /// compare the file against this before touching the other side's highlights, so an
    /// MCP mark or unmark and an unrelated app-side edit both survive.
    private var knownHighlightIDs: [UUID: Set<UUID>] = [:]
    private let logger = SlovoLog.logger("MeetingStore")

    /// Reads every `meeting.json` under the meetings folder.
    public init(directories: AppDirectories) {
        self.directories = directories
        let loaded = loadAll()
        for record in loaded {
            knownHighlightIDs[record.id] = Set(record.highlights.map(\.id))
        }
        meetings = Self.sorted(loaded)
    }

    public func meeting(id: UUID) -> MeetingRecord? {
        meetings.first { $0.id == id }
    }

    public func create(title: String) throws -> MeetingRecord {
        let now = Date()
        let trimmed = TranscriptMerger.normalizedText(title)
        let record = MeetingRecord(title: trimmed.isEmpty ? MeetingTitles.defaultTitle(for: now) : trimmed,
                                   createdAt: now, status: .recording)
        do {
            try FileManager.default.createDirectory(at: folder(for: record.id), withIntermediateDirectories: true)
            try write(record)
        } catch {
            try? FileManager.default.removeItem(at: folder(for: record.id))
            throw MeetingErrors.createFailed(error)
        }
        meetings = Self.sorted(meetings + [record])
        return record
    }

    public func update(_ meeting: MeetingRecord) {
        guard let index = meetings.firstIndex(where: { $0.id == meeting.id }), meetings[index] != meeting else { return }
        let reorder = meetings[index].createdAt != meeting.createdAt
        meetings[index] = meeting
        if reorder {
            meetings = Self.sorted(meetings)
        }
        markDirty(meeting.id)
    }

    public func delete(id: UUID) {
        meetings.removeAll { $0.id == id }
        dirty.remove(id)
        readRecords.removeValue(forKey: id)
        knownHighlightIDs.removeValue(forKey: id)
        Self.removeItem(at: folder(for: id))
    }

    public func deleteAudio(id: UUID) {
        let microphoneRemoved = Self.removeItem(at: microphoneAudioURL(for: id))
        let systemRemoved = Self.removeItem(at: systemAudioURL(for: id))
        let screenRemoved = Self.removeItem(at: screenRecordingURL(for: id))
        guard microphoneRemoved && systemRemoved && screenRemoved,
              let index = meetings.firstIndex(where: { $0.id == id }) else { return }
        var changed = false
        if meetings[index].hasAudio {
            meetings[index].hasAudio = false
            changed = true
        }
        if meetings[index].hasScreenRecording {
            meetings[index].hasScreenRecording = false
            changed = true
        }
        if changed {
            markDirty(id)
        }
    }

    public var meetingsFolder: URL {
        directories.meetingsFolder
    }

    public func folder(for id: UUID) -> URL {
        directories.meetingFolder(id: id)
    }

    public func microphoneAudioURL(for id: UUID) -> URL {
        folder(for: id).appendingPathComponent("mic.caf")
    }

    public func systemAudioURL(for id: UUID) -> URL {
        folder(for: id).appendingPathComponent("system.caf")
    }

    public func screenRecordingURL(for id: UUID) -> URL {
        folder(for: id).appendingPathComponent("screen.mov")
    }

    public func transcriptURL(for id: UUID) -> URL {
        folder(for: id).appendingPathComponent("transcript.md")
    }

    public func recordURL(for id: UUID) -> URL {
        folder(for: id).appendingPathComponent(Self.recordFileName)
    }

    public func flush() throws {
        pendingSave?.cancel()
        pendingSave = nil
        try saveDirty()
    }

    // MARK: - Saving

    private func markDirty(_ id: UUID) {
        dirty.insert(id)
        pendingSave?.cancel()
        pendingSave = Task { [weak self] in
            try? await Task.sleep(for: Self.saveDelay)
            guard !Task.isCancelled, let self else { return }
            self.pendingSave = nil
            self.saveDirtyReportingFailure()
        }
    }

    /// Whether the last background save failed; the failure is announced once per run.
    private var isSaveFailing = false
    private static let retryDelay: Duration = .seconds(30)

    /// A background save has no caller to throw to. The meetings stay dirty, the interface
    /// is told once (see `StoreFileEvents.saveFailed`) and the save is tried again later.
    private func saveDirtyReportingFailure() {
        do {
            try saveDirty()
            isSaveFailing = false
        } catch {
            logger.error("Saving meetings failed: \(error.localizedDescription, privacy: .public)")
            if !isSaveFailing {
                isSaveFailing = true
                NotificationCenter.default.post(name: StoreFileEvents.saveFailed, object: nil, userInfo: [
                    "message": error.localizedDescription, "file": "Meetings",
                ])
            }
            pendingSave?.cancel()
            pendingSave = Task { [weak self] in
                try? await Task.sleep(for: Self.retryDelay)
                guard !Task.isCancelled, let self else { return }
                self.pendingSave = nil
                self.saveDirtyReportingFailure()
            }
        }
    }

    private func saveDirty() throws {
        var firstError: Error?
        for id in dirty.sorted(by: { $0.uuidString < $1.uuidString }) {
            guard let record = meeting(id: id) else {
                dirty.remove(id)
                continue
            }
            do {
                // The document is written from the record the file now holds, so saving a
                // rename or a star cannot drop Claude's notes from `transcript.md` while
                // `meeting.json` keeps them.
                let stored = try write(record)
                // The meeting in memory follows the file for the same reason: without this a
                // rename made in a session that has not refreshed yet would leave the card
                // without the Claude tab, or without a star the MCP server just placed,
                // while `meeting.json` already holds it.
                if let index = meetings.firstIndex(where: { $0.id == id }) {
                    if meetings[index].claudeNotes != stored.claudeNotes {
                        meetings[index].claudeNotes = stored.claudeNotes
                    }
                    if meetings[index].highlights != stored.highlights {
                        meetings[index].highlights = stored.highlights
                    }
                }
                try writeTranscript(of: stored)
                dirty.remove(id)
            } catch {
                firstError = firstError ?? MeetingErrors.saveFailed(error)
            }
        }
        if let firstError {
            throw firstError
        }
    }

    /// Writes to a temporary file and replaces the old one, so a crash never leaves
    /// half a file behind. Returns the record the file now holds, which is what every
    /// document derived from it has to be written from.
    ///
    /// What Claude wrote is taken from the file rather than from the record in memory: the
    /// MCP server writes those notes from its own process, and saving a rename here must
    /// not throw them away. The app changes them only through `setClaudeNotes`, which puts
    /// them in the file first.
    ///
    /// Highlights are different: the app edits them too, so neither side's copy simply
    /// wins. `reconciledHighlights` folds in whatever the MCP server marked or unmarked
    /// since `knownHighlightIDs` was last set, on top of this save's own highlights.
    @discardableResult
    private func write(_ record: MeetingRecord) throws -> MeetingRecord {
        let url = recordURL(for: record.id)
        let folder = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        // The MCP server changes this file from its own process under the same lock, so its
        // notes or marks cannot land between this read and this write and be undone by it.
        let stored = try MeetingFileLock.withLockIfPossible(meetingFolder: folder) { () throws -> MeetingRecord in
            let onDisk = storedRecord(id: record.id)
            var stored = record
            stored.claudeNotes = onDisk?.claudeNotes
            stored.highlights = Self.reconciledHighlights(record.highlights, withDisk: onDisk?.highlights ?? [],
                                                           knownIDs: knownHighlightIDs[record.id])
            try Self.encoder.encode(stored).write(to: url, options: .atomic)
            return stored
        }
        knownHighlightIDs[record.id] = Set(stored.highlights.map(\.id))
        return stored
    }

    /// The record the meeting's file holds now; nil when there is no readable file yet.
    private func storedRecord(id: UUID) -> MeetingRecord? {
        guard let data = try? Data(contentsOf: recordURL(for: id)) else { return nil }
        return try? Self.decoder.decode(MeetingRecord.self, from: data)
    }

    /// Merges `ours` with what the file holds now (`disk`), given the highlight ids both
    /// sides last agreed on (`knownIDs`; nil, as for a meeting never read before, falls
    /// back to treating `disk` itself as that baseline, which is exactly right for a file
    /// that does not exist yet: there is nothing to have disagreed about).
    ///
    /// A highlight that appeared on disk since `knownIDs` was taken is added even where
    /// `ours` does not have it — the MCP server marked it from its own process. One that
    /// vanished from disk is removed even where `ours` still has it — the MCP server
    /// unmarked it. Anything neither side touched relative to `knownIDs` is left exactly as
    /// `ours` has it, so an edit already sitting in `ours` (a star just added or removed by
    /// the app itself, a note just changed) survives.
    private static func reconciledHighlights(_ ours: [MeetingHighlight], withDisk disk: [MeetingHighlight],
                                             knownIDs: Set<UUID>?) -> [MeetingHighlight] {
        let diskIDs = Set(disk.map(\.id))
        let knownIDs = knownIDs ?? diskIDs
        let removedFromDisk = knownIDs.subtracting(diskIDs)
        let addedOnDisk = disk.filter { !knownIDs.contains($0.id) }
        var merged = ours.filter { !removedFromDisk.contains($0.id) }
        let alreadyPresent = Set(merged.map(\.id))
        merged.append(contentsOf: addedOnDisk.filter { !alreadyPresent.contains($0.id) })
        return merged
    }

    /// Replaces or removes what Claude wrote about a meeting. The file owns these notes, so
    /// the change reaches it first and the meeting in memory follows; the save that follows
    /// brings `transcript.md` in step.
    public func setClaudeNotes(_ notes: MeetingNotes?, for id: UUID) {
        guard let index = meetings.firstIndex(where: { $0.id == id }) else { return }
        let stored: MeetingNotes?
        do {
            stored = try MeetingNotesFile(directories: directories).write(notes, to: id)
        } catch {
            logger.error("Changing Claude's notes failed: \(error.localizedDescription, privacy: .public)")
            return
        }
        meetings[index].claudeNotes = stored
        markDirty(id)
    }

    /// Re-reads what Claude wrote and marked about the meetings already loaded.
    ///
    /// The file owns the notes, and holds highlights the MCP server may have marked or
    /// unmarked, both from a process of its own that tells the running app nothing. Slovo
    /// is a menu bar app that stays running for days, so without this the meeting card
    /// would show them only after a relaunch or an unrelated save. `transcript.md` is
    /// derived from the record and is brought in step here instead of at the next
    /// unrelated save; `meeting.json` is left alone, because it is the file this just came
    /// from. A meeting whose file cannot be read keeps what it has.
    ///
    /// A menu bar app is activated many times a day, so this must stay cheap: a meeting
    /// whose file has not moved since it was last read costs one `lstat` and nothing more.
    /// Only a file with a new identity, size or modification date is decoded again.
    public func refreshClaudeNotes() {
        for index in meetings.indices {
            let id = meetings[index].id
            guard let stamp = recordStamp(id: id), readRecords[id] != stamp else { continue }
            // Remembered before decoding, so a file that cannot be decoded is not read
            // again either until it changes.
            readRecords[id] = stamp
            guard let stored = storedRecord(id: id) else { continue }
            let highlights = Self.reconciledHighlights(meetings[index].highlights, withDisk: stored.highlights,
                                                        knownIDs: knownHighlightIDs[id])
            // The baseline is what the file holds, not the merge: a star or an unstar the
            // app has not saved yet is not in the file, and taking it for agreed would make
            // the next save read its absence there as the server's unmark, and drop it.
            knownHighlightIDs[id] = Set(stored.highlights.map(\.id))
            guard meetings[index].claudeNotes != stored.claudeNotes || meetings[index].highlights != highlights else {
                continue
            }
            meetings[index].claudeNotes = stored.claudeNotes
            meetings[index].highlights = highlights
            do {
                try writeTranscript(of: meetings[index])
            } catch {
                logger.error("""
                    Saving the transcript after Claude's notes or highlights changed failed: \
                    \(error.localizedDescription, privacy: .public)
                    """)
            }
        }
    }

    /// What identifies the version of a meeting's record file. Nil when there is no plain
    /// file there, it cannot be examined, or it is larger than a record can sensibly be
    /// (`MeetingArchive.maximumFileBytes`), which keeps a huge file from being decoded on
    /// every activation. Symbolic links are not followed.
    private func recordStamp(id: UUID) -> RecordStamp? {
        var info = stat()
        guard lstat(recordURL(for: id).path, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG,
              info.st_size <= Int64(MeetingArchive.maximumFileBytes) else { return nil }
        return RecordStamp(info)
    }

    /// A file's identity, size and modification date as one comparable value: the same
    /// stamp means the file still holds what was read from it.
    private struct RecordStamp: Equatable {
        let inode: UInt64
        let size: Int64
        let modifiedSeconds: Int
        let modifiedNanoseconds: Int

        init(_ info: stat) {
            inode = info.st_ino
            size = info.st_size
            modifiedSeconds = info.st_mtimespec.tv_sec
            modifiedNanoseconds = info.st_mtimespec.tv_nsec
        }
    }

    /// Keeps `transcript.md` in step with every save of a transcribed meeting, so renames,
    /// merges, stars and title changes reach the file that Export, Finder and a lost
    /// `meeting.json` rely on. Other statuses leave an existing file alone: a meeting being
    /// processed again keeps its previous transcript until the new one is saved.
    private func writeTranscript(of record: MeetingRecord) throws {
        guard record.status == .transcribed else { return }
        let url = transcriptURL(for: record.id)
        let data = Data(TranscriptMerger.markdown(meeting: record, language: transcriptLanguage()).utf8)
        // An unchanged document is not rewritten, so saving an unrelated change does not
        // touch the file.
        if let existing = try? Data(contentsOf: url), existing == data {
            return
        }
        try data.write(to: url, options: .atomic)
    }

    @discardableResult
    fileprivate static func removeItem(at url: URL) -> Bool {
        do {
            try FileManager.default.removeItem(at: url)
            return true
        } catch let error as NSError where error.domain == NSCocoaErrorDomain
            && (error.code == NSFileNoSuchFileError || error.code == NSFileReadNoSuchFileError) {
            return true
        } catch {
            SlovoLog.logger("MeetingStore").error(
                "Removing \(url.lastPathComponent, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    // MARK: - Loading

    private func loadAll() -> [MeetingRecord] {
        let folders = (try? FileManager.default.contentsOfDirectory(
            at: directories.meetingsFolder, includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles])) ?? []
        return folders.compactMap { folder -> MeetingRecord? in
            guard (try? folder.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true,
                  let id = UUID(uuidString: folder.lastPathComponent) else {
                return nil
            }
            return loadMeeting(id: id)
        }
    }

    private func loadMeeting(id: UUID) -> MeetingRecord? {
        let url = recordURL(for: id)
        if FileManager.default.fileExists(atPath: url.path) {
            do {
                var record = try Self.decoder.decode(MeetingRecord.self, from: Data(contentsOf: url))
                // The folder name is what every other path is built from.
                record.id = id
                return repairInterrupted(record)
            } catch {
                let moved = moveAside(url)
                logger.error("""
                    \(url.path, privacy: .public) is damaged (\(error.localizedDescription, privacy: .public)); \
                    moved to \(moved?.lastPathComponent ?? "nowhere", privacy: .public)
                    """)
            }
        }
        return recover(id: id)
    }

    /// A meeting still marked as recording or transcribing was cut off when the app
    /// quit; nothing can be running at launch.
    private func repairInterrupted(_ record: MeetingRecord) -> MeetingRecord {
        var repaired = record
        let microphone = microphoneAudioURL(for: record.id)
        let system = systemAudioURL(for: record.id)
        let screen = screenRecordingURL(for: record.id)
        let fileManager = FileManager.default
        switch record.status {
        case .recording:
            let hasMicrophone = fileManager.fileExists(atPath: microphone.path)
            let hasSystem = fileManager.fileExists(atPath: system.path)
            repaired.status = .recorded
            repaired.hasAudio = hasMicrophone || hasSystem
            repaired.hasSystemAudio = record.hasSystemAudio && hasSystem
            repaired.hasScreenRecording = record.hasScreenRecording && fileManager.fileExists(atPath: screen.path)
            repaired.duration = max(record.duration, MeetingClip.duration(of: microphone), MeetingClip.duration(of: system))
        case .transcribing:
            // A run replaces the transcript only when it succeeds, so segments or an engine
            // mean an earlier transcript that a cut-off re-run left in place.
            if !record.segments.isEmpty || record.engineID != nil {
                repaired.status = .transcribed
            } else if record.hasAudio {
                repaired.status = .recorded
            } else {
                repaired.status = .failed
                repaired.errorMessage = MeetingErrors.interruptedMessage()
            }
        case .recorded, .transcribed, .failed:
            return record
        }
        persistRepaired(repaired)
        return repaired
    }

    /// Rebuilds a record when `meeting.json` is missing or damaged but audio or a
    /// transcript survived, so the meeting can still be read, transcribed or deleted.
    ///
    /// A transcript comes back whenever `transcript.md` is there, audio or not: rebuilt
    /// from the audio alone the meeting would read as never transcribed, and transcribing
    /// it again would overwrite the one file that still holds its lines, names and moments.
    private func recover(id: UUID) -> MeetingRecord? {
        let microphone = microphoneAudioURL(for: id)
        let system = systemAudioURL(for: id)
        let fileManager = FileManager.default
        let hasMicrophone = fileManager.fileExists(atPath: microphone.path)
        let hasSystem = fileManager.fileExists(atPath: system.path)
        let hasScreenRecording = fileManager.fileExists(atPath: screenRecordingURL(for: id).path)
        var audio: SurvivingAudio?
        if hasMicrophone || hasSystem {
            let file = hasMicrophone ? microphone : system
            audio = SurvivingAudio(hasSystemAudio: hasSystem,
                                   duration: max(MeetingClip.duration(of: microphone), MeetingClip.duration(of: system)),
                                   created: try? file.resourceValues(forKeys: [.creationDateKey]).creationDate)
        }
        if let recovered = recoverFromTranscript(id: id, audio: audio, hasScreenRecording: hasScreenRecording) {
            return recovered
        }
        guard let audio else { return nil }

        let created = audio.created ?? Date()
        let record = MeetingRecord(id: id, title: MeetingTitles.defaultTitle(for: created), createdAt: created,
                                   duration: audio.duration, status: .recorded, hasSystemAudio: audio.hasSystemAudio,
                                   hasAudio: true, hasScreenRecording: hasScreenRecording)
        persistRepaired(record)
        return record
    }

    /// The tracks a meeting whose record was lost still has.
    private struct SurvivingAudio {
        let hasSystemAudio: Bool
        let duration: TimeInterval
        let created: Date?
    }

    /// A transcribed meeting whose record was lost: it comes back from `transcript.md`, its
    /// `# title`, `[mm:ss] ★ Speaker: text` lines, speaker names and starred lines, with
    /// whatever audio survived next to it. Nil when there is no transcript, or when it
    /// cannot be read but audio can still be transcribed again; without audio an
    /// unreadable transcript still gives a failed record, so the folder stays visible and
    /// can be deleted from the list.
    private func recoverFromTranscript(id: UUID, audio: SurvivingAudio?, hasScreenRecording: Bool) -> MeetingRecord? {
        let url = transcriptURL(for: id)
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        // The store rewrites transcript.md on every save, so its own date can be recent for
        // an old meeting; the folder keeps the day the meeting began. The earlier of the two
        // is the one the MCP archive uses too, so a recovered meeting keeps its age for
        // retention and for Claude's age limit.
        let transcriptCreated = try? url.resourceValues(forKeys: [.creationDateKey]).creationDate
        let folderCreated = try? folder(for: id).resourceValues(forKeys: [.creationDateKey]).creationDate
        let created = audio?.created
            ?? [transcriptCreated, folderCreated].compactMap { $0 }.min() ?? Date()
        guard let text = try? String(contentsOf: url, encoding: .utf8) else {
            guard audio == nil else { return nil }
            let record = MeetingRecord(id: id, title: MeetingTitles.defaultTitle(for: created), createdAt: created,
                                       status: .failed, hasSystemAudio: false, hasAudio: false,
                                       hasScreenRecording: hasScreenRecording,
                                       errorMessage: CoreStrings.string("meetings.error.recordDamaged", table: .meetings))
            persistRepaired(record)
            return record
        }
        let parsed = Self.parseTranscript(text)
        let title = parsed.title.flatMap { $0.isEmpty ? nil : $0 } ?? MeetingTitles.defaultTitle(for: created)
        // Lines of a document hold merged speech like segments of the first format did, so
        // the record is format 1 even when names and moments came back.
        let record = MeetingRecord(id: id, title: title, createdAt: created,
                                   duration: max(audio?.duration ?? 0, parsed.segments.last?.end ?? 0),
                                   status: .transcribed, hasSystemAudio: audio?.hasSystemAudio ?? false,
                                   hasAudio: audio != nil, hasScreenRecording: hasScreenRecording,
                                   segments: parsed.segments, speakers: parsed.speakers, highlights: parsed.highlights,
                                   formatVersion: 1)
        // Only meeting.json is written here: the transcript is the source being recovered
        // from and is kept exactly as found.
        persistRepaired(record)
        return record
    }

    /// Reads what `TranscriptMerger.markdown` wrote, with speaker names in either language.
    /// Nonisolated and free of file access, so the MCP server's read-only archive can
    /// recover a meeting from its transcript in memory.
    nonisolated static func parseTranscript(_ text: String)
        -> (title: String?, segments: [TranscriptSegment], speakers: [MeetingSpeaker], highlights: [MeetingHighlight]) {
        TranscriptDocumentParser.parse(text)
    }

    private func persistRepaired(_ record: MeetingRecord) {
        do {
            try write(record)
        } catch {
            dirty.insert(record.id)
            logger.error("Saving a repaired meeting failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// Renames a damaged file to `<name>.corrupt-<timestamp>.json` next to it.
    private func moveAside(_ url: URL) -> URL? {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let base = url.deletingPathExtension().lastPathComponent + ".corrupt-" + formatter.string(from: Date())
        let folder = url.deletingLastPathComponent()
        var destination = folder.appendingPathComponent(base + ".json")
        var suffix = 2
        while FileManager.default.fileExists(atPath: destination.path) {
            destination = folder.appendingPathComponent("\(base)-\(suffix).json")
            suffix += 1
        }
        do {
            try FileManager.default.moveItem(at: url, to: destination)
            return destination
        } catch {
            return nil
        }
    }

    // MARK: - Coding

    private static var encoder: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }

    private static var decoder: JSONDecoder {
        JSONDecoder()
    }

    /// Newest first; ties keep a stable order by id.
    private static func sorted(_ meetings: [MeetingRecord]) -> [MeetingRecord] {
        meetings.sorted { left, right in
            left.createdAt == right.createdAt ? left.id.uuidString < right.id.uuidString : left.createdAt > right.createdAt
        }
    }
}

/// The recovery parser for `transcript.md` of either format and interface language.
///
/// It skips headings, list items and the participants line, and turns every
/// `[mm:ss] ★ Name: text` line into a segment. Speaker names map back as far as a
/// document allows: Me/Я and Others/Собеседники become the track fallbacks, «Спикер N»
/// a numbered microphone speaker, and any other name a numbered microphone speaker with
/// that name. The system track of separated calls cannot be told apart in a document.
private enum TranscriptDocumentParser {
    private struct Line {
        var seconds: Int
        var starred: Bool
        var name: String
        var text: String
    }

    private struct Vocabulary {
        var fallbacks: [String: Speaker] = [:]
        var numbered: [(prefix: String, suffix: String)] = []
        var participantPrefixes: [String] = []

        init() {
            for language in [AppLanguage.en, .ru] {
                fallbacks[TranscriptMerger.speakerName(.me, language: language)] = .me
                fallbacks[TranscriptMerger.speakerName(.others, language: language)] = .others
                let pattern = CoreStrings.string("meetings.speaker.numbered", table: .meetings, language: language)
                if let placeholder = pattern.range(of: "%ld") {
                    numbered.append((String(pattern[..<placeholder.lowerBound]), String(pattern[placeholder.upperBound...])))
                }
                let participants = CoreStrings.string("meetings.transcript.participants", table: .meetings, language: language)
                if let placeholder = participants.range(of: "%@") {
                    let prefix = String(participants[..<placeholder.lowerBound])
                    if !prefix.isEmpty {
                        participantPrefixes.append(prefix)
                    }
                }
            }
        }

        func number(inLabel name: String) -> Int? {
            for pattern in numbered where name.hasPrefix(pattern.prefix) && name.hasSuffix(pattern.suffix) {
                let middle = name.dropFirst(pattern.prefix.count).dropLast(pattern.suffix.count)
                // Above the bound the speaker id would not be read back from meeting.json,
                // and the next named speaker's number could overflow.
                if !middle.isEmpty, middle.allSatisfy(\.isASCII), middle.allSatisfy(\.isNumber),
                   let value = Int(middle).flatMap(MeetingSpeaker.validNumber) {
                    return value
                }
            }
            return nil
        }
    }

    static func parse(_ text: String)
        -> (title: String?, segments: [TranscriptSegment], speakers: [MeetingSpeaker], highlights: [MeetingHighlight]) {
        let vocabulary = Vocabulary()
        var title: String?
        var lines: [Line] = []
        for rawLine in text.split(whereSeparator: \.isNewline) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if title == nil, line.hasPrefix("# ") {
                title = String(line.dropFirst(2)).trimmingCharacters(in: .whitespaces)
                continue
            }
            if line.hasPrefix("## ") || line.hasPrefix("- ")
                || vocabulary.participantPrefixes.contains(where: { line.hasPrefix($0) }) {
                continue
            }
            if let parsed = parseLine(line) {
                lines.append(parsed)
            }
        }

        // Named speakers get numbers above every «Спикер N» in the document, so the two
        // kinds never collide.
        var nextNumber = lines.compactMap { vocabulary.number(inLabel: $0.name) }.max() ?? 0
        var speakers: [MeetingSpeaker] = []
        var speakerForName: [String: MeetingSpeaker] = [:]
        var segments: [TranscriptSegment] = []
        var highlights: [MeetingHighlight] = []

        for (index, line) in lines.enumerated() {
            let start = TimeInterval(line.seconds)
            if let last = segments.indices.last {
                segments[last].end = max(segments[last].start, start)
            }

            var track = Speaker.me
            var speakerID: String?
            if let fallback = vocabulary.fallbacks[line.name] {
                track = fallback
            } else {
                let speaker: MeetingSpeaker
                if let known = speakerForName[line.name] {
                    speaker = known
                } else {
                    if let number = vocabulary.number(inLabel: line.name) {
                        speaker = MeetingSpeaker(id: "s\(number)", track: .microphone, number: number,
                                                 colorIndex: (number - 1) % MeetingSpeaker.paletteSize)
                    } else {
                        nextNumber += 1
                        speaker = MeetingSpeaker(id: "s\(nextNumber)", track: .microphone, number: nextNumber,
                                                 displayName: MeetingEdits.sanitizedName(line.name),
                                                 colorIndex: (nextNumber - 1) % MeetingSpeaker.paletteSize)
                    }
                    speakerForName[line.name] = speaker
                    if !speakers.contains(where: { $0.id == speaker.id }) {
                        speakers.append(speaker)
                    }
                }
                speakerID = speaker.id
            }

            segments.append(TranscriptSegment(start: start, end: start, speaker: track, text: line.text,
                                              speakerID: speakerID))
            if line.starred {
                highlights.append(MeetingHighlight(id: stableID(index: index, line: line), time: start,
                                                   source: .transcript,
                                                   createdAt: Date(timeIntervalSinceReferenceDate: 0)))
            }
        }
        return (title, segments, speakers, highlights)
    }

    /// `[mm:ss]` or `[h:mm:ss]`, an optional `★ `, a name of at most 40 characters and
    /// `: ` before the text.
    private static func parseLine(_ line: String) -> Line? {
        guard line.hasPrefix("["), let close = line.firstIndex(of: "]") else { return nil }
        let stamp = line[line.index(after: line.startIndex)..<close]
        let parts = stamp.split(separator: ":", omittingEmptySubsequences: false).map { Int($0) }
        guard (2...3).contains(parts.count), parts.allSatisfy({ ($0 ?? -1) >= 0 }) else { return nil }
        // A recovered document can be edited by hand. Bound each step before doing
        // arithmetic, so a large but valid Int cannot trap while loading the app or MCP.
        let maximumSeconds = Int(MeetingTime.maximumSeconds)
        var seconds = 0
        for part in parts.compactMap({ $0 }) {
            guard part <= maximumSeconds, seconds <= (maximumSeconds - part) / 60 else { return nil }
            seconds = seconds * 60 + part
        }

        var rest = line[line.index(after: close)...].trimmingCharacters(in: .whitespaces)
        var starred = false
        if rest.hasPrefix(TranscriptMerger.highlightMark + " ") {
            starred = true
            rest = String(rest.dropFirst(TranscriptMerger.highlightMark.count)).trimmingCharacters(in: .whitespaces)
        }
        guard let colon = rest.range(of: ": ") else { return nil }
        let name = rest[..<colon.lowerBound].trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty, name.count <= MeetingEdits.maximumNameCharacters else { return nil }
        return Line(seconds: seconds, starred: starred, name: name, text: String(rest[colon.upperBound...]))
    }

    /// The same document always gives the same highlight ids, so a reader that parses it on
    /// every request, like the MCP server, can refer to a moment between requests. FNV-1a
    /// is used because Swift's `Hasher` is seeded differently in every process.
    private static func stableID(index: Int, line: Line) -> UUID {
        var first: UInt64 = 0xcbf2_9ce4_8422_2325
        var second: UInt64 = 0x6c62_272e_07bb_0142
        for byte in "\(index)|\(line.seconds)|\(line.name)|\(line.text)".utf8 {
            first = (first ^ UInt64(byte)) &* 0x0000_0100_0000_01b3
            second = (second ^ UInt64(byte)) &* 0x0000_0100_0000_01b3
        }
        var bytes = [UInt8](repeating: 0, count: 16)
        for offset in 0..<8 {
            bytes[offset] = UInt8(truncatingIfNeeded: first >> (UInt64(offset) * 8))
            bytes[offset + 8] = UInt8(truncatingIfNeeded: second >> (UInt64(offset) * 8))
        }
        // Version 8 (custom) and the RFC 4122 variant, so the value is a well-formed UUID.
        bytes[6] = (bytes[6] & 0x0F) | 0x80
        bytes[8] = (bytes[8] & 0x3F) | 0x80
        return UUID(uuid: (bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7],
                           bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]))
    }
}

extension MeetingStoring {
    /// Removes `mic.caf` and `system.caf` and sets `hasAudio` to false, leaving
    /// `screen.mov` in place. "Keep meeting audio: delete after transcription" is about the
    /// audio a transcript replaces; a screen recording has nothing that replaces it, so it
    /// stays until the meeting is deleted or an age limit removes it with `deleteAudio`.
    func removeAudioTracks(id: UUID) {
        let microphoneRemoved = MeetingStore.removeItem(at: microphoneAudioURL(for: id))
        let systemRemoved = MeetingStore.removeItem(at: systemAudioURL(for: id))
        guard microphoneRemoved && systemRemoved else { return }
        guard var meeting = meetings.first(where: { $0.id == id }), meeting.hasAudio else { return }
        meeting.hasAudio = false
        update(meeting)
    }
}
