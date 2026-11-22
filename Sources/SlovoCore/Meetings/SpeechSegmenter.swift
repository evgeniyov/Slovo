import Foundation

/// A stretch of one track worth transcribing.
public struct SpeechSegment: Equatable, Sendable {
    public var samples: Range<Int>
    public var start: TimeInterval
    public var end: TimeInterval

    public init(samples: Range<Int>, start: TimeInterval, end: TimeInterval) {
        self.samples = samples
        self.start = start
        self.end = end
    }
}

/// Cuts a track into segments of at most `maxSeconds` at low-energy points (the
/// `GigaAMChunker` logic) and skips silent ones. Pure.
///
/// The work happens on an energy envelope of 25 ms frames rather than on samples, so
/// a file of any length is scanned block by block and only the envelope (40 values
/// per second) is held in memory:
///
/// 1. Frames louder than `silenceRMS` are speech. Speech separated by less than
///    `pauseSeconds` of quiet stays together, so a segment follows one turn of talk
///    and its start time places it correctly among the other speaker's lines.
/// 2. Each stretch is padded by `paddingSeconds` so soft word onsets survive.
/// 3. Stretches longer than `maxSeconds` go through `GigaAMChunker` applied to the
///    envelope, which cuts at the quietest smoothed moment near the end of each
///    window, as it does for GigaAM itself.
public struct SpeechSegmenter: Sendable {
    public var maxSeconds: Double
    /// Segments whose RMS never exceeds this are silent.
    public var silenceRMS: Float
    /// Quiet at least this long separates two segments.
    public var pauseSeconds: Double = 1.0
    /// Audio kept before and after the detected speech.
    public var paddingSeconds: Double = 0.2
    /// Stretches with less loud audio than this are clicks or bumps, not speech.
    public var minimumSpeechSeconds: Double = 0.1

    /// Envelope resolution; an integer rate lets `GigaAMChunker` measure it in seconds.
    static let framesPerSecond = 40
    /// `GigaAMChunker` looks this far back from the end of a window for a pause.
    static let searchSeconds: Double = 5
    /// Neighbours averaged on each side of a frame, about 150 ms, as in the chunker.
    static let smoothingFrames = 6

    public init(maxSeconds: Double = 20, silenceRMS: Float = 0.005) {
        self.maxSeconds = maxSeconds
        self.silenceRMS = silenceRMS
    }

    public func segments(for samples: [Float], sampleRate: Int = 16_000) -> [SpeechSegment] {
        guard sampleRate > 0, !samples.isEmpty else { return [] }
        var envelope = EnergyEnvelope(sampleRate: sampleRate)
        samples.withUnsafeBufferPointer { envelope.append($0) }
        envelope.finish()
        return segments(for: envelope)
    }

    /// Segments of an audio file on its 16 kHz timeline, reading it block by block.
    public func segments(inFileAt url: URL) throws -> [SpeechSegment] {
        segments(for: try envelope(inFileAt: url))
    }

    /// The energy envelope of a file on its 16 kHz timeline. The meeting pipeline reads a
    /// track once and uses the envelope both for speech detection and for splitting long
    /// speaker turns.
    func envelope(inFileAt url: URL) throws -> EnergyEnvelope {
        let reader = try MeetingTrackReader(url: url)
        var envelope = EnergyEnvelope(sampleRate: MeetingAudioFormat.sampleRate)
        while true {
            try Task.checkCancellation()
            let block = try reader.read(MeetingAudioFormat.sampleRate)
            if block.isEmpty { break }
            block.withUnsafeBufferPointer { envelope.append($0) }
        }
        envelope.finish()
        return envelope
    }

    /// Segments of one slice of an envelope, with times on the whole track's timeline, for
    /// splitting a long speaker turn at quiet points.
    ///
    /// Unlike `segments(for:)`, the slice is not searched for pauses: the diarizer already
    /// decided it is one person's turn, so it is only cut into pieces of at most
    /// `maxSeconds` at the quietest moments, and pieces without a frame above
    /// `silenceRMS` are dropped.
    func segments(for envelope: EnergyEnvelope, within frames: Range<Int>) -> [SpeechSegment] {
        let lower = max(0, min(frames.lowerBound, envelope.frames.count))
        let upper = max(lower, min(frames.upperBound, envelope.frames.count))
        guard upper > lower, maxSeconds > 0 else { return [] }
        return pieces(of: envelope, stretch: lower..<upper)
    }

