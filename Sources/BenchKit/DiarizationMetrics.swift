import Foundation

// MARK: - Inputs

/// A reference turn of a synthetic case, as `turns.json` stores it.
public struct ReferenceTurn: Codable, Equatable, Sendable {
    public var speaker: String
    /// "microphone" or "system".
    public var track: String
    public var start: Double
    public var end: Double
    public var text: String

    public init(speaker: String, track: String, start: Double, end: Double, text: String) {
        self.speaker = speaker
        self.track = track
        self.start = start
        self.end = end
        self.text = text
    }

    public var duration: Double {
        max(0, end - start)
    }

    public var segment: SpeakerSegment {
        SpeakerSegment(speaker: speaker, start: start, end: end)
    }
}

/// A transcript piece with the hypothesis speaker it was given.
public struct AttributedText: Codable, Equatable, Sendable {
    public var speaker: String
    public var start: Double
    public var end: Double
    public var text: String

    public init(speaker: String, start: Double, end: Double, text: String) {
        self.speaker = speaker
        self.start = start
        self.end = end
        self.text = text
    }
}

// MARK: - Results

/// Reference turn lengths the evaluation reports separately, because errors gather
/// in short interjections.
public enum TurnLength: String, Codable, CaseIterable, Sendable {
    case underOneSecond = "< 1 s"
    case oneToThreeSeconds = "1–3 s"
    case threeToTenSeconds = "3–10 s"
    case overTenSeconds = "> 10 s"

    public init(seconds: Double) {
        switch seconds {
        case ..<1: self = .underOneSecond
        case ..<3: self = .oneToThreeSeconds
        case ...10: self = .threeToTenSeconds
        default: self = .overTenSeconds
        }
    }
}

/// How many of `total` items were counted as `hits`.
public struct Tally: Codable, Equatable, Sendable {
    public var hits: Int
    public var total: Int

    public init(hits: Int = 0, total: Int = 0) {
        self.hits = hits
        self.total = total
    }

    public var rate: Double? {
        total == 0 ? nil : Double(hits) / Double(total)
    }

    public static func + (lhs: Tally, rhs: Tally) -> Tally {
        Tally(hits: lhs.hits + rhs.hits, total: lhs.total + rhs.total)
    }
}

/// Correctly attributed reference turns per length.
public struct AttributionResult: Codable, Equatable, Sendable {
    /// Keyed by `TurnLength.rawValue`, which keeps the JSON readable.
    public var byLength: [String: Tally]

    public init(byLength: [String: Tally] = [:]) {
        self.byLength = byLength
    }

    public subscript(length: TurnLength) -> Tally {
        byLength[length.rawValue] ?? Tally()
    }

    /// Turns of 3 s or longer, the figure the pass bar uses.
    public var threeSecondsOrLonger: Tally {
        self[.threeToTenSeconds] + self[.overTenSeconds]
    }

    public var all: Tally {
        TurnLength.allCases.reduce(Tally()) { $0 + self[$1] }
    }

    public static func + (lhs: AttributionResult, rhs: AttributionResult) -> AttributionResult {
        var merged = lhs.byLength
        for (key, tally) in rhs.byLength {
            merged[key, default: Tally()] = merged[key, default: Tally()] + tally
        }
        return AttributionResult(byLength: merged)
    }
}

public struct ShortTurnResult: Codable, Equatable, Sendable {
    public var total: Int
    /// Short turns their own speaker covers for the most part.
    public var recalled: Int
    /// Short turns mostly covered by the first hypothesis cluster while it stands for
    /// another speaker: the bias of frames without an embedding vote.
    public var absorbedByFirstCluster: Int

    public init(total: Int = 0, recalled: Int = 0, absorbedByFirstCluster: Int = 0) {
        self.total = total
        self.recalled = recalled
        self.absorbedByFirstCluster = absorbedByFirstCluster
    }

    public var recall: Double? {
        total == 0 ? nil : Double(recalled) / Double(total)
    }

    public var absorbedShare: Double? {
        total == 0 ? nil : Double(absorbedByFirstCluster) / Double(total)
    }

    public static func + (lhs: ShortTurnResult, rhs: ShortTurnResult) -> ShortTurnResult {
        ShortTurnResult(total: lhs.total + rhs.total, recalled: lhs.recalled + rhs.recalled,
                        absorbedByFirstCluster: lhs.absorbedByFirstCluster + rhs.absorbedByFirstCluster)
    }
}

