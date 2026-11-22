import AVFoundation
import BenchKit
import Foundation
import SlovoCore

/// `diarize`: separates speakers in synthetic cases or a real Slovo recording and scores
/// the result. It never downloads anything: without the speaker model it stops.
@MainActor
enum DiarizeCommand {
    struct RunOptions {
        var collar = 0.25
        var speakers: Int?
        var tunings: [DiarizationTuning] = [.standard]
        var sweep = false
        var transcribeEngine: EngineID?
        var modelFolder = ModelStore.root.appendingPathComponent("diarization", isDirectory: true)
        var fake: String?
        var condition: String?
        var longMinutes: Double?
    }

    /// One audio track of a case, as the diarizer sees it.
    struct CaseTrack: Sendable {
        /// "microphone" or "system", as in `turns.json`.
        var name: String
        /// The RTTM file id: "mic" or "system".
        var fileID: String
        var url: URL
        var duration: Double
    }

    struct EvaluationCase {
        var name: String
        var scenario: String
        var condition: String
        /// The reference speaker holding the microphone in a call.
        var me: String?
        /// Where hypothesis and result files go.
        var output: URL
        var tracks: [CaseTrack]
        var references: [ReferenceTurn]
        var voices: [String]

        var hasSystem: Bool {
            tracks.contains { $0.name == "system" }
        }

        var speakerCount: Int {
            Set(references.map(\.speaker)).count
        }
    }

    /// Step {0.2, 0.1} × minimum segment {1, 0.5, 0} × zero-vote {off, on} × threshold
    /// {0.6, 0.7, 0.775, 0.85}; each runs with and without the true count.
    static var sweepTunings: [DiarizationTuning] {
        var tunings: [DiarizationTuning] = []
        for step in [0.2, 0.1] {
            for minimum in [1.0, 0.5, 0.0] {
                for zeroVote in [false, true] {
                    for threshold in [0.6, 0.7, 0.775, 0.85] {
                        var tuning = DiarizationTuning.standard
                        tuning.segmentationStepRatio = step
                        tuning.minimumSegmentSeconds = minimum
                        tuning.zeroVoteReembed = zeroVote
                        tuning.clusteringThreshold = threshold
                        tunings.append(tuning)
                    }
                }
            }
        }
        return tunings
    }

    static func run(paths: BenchPaths, arguments: [String]) async throws {
        var arguments = arguments
        if arguments.contains("--summary") {
            try summarize(paths: paths)
            return
        }
        let options = try parse(&arguments)
        let truth = Options.take("--truth", from: &arguments).map { URL(fileURLWithPath: $0) }
        let truthSystem = Options.take("--truth-system", from: &arguments).map { URL(fileURLWithPath: $0) }
        let casePath = Options.take("--case", from: &arguments).map { URL(fileURLWithPath: $0, isDirectory: true) }
        if let meeting = Options.take("--meeting", from: &arguments).map({ URL(fileURLWithPath: $0, isDirectory: true) }) {
            try await runMeeting(meeting, truth: truth, truthSystem: truthSystem, alignedCase: casePath, options: options,
                                 paths: paths)
        } else if let casePath {
            try await runCase(casePath, options: options, paths: paths)
        } else {
            throw BenchError("Нужен --case <папка случая>, --meeting <папка встречи> или --summary")
        }
    }

    private static func parse(_ arguments: inout [String]) throws -> RunOptions {
        var options = RunOptions()
        if let text = Options.take("--collar", from: &arguments) {
            guard let value = Double(text), value.isFinite, value >= 0 else {
                throw BenchError("--collar: число секунд, например 0.25")
            }
            options.collar = value
        }
        if let text = Options.take("--speakers", from: &arguments) {
            guard let value = Int(text), (1...8).contains(value) else { throw BenchError("--speakers: от 1 до 8") }
            options.speakers = value
        }
        let configuration = Options.take("--config", from: &arguments)
        options.sweep = arguments.contains("--sweep")
        if options.sweep {
            options.tunings = sweepTunings
        } else if let configuration {
            guard let tuning = DiarizationTuning(configuration: configuration) else {
                throw BenchError("Не удалось разобрать --config «\(configuration)». Пример: step=0.1,minSeg=0,zeroVote=on,threshold=0.775")
            }
            options.tunings = [tuning]
        }
        if let engine = Options.take("--transcribe", from: &arguments) {
            guard let id = EngineID(rawValue: engine) else {
                throw BenchError("Неизвестный движок «\(engine)». Список: swift run slovo-bench engines")
            }
            options.transcribeEngine = id
        }
        if let folder = Options.take("--models", from: &arguments) {
            options.modelFolder = URL(fileURLWithPath: folder, isDirectory: true)
        }
        if let fake = Options.take("--fake-diarizer", from: &arguments) {
            guard fake == "oracle" else { throw BenchError("--fake-diarizer бывает только oracle") }
            options.fake = fake
        }
        options.condition = Options.take("--condition", from: &arguments)
        if let text = Options.take("--long-minutes", from: &arguments) {
            guard let minutes = Double(text), minutes > 0, minutes <= 240 else {
                throw BenchError("--long-minutes: от 1 до 240")
            }
            options.longMinutes = minutes
        }
        return options
    }

