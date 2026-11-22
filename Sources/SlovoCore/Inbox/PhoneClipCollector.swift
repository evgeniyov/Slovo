import Foundation

/// adb runs here, one command at a time: a pull of a long clip must not race the next
/// poll, and none of this waiting belongs on the main actor.
private let adbQueue = DispatchQueue(label: "local.slovo.phone-clips")

/// A command that has not answered by then is stuck — a locked phone, a cable half out —
/// and is killed, so the next poll starts from a clean adb.
private let adbCommandTimeout: TimeInterval = 120

/// Where adb lands when Android Studio or Homebrew installs it. Outside the type because a
/// default argument is read without the main actor that the collector runs on.
private let defaultAdbPaths = ["~/Library/Android/sdk/platform-tools/adb", "/opt/homebrew/bin/adb"]

/// Collects the clips a phone recorded over the USB cable and leaves them in the inbox,
/// where `InboxImporter` turns each one into a meeting.
///
/// Nothing listens on the network: this asks adb what is plugged in, copies the pair of
/// files a clip is made of, and deletes from the phone only what arrived whole. A Mac
/// without adb is an ordinary Mac rather than a broken one, so that is said once and then
/// left alone.
///
/// The phone's side of the bargain is that a clip appears in the folder only once it is
/// complete, with its sidecar already beside it. This side does not take that on trust:
/// audio with no sidecar is left where it is until it has stopped changing for a minute,
/// which is far longer than publishing a clip takes.
@MainActor
public final class PhoneClipCollector {
    /// Where the phone app writes: MediaStore's shared `Download/Slovo`.
    static let phoneFolder = "/sdcard/Download/Slovo"
    /// How often the cable is looked at; a pull then takes as long as it takes.
    static let pollSeconds: TimeInterval = 15
    /// A file on its way from the phone is written under this prefix and renamed once whole;
    /// the leading dot keeps the importer, which skips hidden files, away from it.
    static let partialPrefix = ".partial-"
    /// How long audio with no sidecar has to sit still before it is taken for what it looks
    /// like. Publishing a clip takes a moment, so a minute is the difference between a
    /// recording another app left in the folder and one of this phone's clips caught
    /// between its audio appearing and its sidecar landing.
    static let sidecarGraceSeconds: TimeInterval = 60

    /// A clip on the phone: the audio, and the sidecar next to it when it has one.
    struct PhoneClip: Equatable, Sendable {
        var audio: String
        var sidecar: String?
    }

    /// What the phone said about its folder: its own clock, and the modification time of
    /// each clip that was asked about.
    struct PhoneTimes: Equatable, Sendable {
        var now: Date?
        var modified: [String: Date] = [:]
    }

    /// What adb printed, and whether it worked.
    struct CommandResult: Sendable {
        var status: Int32
        var output: String

        var succeeded: Bool { status == 0 }
    }

    private let directories: AppDirectories
    private let settings: AppSettings
    private let adbPaths: [String]
    private let adbEnvironment: [String: String]
    private let logger = SlovoLog.logger("PhoneClips")
    private var poll: Task<Void, Never>?
    /// Said once. Without it a Mac that has no adb would repeat the same line every poll,
    /// and never installing adb is a choice, not a fault.
    private var saidAdbIsMissing = false
    /// Phones already told about, so a folder listing the poll cannot date is said once
    /// rather than every fifteen seconds. Internal so a test can see what was said.
    private(set) var saidTimesAreUnreadable: Set<String> = []

    public convenience init(directories: AppDirectories, settings: AppSettings) {
        self.init(directories: directories, settings: settings, adbPaths: defaultAdbPaths,
                  environment: ProcessInfo.processInfo.environment)
    }

    /// Where adb is looked for is given rather than assumed, so a test can put a stand-in
    /// for the cable — and for the phone behind it — at the end of a path of its own.
    init(directories: AppDirectories, settings: AppSettings, adbPaths: [String],
         environment: [String: String]) {
        self.directories = directories
        self.settings = settings
        self.adbPaths = adbPaths
        self.adbEnvironment = environment
    }

    /// Polls while the app runs; each pass does nothing unless the inbox is switched on.
    public func start() {
        guard poll == nil else { return }
        poll = Task { [weak self] in
            while !Task.isCancelled {
                await self?.collect()
                try? await Task.sleep(for: .seconds(Self.pollSeconds))
            }
        }
    }

    public func stop() {
        poll?.cancel()
        poll = nil
    }

