import Foundation

/// English words in mixed dictation. GigaAM writes the text; when that text has a word
/// GigaAM was unsure of (`MixedLanguageMerger.needsHelper`), a multilingual helper —
/// Whisper turbo or Parakeet, as `SettingsKey.englishHelper` chooses — transcribes the same
/// recording and `MixedLanguageMerger` puts the helper's English in. Pure-Russian
/// dictations never run the helper, so they keep GigaAM's latency.
///
/// A helper can only improve a dictation, never fail it or hold it up: a helper that is
/// missing, fails, or has not answered within `timeLimit(forAudio:)` leaves GigaAM's text
/// as it was, and cancelling the dictation stops waiting for it at once. The helper engine
/// comes from `EngineProviding`, so it loads on first need and is released with the other
/// engines when idle; a load that outlasts the limit keeps going and serves the next dictation.
@MainActor
public final class MixedLanguageHelper: MixedLanguageHelping {
    /// What happened to one dictation's text.
    public enum Outcome: Equatable, Sendable {
        /// Not mixed dictation by GigaAM, the setting is off, or no helper model is ready.
        case notApplicable
        /// GigaAM's text had no suspect word, so the helper did not run.
        case notNeeded
        /// The helper answered; `replacements` runs of GigaAM's words took its English.
        case merged(helper: EngineID, replacements: Int)
        case failed(helper: EngineID)
        case timedOut(helper: EngineID)
        case cancelled(helper: EngineID)
    }

    public struct Result: Equatable, Sendable {
        public let text: String
        public let outcome: Outcome
    }

    /// The shortest wait: a short dictation's helper may still have to load, and Whisper
    /// decodes a whole 30 s window however little was said (about 1.8 s on an M4).
    public static let minimumTimeLimit: TimeInterval = 3
    /// Past the minimum, a helper may take this share of the recording's length. Whisper
    /// needs about a tenth of it (2.5 s for 30 s on an M4) and Parakeet about a hundredth,
    /// so only a stuck or starved helper reaches it.
    public static let timeLimitShare = 0.5

    /// How long a dictation waits for its helper: max(3 s, half the recording).
    public static func timeLimit(forAudio duration: TimeInterval) -> TimeInterval {
        max(minimumTimeLimit, duration * timeLimitShare)
    }

    private let engines: EngineProviding
    private let settings: AppSettings
    private let status: @MainActor (EngineID) -> ModelStatus
    private let merger: MixedLanguageMerger
    private let log = SlovoLog.logger("MixedLanguage")
    /// Replaces `timeLimit(forAudio:)`; tests shorten it.
    var timeLimitOverride: TimeInterval?

    /// - Parameter status: model statuses, which decide what "auto" picks and whether a
    ///   chosen helper can run.
    public init(engines: EngineProviding, settings: AppSettings,
                status: @escaping @MainActor (EngineID) -> ModelStatus,
                merger: MixedLanguageMerger = MixedLanguageMerger()) {
        self.engines = engines
        self.settings = settings
        self.status = status
        self.merger = merger
    }

    public convenience init(engines: EngineProviding, models: ModelManaging, settings: AppSettings) {
        self.init(engines: engines, settings: settings, status: { models.status(of: $0) })
    }

    /// The helper a dictation by `engine` in `languageMode` would get now; nil when none.
    public func helper(for engine: EngineID, languageMode: LanguageMode) -> EngineID? {
        guard let helper = settings.englishHelper(for: engine, languageMode: languageMode, status: status),
              helper != engine else { return nil }
        return helper
    }

    /// Starts loading the helper a dictation that begins now would use, so it is ready when
    /// the dictation ends: after launch or an idle unload, Parakeet can take many seconds to
    /// compile for the Neural Engine and Whisper to build its Metal shaders — longer than the
    /// time limit, which would leave the first mixed dictation without its English. Only
    /// loads; nothing is transcribed unless GigaAM's text asks for it.
    public func warmUp(for engine: EngineID, languageMode: LanguageMode) {
        guard let helperID = helper(for: engine, languageMode: languageMode),
              engines.loadState(of: helperID) == .unloaded else { return }
        let engines = self.engines
        log.info("Loading the English helper \(helperID.rawValue, privacy: .public) while the dictation records")
        Task {
            _ = try? await engines.engine(for: helperID)
        }
    }

    public func improve(_ text: String, audio: URL, duration: TimeInterval, engine: EngineID,
                        languageMode: LanguageMode, vocabulary: [String]) async -> String {
        await run(text, audio: audio, duration: duration, engine: engine, languageMode: languageMode,
                  vocabulary: vocabulary).text
    }