    // MARK: - Synthetic cases

    private static func runCase(_ folder: URL, options: RunOptions, paths: BenchPaths) async throws {
        var evaluation = try loadCase(folder, condition: options.condition)
        let diarizer = try await makeDiarizer(options: options, references: evaluation.references,
                                              voices: evaluation.voices)
        var temporaryTrack: URL?
        defer {
            if let temporaryTrack {
                try? FileManager.default.removeItem(at: temporaryTrack)
            }
        }
        if let minutes = options.longMinutes {
            evaluation = try lengthened(evaluation, minutes: minutes)
            temporaryTrack = evaluation.tracks.first?.url
        }
        print("\n== \(evaluation.name): \(String(format: "%.1f", (evaluation.tracks.map(\.duration).max() ?? 0) / 60)) мин, "
            + "реплик \(evaluation.references.count), спикеров \(evaluation.speakerCount)")

        let counts: [Int?] = options.sweep ? [nil, evaluation.speakerCount] : [options.speakers]
        var loadSeconds = diarizer.loadSeconds
        var results: [DiarizationCaseResult] = []
        for tuning in options.tunings {
            for speakers in counts {
                let run = try await evaluate(evaluation, diarizer: diarizer.diarizer, tuning: tuning, speakers: speakers,
                                             collar: options.collar, modelLoadSeconds: loadSeconds)
                loadSeconds = nil
                var result = run.result
                if let engine = options.transcribeEngine {
                    result.transcription = try await transcribe(evaluation, engine: engine, provider: diarizer.provider,
                                                                speakers: speakers)
                }
                let slug = self.slug(tuning, speakers: speakers)
                try FileManager.default.createDirectory(at: evaluation.output, withIntermediateDirectories: true)
                try run.hypothesis.write(to: evaluation.output.appendingPathComponent("hyp-\(slug).rttm"),
                                         atomically: true, encoding: .utf8)
                try result.encoded().write(to: evaluation.output.appendingPathComponent("result-\(slug).json"))
                print(line(result))
                results.append(result)
            }
        }
        try appendReport(title: evaluation.name, results: results, fake: options.fake != nil, notes: [], paths: paths)
    }

    static func loadCase(_ folder: URL, condition: String?) throws -> EvaluationCase {
        let turnsURL = folder.appendingPathComponent("turns.json")
        guard FileManager.default.fileExists(atPath: turnsURL.path) else {
            throw BenchError("В \(folder.path) нет turns.json. Сначала: swift run slovo-bench diarize-synth --scenario … --condition …")
        }
        let references = try DiarizationCase.decoder.decode([ReferenceTurn].self, from: Data(contentsOf: turnsURL))
        let render = try? DiarizationCase.decoder.decode(DiarizeSynthCommand.RenderInfo.self,
                                                         from: Data(contentsOf: folder.appendingPathComponent("render.json")))
        var tracks: [CaseTrack] = []
        for (name, file) in [("microphone", "mic"), ("system", "system")] {
            let url = folder.appendingPathComponent("\(file).caf")
            if FileManager.default.fileExists(atPath: url.path) {
                tracks.append(CaseTrack(name: name, fileID: file, url: url, duration: try duration(of: url)))
            }
        }
        guard !tracks.isEmpty else { throw BenchError("В \(folder.path) нет mic.caf") }
        return EvaluationCase(name: folder.lastPathComponent, scenario: render?.scenario ?? folder.lastPathComponent,
                              condition: condition ?? render?.condition ?? "unknown", me: render?.me, output: folder,
                              tracks: tracks, references: references,
                              voices: render.map { $0.voices.keys.sorted() } ?? Set(references.map(\.speaker)).sorted())
    }

    // MARK: - Diarizers

    struct PreparedDiarizer {
        var diarizer: BenchDiarizer
        /// What the meeting pipeline uses for `--transcribe`.
        var provider: DiarizationProviding
        var loadSeconds: Double?
    }

    private static func makeDiarizer(options: RunOptions, references: [ReferenceTurn],
                                     voices: [String]) async throws -> PreparedDiarizer {
        if options.fake != nil {
            print("  Диаризатор подменён (fake-oracle): реплики строятся из эталона, цифры проверяют только сам стенд.")
            let oracle = OracleDiarizer(references: references, voices: voices, seed: 11)
            return PreparedDiarizer(diarizer: oracle, provider: OracleProvider(oracle: oracle), loadSeconds: nil)
        }
        let service = DiarizationService(modelsRoot: modelsRoot(for: options.modelFolder))
        guard service.isSupportedOnThisSystem else {
            throw BenchError("Разделению по спикерам нужна macOS 15 или новее")
        }
        guard case .ready = service.status else {
            throw BenchError("""
                Модель спикеров не готова в \(service.modelFolder.path): \(describe(service.status)). \
                slovo-bench её не скачивает. Скачайте модель в Slovo → Настройки → Встречи → Модель спикеров \
                или укажите --models <папка diarization>.
                """)
        }
        let started = ProcessInfo.processInfo.systemUptime
        let diarizer = try await FluidSpeakerDiarizer.load(modelFolder: service.modelFolder)
        let seconds = ProcessInfo.processInfo.systemUptime - started
        print("  Модель спикеров загружена за \(String(format: "%.1f", seconds)) с (первая загрузка включает подготовку Core ML)")
        return PreparedDiarizer(diarizer: ModelDiarizer(diarizer: diarizer), provider: service, loadSeconds: seconds)
    }

