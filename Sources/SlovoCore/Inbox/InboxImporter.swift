import AVFoundation
import Foundation

/// Audio dropped into `Inbox/` becomes a meeting: a clip recorded on a phone, a voice
/// message, a file copied in by hand. What fills the folder — a sync tool, Finder, a
/// cable — is deliberately outside this type, so the import path outlives any transport.
///
/// A clip that arrives with the `.json` sidecar a phone writes keeps that phone's time,
/// name and marked moments; audio that arrives alone is imported exactly as before.
@MainActor
public final class InboxImporter {
    /// What AVFoundation opens without help. Also the file picker's allowed types for a
    /// manual import, so the two ways of adding a file never disagree.
    public static let audioExtensions: Set<String> = ["m4a", "mp3", "wav", "caf", "aac", "aif", "aiff", "mp4", "mov"]
    /// A file has to stop growing before it is taken, so a half-copied clip is left alone.
    static let settleSeconds: TimeInterval = 3
    /// Shorter than this is a stray tap, not speech.
    static let minimumSeconds = 0.5
    static let pollSeconds: TimeInterval = 5
    /// How long a sidecar waits for its audio. The phone writes the sidecar after the clip
    /// and a transport copies it last, so after this long the audio is not coming at all.
    static let strandedSidecarSeconds: TimeInterval = 600
    /// Imported files move here rather than being deleted: the folder is often the shared
    /// end of a sync, and destroying what arrived is not this code's decision to make.
    static let importedFolderName = "Imported"
    /// A clip being turned into a meeting waits under `.importing-<meeting id>-<name>`: hidden,
    /// so no sweep takes it again, and naming its meeting, so an import cut off by a crash
    /// or a quit finishes that meeting at the next sweep instead of starting a second one.
    static let claimPrefix = ".importing-"

    /// What a refusal is remembered by: the same name with a new size or date is a new file
    /// (a copy that has since completed, a clip replaced by hand) and is tried again.
    struct FileIdentity: Hashable {
        let name: String
        let size: Int
        let modified: Date
    }

    /// A clip left under its claim name, with the meeting it was becoming.
    struct ClaimedClip: Equatable {
        let url: URL
        let meetingID: UUID
        /// The clip's own name, which it goes back to or on to `Imported/` under.
        let name: String
    }

    private let directories: AppDirectories
    private let store: any MeetingStoring
    private let transcriber: MeetingTranscriber
    private let settings: AppSettings
    private let logger = SlovoLog.logger("Inbox")
    private var watch: Task<Void, Never>?
    private var isSweeping = false
    /// Files that failed once, so a broken clip cannot spin the sweep forever. Keyed by
    /// name, size and date, so a file that changes afterwards is tried again.
    private var refused: Set<FileIdentity> = []

    public init(directories: AppDirectories, store: any MeetingStoring,
                transcriber: MeetingTranscriber, settings: AppSettings) {
        self.directories = directories
        self.store = store
        self.transcriber = transcriber
        self.settings = settings
    }

    /// Polls instead of watching the file system: at one sweep every five seconds the
    /// difference is invisible, and a poll has no failure modes worth debugging.
    public func start() {
        guard watch == nil else { return }
        watch = Task { [weak self] in
            while !Task.isCancelled {
                await self?.sweep()
                try? await Task.sleep(for: .seconds(Self.pollSeconds))
            }
        }
    }

    public func stop() {
        watch?.cancel()
        watch = nil
    }