public struct SpeakerCountResult: Codable, Equatable, Sendable {
    public var reference: Int
    public var detected: Int

    public init(reference: Int, detected: Int) {
        self.reference = reference
        self.detected = detected
    }

    /// Positive when the hypothesis has too many speakers.
    public var error: Int {
        detected - reference
    }

    public var isExact: Bool {
        error == 0
    }
}

/// Diarization error in seconds and as a rate of the scored reference speech.
public struct DERBreakdown: Codable, Equatable, Sendable {
    public var miss: Double
    public var falseAlarm: Double
    public var confusion: Double
    public var referenceSeconds: Double

    public init(miss: Double, falseAlarm: Double, confusion: Double, referenceSeconds: Double) {
        self.miss = miss
        self.falseAlarm = falseAlarm
        self.confusion = confusion
        self.referenceSeconds = referenceSeconds
    }

    public var der: Double {
        referenceSeconds > 0 ? (miss + falseAlarm + confusion) / referenceSeconds : 0
    }

    /// Errors of several tracks or cases over their combined reference speech.
    public static func pooled(_ parts: [DERBreakdown]) -> DERBreakdown {
        DERBreakdown(miss: parts.reduce(0) { $0 + $1.miss }, falseAlarm: parts.reduce(0) { $0 + $1.falseAlarm },
                     confusion: parts.reduce(0) { $0 + $1.confusion },
                     referenceSeconds: parts.reduce(0) { $0 + $1.referenceSeconds })
    }
}

/// Word errors that survive JSON, unlike `ErrorCounts`.
public struct WordErrors: Codable, Equatable, Sendable {
    public var errors: Int
    public var referenceWords: Int

    public init(_ counts: ErrorCounts) {
        errors = counts.errors
        referenceWords = counts.referenceLength
    }

    public var rate: Double {
        referenceWords == 0 ? 0 : Double(errors) / Double(referenceWords)
    }
}

// MARK: - Metrics

public enum DiarizationMetrics {
    /// Turns shorter than this are the backchannels («да», «угу») the evaluation watches.
    public static let shortTurnSeconds = 1.0
    /// The share of microphone speech one voice needs for the automatic «Я» label.
    public static let meShare = 0.8
    /// Clip boundaries this close to a word's edge do not count as cutting it.
    public static let cutMarginSeconds = 0.05

    /// Seconds each hypothesis speaker covers inside `start..<end`.
    public static func coverage(start: Double, end: Double, hypothesis: [SpeakerSegment]) -> [String: Double] {
        var seconds: [String: Double] = [:]
        for segment in hypothesis {
            let overlap = min(end, segment.end) - max(start, segment.start)
            if overlap > 0 {
                seconds[segment.speaker, default: 0] += overlap
            }
        }
        return seconds
    }

    /// A reference turn is correct when the hypothesis speakers mapped to its speaker
    /// cover more than half of it.
    public static func attribution(reference: [ReferenceTurn], hypothesis: [SpeakerSegment],
                                   mapping: [String: String]) -> AttributionResult {
        var result = AttributionResult()
        for turn in reference where turn.duration > 0 {
            let key = TurnLength(seconds: turn.duration).rawValue
            var tally = result.byLength[key] ?? Tally()
            tally.total += 1
            if ownCoverage(of: turn, hypothesis: hypothesis, mapping: mapping) > turn.duration / 2 {
                tally.hits += 1
            }
            result.byLength[key] = tally
        }
        return result
    }

    /// Recall of turns under one second, and how many of them the first cluster took.
    public static func shortTurns(reference: [ReferenceTurn], hypothesis: [SpeakerSegment],
                                  mapping: [String: String], firstCluster: String) -> ShortTurnResult {
        var result = ShortTurnResult()
        for turn in reference where turn.duration > 0 && turn.duration < shortTurnSeconds {
            result.total += 1
            if ownCoverage(of: turn, hypothesis: hypothesis, mapping: mapping) > turn.duration / 2 {
                result.recalled += 1
            } else if let first = coverage(start: turn.start, end: turn.end, hypothesis: hypothesis)[firstCluster],
                      first > turn.duration / 2, mapping[firstCluster] != turn.speaker {
                result.absorbedByFirstCluster += 1
            }
        }
        return result
    }