    /// One pass: the cable, then every phone that answered on it.
    func collect() async {
        guard settings.inboxEnabled else { return }
        guard let adb = Self.adbURL(defaultPaths: adbPaths, environment: adbEnvironment) else {
            if !saidAdbIsMissing {
                saidAdbIsMissing = true
                logger.info("adb is not installed, so nothing is collected over the cable")
            }
            return
        }
        saidAdbIsMissing = false

        let devices = await Self.run(adb, ["devices"])
        // A phone that is only plugged in, unauthorized or still waking up is not a
        // failure: the next poll finds it once its owner has allowed USB debugging.
        guard devices.succeeded else { return }
        // Every phone that answered, not the first of them: a second phone on the cable
        // carries its own clips, and they would otherwise wait for the first to be unplugged.
        for serial in Self.authorizedSerials(in: devices.output) {
            await collect(from: serial, adb: adb)
        }
    }

    /// One phone: what is in its folder, how old the clips that came without a sidecar are,
    /// and then every clip that is complete.
    private func collect(from serial: String, adb: URL) async {
        let listing = await Self.run(adb, ["-s", serial, "shell", "ls", "-1", Self.phoneFolder])
        guard listing.succeeded else { return }
        let clips = Self.clips(in: listing.output)
        let waiting = clips.filter { $0.sidecar == nil }.map(\.audio)
        let times = waiting.isEmpty ? PhoneTimes() : await phoneTimes(of: waiting, from: serial, adb: adb)
        noteUnreadableTimes(times, asked: waiting, from: serial)
        for clip in Self.collectable(clips, times: times) {
            await collect(clip, from: serial, adb: adb)
        }
    }

    /// Says once that a phone answered with nothing a clip's age can be read from — a `stat`
    /// that does not take these flags, a shell that swallowed the command. Every clip that
    /// arrived without a sidecar then waits for a pass that never comes, which is the safe
    /// thing to do and, left unsaid, looks exactly like a phone with nothing to hand over.
    /// A phone that answers properly is forgotten again, so a cable that starts failing
    /// later is not silent because of what happened before it.
    private func noteUnreadableTimes(_ times: PhoneTimes, asked: [String], from serial: String) {
        guard !asked.isEmpty else { return }
        guard !Self.timesAreReadable(times, asked: asked) else {
            saidTimesAreUnreadable.remove(serial)
            return
        }
        guard saidTimesAreUnreadable.insert(serial).inserted else { return }
        logger.notice("\(serial, privacy: .public) reported no clock or clip times, so its clips without a sidecar keep waiting; its stat may not take the flags used")
    }

    // MARK: - Reading what adb printed

    /// The serials of phones ready to talk to. `adb devices` prints a header and then one
    /// `<serial> <state>` line each; only "device" is usable, while "unauthorized" is a
    /// phone whose owner has not tapped Allow and "offline" one that is still waking up.
    static func authorizedSerials(in output: String) -> [String] {
        output.split(whereSeparator: \.isNewline).compactMap { line in
            let fields = line.split(whereSeparator: \.isWhitespace)
            guard fields.count >= 2, fields[1] == "device" else { return nil }
            return String(fields[0])
        }
    }

    /// The clips in a folder listing, each with the sidecar that shares its name. A sidecar
    /// with no audio beside it is left on the phone: the clip is what makes a meeting, and
    /// a sidecar pulled alone would only sit in the inbox waiting.
    static func clips(in listing: String) -> [PhoneClip] {
        let names = listing.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter(isSafeName)
        var sidecarsByStem: [String: String] = [:]
        for name in names where fileExtension(of: name) == InboxSidecar.fileExtension {
            sidecarsByStem[stem(of: name)] = name
        }
        return names
            .filter { InboxImporter.audioExtensions.contains(fileExtension(of: $0)) }
            .sorted()
            .map { PhoneClip(audio: $0, sidecar: sidecarsByStem[stem(of: $0)]) }
    }

    /// The clips that may be taken now.
    ///
    /// A clip with its sidecar beside it is finished, because that is the order the phone
    /// publishes them in. Audio on its own is either a recording some other app left in the
    /// folder or one of this phone's clips caught between its two files appearing, and only
    /// age tells the two apart — so it waits out the grace period first. Audio whose age
    /// the phone did not report waits for a later pass instead of being guessed at: guessing
    /// wrong costs a meeting every moment that was marked during it.
    static func collectable(_ clips: [PhoneClip], times: PhoneTimes) -> [PhoneClip] {
        clips.filter { clip in
            guard clip.sidecar == nil else { return true }
            guard let now = times.now, let modified = times.modified[clip.audio] else { return false }
            return now.timeIntervalSince(modified) >= sidecarGraceSeconds
        }
    }