    func segments(for envelope: EnergyEnvelope) -> [SpeechSegment] {
        let energy = envelope.frames
        guard !energy.isEmpty, maxSeconds > 0 else { return [] }
        let frameSeconds = Double(envelope.frameLength) / Double(envelope.sampleRate)
        let pauseFrames = max(1, Int((pauseSeconds / frameSeconds).rounded()))
        let paddingFrames = max(0, Int((paddingSeconds / frameSeconds).rounded()))
        let minimumSpeechFrames = max(1, Int((minimumSpeechSeconds / frameSeconds).rounded()))

        return speechStretches(energy, pauseFrames: pauseFrames, paddingFrames: paddingFrames,
                               minimumSpeechFrames: minimumSpeechFrames)
            .flatMap { pieces(of: envelope, stretch: $0) }
    }

    /// Cuts a frame range into pieces of at most `maxSeconds` at low-energy points and keeps
    /// the pieces that hold some sound.
    private func pieces(of envelope: EnergyEnvelope, stretch: Range<Int>) -> [SpeechSegment] {
        let energy = envelope.frames
        let chunks = GigaAMChunker.ranges(for: Array(energy[stretch]),
                                          sampleRate: Self.framesPerSecond,
                                          maxSeconds: maxSeconds,
                                          searchSeconds: Self.searchSeconds,
                                          frameSeconds: 1 / Double(Self.framesPerSecond),
                                          smoothingFrames: Self.smoothingFrames)
        var result: [SpeechSegment] = []
        for chunk in chunks {
            let frames = (stretch.lowerBound + chunk.lowerBound)..<(stretch.lowerBound + chunk.upperBound)
            // A tail cut off a long stretch can hold only padding.
            guard energy[frames].contains(where: { $0 > silenceRMS }) else { continue }
            let lower = min(envelope.sampleCount, frames.lowerBound * envelope.frameLength)
            let upper = min(envelope.sampleCount, frames.upperBound * envelope.frameLength)
            guard upper > lower else { continue }
            result.append(SpeechSegment(samples: lower..<upper,
                                        start: Double(lower) / Double(envelope.sampleRate),
                                        end: Double(upper) / Double(envelope.sampleRate)))
        }
        return result
    }

    /// Padded frame ranges of speech, in order and without overlaps.
    private func speechStretches(_ energy: [Float], pauseFrames: Int, paddingFrames: Int,
                                 minimumSpeechFrames: Int) -> [Range<Int>] {
        var stretches: [(range: Range<Int>, loudFrames: Int)] = []
        for (index, value) in energy.enumerated() where value > silenceRMS {
            if let last = stretches.last, index - last.range.upperBound < pauseFrames {
                stretches[stretches.count - 1] = (last.range.lowerBound..<(index + 1), last.loudFrames + 1)
            } else {
                stretches.append((index..<(index + 1), 1))
            }
        }

        var padded: [Range<Int>] = []
        for stretch in stretches where stretch.loudFrames >= minimumSpeechFrames {
            var range = max(0, stretch.range.lowerBound - paddingFrames)..<min(energy.count, stretch.range.upperBound + paddingFrames)
            if let last = padded.last, range.lowerBound <= last.upperBound {
                range = last.lowerBound..<max(last.upperBound, range.upperBound)
                padded[padded.count - 1] = range
            } else {
                padded.append(range)
            }
        }
        return padded
    }
}

/// RMS of consecutive frames of a signal, built incrementally.
struct EnergyEnvelope {
    let sampleRate: Int
    let frameLength: Int
    private(set) var frames: [Float] = []
    private(set) var sampleCount = 0
    private var sum: Double = 0
    private var count = 0

    init(sampleRate: Int) {
        self.sampleRate = sampleRate
        frameLength = max(1, sampleRate / SpeechSegmenter.framesPerSecond)
    }

    mutating func append(_ samples: UnsafeBufferPointer<Float>) {
        for sample in samples {
            sum += Double(sample) * Double(sample)
            count += 1
            if count == frameLength {
                closeFrame()
            }
        }
        sampleCount += samples.count
    }

    /// Closes a final partial frame.
    mutating func finish() {
        if count > 0 {
            closeFrame()
        }
    }

    private mutating func closeFrame() {
        frames.append(Float((sum / Double(count)).squareRoot()))
        sum = 0
        count = 0
    }
}