    /// Like `improve`, also saying what happened; for tests and the bench.
    public func run(_ text: String, audio: URL, duration: TimeInterval, engine: EngineID,
                    languageMode: LanguageMode, vocabulary: [String]) async -> Result {
        guard let helperID = helper(for: engine, languageMode: languageMode) else {
            return Result(text: text, outcome: .notApplicable)
        }
        guard merger.needsHelper(text) else {
            return Result(text: text, outcome: .notNeeded)
        }
        let limit = timeLimitOverride ?? Self.timeLimit(forAudio: duration)
        let clock = ContinuousClock()
        let started = clock.now
        let race = FirstAnswer()
        let engines = self.engines
        // Unstructured on purpose: a helper that ignores cancellation must not keep the
        // dictation waiting, so nothing here waits for it to end, only for the first answer.
        let work = Task {
            do {
                let helper: any TranscriptionEngine
                if let languageEngines = engines as? LanguageEngineProviding {
                    // A history rerun may be mixed even while live dictation is English.
                    helper = try await languageEngines.engine(for: helperID, languageMode: .mixed)
                } else {
                    helper = try await engines.engine(for: helperID)
                }
                try Task.checkCancellation()
                let heard: String
                if let hinted = helper as? VocabularyTranscribing {
                    heard = try await hinted.transcribe(fileURL: audio, vocabulary: vocabulary)
                } else {
                    heard = try await helper.transcribe(fileURL: audio)
                }
                race.finish(.heard(heard))
            } catch {
                race.finish(.failed(error))
            }
        }
        let timer = Task {
            try? await Task.sleep(for: .seconds(limit))
            if !Task.isCancelled {
                race.finish(.timedOut)
            }
        }
        let answer = await withTaskCancellationHandler {
            await race.wait()
        } onCancel: {
            race.finish(.cancelled)
        }
        work.cancel()
        timer.cancel()
        let seconds = String(format: "%.2f", started.duration(to: clock.now).seconds)

        switch answer {
        case let .heard(heard):
            engines.markUsed(helperID)
            let merger = self.merger
            let merge = Task.detached(priority: .userInitiated) {
                merger.merge(russian: text, helper: heard)
            }
            let merged = await withTaskCancellationHandler {
                await merge.value
            } onCancel: {
                merge.cancel()
            }
            guard !Task.isCancelled else { return Result(text: text, outcome: .cancelled(helper: helperID)) }
            log.info("\(helperID.rawValue, privacy: .public) answered in \(seconds, privacy: .public) s; replaced \(merged.replacements) of GigaAM's word runs")
            // Without a replacement GigaAM's text stays exactly as written.
            return Result(text: merged.replacements > 0 ? merged.text : text,
                          outcome: .merged(helper: helperID, replacements: merged.replacements))
        case let .failed(error):
            log.error("\(helperID.rawValue, privacy: .public) failed after \(seconds, privacy: .public) s; keeping GigaAM's text: \(error.localizedDescription, privacy: .public)")
            return Result(text: text, outcome: .failed(helper: helperID))
        case .timedOut:
            log.notice("\(helperID.rawValue, privacy: .public) did not answer within \(String(format: "%.1f", limit), privacy: .public) s; keeping GigaAM's text")
            return Result(text: text, outcome: .timedOut(helper: helperID))
        case .cancelled:
            log.info("The dictation was cancelled; stopped \(helperID.rawValue, privacy: .public)")
            return Result(text: text, outcome: .cancelled(helper: helperID))
        }
    }
}

/// The first of the helper's text, its failure, the time limit and cancellation; later
/// ones are ignored.
private final class FirstAnswer: @unchecked Sendable {
    enum Answer {
        case heard(String)
        case failed(Error)
        case timedOut
        case cancelled
    }

    private let lock = NSLock()
    private var answer: Answer?
    private var waiter: CheckedContinuation<Answer, Never>?

    func finish(_ value: Answer) {
        let waiting = lock.withLock { () -> CheckedContinuation<Answer, Never>? in
            guard answer == nil else { return nil }
            answer = value
            let current = waiter
            waiter = nil
            return current
        }
        waiting?.resume(returning: value)
    }

    func wait() async -> Answer {
        await withCheckedContinuation { (continuation: CheckedContinuation<Answer, Never>) in
            let ready = lock.withLock { () -> Answer? in
                if let answer {
                    return answer
                }
                waiter = continuation
                return nil
            }
            if let ready {
                continuation.resume(returning: ready)
            }
        }
    }
}

private extension Duration {
    var seconds: Double {
        let parts = components
        return Double(parts.seconds) + Double(parts.attoseconds) / 1e18
    }
}