    /// Accepts a models root, its `diarization` folder or FluidAudio's folder inside that.
    static func modelsRoot(for folder: URL) -> URL {
        switch folder.lastPathComponent {
        case "diarization":
            return folder.deletingLastPathComponent()
        case "speaker-diarization", "speaker-diarization-coreml":
            return folder.deletingLastPathComponent().deletingLastPathComponent()
        default:
            return folder
        }
    }

    private static func describe(_ status: ModelStatus) -> String {
        switch status {
        case .notDownloaded: return "не скачана"
        case let .damaged(reason): return reason
        case .downloading: return "скачивается"
        default: return "статус неизвестен"
        }
    }

    // MARK: - Scoring

    /// The count hint of the app's pipeline for N people: an in-person meeting gets
    /// exactly N on the microphone; a call gets at most N there and N − 1 on system audio.
    static func hints(speakers: Int?, hasSystem: Bool) -> (microphone: SpeakerCountHint, system: SpeakerCountHint) {
        guard let speakers, speakers > 0 else { return (.automatic, .automatic) }
        if hasSystem {
            return (SpeakerCountHint(maximum: speakers), SpeakerCountHint(maximum: max(1, speakers - 1)))
        }
        return (SpeakerCountHint(exactly: speakers), .automatic)
    }

    static func evaluate(_ evaluation: EvaluationCase, diarizer: BenchDiarizer, tuning: DiarizationTuning,
                         speakers: Int?, collar: Double,
                         modelLoadSeconds: Double?) async throws -> (result: DiarizationCaseResult, hypothesis: String) {
        let hints = hints(speakers: speakers, hasSystem: evaluation.hasSystem)
        var tracks: [TrackResult] = []
        var attribution = AttributionResult()
        var shortTurns = ShortTurnResult()
        var hypothesisText = ""
        var hypotheses: [String: [SpeakerSegment]] = [:]
        var mappings: [String: [String: String]] = [:]
        var centroids: [String: [Int: [Float]]] = [:]
        var wall = 0.0

        for track in evaluation.tracks {
            let hint = track.name == "system" ? hints.system : hints.microphone
            let started = ProcessInfo.processInfo.systemUptime
            let diagnosis = try await diarizer.diagnose(track: track, tuning: tuning, hint: hint)
            let seconds = diagnosis.timings["wall"] ?? ProcessInfo.processInfo.systemUptime - started
            wall += seconds

            let cleaned = SpeakerTurns.normalize(diagnosis.turns, trackDuration: track.duration, hint: hint)
            let hypothesis = cleaned.map { SpeakerSegment(speaker: label($0.cluster), start: $0.start, end: $0.end) }
            let raw = diagnosis.turns.map { SpeakerSegment(speaker: label($0.cluster), start: $0.start, end: $0.end) }
            let references = evaluation.references.filter { $0.track == track.name }
            let referenceSegments = references.map(\.segment)

            let score = der(referenceSegments, hypothesis, collar: collar)
            let strict = der(referenceSegments, hypothesis, collar: 0)
            let rawScore = der(referenceSegments, raw, collar: collar)
            let mapping = score.mapping
            tracks.append(TrackResult(track: track.name, audioSeconds: track.duration, der: breakdown(score),
                                      derStrict: breakdown(strict), derRaw: breakdown(rawScore),
                                      speakers: DiarizationMetrics.speakerCount(reference: referenceSegments,
                                                                                hypothesis: hypothesis),
                                      mapping: mapping, wallSeconds: seconds))
            attribution = attribution + DiarizationMetrics.attribution(reference: references, hypothesis: hypothesis,
                                                                       mapping: mapping)
            shortTurns = shortTurns + DiarizationMetrics.shortTurns(reference: references, hypothesis: hypothesis,
                                                                    mapping: mapping, firstCluster: label(0))
            hypothesisText += RTTM.write(hypothesis, file: track.fileID)
            hypotheses[track.name] = hypothesis
            mappings[track.name] = mapping
            var labelled: [Int: [Float]] = [:]
            for (cluster, centroid) in diagnosis.centroids {
                labelled[cluster] = centroid
            }
            centroids[track.name] = labelled
        }

        var meLabel: MeLabelResult?
        var echo: [EchoPair] = []
        if evaluation.hasSystem {
            let automatic = DiarizationMetrics.automaticMe(microphone: hypotheses["microphone"] ?? [])
            meLabel = MeLabelResult(expected: evaluation.me,
                                    labelled: automatic.map { mappings["microphone"]?[$0] ?? "?" })
            echo = echoPairs(evaluation, hypotheses: hypotheses, mappings: mappings, centroids: centroids)
        }

        let result = DiarizationCaseResult(
            caseName: evaluation.name, scenario: evaluation.scenario, condition: evaluation.condition,
            configuration: tuning.description, hint: speakers == nil ? "automatic" : "exact", diarizer: diarizer.name,
            date: Date(), system: ProcessInfo.processInfo.operatingSystemVersionString, collar: collar, tracks: tracks,
            attribution: attribution, shortTurns: shortTurns, meLabel: meLabel, transcription: nil, echo: echo,
            modelLoadSeconds: modelLoadSeconds, diarizationSeconds: wall,
            peakFootprintMB: ProcessMetrics.peakFootprintMB(), peakResidentMB: ProcessMetrics.peakResidentMB())
        return (result, hypothesisText)
    }

