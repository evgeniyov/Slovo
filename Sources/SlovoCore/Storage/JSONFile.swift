import AppKit
import Foundation

/// What reading a store file found.
public enum JSONFileLoad<Value> {
    case loaded(Value)
    /// No file yet: start empty.
    case missing
    /// The file could not be decoded and was renamed to `<name>.corrupt-<timestamp>.json`
    /// (nil when even the rename failed); start empty.
    case corrupt(movedTo: URL?)
    /// A stored array whose unreadable elements were dropped. The file itself was copied to
    /// `<name>.corrupt-<timestamp>.json` first (nil when the copy failed), so the next save
    /// loses nothing that is not kept there.
    case partial(Value, dropped: Int, copiedTo: URL?)
}

/// One JSON file of a store: atomic saves (temporary file, then replace) and
/// corruption handling shared by every store.
public struct JSONFile<Value: Codable> {
    public let url: URL

    public init(url: URL) {
        self.url = url
    }

    /// Reads the file. A stored array keeps every element that decodes and drops the rest,
    /// so one damaged or newer-format entry does not cost the whole store.
    public func load() -> JSONFileLoad<Value> {
        let fileManager = FileManager.default
        guard fileManager.fileExists(atPath: url.path) else { return .missing }
        do {
            let data = try Data(contentsOf: url)
            guard let arrayType = Value.self as? any ElementDroppingArray.Type else {
                return .loaded(try JSONDecoder().decode(Value.self, from: data))
            }
            let decoded = try arrayType.decodeDroppingUnreadableElements(from: data)
            guard let value = decoded.value as? Value else {
                throw DecodingError.typeMismatch(Value.self, .init(codingPath: [], debugDescription: "Unexpected array type"))
            }
            guard decoded.dropped > 0 else { return .loaded(value) }
            let logger = SlovoLog.logger("Storage")
            logger.error("\(url.lastPathComponent, privacy: .public): dropped \(decoded.dropped) unreadable entries")
            let backup = copyAside()
            if let backup {
                logger.error("Kept the original file as \(backup.lastPathComponent, privacy: .public)")
            }
            return .partial(value, dropped: decoded.dropped, copiedTo: backup)
        } catch {
            let logger = SlovoLog.logger("Storage")
            logger.error("\(url.lastPathComponent, privacy: .public) is unreadable: \(String(describing: error), privacy: .public)")
            let backup = moveAside()
            if let backup {
                logger.error("Moved the damaged file to \(backup.lastPathComponent, privacy: .public)")
            }
            return .corrupt(movedTo: backup)
        }
    }

    /// Encodes the value and replaces the file in one rename, so a crash never leaves
    /// a half-written store behind.
    public func save(_ value: Value) throws {
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(value)
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try data.write(to: url, options: [.atomic])
        } catch {
            throw SlovoError(CoreStrings.format("storage.error.saveFailed", table: .storage,
                                                url.lastPathComponent, error.localizedDescription))
        }
    }

    /// `<name>.corrupt-<yyyyMMdd-HHmmss>.json` next to the original; a counter keeps
    /// earlier backups from being overwritten.
    func corruptBackupURL(date: Date = Date()) -> URL {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let folder = url.deletingLastPathComponent()
        let name = url.deletingPathExtension().lastPathComponent
        let stamp = formatter.string(from: date)
        var candidate = folder.appendingPathComponent("\(name).corrupt-\(stamp).json")
        var counter = 2
        while FileManager.default.fileExists(atPath: candidate.path) {
            candidate = folder.appendingPathComponent("\(name).corrupt-\(stamp)-\(counter).json")
            counter += 1
        }
        return candidate
    }

    private func copyAside() -> URL? {
        let destination = corruptBackupURL()
        do {
            try FileManager.default.copyItem(at: url, to: destination)
            return destination
        } catch {
            SlovoLog.logger("Storage").error("Could not copy \(url.lastPathComponent, privacy: .public) aside: \(error.localizedDescription, privacy: .public)")
            return nil
        }
    }

    private func moveAside() -> URL? {
        let destination = corruptBackupURL()
        do {
            try FileManager.default.moveItem(at: url, to: destination)
            return destination
        } catch {
            SlovoLog.logger("Storage").error("Could not move \(url.lastPathComponent, privacy: .public) aside: \(error.localizedDescription, privacy: .public)")
            return nil
        }
    }
}

/// An array that can be decoded element by element, dropping elements that fail.
protocol ElementDroppingArray {
    /// The decoded array as `Self`, and how many elements were dropped. Throws when the
    /// data is not a JSON array at all.
    static func decodeDroppingUnreadableElements(from data: Data) throws -> (value: Any, dropped: Int)
}

extension Array: ElementDroppingArray where Element: Decodable {
    static func decodeDroppingUnreadableElements(from data: Data) throws -> (value: Any, dropped: Int) {
        let slots = try JSONDecoder().decode([DecodedSlot<Element>].self, from: data)
        let elements = slots.compactMap(\.value)
        return (elements, slots.count - elements.count)
    }
}

/// One element of a stored array: the element, or nil when it could not be decoded.
/// Decoding a slot never fails, so the array around it keeps its place.
private struct DecodedSlot<Element: Decodable>: Decodable {
    let value: Element?

    init(from decoder: Decoder) throws {
        value = try? decoder.singleValueContainer().decode(Element.self)
    }
}

/// Coalesces rapid changes into one save after a short delay.
@MainActor
public final class SaveDebouncer {
    public let delay: Duration
    private let action: @MainActor () -> Void
    private var task: Task<Void, Never>?

