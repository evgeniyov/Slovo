import Combine
import Foundation

/// `notes.json`: Scratchpad notes, saved automatically as they change.
@MainActor
public final class NoteStore: ObservableObject, NoteStoring {
    /// Most recently updated first.
    @Published public private(set) var notes: [Note] = []

    public let directories: AppDirectories
    /// Set when `notes.json` was damaged and moved aside at launch; localized.
    public let loadWarning: String?

    private let storage: StoreFile<[Note]>

    public init(directories: AppDirectories) {
        self.directories = directories
        storage = StoreFile(url: directories.notesFile)
        loadWarning = storage.loadWarning
        notes = Self.recentFirst(storage.initialValue ?? [])
    }

    @discardableResult
    public func create(text: String) -> Note {
        let note = Note(text: text)
        notes.insert(note, at: 0)
        save()
        return note
    }

    /// Replaces the note with the same id; a changed text stamps `updatedAt` and moves
    /// the note to the top.
    public func update(_ note: Note) {
        guard let index = notes.firstIndex(where: { $0.id == note.id }) else { return }
        var updated = note
        let old = notes[index]
        if old.text != note.text, note.updatedAt == old.updatedAt {
            updated.updatedAt = Date()
        }
        guard updated != old else { return }
        notes[index] = updated
        notes = Self.recentFirst(notes)
        save()
    }

    /// Appends dictated text, separated by a space unless the note is empty or already
    /// ends with whitespace or a new line.
    public func append(_ text: String, toNote id: UUID) {
        let addition = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !addition.isEmpty, let index = notes.firstIndex(where: { $0.id == id }) else { return }
        var note = notes[index]
        note.text = Self.joined(note.text, addition)
        note.updatedAt = Date()
        notes[index] = note
        notes = Self.recentFirst(notes)
        save()
    }

    public func delete(id: UUID) {
        guard notes.contains(where: { $0.id == id }) else { return }
        notes.removeAll { $0.id == id }
        save()
    }

    public func flush() throws {
        try storage.flush()
    }

    static func joined(_ existing: String, _ addition: String) -> String {
        guard let last = existing.last else { return addition }
        return last.isWhitespace || last.isNewline ? existing + addition : existing + " " + addition
    }

    private static func recentFirst(_ notes: [Note]) -> [Note] {
        notes.sorted { $0.updatedAt > $1.updatedAt }
    }

    private func save() {
        storage.scheduleSave(notes)
    }
}
