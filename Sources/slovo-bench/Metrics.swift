import BenchKit
import Darwin
import Foundation

enum ProcessMetrics {
    /// CPU time used so far by every thread of this process, user plus system.
    static func cpuSeconds() -> Double {
        var usage = rusage()
        guard getrusage(RUSAGE_SELF, &usage) == 0 else { return 0 }
        return seconds(usage.ru_utime) + seconds(usage.ru_stime)
    }

    /// Lifetime peak of the physical footprint, the figure Activity Monitor shows as Memory.
    static func peakFootprintMB() -> Double {
        vmInfo().map { Double($0.ledger_phys_footprint_peak) / 1_048_576 } ?? 0
    }

    /// Lifetime peak of resident memory, including pages mapped from files.
    static func peakResidentMB() -> Double {
        vmInfo().map { Double($0.resident_size_peak) / 1_048_576 } ?? 0
    }

    private static func vmInfo() -> task_vm_info_data_t? {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        return result == KERN_SUCCESS ? info : nil
    }

    private static func seconds(_ value: timeval) -> Double {
        Double(value.tv_sec) + Double(value.tv_usec) / 1_000_000
    }
}

/// System services that do the transcription outside the bench process.
enum HelperProcesses {
    static let executableNames: Set<String> = ["localspeechrecognition"]

    struct Snapshot {
        var processes = 0
        var cpuSeconds = 0.0
        var footprintMB = 0.0
        var residentMB = 0.0
    }

    static func snapshot() -> Snapshot {
        var snapshot = Snapshot()
        let estimated = proc_listallpids(nil, 0)
        guard estimated > 0 else { return snapshot }
        var pids = [pid_t](repeating: 0, count: Int(estimated) + 64)
        let listed = pids.withUnsafeMutableBytes { proc_listallpids($0.baseAddress, Int32($0.count)) }
        guard listed > 0 else { return snapshot }

        var timebase = mach_timebase_info_data_t()
        mach_timebase_info(&timebase)
        var path = [CChar](repeating: 0, count: Int(MAXPATHLEN) * 4)

        for pid in pids.prefix(Int(listed)) where pid > 0 {
            guard proc_pidpath(pid, &path, UInt32(path.count)) > 0,
                  executableNames.contains(URL(fileURLWithPath: String(cString: path)).lastPathComponent) else { continue }
            var info = rusage_info_v4()
            let status = withUnsafeMutablePointer(to: &info) { pointer in
                pointer.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) {
                    proc_pid_rusage(pid, RUSAGE_INFO_V4, $0)
                }
            }
            guard status == 0 else { continue }
            // rusage_info times are in Mach absolute time units.
            let ticks = Double(info.ri_user_time + info.ri_system_time)
            snapshot.processes += 1
            snapshot.cpuSeconds += ticks * Double(timebase.numer) / Double(timebase.denom) / 1e9
            snapshot.footprintMB += Double(info.ri_phys_footprint) / 1_048_576
            snapshot.residentMB += Double(info.ri_resident_size) / 1_048_576
        }
        return snapshot
    }
}

/// Allocated size of every regular file under a directory.
func directorySizeMB(_ url: URL) -> Double {
    let keys: Set<URLResourceKey> = [.totalFileAllocatedSizeKey, .isRegularFileKey]
    guard let enumerator = FileManager.default.enumerator(at: url, includingPropertiesForKeys: Array(keys)) else {
        return 0
    }
    var bytes = 0
    for case let file as URL in enumerator {
        guard let values = try? file.resourceValues(forKeys: keys), values.isRegularFile == true else { continue }
        bytes += values.totalFileAllocatedSize ?? 0
    }
    return Double(bytes) / 1_048_576
}

/// Reads whole-system power and utilization from `macmon pipe`, tagging each
/// sample with the phase of the run it belongs to.
final class SystemSampler: @unchecked Sendable {
    enum Phase {
        case idle
        case load
        case other
    }

    private struct Reading: Decodable {
        let systemPower: Double
        let anePower: Double
        let pCores: [Double]
        let eCores: [Double]
        let gpu: [Double]
        let memory: Memory

        struct Memory: Decodable {
            let ramUsage: Double

            enum CodingKeys: String, CodingKey {
                case ramUsage = "ram_usage"
            }
        }