    /// What `date +%s; stat -c '%Y %n'` printed: the phone's clock alone on a line, then one
    /// `<epoch> <path>` line per file. A name can hold spaces, so a line is split once, and
    /// a line that does not begin with a number is stat naming a file that has just gone, or
    /// adb talking about its daemon, and is skipped.
    static func phoneTimes(in output: String) -> PhoneTimes {
        var times = PhoneTimes()
        for line in output.split(whereSeparator: \.isNewline) {
            let text = line.trimmingCharacters(in: .whitespacesAndNewlines)
            guard let space = text.firstIndex(of: " ") else {
                if times.now == nil, let seconds = TimeInterval(text) {
                    times.now = Date(timeIntervalSince1970: seconds)
                }
                continue
            }
            guard let seconds = TimeInterval(text[..<space]) else { continue }
            let name = URL(fileURLWithPath: String(text[text.index(after: space)...])).lastPathComponent
            guard !name.isEmpty else { continue }
            times.modified[name] = Date(timeIntervalSince1970: seconds)
        }
        return times
    }

    /// Whether the phone gave both ends of the comparison an age needs: its own clock, and a
    /// time for at least one of the clips asked about. One clip missing from the answer is
    /// `stat` naming a file that has just gone, not a phone that cannot be read.
    static func timesAreReadable(_ times: PhoneTimes, asked: [String]) -> Bool {
        times.now != nil && asked.contains { times.modified[$0] != nil }
    }

    /// Names travel into a shell command on the phone and into a path on the Mac, so only a
    /// plain file name that still fits the Mac's limit while it is copied is taken: nothing
    /// that leaves the folder, and nothing that could end the quoting around it. A leading
    /// dot is refused in its own right: MediaStore parks a clip that is still being written
    /// under `.pending-<n>-<name>`, so the dot marks exactly the file that must never be
    /// pulled, and `.` and `..` are not files at all.
    static func isSafeName(_ name: String) -> Bool {
        // A name has to leave room for the prefix it is copied under within the 255 bytes a
        // file name may have.
        guard !name.isEmpty, name.utf8.count <= 255 - partialPrefix.utf8.count, !name.hasPrefix(".") else {
            return false
        }
        let refused: Set<Character> = ["/", "'", "\"", "\\", "`", "$"]
        guard !name.contains(where: refused.contains) else { return false }
        return !name.unicodeScalars.contains { $0.value < 0x20 }
    }

