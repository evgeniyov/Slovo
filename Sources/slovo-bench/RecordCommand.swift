import AVFoundation
import Foundation

enum RecordCommand {
    static func run(paths: BenchPaths, freeSpeech: Bool) async throws {
        guard await AVCaptureDevice.requestAccess(for: .audio) else {
            print("""
                Нет доступа к микрофону. Разрешите его приложению, в котором открыт терминал: \
                Системные настройки → Конфиденциальность и безопасность → Микрофон.
                """)
            return
        }
        try FileManager.default.createDirectory(at: paths.clips, withIntermediateDirectories: true)
        if freeSpeech {
            try recordFreeSpeech(paths)
        } else {
            try recordPhrases(paths)
        }
    }

    private static func recordPhrases(_ paths: BenchPaths) throws {
        let phrases = try String(contentsOf: paths.phrases, encoding: .utf8)
            .split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && !$0.hasPrefix("#") }
        let pending = phrases.indices.filter { !exists(paths.audio(phraseID($0))) }

        print("Фраз: \(phrases.count), осталось записать: \(pending.count).")
        print("Читайте так, как обычно говорите, а не как диктор. После Enter подождите полсекунды и начинайте.")

        for index in pending {
            let id = phraseID(index)
            print("\n[\(index + 1)/\(phrases.count)] \(phrases[index])")
            while true {
                guard let start = Console.ask("  Enter — запись, s — пропустить, q — выйти: "), start != "q" else { return }
                if start == "s" { break }
                let seconds = try recordUntilEnter(to: paths.audio(id))
                try phrases[index].write(to: paths.reference(id), atomically: true, encoding: .utf8)
                let prompt = "  Записано \(format(seconds)) с. Enter — дальше, r — перезаписать, q — выйти: "
                guard let next = Console.ask(prompt), next != "q" else { return }
                if next != "r" { break }
            }
        }
        print("\nВсе фразы записаны. Дальше: swift run slovo-bench run --engine all")
    }

    private static func recordFreeSpeech(_ paths: BenchPaths) throws {
        print("Свободная речь: говорите о рабочих делах, как в чате или на созвоне, по 10–30 секунд.")
        print("Эталон потом впишите в .txt с тем же именем: проще всего взять лучший вариант из отчёта и поправить.")

        var number = 1
        while true {
            while exists(paths.audio(String(format: "f%03d", number))) {
                number += 1
            }
            let id = String(format: "f%03d", number)
            guard let start = Console.ask("\n\(id): Enter — запись, q — выйти: "), start != "q" else { return }
            while true {
                let seconds = try recordUntilEnter(to: paths.audio(id))
                if !exists(paths.reference(id)) {
                    try "".write(to: paths.reference(id), atomically: true, encoding: .utf8)
                }
                let prompt = "  Записано \(format(seconds)) с. Enter — следующая, r — перезаписать, q — выйти: "
                guard let next = Console.ask(prompt), next != "q" else { return }
                if next != "r" { break }
            }
        }
    }

    /// Records 16 kHz mono 16-bit PCM, the input format every candidate engine expects.
    private static func recordUntilEnter(to url: URL) throws -> TimeInterval {
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: 16_000.0,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false
        ]
        let recorder = try AVAudioRecorder(url: url, settings: settings)
        guard recorder.prepareToRecord(), recorder.record() else {
            throw BenchError("Не удалось начать запись. Проверьте микрофон: Системные настройки → Звук → Вход.")
        }
        print("  ● Идёт запись… Enter — стоп", terminator: "")
        _ = readLine()
        let seconds = recorder.currentTime
        recorder.stop()
        return seconds
    }

    private static func phraseID(_ index: Int) -> String {
        String(format: "r%03d", index + 1)
    }

    private static func exists(_ url: URL) -> Bool {
        FileManager.default.fileExists(atPath: url.path)
    }

    private static func format(_ seconds: TimeInterval) -> String {
        String(format: "%.1f", seconds)
    }
}