    /// Microphone clusters against system clusters. A microphone cluster that stands
    /// for nobody on the microphone is named after the remote voice it overlaps most:
    /// that is an echo from the laptop speakers.
    private static func echoPairs(_ evaluation: EvaluationCase, hypotheses: [String: [SpeakerSegment]],
                                  mappings: [String: [String: String]],
                                  centroids: [String: [Int: [Float]]]) -> [EchoPair] {
        let systemReferences = evaluation.references.filter { $0.track == "system" }.map(\.segment)
        let microphoneMapping = mappings["microphone"] ?? [:]
        let unmapped = (hypotheses["microphone"] ?? []).filter { microphoneMapping[$0.speaker] == nil }
        let echoes = DiarizationMetrics.overlapMapping(reference: systemReferences, hypothesis: unmapped)
        var pairs: [EchoPair] = []
        for (microphoneCluster, microphoneCentroid) in (centroids["microphone"] ?? [:]).sorted(by: { $0.key < $1.key }) {
            for (systemCluster, systemCentroid) in (centroids["system"] ?? [:]).sorted(by: { $0.key < $1.key }) {
                guard let cosine = DiarizationMetrics.cosineSimilarity(microphoneCentroid, systemCentroid) else { continue }
                let microphoneLabel = label(microphoneCluster)
                let systemLabel = label(systemCluster)
                pairs.append(EchoPair(microphoneCluster: microphoneLabel, systemCluster: systemLabel,
                                      microphoneSpeaker: microphoneMapping[microphoneLabel] ?? echoes[microphoneLabel],
                                      systemSpeaker: mappings["system"]?[systemLabel], cosine: cosine))
            }
        }
        return pairs
    }

    private static func der(_ reference: [SpeakerSegment], _ hypothesis: [SpeakerSegment],
                            collar: Double) -> DiarizationScoring.Score {
        DiarizationScoring.der(reference: reference.map { .init(speaker: $0.speaker, start: $0.start, end: $0.end) },
                               hypothesis: hypothesis.map { .init(speaker: $0.speaker, start: $0.start, end: $0.end) },
                               collar: collar)
    }

    private static func breakdown(_ score: DiarizationScoring.Score) -> DERBreakdown {
        DERBreakdown(miss: score.miss, falseAlarm: score.falseAlarm, confusion: score.confusion,
                     referenceSeconds: score.referenceSpeechSeconds)
    }

    /// FluidAudio's own naming: cluster 0 is S1.
    static func label(_ cluster: Int) -> String {
        "S\(cluster + 1)"
    }

    static func slug(_ tuning: DiarizationTuning, speakers: Int?) -> String {
        tuning.description.replacingOccurrences(of: " ", with: "-").replacingOccurrences(of: "=", with: "")
            + (speakers.map { "-n\($0)" } ?? "-auto")
    }

    // MARK: - Transcription

    /// Runs the app's meeting pipeline on a copy of the case in a temporary data folder
    /// with a throwaway preferences suite: once with speaker separation, once without.
    /// Engines read the installed models and never download.
    private static func transcribe(_ evaluation: EvaluationCase, engine: EngineID, provider: DiarizationProviding,
                                   speakers: Int?) async throws -> TranscriptionResult {
        let base = FileManager.default.temporaryDirectory
            .appendingPathComponent("slovo-bench-meeting-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: base) }
        let suite = "local.slovo.bench.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suite) else {
            throw BenchError("Не удалось создать временные настройки")
        }
        defer {
            defaults.removePersistentDomain(forName: suite)
            CFPreferencesAppSynchronize(suite as CFString)
            // Removing the domain can leave its empty file behind; the file belongs to this run only.
            if let library = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask).first {
                try? FileManager.default.removeItem(at: library.appendingPathComponent("Preferences/\(suite).plist"))
            }
        }
        let settings = AppSettings(defaults: defaults)
        settings.meetingEngine = engine
        // The model manager tidies download leftovers under its root, so it gets a
        // private root that links to the installed models instead of the real folder.
        let modelsRoot = base.appendingPathComponent("Models", isDirectory: true)
        try FileManager.default.createDirectory(at: modelsRoot, withIntermediateDirectories: true)
        let installed = (try? FileManager.default.contentsOfDirectory(at: ModelStore.root, includingPropertiesForKeys: nil)) ?? []
        for folder in installed where !folder.lastPathComponent.hasPrefix(".") && folder.lastPathComponent != "diarization" {
            try FileManager.default.createSymbolicLink(at: modelsRoot.appendingPathComponent(folder.lastPathComponent),
                                                       withDestinationURL: folder)
        }
        let models = ModelManager(modelsRoot: modelsRoot)
        models.refresh()
        let engines = EngineManager(settings: settings, models: models, factory: EngineManager.factory(modelsRoot: modelsRoot))
        defer { engines.unload(engine) }

