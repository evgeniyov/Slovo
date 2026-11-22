import AVFoundation
import Foundation

struct BenchPaths {
    let root: URL

    var phrases: URL { root.appendingPathComponent("phrases.txt") }
    var vocabulary: URL { root.appendingPathComponent("vocabulary.txt") }
    var clips: URL { root.appendingPathComponent("clips", isDirectory: true) }
    var results: URL { root.appendingPathComponent("results", isDirectory: true) }

    func audio(_ id: String) -> URL { clips.appendingPathComponent("\(id).wav") }
    func reference(_ id: String) -> URL { clips.appendingPathComponent("\(id).txt") }
}

/// A recording in Bench/clips and its optional reference text next to it.
struct Clip {
    let id: String
    let audio: URL

    static func list(in directory: URL) throws -> [Clip] {
        guard FileManager.default.fileExists(atPath: directory.path) else { return [] }
        return try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension.lowercased() == "wav" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
            .map { Clip(id: $0.deletingPathExtension().lastPathComponent, audio: $0) }
    }

    /// References keyed by clip id; empty .txt files are skipped.
    static func references(in directory: URL) throws -> [String: String] {
        var references: [String: String] = [:]
        for clip in try list(in: directory) {
            let url = clip.audio.deletingPathExtension().appendingPathExtension("txt")
            guard let text = try? String(contentsOf: url, encoding: .utf8) else { continue }
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty {
                references[clip.id] = trimmed
            }
        }
        return references
    }

    func durationSeconds() throws -> Double {
        let file = try AVAudioFile(forReading: audio)
        return Double(file.length) / file.fileFormat.sampleRate
    }
}

struct BenchError: LocalizedError {
    let message: String

    init(_ message: String) {
        self.message = message
    }

    var errorDescription: String? { message }
}

enum Options {
    /// Removes `name value` from the arguments and returns the value.
    static func take(_ name: String, from arguments: inout [String]) -> String? {
        guard let index = arguments.firstIndex(of: name), index + 1 < arguments.count else { return nil }
        let value = arguments[index + 1]
        arguments.removeSubrange(index...(index + 1))
        return value
    }
}

enum Console {
    /// Prints a prompt and reads one line; nil means input was closed.
    static func ask(_ prompt: String) -> String? {
        print(prompt, terminator: "")
        return readLine()?.trimmingCharacters(in: .whitespaces).lowercased()
    }
}

extension Duration {
    var seconds: Double {
        Double(components.seconds) + Double(components.attoseconds) / 1e18
    }
}
