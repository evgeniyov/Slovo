import BenchKit
import Foundation
import SlovoCore

/// `diarize-synth`: renders a scripted multi-speaker meeting into the tracks a Slovo
/// meeting has, with the exact truth next to them. Reads Bench/clips, writes only into
/// the case folder.
enum DiarizeSynthCommand {
    /// A committed scenario from `Bench/diarization/scenarios`.
    struct Scenario: Decodable {
        struct Voice: Decodable {
            /// "clips" (the user's recordings), "speech" (a system voice) or "tone" (a
            /// synthetic buzz for testing the harness).
            var source: String
            var voice: String?
            var pitchCents: Double?
            var rate: Double?
            var pitchHz: Double?
            /// "microphone" (the default) or "system" for a remote participant.
            var track: String?
        }

        struct Turn: Decodable {
            var speaker: String
            var text: String?
            var clip: String?
            var clips: [String]?
            /// Length of a tone turn.
            var seconds: Double?
            /// Seconds after the previous turn ends; negative overlaps it.
            var gap: Double
        }

        var name: String
        var seed: UInt64?
        /// The voice holding the microphone; defaults to the clips voice.
        var me: String?
        var voices: [String: Voice]
        var turns: [Turn]
    }

    struct Condition {
        var name: String
        var farField = false
        var snrDecibels: Double?
        var remoteEcho = false

        static func named(_ name: String) -> Condition? {
            switch name {
            case "clean", "call-headphones": return Condition(name: name)
            case "far": return Condition(name: name, farField: true)
            case "far-noisy-20": return Condition(name: name, farField: true, snrDecibels: 20)
            case "far-noisy-10": return Condition(name: name, farField: true, snrDecibels: 10)
            case "call-speakers": return Condition(name: name, remoteEcho: true)
            default: return nil
            }
        }

        static let rendered = ["clean", "far", "far-noisy-20", "far-noisy-10", "call-headphones", "call-speakers"]
        static let manual = ["rerecorded-standard", "rerecorded-isolation"]
    }

    /// `render.json`: how a case was made.
    struct RenderInfo: Codable {
        struct VoiceInfo: Codable {
            var source: String
            var voice: String?
            var pitchCents: Double?
            var rate: Double?
            var track: String
            var gainDecibels: Double
            /// The installed voice used instead of a missing one.
            var substitutedBy: String?
        }

        struct NoiseInfo: Codable {
            var snrDecibels: Double
            var speechRMS: Double
            var noiseRMS: Double
        }

        struct EchoInfo: Codable {
            var gainDecibels: Double
            var delayMilliseconds: Double
        }

        var scenario: String
        var condition: String
        var seed: UInt64
        var sampleRate: Double
        var durationSeconds: Double
        var me: String?
        var voices: [String: VoiceInfo]
        var noise: NoiseInfo?
        var echo: EchoInfo?
        /// Scale applied to keep each track's peak below full scale.
        var peakScale: [String: Double]
        var turnsByLength: [String: Int]
        var overlappingTurns: Int
        var date: Date
    }

    static let farGainRange = -18.0 ... -6.0
    static let echoGainDecibels = -20.0
    static let echoDelaySeconds = 0.03
    static let leadInSeconds = 0.5
    static let tailSeconds = 1.0
    static let clipPauseSeconds = 0.3
    static let maximumClipUses = 2
    /// Stands in for voices this Mac does not have, when asked to.
    static let substituteVoice = "com.apple.voice.compact.ru-RU.Milena"
    static let substitutePitches: [Double] = [300, -250, 150, -100]