        let store = MeetingStore(directories: AppDirectories(base: base))
        var record = try store.create(title: evaluation.name)
        for track in evaluation.tracks {
            let destination = track.name == "system" ? store.systemAudioURL(for: record.id)
                : store.microphoneAudioURL(for: record.id)
            try FileManager.default.copyItem(at: track.url, to: destination)
        }
        record.status = .recorded
        record.hasAudio = true
        record.hasSystemAudio = evaluation.hasSystem
        record.duration = evaluation.tracks.map(\.duration).max() ?? 0
        store.update(record)
        try store.flush()

        let transcriber = MeetingTranscriber(store: store, engines: engines, settings: settings, diarization: provider)
        print("  расшифровка \(engine.rawValue) с разделением по спикерам…")
        let separated = try await transcriber.transcribe(
            meetingID: record.id,
            options: MeetingProcessingOptions(mode: .full, separateSpeakers: true, expectedSpeakers: speakers, engineID: engine))
        let separatedPieces = pieces(separated)
        if !separated.segments.contains(where: { $0.speakerID != nil }) {
            print("  конвейер встреч не разделил спикеров: WER по спикерам относится ко всей речи трека как к одному голосу")
        }
        print("  расшифровка \(engine.rawValue) без разделения…")
        let plain = try await transcriber.transcribe(
            meetingID: record.id,
            options: MeetingProcessingOptions(mode: .full, separateSpeakers: false, expectedSpeakers: nil, engineID: engine))