    /// Imports every clip that has settled, then gives up on sidecars whose audio never
    /// came. One clip at a time: a long file holds the engine, and nothing here is urgent.
    func sweep(now: Date = Date()) async {
        guard settings.inboxEnabled, !isSweeping else { return }
        isSweeping = true
        defer { isSweeping = false }
        // Imports an earlier run started and never finished come first, so their clips are
        // settled before anything new is taken.
        for claimed in claimedClips() {
            do {
                try Task.checkCancellation()
                try await resumeImport(of: claimed, now: now)
            } catch is CancellationError {
                return
            } catch {
                refuse(claimed.url)
                logger.error("Finishing the import of \(claimed.name, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            }
        }
        for url in readyFiles(now: now) {
            do {
                try Task.checkCancellation()
                try await importClip(at: url, now: now)
            } catch is CancellationError {
                return
            } catch {
                refuse(url)
                logger.error("Importing \(url.lastPathComponent, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            }
        }
        dropStrandedSidecars(now: now)
    }

    /// Remembers the file now at `url`, if any, as one not to try again while it is unchanged.
    private func refuse(_ url: URL) {
        if let identity = Self.identity(of: url) {
            refused.insert(identity)
        }
    }

    /// The name, size and modification date of the file at `url`, read afresh.
    static func identity(of url: URL) -> FileIdentity? {
        let fresh = URL(fileURLWithPath: url.path)
        guard let values = try? fresh.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey]),
              let modified = values.contentModificationDate else { return nil }
        return FileIdentity(name: fresh.lastPathComponent, size: values.fileSize ?? 0, modified: modified)
    }

    /// Audio files old enough to have finished copying, oldest first.
    func readyFiles(now: Date = Date()) -> [URL] {
        let keys: [URLResourceKey] = [.contentModificationDateKey, .isRegularFileKey, .fileSizeKey]
        // Hidden files are skipped: a copy still arriving as `.partial-<name>`, a clip already
        // claimed by an import, and whatever a sync tool keeps to itself.
        guard let items = try? FileManager.default.contentsOfDirectory(at: directories.inboxFolder,
                                                                      includingPropertiesForKeys: keys,
                                                                      options: [.skipsHiddenFiles]) else {
            return []
        }
        return items.compactMap { url -> (URL, Date)? in
            guard Self.audioExtensions.contains(url.pathExtension.lowercased()),
                  !url.lastPathComponent.hasPrefix("."),
                  let values = try? url.resourceValues(forKeys: Set(keys)),
                  values.isRegularFile == true,
                  let modified = values.contentModificationDate,
                  !refused.contains(FileIdentity(name: url.lastPathComponent, size: values.fileSize ?? 0,
                                                 modified: modified)),
                  now.timeIntervalSince(modified) >= Self.settleSeconds else {
                return nil
            }
            return (url, modified)
        }
        .sorted { $0.1 < $1.1 }
        .map(\.0)
    }

    /// Sidecars left waiting by audio that never arrived, oldest first.
    func strandedSidecars(now: Date = Date()) -> [URL] {
        let keys: [URLResourceKey] = [.contentModificationDateKey, .isRegularFileKey, .fileSizeKey]
        guard let items = try? FileManager.default.contentsOfDirectory(at: directories.inboxFolder,
                                                                      includingPropertiesForKeys: keys,
                                                                      options: [.skipsHiddenFiles]) else {
            return []
        }
        // A clip under its claim name still owns its sidecar.
        let audioNames = items.map(\.lastPathComponent) + claimedClips(includingRefused: true).map(\.name)
        let audioStems = Set(audioNames.filter { Self.audioExtensions.contains(Self.fileExtension(of: $0)) }
            .map(Self.stem(of:)))
        return items.compactMap { url -> (URL, Date)? in
            guard url.pathExtension.lowercased() == InboxSidecar.fileExtension,
                  !audioStems.contains(url.deletingPathExtension().lastPathComponent),
                  let values = try? url.resourceValues(forKeys: Set(keys)),
                  values.isRegularFile == true,
                  let modified = values.contentModificationDate,
                  !refused.contains(FileIdentity(name: url.lastPathComponent, size: values.fileSize ?? 0,
                                                 modified: modified)),
                  now.timeIntervalSince(modified) >= Self.strandedSidecarSeconds else {
                return nil
            }
            return (url, modified)
        }
        .sorted { $0.1 < $1.1 }
        .map(\.0)
    }

    /// How a clip is processed. A clip the phone calls a meeting is separated into speakers
    /// exactly as the user's settings ask, like one recorded here; a note keeps speakers
    /// off, because separation would cost a model load and a large memory peak to label one
    /// voice "Speaker 1".
    static func processingOptions(for kind: InboxSidecar.Kind, settings: AppSettings) -> MeetingProcessingOptions {
        switch kind {
        case .meeting: return .fromSettings(settings)
        case .note: return MeetingProcessingOptions(mode: .full, separateSpeakers: false)
        }
    }

    /// The moments marked on the phone, on the clip's own timeline. They are the same kind
    /// of mark the shortcut makes here, so they show with the same star. A moment past the
    /// end of the decoded audio is kept at the end rather than dropped: the phone and the
    /// Mac can measure a clip a little differently, and the mark still belongs to it.
    static func highlights(of sidecar: InboxSidecar, duration: TimeInterval,
                           recordedAt: Date) -> [MeetingHighlight] {
        sidecar.moments.map { moment in
            let time = min(moment.time, duration)
            return MeetingHighlight(time: time, note: moment.note, source: .recording,
                                    createdAt: recordedAt.addingTimeInterval(time))
        }
    }

    /// Clips left under their claim name by an import that never finished, oldest first.
    func claimedClips(includingRefused: Bool = false) -> [ClaimedClip] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey]
        guard let items = try? FileManager.default.contentsOfDirectory(at: directories.inboxFolder,
                                                                      includingPropertiesForKeys: keys) else {
            return []
        }
        return items.compactMap { url -> ClaimedClip? in
            guard let (meetingID, name) = Self.parseClaimName(url.lastPathComponent),
                  let values = try? url.resourceValues(forKeys: Set(keys)), values.isRegularFile == true else {
                return nil
            }
            if !includingRefused, let modified = values.contentModificationDate,
               refused.contains(FileIdentity(name: url.lastPathComponent, size: values.fileSize ?? 0, modified: modified)) {
                return nil
            }
            return ClaimedClip(url: url, meetingID: meetingID, name: name)
        }
        .sorted { $0.url.lastPathComponent < $1.url.lastPathComponent }
    }