        enum CodingKeys: String, CodingKey {
            case systemPower = "sys_power"
            case anePower = "ane_power"
            case pCores = "pcpu_usage"
            case eCores = "ecpu_usage"
            case gpu = "gpu_usage"
            case memory
        }

        // macmon reports utilization as [frequency in MHz, busy fraction].
        var pCoreUsage: Double { pCores.last ?? 0 }
        var eCoreUsage: Double { eCores.last ?? 0 }
        var gpuUsage: Double { gpu.last ?? 0 }
        var ramUsedMB: Double { memory.ramUsage / 1_048_576 }
    }

    private static let intervalMilliseconds = 250

    private let process = Process()
    private let output = Pipe()
    private let lock = NSLock()
    private var pending = Data()
    private var samples: [(phase: Phase, reading: Reading)] = []
    private var phase = Phase.other
    private var phaseStarted = ProcessInfo.processInfo.systemUptime

    static func start() -> SystemSampler? {
        let candidates = ["/opt/homebrew/bin/macmon", "/usr/local/bin/macmon"]
        guard let path = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else {
            return nil
        }
        let sampler = SystemSampler()
        sampler.process.executableURL = URL(fileURLWithPath: path)
        sampler.process.arguments = ["pipe", "-i", String(intervalMilliseconds)]
        sampler.process.standardOutput = sampler.output
        sampler.process.standardError = FileHandle.nullDevice
        sampler.output.fileHandleForReading.readabilityHandler = { [weak sampler] handle in
            sampler?.consume(handle.availableData)
        }
        do {
            try sampler.process.run()
        } catch {
            return nil
        }
        return sampler
    }

    deinit {
        terminate()
    }

    func setPhase(_ newPhase: Phase) {
        lock.lock()
        defer { lock.unlock() }
        phase = newPhase
        phaseStarted = ProcessInfo.processInfo.systemUptime
    }

    /// Stops macmon and averages the idle and load samples; nil if either phase got none.
    func stop() -> SystemLoad? {
        terminate()
        lock.lock()
        let collected = samples
        lock.unlock()

        let idle = collected.filter { $0.phase == .idle }.map(\.reading)
        let busy = collected.filter { $0.phase == .load }.map(\.reading)
        guard !idle.isEmpty, !busy.isEmpty else { return nil }

        var load = SystemLoad()
        load.idleSamples = idle.count
        load.loadSamples = busy.count
        load.idlePowerW = mean(idle, \.systemPower)
        load.loadPowerW = mean(busy, \.systemPower)
        load.idlePCoreUsage = mean(idle, \.pCoreUsage)
        load.loadPCoreUsage = mean(busy, \.pCoreUsage)
        load.idleECoreUsage = mean(idle, \.eCoreUsage)
        load.loadECoreUsage = mean(busy, \.eCoreUsage)
        load.idleGPUUsage = mean(idle, \.gpuUsage)
        load.loadGPUUsage = mean(busy, \.gpuUsage)
        load.loadANEPowerW = mean(busy, \.anePower)
        load.idleRAMUsedMB = mean(idle, \.ramUsedMB)
        load.peakRAMUsedMB = busy.map(\.ramUsedMB).max() ?? 0
        return load
    }

    private func terminate() {
        output.fileHandleForReading.readabilityHandler = nil
        if process.isRunning {
            process.terminate()
        }
    }

    private func consume(_ data: Data) {
        guard !data.isEmpty else { return }
        lock.lock()
        defer { lock.unlock() }
        pending.append(data)
        while let newline = pending.firstIndex(of: UInt8(ascii: "\n")) {
            let line = Data(pending[pending.startIndex..<newline])
            pending.removeSubrange(pending.startIndex...newline)
            guard let reading = try? JSONDecoder().decode(Reading.self, from: line) else { continue }
            // A sample averages the interval before it, so one that straddles a
            // phase change is not attributed to either phase.
            let settled = ProcessInfo.processInfo.systemUptime - phaseStarted >= Double(Self.intervalMilliseconds) / 1000
            samples.append((settled ? phase : .other, reading))
        }
    }

    private func mean(_ readings: [Reading], _ value: KeyPath<Reading, Double>) -> Double {
        readings.map { $0[keyPath: value] }.reduce(0, +) / Double(readings.count)
    }
}