        let hypothesis = separatedPieces.map(\.text)
        let mapping = DiarizationMetrics.overlapMapping(
            reference: evaluation.references.map(\.segment),
            hypothesis: hypothesis.map { SpeakerSegment(speaker: $0.speaker, start: $0.start, end: $0.end) })
        var cut = Tally()
        for track in evaluation.tracks {
            cut = cut + DiarizationMetrics.wordsCutAtClipBoundaries(
                reference: evaluation.references.filter { $0.track == track.name },
                clips: separatedPieces.filter { $0.track == track.name }
                    .map { SpeakerSegment(speaker: $0.text.speaker, start: $0.text.start, end: $0.text.end) })
        }
        return TranscriptionResult(
            engine: engine.rawValue,
            attributedWER: WordErrors(DiarizationMetrics.speakerAttributedWER(reference: evaluation.references,
                                                                               hypothesis: hypothesis, mapping: mapping)),
            plainWER: WordErrors(DiarizationMetrics.plainWER(reference: evaluation.references,
                                                             hypothesis: pieces(plain).map(\.text))),
            cutWords: cut)
    }

    private static func pieces(_ record: MeetingRecord) -> [(track: String, text: AttributedText)] {
        record.segments.map { segment in
            (MeetingTrack(segment.speaker).rawValue,
             AttributedText(speaker: record.speakerID(for: segment), start: segment.start, end: segment.end,
                            text: segment.text))
        }
    }

    // MARK: - Real recordings

    private static func runMeeting(_ folder: URL, truth: URL?, truthSystem: URL?, alignedCase: URL?,
                                   options: RunOptions, paths: BenchPaths) async throws {
        let record = try? JSONDecoder().decode(MeetingRecord.self,
                                               from: Data(contentsOf: folder.appendingPathComponent("meeting.json")))
        var tracks: [CaseTrack] = []
        for (name, file) in [("microphone", "mic"), ("system", "system")] {
            let url = folder.appendingPathComponent("\(file).caf")
            if FileManager.default.fileExists(atPath: url.path) {
                tracks.append(CaseTrack(name: name, fileID: file, url: url, duration: try duration(of: url)))
            }
        }
        guard let microphone = tracks.first(where: { $0.name == "microphone" }) else {
            throw BenchError("В \(folder.path) нет mic.caf: записи встречи нет или она удалена")
        }

        var references: [ReferenceTurn] = []
        var notes: [String] = []
        var scenario = "meeting"
        var me: String?
        if let truth {
            references += try TruthCSV.parse(String(contentsOf: truth, encoding: .utf8)).map {
                ReferenceTurn(speaker: $0.speaker, track: "microphone", start: $0.start, end: $0.end, text: "")
            }
            if let truthSystem {
                references += try TruthCSV.parse(String(contentsOf: truthSystem, encoding: .utf8)).map {
                    ReferenceTurn(speaker: $0.speaker, track: "system", start: $0.start, end: $0.end, text: "")
                }
            }
        } else if let alignedCase {
            let source = try loadCase(alignedCase, condition: nil)
            let offset = try alignmentOffset(reference: alignedCase.appendingPathComponent("mix.wav"), recording: microphone.url)
            references = source.references.filter { $0.track == "microphone" }.map {
                ReferenceTurn(speaker: $0.speaker, track: "microphone", start: $0.start + offset, end: $0.end + offset,
                              text: $0.text)
            }
            scenario = source.scenario
            me = source.me
            let note = "Эталон \(source.name) сдвинут на \(String(format: "%.2f", offset)) с по огибающим энергии"
            print("  " + note)
            notes.append(note)
        } else {
            throw BenchError("Для --meeting нужен --truth truth.csv (start,end,name) или --case <папка случая> для перезаписи")
        }
        tracks = tracks.filter { track in track.name == "microphone" || references.contains { $0.track == track.name } }

        let name = "meeting-\(folder.lastPathComponent)"
        let evaluation = EvaluationCase(name: name, scenario: scenario, condition: options.condition ?? "real", me: me,
                                        output: DiarizationCase.meetingResultsFolder(paths).appendingPathComponent(name),
                                        tracks: tracks, references: references,
                                        voices: Set(references.map(\.speaker)).sorted())
        if let record, let words = storedWordAttribution(record, references: references) {
            let note = "Слова сохранённой расшифровки у верного спикера: \(words.hits) из \(words.total)"
                + (words.rate.map { String(format: " (%.1f%%)", $0 * 100) } ?? "")
            print("  " + note)
            notes.append(note)
        }

        let diarizer = try await makeDiarizer(options: options, references: references, voices: evaluation.voices)
        var loadSeconds = diarizer.loadSeconds
        var results: [DiarizationCaseResult] = []
        for tuning in options.tunings {
            for speakers in options.sweep ? [nil, evaluation.speakerCount] : [options.speakers] {
                let run = try await evaluate(evaluation, diarizer: diarizer.diarizer, tuning: tuning, speakers: speakers,
                                             collar: options.collar, modelLoadSeconds: loadSeconds)
                loadSeconds = nil
                let slug = self.slug(tuning, speakers: speakers)
                try FileManager.default.createDirectory(at: evaluation.output, withIntermediateDirectories: true)
                try run.hypothesis.write(to: evaluation.output.appendingPathComponent("hyp-\(slug).rttm"),
                                         atomically: true, encoding: .utf8)
                try run.result.encoded().write(to: evaluation.output.appendingPathComponent("result-\(slug).json"))
                print(line(run.result))
                results.append(run.result)
            }
        }
        try appendReport(title: name, results: results, fake: options.fake != nil, notes: notes, paths: paths)
    }

    /// Words of the meeting's own transcript whose speaker maps to the reference speaker
    /// talking at that time; nil when the transcript has no separated speakers.
    private static func storedWordAttribution(_ record: MeetingRecord, references: [ReferenceTurn]) -> Tally? {
        guard record.segments.contains(where: { $0.speakerID != nil }) else { return nil }
        let referenceSegments = references.map(\.segment)
        let hypothesis = record.segments.map {
            SpeakerSegment(speaker: record.speakerID(for: $0), start: $0.start, end: $0.end)
        }
        let mapping = DiarizationMetrics.overlapMapping(reference: referenceSegments, hypothesis: hypothesis)
        var tally = Tally()
        for segment in record.segments {
            let words = TextNormalizer.words(segment.text).count
            tally.total += words
            let coverage = DiarizationMetrics.coverage(start: segment.start, end: segment.end, hypothesis: referenceSegments)
            if let truth = coverage.max(by: { $0.value < $1.value })?.key,
               mapping[record.speakerID(for: segment)] == truth {
                tally.hits += words
            }
        }
        return tally
    }

    /// Seconds to add to reference times so they match the recording: the lag with the
    /// highest correlation of 100 ms energy envelopes, searched up to three minutes.
    static func alignmentOffset(reference: URL, recording: URL) throws -> Double {
        let frame = 1_600
        func envelope(_ url: URL) throws -> [Double] {
            let samples = try AudioLoader.samples(from: url)
            var values = stride(from: 0, to: samples.count, by: frame).map { start -> Double in
                Double(SpeechRendering.rms(samples[start..<min(samples.count, start + frame)]))
            }
            let mean = values.reduce(0, +) / Double(max(values.count, 1))
            let deviation = (values.reduce(0) { $0 + ($1 - mean) * ($1 - mean) } / Double(max(values.count, 1))).squareRoot()
            values = values.map { deviation > 0 ? ($0 - mean) / deviation : 0 }
            return values
        }
        let source = try envelope(reference)
        let target = try envelope(recording)
        let maximumLag = min(1_800, max(source.count, target.count))
        var bestLag = 0
        var bestScore = -Double.infinity
        for lag in -maximumLag...maximumLag {
            var sum = 0.0
            var count = 0
            for index in source.indices {
                let other = index + lag
                guard other >= 0, other < target.count else { continue }
                sum += source[index] * target[other]
                count += 1
            }
            // Very short overlaps correlate by chance.
            guard count >= min(source.count, target.count) / 2, count > 0 else { continue }
            let score = sum / Double(count)
            if score > bestScore {
                bestScore = score
                bestLag = lag
            }
        }
        return Double(bestLag * frame) / SpeechRendering.sampleRate
    }

    // MARK: - Speed

    /// The microphone track repeated to at least `minutes`, with the references shifted
    /// along, for the one-hour speed and memory measurement.
    private static func lengthened(_ evaluation: EvaluationCase, minutes: Double) throws -> EvaluationCase {
        guard let microphone = evaluation.tracks.first(where: { $0.name == "microphone" }), microphone.duration > 0 else {
            throw BenchError("В случае нет дорожки микрофона")
        }
        let repeats = Int((minutes * 60 / microphone.duration).rounded(.up))
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("slovo-bench-\(evaluation.name)-\(Int(minutes))min-\(UUID().uuidString).caf")
        let source = try AVAudioFile(forReading: microphone.url)
        let writer = try AVAudioFile(forWriting: url, settings: source.fileFormat.settings, commonFormat: .pcmFormatFloat32,
                                     interleaved: false)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: source.processingFormat, frameCapacity: 160_000) else {
            throw BenchError("Не удалось подготовить длинную дорожку")
        }
        var references: [ReferenceTurn] = []
        let base = evaluation.references.filter { $0.track == "microphone" }
        for index in 0..<repeats {
            let reader = try AVAudioFile(forReading: microphone.url)
            while reader.framePosition < reader.length {
                try reader.read(into: buffer)
                guard buffer.frameLength > 0 else { break }
                try writer.write(from: buffer)
            }
            let offset = Double(index) * microphone.duration
            references += base.map {
                ReferenceTurn(speaker: $0.speaker, track: $0.track, start: $0.start + offset, end: $0.end + offset, text: $0.text)
            }
        }
        let track = CaseTrack(name: "microphone", fileID: "mic", url: url, duration: Double(repeats) * microphone.duration)
        return EvaluationCase(name: "\(evaluation.name)-\(Int(minutes))min", scenario: evaluation.scenario,
                              condition: evaluation.condition, me: nil, output: evaluation.output, tracks: [track],
                              references: references, voices: evaluation.voices)
    }

    // MARK: - Reports

    private static func appendReport(title: String, results: [DiarizationCaseResult], fake: Bool, notes: [String],
                                     paths: BenchPaths) throws {
        guard let first = results.first else { return }
        var text = "\n## \(title) · \(ISO8601DateFormatter().string(from: first.date)) · \(first.diarizer)\n\n"
        if fake {
            text += "**Диаризатор подменён (fake-oracle): таблица проверяет стенд, а не модель.**\n\n"
        }
        text += "Условие: \(first.condition). Воротник: \(first.collar) с. \(DiarizationReport.scoringRules)\n\n"
        for note in notes {
            text += note + ".\n\n"
        }
        text += DiarizationReport.caseTable(results) + "\n"
        try append(text, paths: paths)
        print("  отчёт дописан: \(DiarizationCase.reportURL(paths).path)")
    }

    private static func summarize(paths: BenchPaths) throws {
        var all: [DiarizationCaseResult] = []
        for root in [DiarizationCase.outputFolder(paths), DiarizationCase.meetingResultsFolder(paths)] {
            let folders = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
            for folder in folders.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
                let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
                for file in files where file.lastPathComponent.hasPrefix("result-") && file.pathExtension == "json" {
                    all.append(try DiarizationCaseResult.decode(Data(contentsOf: file)))
                }
            }
        }
        guard !all.isEmpty else {
            print("Прогонов нет. Сначала: swift run slovo-bench diarize --case <папка случая>")
            return
        }
        // Fake runs check the harness; they must not rank against the model.
        let real = all.filter { $0.diarizer != OracleDiarizer.label }
        let results = real.isEmpty ? all : real
        let ranking = DiarizationReport.rank(results.filter { $0.audioSeconds < 3_500 })
        var text = "\n## Сводка · \(ISO8601DateFormatter().string(from: Date()))\n\n"
        if real.isEmpty {
            text += "**Все прогоны сделаны с подменённым диаризатором: сводка проверяет стенд, а не модель.**\n\n"
        }
        text += DiarizationReport.scoringRules + "\n\n" + DiarizationReport.rankingTable(ranking) + "\n"
        var configurations = ranking.first.map { [$0.configuration] } ?? []
        if !configurations.contains(DiarizationTuning.standard.description) {
            configurations.append(DiarizationTuning.standard.description)
        }
        for configuration in configurations {
            text += "\n### Пороги выпуска: \(configuration)\n\n"
                + DiarizationReport.gateTable(DiarizationReport.gate(configuration: configuration, results: results)) + "\n"
        }
        try append(text, paths: paths)
        print(text)
    }

    private static func append(_ text: String, paths: BenchPaths) throws {
        try FileManager.default.createDirectory(at: paths.results, withIntermediateDirectories: true)
        let url = DiarizationCase.reportURL(paths)
        if !FileManager.default.fileExists(atPath: url.path) {
            try Data("# Разделение по спикерам\n".utf8).write(to: url)
        }
        let handle = try FileHandle(forWritingTo: url)
        defer { try? handle.close() }
        try handle.seekToEnd()
        try handle.write(contentsOf: Data(text.utf8))
    }

    private static func line(_ result: DiarizationCaseResult) -> String {
        let longTurns = result.attribution.threeSecondsOrLonger
        var parts = [
            "  \(result.configuration) · \(result.hint): DER \(percent(result.der.der))",
            "без воротника \(percent(result.derStrict.der))",
            "спикеров \(result.speakers.detected)/\(result.speakers.reference)",
            "реплики ≥ 3 с \(longTurns.hits)/\(longTurns.total)"
        ]
        if let transcription = result.transcription {
            parts.append("WER по спикерам \(percent(transcription.attributedWER.rate)), общий \(percent(transcription.plainWER.rate))")
        }
        // A stand-in diarizer takes no time worth reporting.
        if let speed = result.realTimeMultiple, result.diarizer != OracleDiarizer.label {
            parts.append(String(format: "%.0f× реального времени", speed))
        }
        return parts.joined(separator: ", ")
    }

    private static func percent(_ value: Double) -> String {
        String(format: "%.1f%%", value * 100)
    }

    static func duration(of url: URL) throws -> Double {
        let file = try AVAudioFile(forReading: url)
        return Double(file.length) / file.fileFormat.sampleRate
    }
}