    /// adb where Android Studio and Homebrew put it, then anything on PATH. An app started
    /// from the Dock has a short PATH, so the two known paths are what usually answers.
    static func adbURL(defaultPaths: [String] = defaultAdbPaths,
                       environment: [String: String] = ProcessInfo.processInfo.environment) -> URL? {
        var candidates = defaultPaths.map { URL(fileURLWithPath: NSString(string: $0).expandingTildeInPath) }
        for folder in (environment["PATH"] ?? "").split(separator: ":") where !folder.isEmpty {
            candidates.append(URL(fileURLWithPath: String(folder)).appendingPathComponent("adb"))
        }
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    // MARK: - Collecting

    private func collect(_ clip: PhoneClip, from serial: String, adb: URL) async {
        let inbox = directories.inboxFolder
        do {
            try FileManager.default.createDirectory(at: inbox, withIntermediateDirectories: true)
        } catch {
            logger.error("Could not open the inbox folder: \(error.localizedDescription, privacy: .public)")
            return
        }
        // Already here: still in the inbox, or moved on to Imported once it became a
        // meeting. A clip the phone would not let go of comes round again on the next pass,
        // and without the second place to look it would become a second meeting. The phone
        // keeps its copy either way, because a matching name is not enough to decide that
        // this Mac has the clip.
        guard !Self.isAlreadyHere(clip.audio, inbox: inbox) else { return }

        // The sidecar is copied first. An import starts from the audio, so a clip is never
        // picked up while the metadata that belongs to it is still being copied.
        if let sidecar = clip.sidecar {
            guard await pull(sidecar, from: serial, adb: adb, into: inbox) else { return }
        }
        guard await pull(clip.audio, from: serial, adb: adb, into: inbox) else {
            // The sidecar arrived and its audio did not: it goes, since nothing on the Mac
            // belongs to it, and the pair stays on the phone for a later pass.
            if let sidecar = clip.sidecar {
                try? FileManager.default.removeItem(at: inbox.appendingPathComponent(sidecar))
            }
            return
        }
        logger.info("Collected \(clip.audio, privacy: .public) from the phone")

        // Deleted from the phone only now, with both files on the Mac and neither empty.
        // The audio goes first so the sidecar outlives it: while the pair is whole the
        // phone's own rule still holds, so a delete that fails in between leaves a clip the
        // next pass pairs again, not audio the grace period would take for a plain note.
        guard await remove(clip.audio, from: serial, adb: adb) else { return }
        if let sidecar = clip.sidecar {
            _ = await remove(sidecar, from: serial, adb: adb)
        }
    }

    /// Whether a clip of this name is in the inbox or has already been imported out of it.
    static func isAlreadyHere(_ name: String, inbox: URL) -> Bool {
        let imported = inbox.appendingPathComponent(InboxImporter.importedFolderName, isDirectory: true)
        return FileManager.default.fileExists(atPath: inbox.appendingPathComponent(name).path)
            || FileManager.default.fileExists(atPath: imported.appendingPathComponent(name).path)
    }

    /// True once the phone no longer has the file. `rm -f` stays quiet about a file that was
    /// already gone and speaks only of a real refusal — a read-only volume, a cable pulled
    /// mid-command — which is worth hearing: the clip stays where it is, and the guard above
    /// is then all that keeps it from arriving twice.
    private func remove(_ name: String, from serial: String, adb: URL) async -> Bool {
        let result = await Self.run(adb, ["-s", serial, "shell", "rm", "-f", "'\(Self.phoneFolder)/\(name)'"])
        if !result.succeeded {
            logger.error("\(name, privacy: .public) stays on the phone: deleting it there failed")
        }
        return result.succeeded
    }

    /// The phone's clock and the modification times of the named clips, in one command. Both
    /// ends of the comparison are read from the phone, because a Mac whose clock runs ahead
    /// of it would make a clip written a second ago look a minute old.
    private func phoneTimes(of names: [String], from serial: String, adb: URL) async -> PhoneTimes {
        // adb joins these into one line for the phone's shell without quoting anything, so
        // the format string and every path carry their own quotes.
        let paths = names.map { "'\(Self.phoneFolder)/\($0)'" }
        let result = await Self.run(adb, ["-s", serial, "shell", "date", "+%s", ";",
                                          "stat", "-c", "'%Y %n'"] + paths)
        return Self.phoneTimes(in: result.output)
    }

    /// True once the file is on the Mac under its own name and not empty.
    ///
    /// adb writes into a hidden `.partial-<name>` that the importer never looks at, and the
    /// file takes its real name only once the copy is complete: a pull that stalls for a few
    /// seconds would otherwise leave a half-copied clip that has stopped changing, which is
    /// exactly what the importer takes for a finished one. A failed pull leaves nothing behind.
    private func pull(_ name: String, from serial: String, adb: URL, into folder: URL) async -> Bool {
        let target = folder.appendingPathComponent(name)
        let partial = folder.appendingPathComponent(Self.partialName(for: name))
        try? FileManager.default.removeItem(at: partial)
        let result = await Self.run(adb, ["-s", serial, "pull", "\(Self.phoneFolder)/\(name)", partial.path])
        let size = (try? URL(fileURLWithPath: partial.path).resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        // `rename` replaces a stale file of the same name in one step, which a sidecar left by
        // an earlier failed pass may be.
        guard result.succeeded, size > 0, rename(partial.path, target.path) == 0 else {
            try? FileManager.default.removeItem(at: partial)
            logger.error("Pulling \(name, privacy: .public) failed; it stays on the phone")
            return false
        }
        return true
    }

    /// The hidden name a file is copied under until it has arrived whole.
    static func partialName(for name: String) -> String {
        partialPrefix + name
    }

    private static func stem(of name: String) -> String {
        URL(fileURLWithPath: name).deletingPathExtension().lastPathComponent
    }

    private static func fileExtension(of name: String) -> String {
        URL(fileURLWithPath: name).pathExtension.lowercased()
    }

    // MARK: - Running adb

    /// Runs adb away from the main actor and returns what it printed.
    nonisolated static func run(_ executable: URL, _ arguments: [String],
                                timeout: TimeInterval = adbCommandTimeout) async -> CommandResult {
        await withCheckedContinuation { continuation in
            adbQueue.async {
                continuation.resume(returning: runBlocking(executable, arguments, timeout: timeout))
            }
        }
    }

    /// Output and errors are read as one stream: adb writes "daemon started" and other
    /// asides to its error output, and the readers above skip lines they do not know.
    private nonisolated static func runBlocking(_ executable: URL, _ arguments: [String],
                                                timeout: TimeInterval) -> CommandResult {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        process.standardInput = FileHandle.nullDevice
        // The adb server otherwise browses Bonjour on every interface for phones with
        // wireless debugging, and macOS then asks whether Slovo may find devices on the
        // local network, though clips only ever come over USB. The setting applies to a
        // server this command starts; one already running keeps its own.
        var environment = ProcessInfo.processInfo.environment
        environment["ADB_MDNS"] = "0"
        process.environment = environment
        do {
            try process.run()
        } catch {
            return CommandResult(status: -1, output: "")
        }
        let watchdog = DispatchWorkItem {
            if process.isRunning {
                process.terminate()
            }
        }
        DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: watchdog)
        // Read before waiting: a command that filled the pipe would never reach its exit.
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        watchdog.cancel()
        return CommandResult(status: process.terminationStatus, output: String(decoding: data, as: UTF8.self))
    }
}