    static func run(paths: BenchPaths, arguments: [String]) async throws {
        var arguments = arguments
        guard let scenarioPath = Options.take("--scenario", from: &arguments),
              let conditionName = Options.take("--condition", from: &arguments) else {
            throw BenchError("Нужны --scenario <файл.json> и --condition <\(Condition.rendered.joined(separator: "|"))>")
        }
        if Condition.manual.contains(conditionName) {
            print("""
                Условие \(conditionName) записывается вручную: включите mix.wav случая cowork-… на колонке, \
                переставляя её вокруг стола, и запишите встречу в Slovo встроенным микрофоном MacBook \
                (режим \(conditionName == "rerecorded-standard" ? "«Стандартный»" : "«Изоляция голоса»") в Пункте управления). \
                Потом: slovo-bench diarize --meeting <папка встречи> --case <папка случая> --condition \(conditionName)
                """)
            return
        }
        guard let condition = Condition.named(conditionName) else {
            throw BenchError("Неизвестное условие «\(conditionName)». Есть: \(Condition.rendered.joined(separator: ", "))")
        }
        let seedOverride = Options.take("--seed", from: &arguments).flatMap { UInt64($0) }
        let substitute = arguments.contains("--substitute-voices")
        let scenarioURL = URL(fileURLWithPath: scenarioPath)
        let scenario: Scenario
        do {
            scenario = try JSONDecoder().decode(Scenario.self, from: Data(contentsOf: scenarioURL))
        } catch {
            throw BenchError("Не удалось прочитать сценарий \(scenarioURL.path): \(error.localizedDescription)")
        }
        let output = Options.take("--out", from: &arguments).map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? DiarizationCase.outputFolder(paths).appendingPathComponent("\(scenario.name)-\(condition.name)", isDirectory: true)
        try await render(scenario, condition: condition, seed: seedOverride ?? scenario.seed ?? 7,
                         substituteMissingVoices: substitute, clips: paths.clips, output: output)
    }

    // MARK: - Rendering

    private struct PlacedTurn {
        var speaker: String
        var track: String
        var start: Double
        var samples: [Float]
        var text: String

        var end: Double {
            start + Double(samples.count) / SpeechRendering.sampleRate
        }
    }