/// A diarizer the bench can score: the real model or a stand-in for testing the bench.
protocol BenchDiarizer: Sendable {
    var name: String { get }
    func diagnose(track: DiarizeCommand.CaseTrack, tuning: DiarizationTuning,
                  hint: SpeakerCountHint) async throws -> DiarizationDiagnosis
}

final class ModelDiarizer: BenchDiarizer {
    let name = DiarizationService.modelIdentifier
    private let diarizer: FluidSpeakerDiarizer

    init(diarizer: FluidSpeakerDiarizer) {
        self.diarizer = diarizer
    }

    func diagnose(track: DiarizeCommand.CaseTrack, tuning: DiarizationTuning,
                  hint: SpeakerCountHint) async throws -> DiarizationDiagnosis {
        try await diarizer.diagnose(fileAt: track.url, config: tuning, hint: hint)
    }
}

/// Turns made from the reference with seeded boundary jitter, sub-0.6 s turns given to
/// the speaker before them, and one-hot centroids per voice. It exercises every part of
/// the bench without the model and says nothing about diarization quality.
final class OracleDiarizer: BenchDiarizer, SpeakerDiarizing {
    static let label = "fake-oracle"
    let name = OracleDiarizer.label
    private let references: [ReferenceTurn]
    private let voices: [String]
    private let seed: UInt64

