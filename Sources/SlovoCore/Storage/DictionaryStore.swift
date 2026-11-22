import Combine
import Foundation

/// `dictionary.json`: terms and replacements.
@MainActor
public final class DictionaryStore: ObservableObject, DictionaryStoring {
    /// In the order they were added.
    @Published public private(set) var items: [DictionaryItem] = []

    public let directories: AppDirectories
    /// Set when `dictionary.json` was damaged and moved aside at launch; localized.
    public let loadWarning: String?

    private let storage: StoreFile<[DictionaryItem]>
    private let logger = SlovoLog.logger("Dictionary")

    public init(directories: AppDirectories) {
        self.directories = directories
        storage = StoreFile(url: directories.dictionaryFile)
        loadWarning = storage.loadWarning
        items = storage.initialValue ?? []
    }

    public var terms: [DictionaryItem] {
        items.filter { $0.kind == .term }
    }

    public var replacements: [DictionaryItem] {
        items.filter { $0.kind == .replacement }
    }

    /// Appends an item; an item with the same id is replaced instead.
    public func add(_ item: DictionaryItem) {
        if let index = items.firstIndex(where: { $0.id == item.id }) {
            items[index] = item
        } else {
            items.append(item)
        }
        save()
    }

    public func update(_ item: DictionaryItem) {
        guard let index = items.firstIndex(where: { $0.id == item.id }) else { return }
        items[index] = item
        save()
    }

    public func delete(id: UUID) {
        guard items.contains(where: { $0.id == id }) else { return }
        items.removeAll { $0.id == id }
        save()
    }

    public func flush() throws {
        try storage.flush()
    }

    /// Adds the bundled vocabulary and spelling replacements once. An existing
    /// dictionary only receives additions since its saved seed version.
    ///
    /// Existing terms and replacement triggers take precedence. The flags are set
    /// only after the file was written, so a failed save seeds again next launch.
    public func seedIfNeeded(settings: AppSettings, terms: [String] = DictionarySeed.bundledTerms()) {
        let latest = DictionarySeed.latestVersion
        let seeded = settings.dictionarySeeded
        let previousVersion = seeded ? settings.dictionarySeedVersion : 0
        guard previousVersion < latest, seeded || !terms.isEmpty else { return }
        let additions = DictionarySeed.additions.filter { $0.version > previousVersion }
        let newTerms = seeded ? additions.flatMap(\.terms) : terms
        var known = Set(self.terms.map { Self.matchKey($0.text) })
        let now = Date()
        var added = false
        for term in newTerms {
            let trimmed = term.trimmingCharacters(in: .whitespacesAndNewlines)
            let key = Self.matchKey(trimmed)
            guard !key.isEmpty, !known.contains(key) else { continue }
            known.insert(key)
            items.append(.term(trimmed, createdAt: now))
            added = true
        }
        var triggers = Set(replacements.map { ReplacementRule.pattern(for: $0.text) })
        for replacement in additions.flatMap(\.replacements) {
            guard triggers.insert(ReplacementRule.pattern(for: replacement.from)).inserted else { continue }
            items.append(.replacement(from: replacement.from, to: replacement.to, createdAt: now))
            added = true
        }
        if added || !seeded {
            save()
        }
        do {
            try flush()
            settings.dictionarySeeded = true
            settings.dictionarySeedVersion = latest
        } catch {
            logger.error("Could not save the seeded dictionary: \(error.localizedDescription, privacy: .public)")
        }
    }

    private static func matchKey(_ text: String) -> String {
        text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }

    private func save() {
        storage.scheduleSave(items)
    }
}

/// The vocabulary bundled with SlovoCore and its versioned upgrades.
public enum DictionarySeed {
    /// New bundled terms and exact replacements, by seed version.
    /// Version 1 is the original vocabulary.
    public static let additions: [(version: Int, terms: [String], replacements: [(from: String, to: String)])] = [
        (2, ["GitHub"], []),
        (3, ["iOS", "AppKit", "ChatGPT", "OpenAI", "DeepSeek"], []),
        (4, [], [
            ("клодо", "Claude"),
            ("Cloud cod", "Claude Code"),
            ("Cloud кот", "Claude Code"),
            ("клот-код", "Claude Code"),
            ("клот код", "Claude Code"),
            ("Makos", "macOS"),
            ("DeepSig", "DeepSeek"),
            ("DepSik", "DeepSeek"),
            ("дипсик", "DeepSeek"),
            ("дипсика", "DeepSeek"),
            ("дипсику", "DeepSeek"),
            ("дипсиком", "DeepSeek"),
            ("дипсике", "DeepSeek"),
            ("чат GPT", "ChatGPT"),
            ("чата GPT-на", "ChatGPT"),
            ("чата GPT", "ChatGPT"),
            ("чату GPT", "ChatGPT"),
            ("чатом GPT", "ChatGPT"),
            ("чате GPT", "ChatGPT"),
            ("Chat GPT", "ChatGPT"),
            ("Vescode", "VS Code"),
            ("Vic-Code", "VS Code"),
            ("Macbook", "MacBook"),
            ("гитхабом", "GitHub"),
            ("гидхаб", "GitHub"),
            ("гитхап", "GitHub"),
            ("комит", "коммит"),
            ("комита", "коммита"),
            ("комиту", "коммиту"),
            ("комитом", "коммитом"),
            ("комите", "коммите"),
            ("комиты", "коммиты"),
            ("комитов", "коммитов"),
            ("комитам", "коммитам"),
            ("комитами", "коммитами"),
            ("комитах", "коммитах"),
            ("комить", "коммитить"),
            ("закомить", "закоммитить"),
        ]),
        (5, ["VPN", "IP", "LAN", "sudo", "Mac", "MacBook"], [
            ("впн", "VPN"),
            ("ви пи эн", "VPN"),
            ("ви-пи-эн", "VPN"),
            ("ай пи", "IP"),
            ("ай-пи", "IP"),
            ("айпишник", "IP-шник"),
            ("айпишника", "IP-шника"),
            ("айпишнике", "IP-шнике"),
            ("лан-кабель", "LAN-кабель"),
            ("си ай", "CI"),
            ("си-ай", "CI"),
            ("сиай", "CI"),
            ("судо", "sudo"),
            ("Xcod", "Xcode"),
            ("икс код", "Xcode"),
            ("макбук", "MacBook"),
            ("на маке", "на Mac"),
            ("гетхаба", "GitHub"),
            ("гитхаба", "GitHub"),
            ("меш-сеть", "mesh-сеть"),
            ("меш-сетью", "mesh-сетью"),
        ]),
    ]

    public static var latestVersion: Int {
        additions.map(\.version).max() ?? 1
    }

    /// Non-empty lines that are not `#` comments.
    public static func bundledTerms() -> [String] {
        guard let url = CoreStrings.resourceBundle.url(forResource: "vocabulary", withExtension: "txt"),
              let text = try? String(contentsOf: url, encoding: .utf8) else {
            SlovoLog.logger("Dictionary").error("vocabulary.txt is missing from the resource bundle")
            return []
        }
        return terms(fromVocabulary: text)
    }

    /// One term per line; blank lines and lines starting with `#` are skipped, and a
    /// term repeated with different case keeps its first spelling.
    public static func terms(fromVocabulary text: String) -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        for line in text.components(separatedBy: .newlines) {
            let term = line.trimmingCharacters(in: .whitespaces)
            guard !term.isEmpty, !term.hasPrefix("#"), seen.insert(term.lowercased()).inserted else { continue }
            result.append(term)
        }
        return result
    }
}