    static func render(_ scenario: Scenario, condition: Condition, seed: UInt64, substituteMissingVoices: Bool,
                       clips: URL, output: URL) async throws {
        try validate(scenario, condition: condition)
        var generator = SeededGenerator(seed: seed)
        let substitutions = try voiceSubstitutions(scenario, allowed: substituteMissingVoices)
        for (voice, pitch) in substitutions.sorted(by: { $0.key < $1.key }) {
            print("  голос \(voice) не установлен: вместо него Милена со сдвигом \(Int(pitch)) центов")
        }

        // Voices first, in script order, so the random draws below do not depend on
        // how long synthesis takes.
        var placed: [PlacedTurn] = []
        var speechCache: [String: [Float]] = [:]
        var previousEnd = leadInSeconds
        for (index, turn) in scenario.turns.enumerated() {
            guard let voice = scenario.voices[turn.speaker] else { continue }
            let samples = try await audio(for: turn, voice: voice, voiceID: turn.speaker, clips: clips,
                                          substitutions: substitutions, cache: &speechCache, generator: &generator)
            let start = index == 0 ? leadInSeconds + max(0, turn.gap) : max(0, previousEnd + turn.gap)
            let placedTurn = PlacedTurn(speaker: turn.speaker, track: voice.track ?? "microphone", start: start,
                                        samples: samples, text: try text(for: turn, clips: clips))
            previousEnd = placedTurn.end
            placed.append(placedTurn)
            if (index + 1) % 10 == 0 || index + 1 == scenario.turns.count {
                print("  озвучено реплик: \(index + 1) из \(scenario.turns.count)")
            }
        }

        let duration = (placed.map(\.end).max() ?? 0) + tailSeconds
        let length = Int((duration * SpeechRendering.sampleRate).rounded(.up))
        let hasSystem = placed.contains { $0.track == "system" }

        var gains: [String: Double] = [:]
        for id in scenario.voices.keys.sorted() {
            let isMicrophone = (scenario.voices[id]?.track ?? "microphone") == "microphone"
            gains[id] = condition.farField && isMicrophone ? Double.random(in: farGainRange, using: &generator) : 0
        }

        var cleanMicrophone = [Float](repeating: 0, count: length)
        var microphone = [Float](repeating: 0, count: length)
        var system = [Float](repeating: 0, count: length)
        for turn in placed {
            let offset = Int(turn.start * SpeechRendering.sampleRate)
            if turn.track == "system" {
                SpeechRendering.mix(turn.samples, into: &system, at: offset)
            } else {
                SpeechRendering.mix(turn.samples, into: &cleanMicrophone, at: offset)
                SpeechRendering.mix(turn.samples, into: &microphone, at: offset,
                                    gain: SpeechRendering.gain(decibels: gains[turn.speaker] ?? 0))
            }
        }
        if condition.farField {
            microphone = try SpeechRendering.room(microphone)
        }
        var echo: RenderInfo.EchoInfo?
        if condition.remoteEcho {
            var delayed = [Float](repeating: 0, count: length)
            SpeechRendering.mix(system, into: &delayed, at: Int(echoDelaySeconds * SpeechRendering.sampleRate),
                                gain: SpeechRendering.gain(decibels: echoGainDecibels))
            SpeechRendering.mix(try SpeechRendering.room(delayed), into: &microphone, at: 0)
            echo = RenderInfo.EchoInfo(gainDecibels: echoGainDecibels, delayMilliseconds: echoDelaySeconds * 1_000)
        }
        var noise: RenderInfo.NoiseInfo?
        if let snr = condition.snrDecibels {
            // The level of speech itself, not of the pauses, sets the noise level.
            var speech: [Float] = []
            for turn in placed where turn.track == "microphone" {
                let start = Int(turn.start * SpeechRendering.sampleRate)
                let end = min(length, Int(turn.end * SpeechRendering.sampleRate))
                if end > start {
                    speech.append(contentsOf: microphone[start..<end])
                }
            }
            let speechRMS = Double(SpeechRendering.rms(speech[...]))
            let noiseRMS = speechRMS / pow(10, snr / 20)
            SpeechRendering.mix(SpeechRendering.pinkNoise(count: length, generator: &generator), into: &microphone,
                                at: 0, gain: Float(noiseRMS))
            noise = RenderInfo.NoiseInfo(snrDecibels: snr, speechRMS: speechRMS, noiseRMS: noiseRMS)
        }

        var peakScale: [String: Double] = [:]
        peakScale["microphone"] = limit(&microphone)
        peakScale["mix"] = limit(&cleanMicrophone)
        if hasSystem {
            peakScale["system"] = limit(&system)
        }

        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        try SpeechRendering.writeTrack(microphone, to: output.appendingPathComponent("mic.caf"))
        let systemURL = output.appendingPathComponent("system.caf")
        if hasSystem {
            try SpeechRendering.writeTrack(system, to: systemURL)
        } else {
            try? FileManager.default.removeItem(at: systemURL)
        }
        try WAVFile.write(RecordedAudio(samples: cleanMicrophone, sampleRate: SpeechRendering.sampleRate),
                          to: output.appendingPathComponent("mix.wav"))

        let references = placed.map {
            ReferenceTurn(speaker: $0.speaker, track: $0.track, start: rounded($0.start), end: rounded($0.end), text: $0.text)
        }
        try DiarizationCase.encoder.encode(references).write(to: output.appendingPathComponent("turns.json"))
        let rttm = RTTM.write(references.filter { $0.track == "microphone" }.map(\.segment), file: "mic")
            + RTTM.write(references.filter { $0.track == "system" }.map(\.segment), file: "system")
        try rttm.write(to: output.appendingPathComponent("truth.rttm"), atomically: true, encoding: .utf8)

        var byLength: [String: Int] = [:]
        for reference in references {
            byLength[TurnLength(seconds: reference.duration).rawValue, default: 0] += 1
        }
        let overlapping = zip(placed, placed.dropFirst()).filter { $1.start < $0.end }.count
        var voices: [String: RenderInfo.VoiceInfo] = [:]
        for (id, voice) in scenario.voices {
            voices[id] = RenderInfo.VoiceInfo(source: voice.source, voice: voice.voice, pitchCents: voice.pitchCents,
                                              rate: voice.rate, track: voice.track ?? "microphone",
                                              gainDecibels: gains[id] ?? 0,
                                              substitutedBy: substitutions[id].map { "\(substituteVoice) \(Int($0)) cents" })
        }
        let info = RenderInfo(scenario: scenario.name, condition: condition.name, seed: seed,
                              sampleRate: SpeechRendering.sampleRate, durationSeconds: rounded(duration),
                              me: scenario.me ?? scenario.voices.first(where: { $0.value.source == "clips" })?.key,
                              voices: voices, noise: noise, echo: echo, peakScale: peakScale, turnsByLength: byLength,
                              overlappingTurns: overlapping, date: Date())
        try DiarizationCase.encoder.encode(info).write(to: output.appendingPathComponent("render.json"))

        let lengths = TurnLength.allCases.map { "\($0.rawValue): \(byLength[$0.rawValue] ?? 0)" }.joined(separator: ", ")
        print("""
            Готово: \(output.path)
              длительность \(String(format: "%.1f", duration / 60)) мин, реплик \(references.count) (\(lengths)), \
            с перекрытием \(overlapping)
            """)
    }

