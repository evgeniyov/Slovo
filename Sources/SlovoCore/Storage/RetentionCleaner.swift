import Foundation

/// What one retention pass removed.
public struct RetentionReport: Equatable, Sendable {
    public var removedEntries: Int
    public var removedDictationAudio: Int
    public var removedMeetingAudio: Int

    public init(removedEntries: Int = 0, removedDictationAudio: Int = 0, removedMeetingAudio: Int = 0) {
        self.removedEntries = removedEntries
        self.removedDictationAudio = removedDictationAudio
        self.removedMeetingAudio = removedMeetingAudio
    }
}

/// What the next retention pass would remove with the current settings, counted
/// without removing anything.
public struct RetentionPreview: Equatable, Sendable {
    public var entries: Int
    public var dictationAudio: Int
    public var meetingAudio: Int
    /// Bytes the audio files take (dictation recordings, orphans and meeting tracks).
    public var audioBytes: Int64

    public init(entries: Int = 0, dictationAudio: Int = 0, meetingAudio: Int = 0, audioBytes: Int64 = 0) {
        self.entries = entries
        self.dictationAudio = dictationAudio
        self.meetingAudio = meetingAudio
        self.audioBytes = audioBytes
    }

    public var isEmpty: Bool {
        entries == 0 && dictationAudio == 0 && meetingAudio == 0
    }
}

/// Applies Data and privacy retention settings at launch and every 6 hours.
///
/// Ages are wall-clock ages of stored dates (a dictation from 8 days ago), so they use
/// `Date`; the 6-hour cadence is a monotonic sleep.
@MainActor
public final class RetentionCleaner {
    public static let interval: TimeInterval = 6 * 3600

    /// Recordings no entry refers to stay at least this long, even with "never", in case
    /// something is still about to add their entry.
    public static let orphanGracePeriod: TimeInterval = 5 * 60

    private let history: HistoryStoring
    private let meetings: MeetingStoring
    private let settings: AppSettings
    private let recordingsFolder: URL?
    private var task: Task<Void, Never>?
    private let logger = SlovoLog.logger("Retention")

    /// - Parameter recordingsFolder: `Recordings/`, swept for files no entry refers to;
    ///   defaults to the folder of a `HistoryStore`.
    public init(history: HistoryStoring, meetings: MeetingStoring, settings: AppSettings, recordingsFolder: URL? = nil) {
        self.history = history
        self.meetings = meetings
        self.settings = settings
        self.recordingsFolder = recordingsFolder ?? (history as? HistoryStore)?.directories.recordingsFolder
    }

    private struct Plan {
        let entries: [UUID]
        let audioOnly: [UUID]
        let dictationFiles: Set<URL>
        let orphans: [URL]
        let meetingTracksOnly: Bool
        let meetingFiles: [(id: UUID, files: [URL])]
    }

    private func plan(now: Date) -> Plan {
        let historyEntries = history.entries
        let entries = Self.expiredEntryIDs(historyEntries, keep: settings.keepHistory, now: now)
        let expiredEntryIDs = Set(entries)
        let audioOnly = Self.expiredAudioIDs(historyEntries, keep: settings.keepDictationAudio, now: now)
            .filter { !expiredEntryIDs.contains($0) }
        let audioIDs = expiredEntryIDs.union(audioOnly)
        let dictationFiles = Set(historyEntries.filter { audioIDs.contains($0.id) }
            .compactMap { history.audioURL(for: $0)?.standardizedFileURL })
        let keepMeetingAudio = settings.keepMeetingAudio
        let meetingTracksOnly = keepMeetingAudio == .afterTranscription
        let meetingIDs = Self.expiredMeetingAudioIDs(meetings.meetings, keep: keepMeetingAudio, now: now)
        let meetingFiles = meetingIDs.map { id in
            var urls = [meetings.microphoneAudioURL(for: id), meetings.systemAudioURL(for: id)]
            if !meetingTracksOnly { urls.append(meetings.screenRecordingURL(for: id)) }
            return (id, urls.filter { FileManager.default.fileExists(atPath: $0.path) })
        }
        return Plan(entries: entries, audioOnly: audioOnly, dictationFiles: dictationFiles,
                    orphans: orphanedRecordings(referencedBy: historyEntries, now: now),
                    meetingTracksOnly: meetingTracksOnly, meetingFiles: meetingFiles)
    }

