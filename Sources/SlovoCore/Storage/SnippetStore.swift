import Combine
import Foundation

/// `snippets.json`.
@MainActor
public final class SnippetStore: ObservableObject, SnippetStoring {
    /// In the order they were added.
    @Published public private(set) var snippets: [Snippet] = []

    public let directories: AppDirectories
    /// Set when `snippets.json` was damaged and moved aside at launch; localized.
    public let loadWarning: String?

    private let storage: StoreFile<[Snippet]>

    public init(directories: AppDirectories) {
        self.directories = directories
        storage = StoreFile(url: directories.snippetsFile)
        loadWarning = storage.loadWarning
        snippets = storage.initialValue ?? []
    }

    /// Appends a snippet; a snippet with the same id is replaced instead.
    public func add(_ snippet: Snippet) {
        if let index = snippets.firstIndex(where: { $0.id == snippet.id }) {
            snippets[index] = snippet
        } else {
            snippets.append(snippet)
        }
        save()
    }

    /// Replaces the snippet with the same id and stamps `updatedAt` when its text changed.
    public func update(_ snippet: Snippet) {
        guard let index = snippets.firstIndex(where: { $0.id == snippet.id }) else { return }
        var updated = snippet
        let old = snippets[index]
        if old.trigger != snippet.trigger || old.expansion != snippet.expansion, snippet.updatedAt == old.updatedAt {
            updated.updatedAt = Date()
        }
        snippets[index] = updated
        save()
    }

    public func delete(id: UUID) {
        guard snippets.contains(where: { $0.id == id }) else { return }
        snippets.removeAll { $0.id == id }
        save()
    }

    public func flush() throws {
        try storage.flush()
    }

    private func save() {
        storage.scheduleSave(snippets)
    }
}