    /// `.importing-<meeting id>-<name>` for a clip becoming that meeting.
    static func claimName(for meetingID: UUID, name: String) -> String {
        claimPrefix + meetingID.uuidString + "-" + name
    }

    /// The meeting id and the clip's own name in a claim name; nil for any other name.
    static func parseClaimName(_ fileName: String) -> (UUID, String)? {
        guard fileName.hasPrefix(claimPrefix) else { return nil }
        let rest = fileName.dropFirst(claimPrefix.count)
        let idLength = 36
        guard rest.count > idLength + 1, let id = UUID(uuidString: String(rest.prefix(idLength))),
              rest.dropFirst(idLength).first == "-" else { return nil }
        let name = String(rest.dropFirst(idLength + 1))
        guard audioExtensions.contains(fileExtension(of: name)) else { return nil }
        return (id, name)
    }

    /// What an import needs from a clip and its sidecar, read before anything is created.
    private struct PreparedClip {
        let audio: StagedAudio
        let sidecar: InboxSidecar?
        let sidecarURL: URL
        let recordedAt: Date
    }

    private struct StagedAudio {
        let url: URL
        let duration: TimeInterval

        func discard() {
            try? FileManager.default.removeItem(at: url.deletingLastPathComponent())
        }
    }

    /// Reads the audio at `url` and the sidecar that belongs to a clip named `name`.
    private func prepare(_ url: URL, name: String) async throws -> PreparedClip {
        let arrived = (try? URL(fileURLWithPath: url.path).resourceValues(forKeys: [.contentModificationDateKey]))?
            .contentModificationDate ?? Date()
        let sidecarURL = InboxSidecar.url(for: directories.inboxFolder.appendingPathComponent(name))
        let sidecar = InboxSidecar.read(from: sidecarURL)
        let audio = try await stageAudio(from: url)
        return PreparedClip(audio: audio,
                            sidecar: sidecar, sidecarURL: sidecarURL, recordedAt: sidecar?.recordedAt ?? arrived)
    }

    private func stageAudio(from source: URL) async throws -> StagedAudio {
        let folder = try FileManager.default.url(for: .itemReplacementDirectory, in: .userDomainMask,
                                                appropriateFor: directories.meetingsFolder, create: true)
        let target = folder.appendingPathComponent("mic.caf")
        let conversion = Task.detached(priority: .utility) {
            try MeetingTrackReader.convertToMeetingTrack(from: source, to: target)
        }
        do {
            let duration = try await withTaskCancellationHandler {
                try await conversion.value
            } onCancel: {
                conversion.cancel()
            }
            try Task.checkCancellation()
            return StagedAudio(url: target, duration: duration)
        } catch {
            try? FileManager.default.removeItem(at: folder)
            throw error
        }
    }

