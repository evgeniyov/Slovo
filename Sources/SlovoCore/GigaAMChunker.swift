import Foundation

/// Splits long recordings into pieces short enough for GigaAM.
///
/// GigaAM is trained on segments up to about 25 seconds and degrades on longer
/// input. Instead of cutting at a fixed offset, which can split a word, each cut
/// goes to the quietest moment near the end of the window.
enum GigaAMChunker {
    static let maxSeconds: Double = 20
    static let windowSamples = Int(maxSeconds * AudioLoader.sampleRate)

    /// Sample ranges that cover `samples` in order without gaps or overlaps.
    ///
    /// - Parameters:
    ///   - maxSeconds: upper bound for every chunk.
    ///   - searchSeconds: how far back from the end of a window to look for a pause.
    ///   - frameSeconds: length of the frames whose RMS is compared.
    ///   - smoothingFrames: neighbours on each side averaged into a frame's score, so
    ///     the short closure of a stop consonant does not look like a pause.
    static func ranges(for samples: [Float],
                       sampleRate: Int = 16_000,
                       maxSeconds: Double = GigaAMChunker.maxSeconds,
                       searchSeconds: Double = 5,
                       frameSeconds: Double = 0.03,
                       smoothingFrames: Int = 5) -> [Range<Int>] {
        guard !samples.isEmpty else { return [] }
        let maxLength = Int(maxSeconds * Double(sampleRate))
        guard maxLength > 0, samples.count > maxLength else { return [0..<samples.count] }

        var ranges: [Range<Int>] = []
        var start = 0
        while samples.count - start > maxLength {
            let cut = cut(in: samples, window: start..<(start + maxLength), sampleRate: sampleRate,
                          searchSeconds: searchSeconds, frameSeconds: frameSeconds, smoothingFrames: smoothingFrames)
            ranges.append(start..<cut)
            start = cut
        }
        ranges.append(start..<samples.count)
        return ranges
    }

    /// Once another sample follows a full window, its quiet cut cannot change.
    static func cut(in samples: [Float], window: Range<Int>, sampleRate: Int = 16_000,
                    searchSeconds: Double = 5, frameSeconds: Double = 0.03, smoothingFrames: Int = 5) -> Int {
        let frameLength = max(1, Int(frameSeconds * Double(sampleRate)))
        let searchLength = min(window.count, max(frameLength, Int(searchSeconds * Double(sampleRate))))
        let region = (window.upperBound - searchLength)..<window.upperBound
        let quietest = quietestPoint(in: samples, region: region, frameLength: frameLength,
                                     smoothingFrames: max(0, smoothingFrames))
        return min(max(quietest, window.lowerBound + 1), window.upperBound)
    }

    /// The middle of the frame with the lowest smoothed RMS; ties go to the later frame.
    static func quietestPoint(in samples: [Float], region: Range<Int>,
                              frameLength: Int, smoothingFrames: Int) -> Int {
        let frameCount = region.count / frameLength
        guard frameCount > 0 else { return region.upperBound }

        var rms = [Double](repeating: 0, count: frameCount)
        samples.withUnsafeBufferPointer { buffer in
            for frame in 0..<frameCount {
                let first = region.lowerBound + frame * frameLength
                var sum: Double = 0
                for index in first..<(first + frameLength) {
                    let value = Double(buffer[index])
                    sum += value * value
                }
                rms[frame] = (sum / Double(frameLength)).squareRoot()
            }
        }

        var prefix = [Double](repeating: 0, count: frameCount + 1)
        for frame in 0..<frameCount {
            prefix[frame + 1] = prefix[frame] + rms[frame]
        }

        var best = 0
        var bestScore = Double.infinity
        for frame in 0..<frameCount {
            let low = max(0, frame - smoothingFrames)
            let high = min(frameCount - 1, frame + smoothingFrames)
            let score = (prefix[high + 1] - prefix[low]) / Double(high - low + 1)
            if score <= bestScore {
                best = frame
                bestScore = score
            }
        }
        return region.lowerBound + best * frameLength + frameLength / 2
    }
}