    /// Distinct speakers with any speech on each side.
    public static func speakerCount(reference: [SpeakerSegment], hypothesis: [SpeakerSegment]) -> SpeakerCountResult {
        SpeakerCountResult(reference: Set(reference.filter { $0.duration > 0 }.map(\.speaker)).count,
                           detected: Set(hypothesis.filter { $0.duration > 0 }.map(\.speaker)).count)
    }

    /// The one-to-one hypothesis → reference mapping with the most overlapping time.
    /// Speakers that overlap nobody stay unmapped.
    public static func overlapMapping(reference: [SpeakerSegment], hypothesis: [SpeakerSegment]) -> [String: String] {
        let referenceLabels = orderedLabels(reference)
        let hypothesisLabels = orderedLabels(hypothesis)
        guard !referenceLabels.isEmpty, !hypothesisLabels.isEmpty else { return [:] }
        var overlap = [[Double]](repeating: [Double](repeating: 0, count: referenceLabels.count),
                                 count: hypothesisLabels.count)
        let referenceIndex = Dictionary(uniqueKeysWithValues: referenceLabels.enumerated().map { ($1, $0) })
        let hypothesisIndex = Dictionary(uniqueKeysWithValues: hypothesisLabels.enumerated().map { ($1, $0) })
        for hyp in hypothesis {
            guard let row = hypothesisIndex[hyp.speaker] else { continue }
            for ref in reference {
                guard let column = referenceIndex[ref.speaker] else { continue }
                let seconds = min(hyp.end, ref.end) - max(hyp.start, ref.start)
                if seconds > 0 {
                    overlap[row][column] += seconds
                }
            }
        }
        var mapping: [String: String] = [:]
        for (row, column) in Assignment.maximizing(overlap) where overlap[row][column] > 0 {
            mapping[hypothesisLabels[row]] = referenceLabels[column]
        }
        return mapping
    }

    /// Each reference speaker's words against the words of the hypothesis pieces mapped
    /// to them, pooled over speakers. Pieces of an unmapped speaker are all insertions.
    public static func speakerAttributedWER(reference: [ReferenceTurn], hypothesis: [AttributedText],
                                            mapping: [String: String]) -> ErrorCounts {
        var referenceText: [String: [String]] = [:]
        for turn in reference.sorted(by: { $0.start < $1.start }) {
            referenceText[turn.speaker, default: []].append(turn.text)
        }
        var hypothesisText: [String: [String]] = [:]
        for piece in hypothesis.sorted(by: { $0.start < $1.start }) {
            // Speaker names cannot contain a newline, so this key never meets a real one.
            let speaker = mapping[piece.speaker] ?? "\n" + piece.speaker
            hypothesisText[speaker, default: []].append(piece.text)
        }
        var total = ErrorCounts(errors: 0, referenceLength: 0)
        for speaker in Set(referenceText.keys).union(hypothesisText.keys) {
            total = total + ErrorRate.words(reference: (referenceText[speaker] ?? []).joined(separator: " "),
                                            hypothesis: (hypothesisText[speaker] ?? []).joined(separator: " "))
        }
        return total
    }

    /// Word errors of the whole transcript in time order, ignoring speakers.
    public static func plainWER(reference: [ReferenceTurn], hypothesis: [AttributedText]) -> ErrorCounts {
        ErrorRate.words(reference: reference.sorted { $0.start < $1.start }.map(\.text).joined(separator: " "),
                        hypothesis: hypothesis.sorted { $0.start < $1.start }.map(\.text).joined(separator: " "))
    }

    /// Reference words a clip boundary falls inside. Word times are not known, so each
    /// turn's words are spread evenly over it. `hits` counts cut words.
    public static func wordsCutAtClipBoundaries(reference: [ReferenceTurn], clips: [SpeakerSegment]) -> Tally {
        let boundaries = clips.flatMap { [$0.start, $0.end] }.sorted()
        var tally = Tally()
        for turn in reference where turn.duration > 0 {
            let words = TextNormalizer.words(turn.text)
            guard !words.isEmpty else { continue }
            let step = turn.duration / Double(words.count)
            for index in words.indices {
                tally.total += 1
                let wordStart = turn.start + Double(index) * step + cutMarginSeconds
                let wordEnd = turn.start + Double(index + 1) * step - cutMarginSeconds
                guard wordEnd > wordStart else { continue }
                if boundaries.contains(where: { $0 > wordStart && $0 < wordEnd }) {
                    tally.hits += 1
                }
            }
        }
        return tally
    }