    public init(delay: Duration = .milliseconds(500), action: @escaping @MainActor () -> Void) {
        self.delay = delay
        self.action = action
    }

    /// Whether an action is waiting for its delay.
    public var isPending: Bool {
        task != nil
    }

    /// Schedules the action, replacing any pending one.
    public func schedule() {
        task?.cancel()
        let delay = self.delay
        task = Task { [weak self] in
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            self?.fire()
        }
    }

    /// Runs a pending action now.
    public func flush() {
        guard task != nil else { return }
        fire()
    }

    /// Drops a pending action without running it.
    public func cancel() {
        task?.cancel()
        task = nil
    }

    private func fire() {
        task?.cancel()
        task = nil
        action()
    }
}

/// Notifications the stores' files post.
public enum StoreFileEvents {
    /// Posted when a save fails, once per run of failures until a save succeeds again.
    /// `userInfo["message"]` is the reason, `userInfo["file"]` the file name.
    public static let saveFailed = Notification.Name("local.slovo.storeFile.saveFailed")
}

/// The file behind one store: loads it once, debounces saves of the latest value and
/// writes anything pending when the app quits.
@MainActor
final class StoreFile<Value: Codable> {
    let file: JSONFile<Value>
    /// The decoded file, nil when it was missing or damaged.
    let initialValue: Value?
    /// A localized note that the file was damaged and moved aside, for the interface.
    let loadWarning: String?

    /// True when the file could not be read and could not be set aside either: saving
    /// would replace the only copy, so the store keeps its changes in memory only.
    let savingDisabled: Bool

    private var pending: Value?
    var hasPendingSave: Bool { pending != nil }
    private var debouncer: SaveDebouncer?
    private var terminationObserver: NotificationToken?
    private let logger = SlovoLog.logger("Storage")

    init(url: URL, delay: Duration = .milliseconds(500), retryDelay: Duration = .seconds(30)) {
        self.retryDelay = retryDelay
        file = JSONFile(url: url)
        let name = url.lastPathComponent
        switch file.load() {
        case let .loaded(value):
            initialValue = value
            loadWarning = nil
            savingDisabled = false
        case .missing:
            initialValue = nil
            loadWarning = nil
            savingDisabled = false
        case let .corrupt(movedTo):
            initialValue = nil
            if movedTo == nil {
                loadWarning = CoreStrings.format("storage.error.notSetAside", table: .storage, name)
                savingDisabled = true
            } else {
                loadWarning = CoreStrings.format("storage.error.corruptMovedAside", table: .storage, name)
                savingDisabled = false
            }
        case let .partial(value, _, copiedTo):
            initialValue = value
            if let copiedTo {
                loadWarning = CoreStrings.format("storage.error.entriesSkipped", table: .storage, name,
                                                 copiedTo.lastPathComponent)
                savingDisabled = false
            } else {
                loadWarning = CoreStrings.format("storage.error.notSetAside", table: .storage, name)
                savingDisabled = true
            }
        }
        if savingDisabled {
            logger.error("Not saving \(name, privacy: .public): its unreadable original could not be set aside")
        }
        debouncer = SaveDebouncer(delay: delay) { [weak self] in
            self?.savePendingLogged()
        }
        terminationObserver = NotificationToken(name: NSApplication.willTerminateNotification) { [weak self] in
            self?.savePendingLogged()
        }
    }

    /// Remembers the latest value and saves it after the debounce.
    func scheduleSave(_ value: Value) {
        pending = value
        guard !savingDisabled else { return }
        debouncer?.schedule()
    }

    /// Writes the pending value now. Throws without writing when saving is disabled, so
    /// the unreadable original is never replaced.
    func flush(notifyOnFailure: Bool = true) throws {
        debouncer?.cancel()
        guard let value = pending else { return }
        guard !savingDisabled else {
            throw SlovoError(CoreStrings.format("storage.error.savingDisabled", table: .storage, file.url.lastPathComponent))
        }
        do {
            try file.save(value)
            pending = nil
            retryTask?.cancel()
            retryTask = nil
            saveError = nil
        } catch {
            logger.error("\(error.localizedDescription, privacy: .public)")
            let wasFailing = saveError != nil
            saveError = error.localizedDescription
            scheduleRetry()
            if notifyOnFailure && !wasFailing {
                NotificationCenter.default.post(name: StoreFileEvents.saveFailed, object: nil, userInfo: [
                    "message": error.localizedDescription, "file": file.url.lastPathComponent,
                ])
            }
            throw error
        }
    }

    private let retryDelay: Duration

    /// The reason of the last failed save, nil while everything pending was written.
    private(set) var saveError: String?
    private var retryTask: Task<Void, Never>?

    private func savePendingLogged() {
        try? flush()
    }

    /// The change stays pending after a failed save; try again later, so a disk that
    /// was full or a folder that was locked does not lose it silently.
    private func scheduleRetry() {
        retryTask?.cancel()
        retryTask = Task { [weak self, retryDelay] in
            try? await Task.sleep(for: retryDelay)
            guard !Task.isCancelled else { return }
            self?.savePendingLogged()
        }
    }
}

/// A block observer on the default notification center that removes itself when
/// released, so `@MainActor` owners need no `deinit`.
final class NotificationToken: @unchecked Sendable {
    private let token: NSObjectProtocol

    init(name: Notification.Name, action: @escaping @MainActor () -> Void) {
        token = NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated {
                action()
            }
        }
    }

    deinit {
        NotificationCenter.default.removeObserver(token)
    }
}