    private func importClip(at url: URL, now: Date) async throws {
        let name = url.lastPathComponent
        let identity = Self.identity(of: url)
        let clip = try await prepare(url, name: name)
        defer { clip.audio.discard() }
        // A sync tool may have replaced the input while it was being decoded.
        guard Self.identity(of: url) == identity else { return }
        guard clip.audio.duration >= Self.minimumSeconds else {
            try moveToImported(url)
            moveSidecarAside(clip.sidecarURL)
            return
        }

        let created = try store.create(title: clip.sidecar?.title ?? MeetingTitles.defaultTitle(for: clip.recordedAt))
        // From here on the clip carries its meeting's id: a crash or a quit before it reaches
        // `Imported/` finishes this meeting at the next sweep instead of making a second one.
        let claimed = directories.inboxFolder.appendingPathComponent(Self.claimName(for: created.id, name: name))
        do {
            try FileManager.default.moveItem(at: url, to: claimed)
        } catch {
            store.delete(id: created.id)
            throw error
        }
        let meeting: MeetingRecord
        do {
            meeting = try fill(created, with: clip, storedAt: now)
        } catch {
            // The meeting is gone again, so the clip goes back under its own name, where the
            // failure is remembered.
            try? unclaim(claimed, as: name)
            throw error
        }
        try finish(meeting, claimed: claimed, name: name, clip: clip)
        await transcribe(meeting, kind: clip.sidecar?.kind)
    }

    /// Finishes an import an earlier run started: the meeting the clip names gets its audio and
    /// fields again from the clip, which is still whole, and the clip moves on. A meeting that
    /// is gone, or was never saved, sends the clip back to be imported afresh.
    private func resumeImport(of claim: ClaimedClip, now: Date) async throws {
        guard let existing = store.meetings.first(where: { $0.id == claim.meetingID }) else {
            try unclaim(claim.url, as: claim.name)
            logger.notice("\(claim.name, privacy: .public) returns to the inbox: its meeting was never saved")
            return
        }
        // A meeting already transcribing or transcribed was taken further since; only the files
        // are left to put away.
        guard existing.status == .recording || existing.status == .recorded else {
            try moveToImported(claim.url, as: claim.name)
            moveSidecarAside(InboxSidecar.url(for: directories.inboxFolder.appendingPathComponent(claim.name)))
            return
        }
        let identity = Self.identity(of: claim.url)
        let clip = try await prepare(claim.url, name: claim.name)
        defer { clip.audio.discard() }
        guard Self.identity(of: claim.url) == identity,
              let current = store.meetings.first(where: { $0.id == claim.meetingID }),
              current.status == .recording || current.status == .recorded else { return }
        let meeting = try fill(current, with: clip, storedAt: now)
        try finish(meeting, claimed: claim.url, name: claim.name, clip: clip)
        logger.notice("Finished importing \(claim.name, privacy: .public), which a previous run had started")
        await transcribe(meeting, kind: clip.sidecar?.kind)
    }

    /// Writes the clip's audio into the meeting and sets what the clip and its sidecar say.
    private func fill(_ created: MeetingRecord, with clip: PreparedClip, storedAt: Date) throws -> MeetingRecord {
        var meeting = try fill(created, audio: clip.audio, recordedAt: clip.recordedAt, storedAt: storedAt)
        if let sidecar = clip.sidecar {
            meeting.source = MeetingSource(origin: .phone, device: sidecar.device)
            meeting.highlights = Self.highlights(of: sidecar, duration: clip.audio.duration, recordedAt: clip.recordedAt)
            store.update(meeting)
        }
        return meeting
    }

    /// Moves the claimed clip and its sidecar on to `Imported/`. A clip that cannot be moved
    /// stays claimed, so the next sweep finishes this meeting rather than making another,
    /// and is remembered so that sweep is not every five seconds.
    private func finish(_ meeting: MeetingRecord, claimed: URL, name: String, clip: PreparedClip) throws {
        do {
            try moveToImported(claimed, as: name)
        } catch {
            refuse(claimed)
            throw error
        }
        moveSidecarAside(clip.sidecarURL)
        logger.info("Imported \(name, privacy: .public) as a \(Int(clip.audio.duration), privacy: .public) s meeting")
    }

    private func transcribe(_ meeting: MeetingRecord, kind: InboxSidecar.Kind?) async {
        let options = Self.processingOptions(for: kind ?? .note, settings: settings)
        _ = try? await transcriber.transcribe(meetingID: meeting.id, options: options)
    }

    /// Gives a claimed clip its own name back, or a free variant of it.
    private func unclaim(_ claimed: URL, as name: String) throws {
        let target = Self.freeURL(in: directories.inboxFolder, name: name)
        try FileManager.default.moveItem(at: claimed, to: target)
    }