    /// The hypothesis speaker holding at least 80% of the microphone speech, as the app's
    /// automatic «Я» rule picks it; nil when nobody does.
    public static func automaticMe(microphone hypothesis: [SpeakerSegment]) -> String? {
        let total = hypothesis.reduce(0) { $0 + $1.duration }
        guard total > 0 else { return nil }
        var seconds: [String: Double] = [:]
        for segment in hypothesis {
            seconds[segment.speaker, default: 0] += segment.duration
        }
        return seconds.filter { $0.value >= meShare * total }.keys.sorted().first
    }

    public static func cosineSimilarity(_ lhs: [Float], _ rhs: [Float]) -> Double? {
        guard lhs.count == rhs.count, !lhs.isEmpty else { return nil }
        var dot = 0.0
        var lhsNorm = 0.0
        var rhsNorm = 0.0
        for index in lhs.indices {
            dot += Double(lhs[index]) * Double(rhs[index])
            lhsNorm += Double(lhs[index]) * Double(lhs[index])
            rhsNorm += Double(rhs[index]) * Double(rhs[index])
        }
        guard lhsNorm > 0, rhsNorm > 0 else { return nil }
        return dot / (lhsNorm.squareRoot() * rhsNorm.squareRoot())
    }

    private static func ownCoverage(of turn: ReferenceTurn, hypothesis: [SpeakerSegment],
                                    mapping: [String: String]) -> Double {
        coverage(start: turn.start, end: turn.end, hypothesis: hypothesis)
            .filter { mapping[$0.key] == turn.speaker }
            .reduce(0) { $0 + $1.value }
    }

    private static func orderedLabels(_ segments: [SpeakerSegment]) -> [String] {
        var seen = Set<String>()
        return segments.sorted { $0.start < $1.start }.compactMap { seen.insert($0.speaker).inserted ? $0.speaker : nil }
    }
}

/// Optimal assignment on a small score matrix (Hungarian method).
enum Assignment {
    /// Row → column pairs that maximize the summed score, one column per row at most.
    static func maximizing(_ score: [[Double]]) -> [(row: Int, column: Int)] {
        let rows = score.count
        let columns = score.first?.count ?? 0
        guard rows > 0, columns > 0 else { return [] }
        let size = max(rows, columns)
        let highest = score.flatMap { $0 }.max() ?? 0
        // Square cost matrix: padding cells cost as much as the worst real cell.
        var cost = [[Double]](repeating: [Double](repeating: highest, count: size + 1), count: size + 1)
        for row in 0..<rows {
            for column in 0..<columns {
                cost[row + 1][column + 1] = highest - score[row][column]
            }
        }
        var u = [Double](repeating: 0, count: size + 1)
        var v = [Double](repeating: 0, count: size + 1)
        var match = [Int](repeating: 0, count: size + 1)
        var way = [Int](repeating: 0, count: size + 1)
        for row in 1...size {
            match[0] = row
            var column0 = 0
            var minimum = [Double](repeating: .infinity, count: size + 1)
            var used = [Bool](repeating: false, count: size + 1)
            repeat {
                used[column0] = true
                let row0 = match[column0]
                var delta = Double.infinity
                var column1 = 0
                for column in 1...size where !used[column] {
                    let reduced = cost[row0][column] - u[row0] - v[column]
                    if reduced < minimum[column] {
                        minimum[column] = reduced
                        way[column] = column0
                    }
                    if minimum[column] < delta {
                        delta = minimum[column]
                        column1 = column
                    }
                }
                for column in 0...size {
                    if used[column] {
                        u[match[column]] += delta
                        v[column] -= delta
                    } else {
                        minimum[column] -= delta
                    }
                }
                column0 = column1
            } while match[column0] != 0
            repeat {
                let column1 = way[column0]
                match[column0] = match[column1]
                column0 = column1
            } while column0 != 0
        }
        var pairs: [(row: Int, column: Int)] = []
        for column in 1...size where match[column] >= 1 && match[column] <= rows && column <= columns {
            pairs.append((match[column] - 1, column - 1))
        }
        return pairs.sorted { $0.row < $1.row }
    }
}

// MARK: - Case results

