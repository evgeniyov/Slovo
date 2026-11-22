import BenchKit
import Foundation
import SlovoCore

enum RunCommand {
    static func run(paths: BenchPaths, engineArgument: String, passes: Int, isChild: Bool) async throws {
        guard !(try Clip.list(in: paths.clips)).isEmpty else {
            print("В \(paths.clips.path) нет записей. Сначала: swift run slovo-bench record")
            return
        }
        if engineArgument == "all" {
            try runEachInOwnProcess(paths: paths, passes: passes)
        } else {
            try await runEngine(id: engineArgument, paths: paths, passes: passes)
        }
        if !isChild {
            print("\nДальше: swift run slovo-bench score")
        }
    }

    /// A fresh process per engine keeps memory and load figures from mixing
    /// with models the previous engine left loaded.
    private static func runEachInOwnProcess(paths: BenchPaths, passes: Int) throws {
        guard let executable = Bundle.main.executableURL else {
            throw BenchError("Не удалось найти исполняемый файл стенда")
        }
        for entry in EngineCatalog.entries {
            let process = Process()
            process.executableURL = executable
            process.arguments = ["run", "--engine", entry.id, "--bench", paths.root.path,
                                 "--repeat", String(passes), "--child"]
            try process.run()
            process.waitUntilExit()
            if process.terminationStatus != 0 {
                print("  \(entry.id): прогон завершился с ошибкой (код \(process.terminationStatus))")
            }
        }
    }

    private static func runEngine(id: String, paths: BenchPaths, passes: Int) async throws {
        let clips = try Clip.list(in: paths.clips)
        guard let firstClip = clips.first else { return }
        let engine = try EngineCatalog.make(id, paths: paths)
        let clock = ContinuousClock()
        print("\n== \(id): записей \(clips.count), проходов \(passes)")

        // The idle baseline is taken before any model is loaded.
        let sampler = SystemSampler.start()
        if let sampler {
            sampler.setPhase(.idle)
            try await Task.sleep(for: .seconds(5))
            sampler.setPhase(.other)
        } else {
            print("  macmon не найден: нагрузка на систему не измеряется")
        }

        let preparation = try await clock.measure { try await engine.prepare() }
        let firstTranscription = await clock.measure { _ = try? await engine.transcribe(fileURL: firstClip.audio) }
        print("  загрузка модели \(format(preparation.seconds)) с, первая фраза \(format(firstTranscription.seconds)) с")

        var texts: [String: String] = [:]
        var failures: [String: String] = [:]
        var times: [String: [Double]] = [:]

        // Apple's service starts on demand, so it is looked up after the first transcription.
        let helpersBefore = HelperProcesses.snapshot()
        sampler?.setPhase(.load)
        let cpuBefore = ProcessMetrics.cpuSeconds()
        let passesStart = clock.now
        for pass in 1...passes {
            for clip in clips {
                var text = ""
                var failure: String?
                let elapsed = await clock.measure {
                    do {
                        text = try await engine.transcribe(fileURL: clip.audio)
                    } catch {
                        failure = error.localizedDescription
                    }
                }
                times[clip.id, default: []].append(elapsed.seconds)
                guard pass == 1 else { continue }
                texts[clip.id] = text
                failures[clip.id] = failure
                print("  \(clip.id)  \(format(elapsed.seconds)) с  \(failure.map { "ошибка: \($0)" } ?? text)")
            }
        }

        var usage = ResourceUsage()
        usage.measuredWallSeconds = passesStart.duration(to: clock.now).seconds
        usage.processCPUSeconds = ProcessMetrics.cpuSeconds() - cpuBefore
        usage.system = sampler?.stop()
        let helpersAfter = HelperProcesses.snapshot()
        if helpersAfter.processes > 0 {
            // A relaunched service restarts its counters, so a negative difference means "unknown".
            let helperCPU = helpersAfter.cpuSeconds - helpersBefore.cpuSeconds
            usage.helperCPUSeconds = helpersBefore.processes > 0 && helperCPU >= 0 ? helperCPU : nil
            usage.helperFootprintMB = helpersAfter.footprintMB
            usage.helperResidentMB = helpersAfter.residentMB
        }
        usage.passes = passes
        usage.prepareSeconds = preparation.seconds
        usage.firstTranscriptionSeconds = firstTranscription.seconds
        usage.peakFootprintMB = ProcessMetrics.peakFootprintMB()
        usage.peakResidentMB = ProcessMetrics.peakResidentMB()
        usage.modelSizeMB = EngineCatalog.modelDirectory(id).map(directorySizeMB)

        var results: [ClipResult] = []
        for clip in clips {
            results.append(ClipResult(clip: clip.id, text: texts[clip.id] ?? "", audioSeconds: try clip.durationSeconds(),
                                      processingSeconds: Report.median((times[clip.id] ?? []).sorted()),
                                      error: failures[clip.id]))
        }
        let cores = usage.processCPUSeconds / max(usage.measuredWallSeconds, 1e-9)
        print("  CPU процесса \(format(cores)) ядра, пик памяти процесса \(Int(usage.peakFootprintMB)) МБ, RSS \(Int(usage.peakResidentMB)) МБ")
        if let helperCPU = usage.helperCPUSeconds, let footprint = usage.helperFootprintMB, let resident = usage.helperResidentMB {
            let helperCores = helperCPU / max(usage.measuredWallSeconds, 1e-9)
            print("  системная служба распознавания: CPU \(format(helperCores)) ядра, память \(Int(footprint)) МБ, RSS \(Int(resident)) МБ")
        }

        let run = EngineRun(engine: id, date: Date(), system: ProcessInfo.processInfo.operatingSystemVersionString,
                            clips: results, resources: usage)
        try FileManager.default.createDirectory(at: paths.results, withIntermediateDirectories: true)
        let url = paths.results.appendingPathComponent("\(id).json")
        try run.encoded().write(to: url)
        print("  сохранено: \(url.path)")
    }

    private static func format(_ value: Double) -> String {
        String(format: "%.2f", value)
    }
}