    /// Runs one pass now.
    @discardableResult
    public func run(now: Date = Date()) -> RetentionReport {
        let plan = plan(now: now)
        let entryCount = history.entries.count
        for id in plan.entries { history.delete(id: id) }
        for id in plan.audioOnly { history.removeAudio(for: id) }

        var report = RetentionReport()
        report.removedEntries = entryCount - history.entries.count
        report.removedDictationAudio = plan.dictationFiles.filter(Self.wasRemoved).count
            + removeOrphanedRecordings(plan.orphans)

        for (id, files) in plan.meetingFiles {
            if plan.meetingTracksOnly {
                meetings.removeAudioTracks(id: id)
            } else {
                meetings.deleteAudio(id: id)
            }
            if !files.isEmpty && files.allSatisfy(Self.wasRemoved) {
                report.removedMeetingAudio += 1
            }
        }

        if report != RetentionReport() {
            logger.info("Retention removed \(report.removedEntries) entries, \(report.removedDictationAudio) dictation recordings, \(report.removedMeetingAudio) meeting recordings")
        }
        if !plan.entries.isEmpty || !plan.audioOnly.isEmpty {
            try? history.flush() // StoreFile reports failures and retries.
        }
        if !plan.meetingFiles.isEmpty {
            do {
                try meetings.flush()
            } catch {
                logger.error("\(error.localizedDescription, privacy: .public)")
            }
        }
        return report
    }

    /// What `run` would remove right now. Nothing is changed.
    public func preview(now: Date = Date()) -> RetentionPreview {
        let plan = plan(now: now)
        let dictationFiles = plan.dictationFiles.union(plan.orphans)
        let meetingsWithFiles = plan.meetingFiles.map(\.files).filter { !$0.isEmpty }
        let bytes = dictationFiles.reduce(0) { $0 + Self.fileSize($1) }
            + meetingsWithFiles.joined().reduce(0) { $0 + Self.fileSize($1) }
        return RetentionPreview(entries: plan.entries.count, dictationAudio: dictationFiles.count,
                                meetingAudio: meetingsWithFiles.count, audioBytes: bytes)
    }

    private static func wasRemoved(_ url: URL) -> Bool {
        // Use a fresh URL so resource values cached by the plan cannot mask a removal.
        do {
            _ = try URL(fileURLWithPath: url.path).resourceValues(forKeys: [.isRegularFileKey])
            return false
        } catch let error as NSError {
            return error.domain == NSCocoaErrorDomain
                && (error.code == NSFileNoSuchFileError || error.code == NSFileReadNoSuchFileError)
        }
    }

    private static func fileSize(_ url: URL) -> Int64 {
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        return Int64(size)
    }