/// One track of one diarization run.
public struct TrackResult: Codable, Equatable, Sendable {
    /// "microphone" or "system".
    public var track: String
    public var audioSeconds: Double
    /// With the run's collar, after the app's turn clean-up.
    public var der: DERBreakdown
    /// Without a collar, after the clean-up.
    public var derStrict: DERBreakdown
    /// With the collar, straight from the library.
    public var derRaw: DERBreakdown
    public var speakers: SpeakerCountResult
    /// Hypothesis label → reference speaker.
    public var mapping: [String: String]
    public var wallSeconds: Double

    public init(track: String, audioSeconds: Double, der: DERBreakdown, derStrict: DERBreakdown, derRaw: DERBreakdown,
                speakers: SpeakerCountResult, mapping: [String: String], wallSeconds: Double) {
        self.track = track
        self.audioSeconds = audioSeconds
        self.der = der
        self.derStrict = derStrict
        self.derRaw = derRaw
        self.speakers = speakers
        self.mapping = mapping
        self.wallSeconds = wallSeconds
    }
}

/// The meeting pipeline's transcript of a case, scored with and without speakers.
public struct TranscriptionResult: Codable, Equatable, Sendable {
    public var engine: String
    public var attributedWER: WordErrors
    /// The same audio transcribed without speaker separation.
    public var plainWER: WordErrors
    /// Reference words a clip boundary falls inside; `hits` are cut words.
    public var cutWords: Tally

    public init(engine: String, attributedWER: WordErrors, plainWER: WordErrors, cutWords: Tally) {
        self.engine = engine
        self.attributedWER = attributedWER
        self.plainWER = plainWER
        self.cutWords = cutWords
    }
}

/// Similarity of a microphone cluster and a system cluster, input for a later rule
/// that recognizes remote voices the microphone picked up from laptop speakers.
public struct EchoPair: Codable, Equatable, Sendable {
    public var microphoneCluster: String
    public var systemCluster: String
    public var microphoneSpeaker: String?
    public var systemSpeaker: String?
    public var cosine: Double

    public init(microphoneCluster: String, systemCluster: String, microphoneSpeaker: String?, systemSpeaker: String?,
                cosine: Double) {
        self.microphoneCluster = microphoneCluster
        self.systemCluster = systemCluster
        self.microphoneSpeaker = microphoneSpeaker
        self.systemSpeaker = systemSpeaker
        self.cosine = cosine
    }

    public var isSameVoice: Bool {
        microphoneSpeaker != nil && microphoneSpeaker == systemSpeaker
    }
}

/// Where the automatic «Я» label landed in a call case.
public struct MeLabelResult: Codable, Equatable, Sendable {
    /// The reference speaker who holds the microphone.
    public var expected: String?
    /// The reference speaker the labelled cluster maps to, "?" for an unmapped cluster,
    /// nil when the rule labelled nobody.
    public var labelled: String?

    public init(expected: String?, labelled: String?) {
        self.expected = expected
        self.labelled = labelled
    }

    public var isCorrect: Bool {
        labelled != nil && labelled == expected
    }
}

/// Everything one `slovo-bench diarize` run measured on one case with one configuration.
public struct DiarizationCaseResult: Codable, Equatable, Sendable {
    public var caseName: String
    public var scenario: String
    public var condition: String
    /// `DiarizationTuning.description`.
    public var configuration: String
    /// "automatic" or "exact".
    public var hint: String
    /// The model identifier, or the name of a fake diarizer.
    public var diarizer: String
    public var date: Date
    public var system: String
    public var collar: Double
    public var tracks: [TrackResult]
    public var attribution: AttributionResult
    public var shortTurns: ShortTurnResult
    public var meLabel: MeLabelResult?
    public var transcription: TranscriptionResult?
    public var echo: [EchoPair]
    /// The first load in the process, including Core ML specialization; nil when the
    /// model was already loaded.
    public var modelLoadSeconds: Double?
    public var diarizationSeconds: Double
    public var peakFootprintMB: Double
    public var peakResidentMB: Double