    /// A file the user picked by hand, from anywhere on disk — not the Inbox folder, and
    /// not gated behind `settings.inboxEnabled`, which only governs the automatic folder
    /// sweep. The file is read in place and never moved or deleted: unlike a clip dropped
    /// into the transient Inbox, this is the user's own copy, sitting wherever they keep it.
    /// Mirrors `MeetingRecorder.stop()`: it only creates the meeting and its audio track,
    /// so the caller starts transcription the same way it would for a fresh recording.
    public func importPickedFile(at url: URL) async throws -> MeetingRecord {
        let now = Date()
        let recordedAt = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? now
        let audio = try await stageAudio(from: url)
        defer { audio.discard() }
        guard audio.duration >= Self.minimumSeconds else {
            throw MeetingErrors.fileTooShort()
        }
        let created = try store.create(title: Self.title(fromFileName: url, recordedAt: recordedAt))
        let meeting = try fill(created, audio: audio, recordedAt: recordedAt, storedAt: now)
        logger.info("Imported \(url.lastPathComponent, privacy: .public) as a \(Int(audio.duration), privacy: .public) s meeting")
        return meeting
    }

    /// The file's own name, since that is the one thing a manual pick says about its
    /// content; a name AVFoundation left empty falls back to the usual date-based title.
    static func title(fromFileName url: URL, recordedAt: Date) -> String {
        let stem = url.deletingPathExtension().lastPathComponent.trimmingCharacters(in: .whitespacesAndNewlines)
        return stem.isEmpty ? MeetingTitles.defaultTitle(for: recordedAt) : stem
    }

    /// Installs a complete CAF before making it visible as a recorded meeting.
    private func fill(_ created: MeetingRecord, audio: StagedAudio, recordedAt: Date,
                      storedAt: Date) throws -> MeetingRecord {
        var meeting = created
        let target = store.microphoneAudioURL(for: meeting.id)
        let files = FileManager.default
        do {
            if files.fileExists(atPath: target.path) {
                _ = try files.replaceItemAt(target, withItemAt: audio.url)
            } else {
                try files.moveItem(at: audio.url, to: target)
            }
        } catch {
            store.delete(id: meeting.id)
            throw error
        }
        // One instant both names the meeting and places it in the list: the source's own
        // time when it has one, and otherwise the moment the audio was last written. The
        // title is made from that same instant, and a title that disagreed with the sort
        // order would leave the list looking shuffled.
        meeting.createdAt = recordedAt
        // Retention counts the audio's age from here instead of from `createdAt`: a clip
        // recorded elsewhere last week becomes this Mac's to keep only now, and counted
        // from the source's time its audio could be deleted before anyone had heard it.
        meeting.audioStoredAt = storedAt
        meeting.duration = audio.duration
        meeting.hasAudio = true
        meeting.status = .recorded
        store.update(meeting)
        return meeting
    }

    /// Moves aside a sidecar whose clip never came, and says so once. Waiting forever would
    /// leave the folder filling with metadata for audio that does not exist.
    private func dropStrandedSidecars(now: Date) {
        for url in strandedSidecars(now: now) {
            do {
                try moveToImported(url)
                logger.notice("\(url.lastPathComponent, privacy: .public) waited for its audio, which never arrived")
            } catch {
                refuse(url)
                logger.error("Moving \(url.lastPathComponent, privacy: .public) aside failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    /// A sidecar leaves the folder with its clip, read or not: left behind, it would later
    /// look like metadata whose audio never arrived.
    private func moveSidecarAside(_ url: URL) {
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        do {
            try moveToImported(url)
        } catch {
            logger.error("Moving \(url.lastPathComponent, privacy: .public) aside failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    private func moveToImported(_ url: URL, as name: String? = nil) throws {
        let folder = directories.inboxFolder.appendingPathComponent(Self.importedFolderName, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try FileManager.default.moveItem(at: url, to: Self.freeURL(in: folder, name: name ?? url.lastPathComponent))
    }

    /// `name` in `folder`, or `<stem>-2.<ext>`, `<stem>-3.<ext>` … when that is taken.
    private static func freeURL(in folder: URL, name: String) -> URL {
        var target = folder.appendingPathComponent(name)
        var attempt = 2
        let stem = stem(of: name)
        let pathExtension = URL(fileURLWithPath: name).pathExtension
        while FileManager.default.fileExists(atPath: target.path) {
            let numbered = pathExtension.isEmpty ? "\(stem)-\(attempt)" : "\(stem)-\(attempt).\(pathExtension)"
            target = folder.appendingPathComponent(numbered)
            attempt += 1
        }
        return target
    }

    private static func stem(of name: String) -> String {
        URL(fileURLWithPath: name).deletingPathExtension().lastPathComponent
    }

    private static func fileExtension(of name: String) -> String {
        URL(fileURLWithPath: name).pathExtension.lowercased()
    }
}
