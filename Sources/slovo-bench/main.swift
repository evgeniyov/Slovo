import Foundation

setvbuf(stdout, nil, _IONBF, 0)

let usage = """
    Использование: slovo-bench <команда> [параметры]

    Команды:
      record               записать фразы из Bench/phrases.txt
      record --free        записать свободную речь (эталон впишете потом)
      run --engine <id>    расшифровать все записи движком; all — всеми по очереди
      score                посчитать точность, скорость и нагрузку, сохранить Bench/results/report.md
      score --postprocess  прогнать сохранённые расшифровки через словарь Slovo (Bench/vocabulary.txt)
                           и сравнить точность до и после; ничего не записывает
      engines              список движков
      diarize-synth --scenario <файл.json> --condition <условие> [--seed n] [--out <папка>] [--substitute-voices]
                           озвучить сценарий встречи из Bench/diarization/scenarios в Bench/diarization/out/<сценарий>-<условие>;
                           условия: clean, far, far-noisy-20, far-noisy-10, call-headphones, call-speakers
                           (rerecorded-standard и rerecorded-isolation записываются вручную)
      diarize --case <папка> [--collar 0.25] [--speakers n] [--config step=0.1,minSeg=0,zeroVote=on,threshold=0.775]
              [--sweep] [--transcribe <движок>] [--models <папка>] [--long-minutes 60]
                           разделить случай по спикерам и оценить: DER, число спикеров, реплики по длине;
                           модель спикеров не скачивает, без неё останавливается
      diarize --meeting <папка встречи> (--truth truth.csv [--truth-system system.csv] | --case <папка>) [--condition имя]
                           оценить настоящую запись Slovo; файлы встречи только читаются
      diarize --summary    сравнить все прогоны, отсортировать конфигурации и проверить пороги выпуска
      diarize ... --fake-diarizer oracle
                           проверить сам стенд без модели: реплики берутся из эталона

    Параметры:
      --bench <папка>      папка стенда, по умолчанию ./Bench
      --repeat <n>         сколько раз прогнать каждую запись для замера времени, по умолчанию 3

    Переменные окружения:
      SLOVO_BENCH_WHISPER_MODEL  файл ggml-large-v3-turbo-q5_0.bin для whisper-large-v3-turbo и hybrid-whisper;
                                 по умолчанию из папки моделей Slovo (whisper-v3-turbo)
    """

var arguments = Array(CommandLine.arguments.dropFirst())
let paths = BenchPaths(root: URL(fileURLWithPath: Options.take("--bench", from: &arguments) ?? "Bench", isDirectory: true))
let passes = max(1, Int(Options.take("--repeat", from: &arguments) ?? "") ?? 3)

do {
    switch arguments.first {
    case "record":
        try await RecordCommand.run(paths: paths, freeSpeech: arguments.contains("--free"))
    case "run":
        guard let engine = Options.take("--engine", from: &arguments) else {
            print(usage)
            exit(2)
        }
        try await RunCommand.run(paths: paths, engineArgument: engine, passes: passes,
                                 isChild: arguments.contains("--child"))
    case "score":
        if arguments.contains("--postprocess") {
            try PostprocessCommand.run(paths: paths)
        } else {
            try ScoreCommand.run(paths: paths)
        }
    case "diarize-synth":
        try await DiarizeSynthCommand.run(paths: paths, arguments: Array(arguments.dropFirst()))
    case "diarize":
        try await DiarizeCommand.run(paths: paths, arguments: Array(arguments.dropFirst()))
    case "transcribe":
        let engineID = Options.take("--engine", from: &arguments) ?? "gigaam-v3-ctc"
        guard let file = arguments.dropFirst().first else {
            print("Использование: slovo-bench transcribe <файл.wav> [--engine gigaam-v3-ctc]")
            exit(2)
        }
        let engine = try EngineCatalog.make(engineID, paths: paths)
        try await engine.prepare()
        let text = try await engine.transcribe(fileURL: URL(fileURLWithPath: file))
        print(text)
    case "engines":
        for entry in EngineCatalog.entries {
            print("\(entry.id.padding(toLength: 24, withPad: " ", startingAt: 0)) \(entry.summary)")
        }
    default:
        print(usage)
    }
} catch {
    FileHandle.standardError.write(Data("Ошибка: \(error.localizedDescription)\n".utf8))
    exit(1)
}