    public init(caseName: String, scenario: String, condition: String, configuration: String, hint: String,
                diarizer: String, date: Date, system: String, collar: Double, tracks: [TrackResult],
                attribution: AttributionResult, shortTurns: ShortTurnResult, meLabel: MeLabelResult?,
                transcription: TranscriptionResult?, echo: [EchoPair], modelLoadSeconds: Double?,
                diarizationSeconds: Double, peakFootprintMB: Double, peakResidentMB: Double) {
        self.caseName = caseName
        self.scenario = scenario
        self.condition = condition
        self.configuration = configuration
        self.hint = hint
        self.diarizer = diarizer
        self.date = date
        self.system = system
        self.collar = collar
        self.tracks = tracks
        self.attribution = attribution
        self.shortTurns = shortTurns
        self.meLabel = meLabel
        self.transcription = transcription
        self.echo = echo
        self.modelLoadSeconds = modelLoadSeconds
        self.diarizationSeconds = diarizationSeconds
        self.peakFootprintMB = peakFootprintMB
        self.peakResidentMB = peakResidentMB
    }

    public var der: DERBreakdown { .pooled(tracks.map(\.der)) }
    public var derStrict: DERBreakdown { .pooled(tracks.map(\.derStrict)) }
    public var derRaw: DERBreakdown { .pooled(tracks.map(\.derRaw)) }
    public var audioSeconds: Double { tracks.reduce(0) { $0 + $1.audioSeconds } }

    public var speakers: SpeakerCountResult {
        SpeakerCountResult(reference: tracks.reduce(0) { $0 + $1.speakers.reference },
                           detected: tracks.reduce(0) { $0 + $1.speakers.detected })
    }

    /// Seconds of audio diarized per second of wall time.
    public var realTimeMultiple: Double? {
        diarizationSeconds > 0 ? audioSeconds / diarizationSeconds : nil
    }

    public func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(self)
    }

    public static func decode(_ data: Data) throws -> DiarizationCaseResult {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(DiarizationCaseResult.self, from: data)
    }
}

// MARK: - Report

/// Markdown tables for `Bench/results/diarization.md`, in Russian like the rest of the
/// bench's reports.
public enum DiarizationReport {
    public static let scoringRules = """
        DER считается по кадрам 10 мс: (пропуск речи + ложная речь + путаница спикеров) / речь эталона. \
        Перекрывающаяся речь эталона учитывается. Воротник (collar) — окно полной ширины вокруг каждой \
        границы эталона, которое не оценивается; «без воротника» — строгий счёт. Опубликованные цифры \
        FluidAudio получены без учёта перекрытий, цифры pyannote — с учётом, поэтому напрямую с этим \
        отчётом их сравнивать нельзя. «До чистки» — выход библиотеки до объединения и перенумерации реплик, \
        которые делает Slovo.
        """

    /// One row per run of a case, lowest DER first.
    public static func caseTable(_ results: [DiarizationCaseResult]) -> String {
        let header = ["Конфигурация", "Подсказка", "DER", "Без воротника", "Пропуск", "Ложная", "Путаница",
                      "До чистки", "Спикеры: найдено / эталон", "Реплики < 1 с", "Реплики ≥ 3 с",
                      "Короткие у S1", "«Я»", "WER по спикерам / общий", "Скорость, ×"]
        let rows = results.sorted { ($0.der.der, $0.configuration, $0.hint) < ($1.der.der, $1.configuration, $1.hint) }
            .map { result -> [String] in
                let der = result.der
                return [
                    result.configuration,
                    result.hint,
                    percent(der.der),
                    percent(result.derStrict.der),
                    percent(share(der.miss, of: der.referenceSeconds)),
                    percent(share(der.falseAlarm, of: der.referenceSeconds)),
                    percent(share(der.confusion, of: der.referenceSeconds)),
                    percent(result.derRaw.der),
                    "\(result.speakers.detected) / \(result.speakers.reference)",
                    tally(result.attribution[.underOneSecond]),
                    tally(result.attribution.threeSecondsOrLonger),
                    result.shortTurns.absorbedShare.map(percent) ?? "—",
                    result.meLabel.map { $0.isCorrect ? "верно" : ($0.labelled == nil ? "нет" : "ошибка") } ?? "—",
                    result.transcription.map { "\(percent($0.attributedWER.rate)) / \(percent($0.plainWER.rate))" } ?? "—",
                    result.realTimeMultiple.map { String(format: "%.0f", $0) } ?? "—"
                ]
            }
        return table(header: header, rows: rows)
    }

    /// A configuration and hint across every case it ran on.
    public struct Ranking: Equatable, Sendable {
        public var configuration: String
        public var hint: String
        public var cases: Int
        /// Mean case DER per condition.
        public var meanDER: [String: Double]
        public var speakerCountExact: Tally
        public var attributionThreeSeconds: Tally
        public var realTimeMultiple: Double?
    }