    private static func validate(_ scenario: Scenario, condition: Condition) throws {
        guard !scenario.turns.isEmpty else { throw BenchError("В сценарии \(scenario.name) нет реплик") }
        var clipUses: [String: Int] = [:]
        for (index, turn) in scenario.turns.enumerated() {
            let place = "\(scenario.name), реплика \(index + 1)"
            guard let voice = scenario.voices[turn.speaker] else {
                throw BenchError("\(place): голос «\(turn.speaker)» не описан в voices")
            }
            guard turn.gap.isFinite, turn.gap > -5 else { throw BenchError("\(place): неверный gap") }
            switch voice.source {
            case "clips":
                let ids = (turn.clips ?? []) + (turn.clip.map { [$0] } ?? [])
                guard !ids.isEmpty else { throw BenchError("\(place): для голоса из записей нужен clip или clips") }
                for id in ids {
                    clipUses[id, default: 0] += 1
                }
            case "speech":
                guard voice.voice != nil, !(turn.text ?? "").isEmpty else {
                    throw BenchError("\(place): для синтезированного голоса нужны voice и text")
                }
            case "tone":
                guard let seconds = turn.seconds, seconds > 0, voice.pitchHz != nil else {
                    throw BenchError("\(place): для тона нужны pitchHz и seconds")
                }
            default:
                throw BenchError("\(place): неизвестный источник голоса «\(voice.source)»")
            }
            guard ["microphone", "system"].contains(voice.track ?? "microphone") else {
                throw BenchError("\(place): track бывает microphone или system")
            }
        }
        if let overused = clipUses.first(where: { $0.value > maximumClipUses }) {
            throw BenchError("\(scenario.name): запись \(overused.key) использована \(overused.value) раза, можно не больше \(maximumClipUses)")
        }
        let hasSystem = scenario.voices.values.contains { $0.track == "system" }
        if condition.name.hasPrefix("call"), !hasSystem {
            throw BenchError("Условие \(condition.name) только для звонков: в \(scenario.name) нет голосов на системном звуке")
        }
    }

    /// Missing speech voices and the pitch offset of their stand-in.
    private static func voiceSubstitutions(_ scenario: Scenario, allowed: Bool) throws -> [String: Double] {
        let missing = scenario.voices
            .filter { $0.value.source == "speech" && !SpeechRendering.isInstalled(voice: $0.value.voice ?? "") }
            .keys.sorted()
        guard !missing.isEmpty else { return [:] }
        guard allowed else {
            let names = missing.compactMap { scenario.voices[$0]?.voice }.joined(separator: ", ")
            throw BenchError("""
                Не установлены голоса: \(names). Добавьте их в Системных настройках → Универсальный доступ → \
                Озвучивание, или запустите с --substitute-voices, чтобы заменить их Миленой со сдвигом высоты \
                (замена пишется в render.json и ослабляет выводы).
                """)
        }
        guard SpeechRendering.isInstalled(voice: substituteVoice) else {
            throw BenchError("Для замены нужен голос \(substituteVoice), он не установлен")
        }
        var result: [String: Double] = [:]
        for (index, id) in missing.enumerated() {
            result[id] = substitutePitches[index % substitutePitches.count]
        }
        return result
    }

