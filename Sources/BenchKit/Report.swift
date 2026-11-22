import Foundation

public struct EngineSummary: Sendable {
    public let engine: String
    public let clipsScored: Int
    public let words: ErrorCounts
    public let characters: ErrorCounts
    public let latinKept: Int
    public let latinTotal: Int
    public let punctuatedClips: Int
    public let medianSeconds: Double
    public let longestSeconds: Double
    public let realTimeFactor: Double
    public let failedClips: Int
    /// Audio in one pass over the successfully transcribed clips.
    public let audioSeconds: Double
    public let resources: ResourceUsage?

    /// Average number of CPU cores the engine's own process kept busy.
    public var processCPUCores: Double? {
        guard let usage = resources, usage.measuredWallSeconds > 0 else { return nil }
        return usage.processCPUSeconds / usage.measuredWallSeconds
    }

    /// Average number of CPU cores used by out-of-process speech services.
    public var helperCPUCores: Double? {
        guard let usage = resources, let seconds = usage.helperCPUSeconds, usage.measuredWallSeconds > 0 else { return nil }
        return seconds / usage.measuredWallSeconds
    }

    /// Whole-system power above the idle baseline while transcribing.
    public var extraPowerW: Double? {
        guard let load = resources?.system else { return nil }
        return max(0, load.loadPowerW - load.idlePowerW)
    }

    /// Extra energy spent per minute of recorded speech.
    public var energyPerAudioMinuteJ: Double? {
        guard let usage = resources, let power = extraPowerW, audioSeconds > 0 else { return nil }
        return power * usage.measuredWallSeconds / (audioSeconds * Double(usage.passes)) * 60
    }

    /// Growth of memory in use across the whole system, which also covers work
    /// done outside the process, such as Apple's speech service.
    public var systemRAMGrowthMB: Double? {
        guard let load = resources?.system else { return nil }
        return max(0, load.peakRAMUsedMB - load.idleRAMUsedMB)
    }
}

public enum Report {
    /// Pools errors over all clips that have a reference. A failed clip counts
    /// as an empty transcript, so an engine cannot look better by crashing.
    public static func summarize(_ run: EngineRun, references: [String: String]) -> EngineSummary {
        var words = ErrorCounts(errors: 0, referenceLength: 0)
        var characters = ErrorCounts(errors: 0, referenceLength: 0)
        var latinKept = 0
        var latinTotal = 0
        var punctuated = 0
        var scored = 0

        for clip in run.clips {
            guard let reference = references[clip.clip] else { continue }
            scored += 1
            words = words + ErrorRate.words(reference: reference, hypothesis: clip.text)
            characters = characters + ErrorRate.characters(reference: reference, hypothesis: clip.text)
            let latin = ErrorRate.latinTerms(reference: reference, hypothesis: clip.text)
            latinKept += latin.kept
            latinTotal += latin.total
            if clip.text.contains(where: { ".,!?;:".contains($0) }) {
                punctuated += 1
            }
        }

        let succeeded = run.clips.filter { $0.error == nil }
        let times = succeeded.map(\.processingSeconds).sorted()
        let audioSeconds = succeeded.reduce(0) { $0 + $1.audioSeconds }

        return EngineSummary(
            engine: run.engine,
            clipsScored: scored,
            words: words,
            characters: characters,
            latinKept: latinKept,
            latinTotal: latinTotal,
            punctuatedClips: punctuated,
            medianSeconds: median(times),
            longestSeconds: times.last ?? 0,
            realTimeFactor: audioSeconds > 0 ? times.reduce(0, +) / audioSeconds : 0,
            failedClips: run.clips.count - succeeded.count,
            audioSeconds: audioSeconds,
            resources: run.resources
        )
    }

    public static func accuracyTable(_ summaries: [EngineSummary]) -> String {
        table(
            header: ["Движок", "WER", "CER", "Англ. термины латиницей", "С пунктуацией", "Сбоев"],
            rows: summaries.map { s in
                [
                    s.engine,
                    percent(s.words.rate),
                    percent(s.characters.rate),
                    "\(s.latinKept)/\(s.latinTotal)",
                    "\(s.punctuatedClips)/\(s.clipsScored)",
                    "\(s.failedClips)"
                ]
            }
        )
    }

    public static func speedTable(_ summaries: [EngineSummary]) -> String {
        table(
            header: ["Движок", "Загрузка модели, с", "Первая фраза, с", "Медиана на фразу, с", "Самая долгая фраза, с", "RTF"],
            rows: summaries.map { s in
                [
                    s.engine,
                    number(s.resources?.prepareSeconds, digits: 2),
                    number(s.resources?.firstTranscriptionSeconds, digits: 2),
                    number(s.medianSeconds, digits: 2),
                    number(s.longestSeconds, digits: 2),
                    number(s.realTimeFactor, digits: 3)
                ]
            }
        )
    }