    /// Sorted by mean `far-noisy-20` DER, then mean `clean` DER, then speed.
    public static func rank(_ results: [DiarizationCaseResult]) -> [Ranking] {
        let groups = Dictionary(grouping: results) { "\($0.configuration)\n\($0.hint)" }
        let rankings = groups.values.compactMap { group -> Ranking? in
            guard let first = group.first else { return nil }
            var meanDER: [String: Double] = [:]
            for (condition, runs) in Dictionary(grouping: group, by: \.condition) {
                meanDER[condition] = runs.reduce(0) { $0 + $1.der.der } / Double(runs.count)
            }
            let speeds = group.compactMap(\.realTimeMultiple)
            return Ranking(configuration: first.configuration, hint: first.hint, cases: group.count, meanDER: meanDER,
                           speakerCountExact: group.reduce(Tally()) { $0 + Tally(hits: $1.speakers.isExact ? 1 : 0, total: 1) },
                           attributionThreeSeconds: group.reduce(Tally()) { $0 + $1.attribution.threeSecondsOrLonger },
                           realTimeMultiple: speeds.isEmpty ? nil : speeds.reduce(0, +) / Double(speeds.count))
        }
        return rankings.sorted { lhs, rhs in
            let keys = { (ranking: Ranking) -> (Double, Double, Double, String, String) in
                (ranking.meanDER["far-noisy-20"] ?? .infinity, ranking.meanDER["clean"] ?? .infinity,
                 -(ranking.realTimeMultiple ?? 0), ranking.configuration, ranking.hint)
            }
            return keys(lhs) < keys(rhs)
        }
    }

    public static func rankingTable(_ rankings: [Ranking]) -> String {
        let conditions = Array(Set(rankings.flatMap { $0.meanDER.keys })).sorted { lhs, rhs in
            (conditionOrder(lhs), lhs) < (conditionOrder(rhs), rhs)
        }
        let header = ["Конфигурация", "Подсказка", "Случаев"] + conditions.map { "DER \($0)" }
            + ["Число спикеров точно", "Реплики ≥ 3 с", "Скорость, ×"]
        let rows = rankings.map { ranking in
            [ranking.configuration, ranking.hint, "\(ranking.cases)"]
                + conditions.map { ranking.meanDER[$0].map(percent) ?? "—" }
                + [tally(ranking.speakerCountExact), tally(ranking.attributionThreeSeconds),
                   ranking.realTimeMultiple.map { String(format: "%.0f", $0) } ?? "—"]
        }
        return table(header: header, rows: rows)
    }

    /// One pass bar of the release gate; `passed` is nil when nothing measured it.
    public struct GateCheck: Equatable, Sendable {
        public var bar: String
        public var value: String
        public var passed: Bool?
    }