    private static func audio(for turn: Scenario.Turn, voice: Scenario.Voice, voiceID: String, clips: URL,
                              substitutions: [String: Double], cache: inout [String: [Float]],
                              generator: inout SeededGenerator) async throws -> [Float] {
        var samples: [Float]
        switch voice.source {
        case "clips":
            let ids = turn.clips ?? turn.clip.map { [$0] } ?? []
            samples = []
            for (index, id) in ids.enumerated() {
                let url = clips.appendingPathComponent("\(id).wav")
                guard FileManager.default.fileExists(atPath: url.path) else {
                    throw BenchError("Нет записи \(url.path)")
                }
                if index > 0 {
                    samples += SpeechRendering.silence(seconds: clipPauseSeconds)
                }
                samples += SpeechRendering.trimmed(try AudioLoader.samples(from: url))
            }
        case "tone":
            samples = SpeechRendering.tone(seconds: turn.seconds ?? 1, pitchHz: voice.pitchHz ?? 150,
                                           generator: &generator)
        default:
            let text = turn.text ?? ""
            let identifier = substitutions[voiceID] == nil ? voice.voice ?? "" : substituteVoice
            let key = identifier + "\n" + text
            if let cached = cache[key] {
                samples = cached
            } else {
                samples = try await SpeechRendering.speak(text, voice: identifier)
                cache[key] = samples
            }
        }
        let cents = (voice.pitchCents ?? 0) + (substitutions[voiceID] ?? 0)
        let rate = voice.rate ?? 1
        if cents != 0 || rate != 1 {
            samples = try SpeechRendering.shifted(samples, cents: cents, rate: rate)
        }
        guard !samples.isEmpty else {
            throw BenchError("Реплика голоса \(voiceID) получилась пустой")
        }
        return samples
    }

    private static func text(for turn: Scenario.Turn, clips: URL) throws -> String {
        if let text = turn.text {
            return text
        }
        let ids = turn.clips ?? turn.clip.map { [$0] } ?? []
        return ids.compactMap { id in
            try? String(contentsOf: clips.appendingPathComponent("\(id).txt"), encoding: .utf8)
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }.joined(separator: " ")
    }

    /// Scales the track down when its peak would clip; returns the scale.
    private static func limit(_ samples: inout [Float]) -> Double {
        let peak = samples.reduce(Float(0)) { max($0, abs($1)) }
        guard peak > 0.98 else { return 1 }
        let scale = 0.98 / peak
        for index in samples.indices {
            samples[index] *= scale
        }
        return Double(scale)
    }

    private static func rounded(_ seconds: Double) -> Double {
        (seconds * 1_000).rounded() / 1_000
    }
}

/// Where the speaker evaluation keeps its files.
enum DiarizationCase {
    /// `Bench/diarization/out`: rendered cases and their results, never committed.
    static func outputFolder(_ paths: BenchPaths) -> URL {
        paths.root.appendingPathComponent("diarization/out", isDirectory: true)
    }

    /// `Bench/results/diarization-meetings`: results of real recordings, which are
    /// never written next to the meeting.
    static func meetingResultsFolder(_ paths: BenchPaths) -> URL {
        paths.results.appendingPathComponent("diarization-meetings", isDirectory: true)
    }

    static func reportURL(_ paths: BenchPaths) -> URL {
        paths.results.appendingPathComponent("diarization.md")
    }

    static var encoder: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }

    static var decoder: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }
}
