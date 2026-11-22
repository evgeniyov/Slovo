import AVFoundation
import Foundation
import Speech

/// Apple's on-device dictation model through SpeechAnalyzer.
///
/// SpeechTranscriber, the newer model, has no Russian, so this uses
/// DictationTranscriber. Its language assets are managed by macOS; the engine
/// never downloads them itself.
@available(macOS 26.0, *)
public final class AppleDictationEngine: TranscriptionEngine {
    public let id: String
    private var locale: Locale
    private let contextualStrings: [String]

    /// - Parameter contextualStrings: terms the recognizer should prefer, such as
    ///   English product names spoken inside Russian speech.
    public init(id: String = "apple-dictation",
                locale: Locale = Locale(identifier: "ru-RU"),
                contextualStrings: [String] = []) {
        self.id = id
        self.locale = locale
        self.contextualStrings = contextualStrings
    }

    public func prepare() async throws {
        guard let supported = await DictationTranscriber.supportedLocale(equivalentTo: locale) else {
            throw TranscriptionError(CoreStrings.format("engines.apple.unsupportedLocale", table: .engines,
                                                        locale.identifier))
        }
        let wanted = supported.identifier(.bcp47)
        let installed = await DictationTranscriber.installedLocales.map { $0.identifier(.bcp47) }
        guard installed.contains(wanted) else {
            throw TranscriptionError(CoreStrings.format("engines.apple.assetsMissing", table: .engines, wanted))
        }
        locale = supported
    }

    /// Whether macOS has the dictation language files for this locale installed. Never
    /// downloads them.
    public static func hasInstalledAssets(for locale: Locale) async -> Bool {
        guard let supported = await DictationTranscriber.supportedLocale(equivalentTo: locale) else { return false }
        let wanted = supported.identifier(.bcp47)
        let installed = await DictationTranscriber.installedLocales
        return installed.contains { $0.identifier(.bcp47) == wanted }
    }

    public func transcribe(fileURL: URL) async throws -> String {
        // Opened before the analyzer exists, so an unreadable file leaves nothing running.
        let file = try AVAudioFile(forReading: fileURL)
        let transcriber = DictationTranscriber(
            locale: locale,
            contentHints: [],
            transcriptionOptions: [.punctuation],
            reportingOptions: [],
            attributeOptions: []
        )
        let analyzer = SpeechAnalyzer(modules: [transcriber])
        if !contextualStrings.isEmpty {
            let context = AnalysisContext()
            context.contextualStrings[.general] = contextualStrings
            try await analyzer.setContext(context)
        }

        let collector = Task { () throws -> [String] in
            var segments: [String] = []
            for try await result in transcriber.results where result.isFinal {
                segments.append(String(result.text.characters))
            }
            return segments
        }

        do {
            if let lastSample = try await analyzer.analyzeSequence(from: file) {
                try await analyzer.finalizeAndFinish(through: lastSample)
            } else {
                await analyzer.cancelAndFinishNow()
            }
        } catch {
            // The results stream ends only when the analyzer finishes; without this the
            // collector would wait on it forever.
            await analyzer.cancelAndFinishNow()
            collector.cancel()
            throw error
        }

        return try await collector.value
            .joined(separator: " ")
            .split(whereSeparator: \.isWhitespace)
            .joined(separator: " ")
    }
}
