import Foundation

public struct ClipResult: Codable, Sendable {
    public var clip: String
    public var text: String
    public var audioSeconds: Double
    public var processingSeconds: Double
    public var error: String?

    public init(clip: String, text: String, audioSeconds: Double, processingSeconds: Double, error: String? = nil) {
        self.clip = clip
        self.text = text
        self.audioSeconds = audioSeconds
        self.processingSeconds = processingSeconds
        self.error = error
    }
}

/// How heavy one engine run was. Each engine runs in its own process, so the
/// process figures belong to that engine alone.
public struct ResourceUsage: Codable, Sendable {
    /// How many times every clip was transcribed; per-clip times are medians over the passes.
    public var passes = 1
    /// Model loading in prepare().
    public var prepareSeconds = 0.0
    /// The first transcription after loading, which also pays for warming the model up.
    public var firstTranscriptionSeconds = 0.0
    /// Wall time of all measured passes.
    public var measuredWallSeconds = 0.0
    /// CPU time of every thread of the process, user plus system, during the measured passes.
    public var processCPUSeconds = 0.0
    /// Lifetime peak of the process's physical footprint, the figure Activity Monitor shows as Memory.
    public var peakFootprintMB = 0.0
    /// Lifetime peak of resident memory, which also counts model files mapped from
    /// disk; the system can evict those pages under memory pressure.
    public var peakResidentMB = 0.0
    /// CPU time of system services that transcribe out of process, such as Apple's
    /// localspeechrecognition, during the measured passes; nil when none was running.
    public var helperCPUSeconds: Double?
    /// Footprint and resident memory of those services right after the measured passes.
    public var helperFootprintMB: Double?
    public var helperResidentMB: Double?
    public var modelSizeMB: Double?
    public var system: SystemLoad?

    public init() {}
}

/// System-wide readings from macmon: idle before the model loads versus the measured passes.
public struct SystemLoad: Codable, Sendable {
    public var idleSamples = 0
    public var loadSamples = 0
    public var idlePowerW = 0.0
    public var loadPowerW = 0.0
    public var idlePCoreUsage = 0.0
    public var loadPCoreUsage = 0.0
    public var idleECoreUsage = 0.0
    public var loadECoreUsage = 0.0
    public var idleGPUUsage = 0.0
    public var loadGPUUsage = 0.0
    public var loadANEPowerW = 0.0
    public var idleRAMUsedMB = 0.0
    public var peakRAMUsedMB = 0.0

    public init() {}
}

/// One engine's transcripts of every clip, saved as Bench/results/<engine>.json.
public struct EngineRun: Codable, Sendable {
    public var engine: String
    public var date: Date
    public var system: String
    public var clips: [ClipResult]
    public var resources: ResourceUsage?

    public init(engine: String, date: Date, system: String, clips: [ClipResult], resources: ResourceUsage? = nil) {
        self.engine = engine
        self.date = date
        self.system = system
        self.clips = clips
        self.resources = resources
    }

    public func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(self)
    }

    public static func decode(_ data: Data) throws -> EngineRun {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(EngineRun.self, from: data)
    }
}