    /// Runs a pass now and then every `interval` until `stop()`.
    public func start() {
        stop()
        run()
        task = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(Self.interval))
                guard !Task.isCancelled else { return }
                self?.run()
            }
        }
    }

    public func stop() {
        task?.cancel()
        task = nil
    }

    /// Deletes `Recordings/<day>/*.wav` files no entry refers to once they are older than
    /// dictation audio may be kept, then day folders left empty. Such files appear when a
    /// damaged `history.json` was moved aside, or when the app quit before a new entry
    /// was saved. A dictation still being transcribed is never among them: its audio
    /// stays in the temporary folder until its entry is added. Returns how many went.
    private func removeOrphanedRecordings(_ orphans: [URL]) -> Int {
        let fileManager = FileManager.default
        var removed = 0
        for file in orphans {
            do {
                try fileManager.removeItem(at: file)
                removed += 1
            } catch {
                logger.error("Could not remove \(file.lastPathComponent, privacy: .public): \(error.localizedDescription, privacy: .public)")
            }
        }
        if !orphans.isEmpty, let folder = recordingsFolder,
           let days = try? fileManager.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey]) {
            for day in days where Self.isDayFolder(day) {
                if let remaining = try? fileManager.contentsOfDirectory(atPath: day.path),
                   remaining.allSatisfy({ $0 == ".DS_Store" }) {
                    try? fileManager.removeItem(at: day)
                }
            }
        }
        return removed
    }

    /// The recordings `removeOrphanedRecordings` would delete.
    private func orphanedRecordings(referencedBy entries: [HistoryEntry], now: Date) -> [URL] {
        guard let folder = recordingsFolder,
              (try? folder.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == false,
              let maximumAge = settings.keepDictationAudio.maximumAge else { return [] }
        let minimumAge = max(maximumAge, Self.orphanGracePeriod)
        // Resolve relative names against the same root used for enumeration.
        let referenced = Set(entries.compactMap(\.audioFileName).map {
            folder.appendingPathComponent($0).standardizedFileURL
        })
        let fileManager = FileManager.default
        guard let days = try? fileManager.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        else { return [] }
        var orphans: [URL] = []
        for day in days where Self.isDayFolder(day) {
            let keys: Set<URLResourceKey> = [.contentModificationDateKey, .isRegularFileKey, .isSymbolicLinkKey]
            let files = (try? fileManager.contentsOfDirectory(at: day, includingPropertiesForKeys: Array(keys))) ?? []
            let dayEnd = Self.endOfDay(named: day.lastPathComponent)
            for file in files where file.pathExtension.lowercased() == "wav" {
                guard !referenced.contains(file.standardizedFileURL) else { continue }
                // A matching extension is not enough: removeItem would recursively erase
                // a directory named *.wav. Symlinks are not recordings owned by this sweep.
                guard let values = try? file.resourceValues(forKeys: keys),
                      values.isRegularFile == true, values.isSymbolicLink != true else { continue }
                let modified = values.contentModificationDate
                // The folder is named after the day of the dictation, which survives a copy
                // that refreshes the modification date; the older of the two counts.
                guard let recorded = [modified, dayEnd].compactMap({ $0 }).min(),
                      now.timeIntervalSince(recorded) >= minimumAge else { continue }
                orphans.append(file)
            }
        }
        return orphans
    }

    private static func isDayFolder(_ url: URL) -> Bool {
        guard let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]) else { return false }
        return values.isDirectory == true && values.isSymbolicLink != true
    }

    /// The end of a `yyyy-MM-dd` recordings day: no recording in that folder is newer.
    static func endOfDay(named name: String, calendar: Calendar = .current) -> Date? {
        let parts = name.split(separator: "-", omittingEmptySubsequences: false).map { Int($0) }
        guard name.count == 10, parts.count == 3, let year = parts[0], let month = parts[1], let day = parts[2],
              (1...12).contains(month), (1...31).contains(day),
              let start = calendar.date(from: DateComponents(year: year, month: month, day: day)) else { return nil }
        return calendar.date(byAdding: .day, value: 1, to: start)
    }

    /// Ids of entries older than the period allows.
    public static func expiredEntryIDs(_ entries: [HistoryEntry], keep: RetentionPeriod, now: Date) -> [UUID] {
        guard let maximumAge = keep.maximumAge else { return [] }
        return entries.filter { isExpired($0.date, maximumAge: maximumAge, now: now) }.map(\.id)
    }

    /// Ids of entries whose audio file should be removed.
    public static func expiredAudioIDs(_ entries: [HistoryEntry], keep: RetentionPeriod, now: Date) -> [UUID] {
        guard let maximumAge = keep.maximumAge else { return [] }
        return entries
            .filter { $0.audioFileName != nil && isExpired($0.date, maximumAge: maximumAge, now: now) }
            .map(\.id)
    }

    /// Ids of meetings whose audio should be removed.
    ///
    /// Only the audio of a transcribed meeting is removed: a meeting that was never
    /// transcribed (still recording or transcribing, recorded and waiting, or failed) has
    /// nothing else, and removing its audio would leave an empty card.
    /// `afterTranscription` needs recognized text and no unresolved empty-result error.
    /// An age is counted from `audioStoredAt`, the moment the audio became this Mac's to
    /// keep, and only from `createdAt` when there is none: a clip recorded on a phone a week
    /// ago carries that week in `createdAt`, and counting it would delete the audio of a
    /// meeting collected minutes ago.
    public static func expiredMeetingAudioIDs(_ meetings: [MeetingRecord], keep: RetentionPeriod, now: Date) -> [UUID] {
        meetings.filter { meeting in
            guard meeting.hasAudio, meeting.status == .transcribed else { return false }
            guard keep != .afterTranscription else {
                return !meeting.segments.isEmpty && meeting.errorMessage == nil
            }
            guard let maximumAge = keep.maximumAge else { return false }
            return isExpired(meeting.audioStoredAt ?? meeting.createdAt, maximumAge: maximumAge, now: now)
        }
        .map(\.id)
    }

    /// An item is expired once it is at least `maximumAge` old; a zero age (`never`)
    /// expires everything up to now.
    private static func isExpired(_ date: Date, maximumAge: TimeInterval, now: Date) -> Bool {
        now.timeIntervalSince(date) >= maximumAge
    }
}