    public static func loadTable(_ summaries: [EngineSummary]) -> String {
        table(
            header: ["Движок", "CPU процесса, ядер", "CPU служб, ядер", "P-ядра, %", "E-ядра, %", "GPU, %", "ANE, Вт",
                     "Доп. мощность, Вт", "Энергия на минуту речи, Дж", "Пик памяти процесса, МБ",
                     "Память служб, МБ", "Рост памяти системы, МБ", "Модель на диске, МБ"],
            // Memory cells read "footprint / resident".
            rows: summaries.map { s in
                let load = s.resources?.system
                return [
                    s.engine,
                    number(s.processCPUCores, digits: 1),
                    number(s.helperCPUCores, digits: 1),
                    usage(load?.loadPCoreUsage, idle: load?.idlePCoreUsage),
                    usage(load?.loadECoreUsage, idle: load?.idleECoreUsage),
                    usage(load?.loadGPUUsage, idle: load?.idleGPUUsage),
                    number(load?.loadANEPowerW, digits: 2),
                    number(s.extraPowerW, digits: 1),
                    number(s.energyPerAudioMinuteJ, digits: 0),
                    memory(s.resources?.peakFootprintMB, resident: s.resources?.peakResidentMB),
                    memory(s.resources?.helperFootprintMB, resident: s.resources?.helperResidentMB),
                    number(s.systemRAMGrowthMB, digits: 0),
                    number(s.resources?.modelSizeMB, digits: 0)
                ]
            }
        )
    }

    public static let legend = """
        **Точность**
        - **WER** — доля ошибок по словам (замены, пропуски, лишние слова) по всем записям вместе. Регистр, пунктуация и ё/е не учитываются.
        - **CER** — то же по буквам.
        - **Англ. термины латиницей** — сколько английских слов из эталона движок написал латиницей.
        - **С пунктуацией** — в скольких расшифровках есть знаки препинания.
        - **Сбоев** — записи, на которых движок выдал ошибку.

        **Скорость**
        - **Загрузка модели** — чтение модели с диска и подготовка. В приложении это происходит один раз при запуске.
        - **Первая фраза** — первая расшифровка после загрузки: столько подождёт первая диктовка.
        - **Медиана на фразу** — типичная задержка после того, как отпустили клавишу (медиана по нескольким проходам).
        - **RTF** — время обработки, делённое на длительность аудио: 0,05 значит в 20 раз быстрее реального времени.

        **Нагрузка**
        - **CPU процесса, ядер** — сколько ядер в среднем занимал сам стенд во время расшифровки. Работа нейроядра (ANE) и системных служб сюда не входит.
        - **CPU служб, Память служб** — то же для системной службы, которая распознаёт речь вне процесса: у движков Apple это localspeechrecognition. У остальных движков прочерк.
        - **P-ядра, E-ядра, GPU** — загрузка по всей системе во время расшифровки, в скобках — фон до загрузки модели.
        - **ANE, Вт** — мощность нейроядра по данным macmon.
        - **Доп. мощность** — насколько выросло потребление всего Mac по сравнению с фоном.
        - **Энергия на минуту речи** — дополнительная энергия на минуту записанного звука: чем меньше, тем меньше разряд батареи.
        - **Пик памяти процесса** — два числа через дробь. Первое — максимум «Памяти» стенда, как в Мониторе системы. Второе — резидентная память (RSS): она включает файлы модели, отображённые с диска, которые система может выгрузить при нехватке памяти. Реальная нагрузка на память — между этими числами.
        - **Рост памяти системы** — насколько выросла занятая память всего Mac. Учитывает системные службы, что важно для движка Apple, но шумит от других программ.

        Системные показатели зависят от фоновых программ: сравнивайте порядок величин, а не десятые доли.
        """

    /// Every clip with its reference and each engine's transcript, for reading the mistakes.
    public static func details(runs: [EngineRun], references: [String: String]) -> String {
        let clipIDs = Set(runs.flatMap { $0.clips.map(\.clip) }).sorted()
        var lines: [String] = []
        for id in clipIDs {
            lines.append("### \(id)")
            lines.append(references[id].map { "Эталон: \($0)" } ?? "Эталона нет")
            lines.append("")
            for run in runs {
                guard let clip = run.clips.first(where: { $0.clip == id }) else { continue }
                var line = "- **\(run.engine)**"
                if let reference = references[id] {
                    line += " (WER \(percent(ErrorRate.words(reference: reference, hypothesis: clip.text).rate)))"
                }
                line += ": " + (clip.error.map { "ошибка: \($0)" } ?? clip.text)
                lines.append(line)
            }
            lines.append("")
        }
        return lines.joined(separator: "\n")
    }

    public static func median(_ sorted: [Double]) -> Double {
        guard !sorted.isEmpty else { return 0 }
        let middle = sorted.count / 2
        return sorted.count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2
    }

    static func percent(_ value: Double) -> String {
        String(format: "%.1f%%", value * 100)
    }

    private static func number(_ value: Double?, digits: Int) -> String {
        value.map { String(format: "%.\(digits)f", $0) } ?? "—"
    }

    private static func memory(_ footprint: Double?, resident: Double?) -> String {
        guard let footprint else { return "—" }
        let value = String(format: "%.0f", footprint)
        return resident.map { value + String(format: " / %.0f", $0) } ?? value
    }

    private static func usage(_ load: Double?, idle: Double?) -> String {
        guard let load else { return "—" }
        let busy = String(format: "%.0f", load * 100)
        return idle.map { busy + String(format: " (фон %.0f)", $0 * 100) } ?? busy
    }

    private static func table(header: [String], rows: [[String]]) -> String {
        var lines = [
            "| " + header.joined(separator: " | ") + " |",
            "|---|" + String(repeating: "---:|", count: header.count - 1)
        ]
        lines += rows.map { "| " + $0.joined(separator: " | ") + " |" }
        return lines.joined(separator: "\n")
    }
}