    init(references: [ReferenceTurn], voices: [String], seed: UInt64) {
        self.references = references
        self.voices = voices
        self.seed = seed
    }

    func diagnose(track: DiarizeCommand.CaseTrack, tuning: DiarizationTuning,
                  hint: SpeakerCountHint) async throws -> DiarizationDiagnosis {
        let started = ProcessInfo.processInfo.systemUptime
        let (turns, speakers) = makeTurns(track: track.name)
        var centroids: [Int: [Float]] = [:]
        for (cluster, speaker) in speakers {
            var vector = [Float](repeating: 0, count: max(1, voices.count))
            if let index = voices.firstIndex(of: speaker) {
                vector[index] = 1
            }
            centroids[cluster] = vector
        }
        return DiarizationDiagnosis(turns: turns, centroids: centroids,
                                    timings: ["wall": ProcessInfo.processInfo.systemUptime - started])
    }

    /// The pipeline names its track files `mic.caf` and `system.caf`.
    func diarize(fileAt url: URL, hint: SpeakerCountHint,
                 progress: @escaping @Sendable (Double) -> Void) async throws -> [SpeakerTurn] {
        progress(1)
        return makeTurns(track: url.lastPathComponent.hasPrefix("system") ? "system" : "microphone").turns
    }

    private func makeTurns(track: String) -> (turns: [SpeakerTurn], speakers: [Int: String]) {
        var generator = SeededGenerator(seed: seed)
        var clusters: [String: Int] = [:]
        var turns: [SpeakerTurn] = []
        for reference in references.filter({ $0.track == track }).sorted(by: { $0.start < $1.start }) {
            let startJitter = Double.random(in: -0.15...0.15, using: &generator)
            let endJitter = Double.random(in: -0.15...0.15, using: &generator)
            let cluster: Int
            if reference.duration < 0.6, let previous = turns.last {
                cluster = previous.cluster
            } else if let known = clusters[reference.speaker] {
                cluster = known
            } else {
                cluster = clusters.count
                clusters[reference.speaker] = cluster
            }
            let start = max(0, reference.start + startJitter)
            turns.append(SpeakerTurn(start: start, end: max(start + 0.05, reference.end + endJitter), cluster: cluster))
        }
        return (turns, Dictionary(uniqueKeysWithValues: clusters.map { ($1, $0) }))
    }
}

/// Hands the oracle to the meeting pipeline for `--transcribe` without the model.
@MainActor
final class OracleProvider: DiarizationProviding {
    private let oracle: OracleDiarizer

    init(oracle: OracleDiarizer) {
        self.oracle = oracle
    }

    var modelStatus: ModelStatus { .ready(diskBytes: 0) }
    var isSupportedOnThisSystem: Bool { true }

    func diarizer() async throws -> any SpeakerDiarizing {
        oracle
    }

    func release() {}

    func waitForLoad() async {}
}