    /// The pass bars for one configuration over every result it has.
    public static func gate(configuration: String, results: [DiarizationCaseResult]) -> [GateCheck] {
        let runs = results.filter { $0.configuration == configuration }
        let automatic = runs.filter { $0.hint == "automatic" }
        let exact = runs.filter { $0.hint == "exact" }
        var checks: [GateCheck] = []

        func meanDER(_ condition: String) -> Double? {
            let matching = automatic.filter { $0.condition == condition }
            return matching.isEmpty ? nil : matching.reduce(0) { $0 + $1.der.der } / Double(matching.count)
        }
        func derCheck(_ condition: String, limit: Double) {
            let value = meanDER(condition)
            checks.append(GateCheck(bar: "DER \(condition) ≤ \(percent(limit))", value: value.map(percent) ?? "не измерено",
                                    passed: value.map { $0 <= limit }))
        }
        derCheck("clean", limit: 0.10)
        derCheck("far", limit: 0.20)
        derCheck("far-noisy-20", limit: 0.20)
        derCheck("rerecorded-standard", limit: 0.20)

        let cowork = automatic.filter { $0.scenario.hasPrefix("cowork") }
        let coworkExact = cowork.reduce(Tally()) { $0 + Tally(hits: $1.speakers.isExact ? 1 : 0, total: 1) }
        checks.append(GateCheck(bar: "Число спикеров без подсказки точно ≥ 80% случаев cowork",
                                value: tally(coworkExact), passed: coworkExact.rate.map { $0 >= 0.8 }))
        let hinted = exact.reduce(Tally()) { $0 + Tally(hits: $1.speakers.isExact ? 1 : 0, total: 1) }
        checks.append(GateCheck(bar: "Число спикеров с подсказкой точно во всех случаях",
                                value: tally(hinted), passed: hinted.rate.map { $0 == 1 }))

        let attribution = automatic.reduce(Tally()) { $0 + $1.attribution.threeSecondsOrLonger }
        checks.append(GateCheck(bar: "Реплики ≥ 3 с отнесены верно ≥ 90%", value: tally(attribution),
                                passed: attribution.rate.map { $0 >= 0.9 }))

        let transcribed = automatic.compactMap(\.transcription)
        if transcribed.isEmpty {
            checks.append(GateCheck(bar: "WER по спикерам не выше общего WER + 3 п.", value: "не измерено", passed: nil))
        } else {
            let attributed = transcribed.reduce((0, 0)) { ($0.0 + $1.attributedWER.errors, $0.1 + $1.attributedWER.referenceWords) }
            let plain = transcribed.reduce((0, 0)) { ($0.0 + $1.plainWER.errors, $0.1 + $1.plainWER.referenceWords) }
            let attributedRate = share(Double(attributed.0), of: Double(attributed.1))
            let plainRate = share(Double(plain.0), of: Double(plain.1))
            checks.append(GateCheck(bar: "WER по спикерам не выше общего WER + 3 п.",
                                    value: "\(percent(attributedRate)) / \(percent(plainRate))",
                                    passed: attributedRate <= plainRate + 0.03))
        }

        let labels = automatic.filter { $0.scenario.hasPrefix("call") }.compactMap(\.meLabel)
        let labelled = labels.filter { $0.labelled != nil }
        let correct = labelled.filter(\.isCorrect)
        checks.append(GateCheck(bar: "Точность автоматического «Я» в звонках 100%",
                                value: labels.isEmpty ? "не измерено"
                                    : "\(correct.count) из \(labelled.count), полнота \(correct.count) из \(labels.count)",
                                passed: labels.isEmpty ? nil : correct.count == labelled.count))

        let long = runs.filter { $0.audioSeconds >= 3_500 && $0.tracks.count == 1 }
        if let slowest = long.max(by: { $0.diarizationSeconds + ($0.modelLoadSeconds ?? 0) < $1.diarizationSeconds + ($1.modelLoadSeconds ?? 0) }) {
            let seconds = slowest.diarizationSeconds + (slowest.modelLoadSeconds ?? 0)
            checks.append(GateCheck(bar: "Час записи с загрузкой модели ≤ 90 с, пик памяти ≤ 2 ГБ",
                                    value: String(format: "%.0f с, %.0f МБ", seconds, slowest.peakResidentMB),
                                    passed: seconds <= 90 && slowest.peakResidentMB <= 2_048))
        } else {
            checks.append(GateCheck(bar: "Час записи с загрузкой модели ≤ 90 с, пик памяти ≤ 2 ГБ",
                                    value: "не измерено", passed: nil))
        }
        return checks
    }

    public static func gateTable(_ checks: [GateCheck]) -> String {
        table(header: ["Порог", "Значение", "Итог"], rows: checks.map { check in
            [check.bar, check.value, check.passed.map { $0 ? "да" : "нет" } ?? "—"]
        })
    }

    static func percent(_ value: Double) -> String {
        Report.percent(value)
    }

    private static func share(_ part: Double, of whole: Double) -> Double {
        whole > 0 ? part / whole : 0
    }

    private static func tally(_ value: Tally) -> String {
        value.total == 0 ? "—" : "\(value.hits)/\(value.total) (\(percent(value.rate ?? 0)))"
    }

    private static func conditionOrder(_ condition: String) -> Int {
        ["clean", "far", "far-noisy-20", "far-noisy-10", "rerecorded-standard", "rerecorded-isolation",
         "call-headphones", "call-speakers"].firstIndex(of: condition) ?? 100
    }

    private static func table(header: [String], rows: [[String]]) -> String {
        var lines = [
            "| " + header.joined(separator: " | ") + " |",
            "|" + String(repeating: "---|", count: header.count)
        ]
        lines += rows.map { "| " + $0.joined(separator: " | ") + " |" }
        return lines.joined(separator: "\n")
    }
}
