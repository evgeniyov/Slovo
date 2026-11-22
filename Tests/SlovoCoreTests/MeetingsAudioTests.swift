import AVFoundation
import CoreAudio
import Foundation
import XCTest
@testable import SlovoCore

/// The audio plumbing behind meeting tracks: writing with gap filling, reading back
/// with conversion, resampling, mixdown, the level meter and silence detection.
final class MeetingsAudioTests: XCTestCase {
    private var temporary: URL!

    override func setUpWithError() throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoMeetingsAudioTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: temporary)
    }

    func testWriterAndReaderRoundTripAt16kHz() throws {
        let url = temporary.appendingPathComponent("round.caf")
        let writer = try MeetingTrackWriter(url: url)
        let steps: [Float] = [0.1, 0.3, 0.5]
        for value in steps {
            writer.append([Float](repeating: value, count: 16_000))
        }
        XCTAssertEqual(writer.finish(), 48_000)
        XCTAssertEqual(MeetingClip.duration(of: url), 3, accuracy: 0.001)

        let reader = try MeetingTrackReader(url: url)
        let middle = try reader.samples(in: 16_000..<32_000)
        XCTAssertEqual(middle.count, 16_000)
        XCTAssertEqual(middle.first ?? 0, 0.3, accuracy: 0.001)
        XCTAssertEqual(middle.last ?? 0, 0.3, accuracy: 0.001)
        let last = try reader.samples(in: 40_000..<60_000)
        XCTAssertEqual(last.count, 8_000, "reading stops at the end of the file")
        XCTAssertEqual(last.first ?? 0, 0.5, accuracy: 0.001)
        XCTAssertEqual(try reader.read(10), [])
    }

    func testWriterFillsLateChunksWithSilenceFromHostTime() throws {
        let url = temporary.appendingPathComponent("gap.caf")
        let origin = AudioGetCurrentHostTime()
        let writer = try MeetingTrackWriter(url: url, originHostTime: origin)
        let tone = [Float](repeating: 0.25, count: 16_000)
        writer.append(tone, hostTime: origin)
        // Jitter below the tolerance is not filled.
        writer.append(tone, hostTime: origin + AudioConvertNanosToHostTime(1_050_000_000))
        writer.append(tone, hostTime: origin + AudioConvertNanosToHostTime(5_000_000_000))
        let frames = writer.finish()
        XCTAssertEqual(Double(frames), 6 * 16_000, accuracy: 20)

        let samples = try MeetingTrackReader(url: url).read(Int(frames))
        XCTAssertEqual(samples[8_000], 0.25, accuracy: 0.001)
        XCTAssertEqual(samples[24_000], 0.25, accuracy: 0.001)
        XCTAssertEqual(samples[48_000], 0, accuracy: 0.0001)
        XCTAssertEqual(samples[88_000], 0.25, accuracy: 0.001)
    }

    func testWriterPadsASleepAndKeepsLaterBuffersOnTheTimeline() throws {
        let url = temporary.appendingPathComponent("sleep.caf")
        let origin = AudioGetCurrentHostTime()
        let second = AudioConvertNanosToHostTime(1_000_000_000)
        let writer = try MeetingTrackWriter(url: url, originHostTime: origin)
        let tone = [Float](repeating: 0.25, count: 16_000)

        writer.append(tone, hostTime: origin)
        // Five seconds of sleep. The host clock does not count them, so the buffer that
        // arrives after the wake is stamped only one second past the origin; without the
        // pad the track would butt-join the two sides and lose the five seconds.
        XCTAssertEqual(writer.padSilence(seconds: 5), 5 * 16_000)
        writer.append(tone, hostTime: origin + second)
        let frames = writer.finish()

        XCTAssertEqual(Double(frames), 7 * 16_000, accuracy: 20)
        let samples = try MeetingTrackReader(url: url).read(Int(frames))
        XCTAssertEqual(MeetingAudioMath.rms(samples[1_000..<15_000]), 0.25, accuracy: 0.001)
        XCTAssertEqual(MeetingAudioMath.rms(samples[17_000..<95_000]), 0, accuracy: 1e-6, "the slept interval")
        XCTAssertEqual(MeetingAudioMath.rms(samples[97_000..<111_000]), 0.25, accuracy: 0.001,
                       "speech after the sleep keeps its true position")
    }

    /// A pad longer than the machine has been awake. Taking it off the origin wrapped the
    /// origin below zero, and every later buffer then landed at frame 0 with the gap
    /// filling dead for the rest of the meeting.
    func testAPadLongerThanTheAwakeUptimeKeepsLaterBuffersOnTheTimeline() throws {
        let url = temporary.appendingPathComponent("longsleep.caf")
        // Recording started ten seconds after a boot; the Mac then slept for thirty.
        let origin = AudioConvertNanosToHostTime(10_000_000_000)
        let second = AudioConvertNanosToHostTime(1_000_000_000)
        let writer = try MeetingTrackWriter(url: url, originHostTime: origin)
        let tone = [Float](repeating: 0.25, count: 16_000)

        writer.append(tone, hostTime: origin)
        XCTAssertEqual(writer.padSilence(seconds: 30), 30 * 16_000)
        writer.append(tone, hostTime: origin + 2 * second)
        let frames = writer.finish()

        XCTAssertEqual(Double(frames), 33 * 16_000, accuracy: 20)
        let samples = try MeetingTrackReader(url: url).read(Int(frames))
        XCTAssertEqual(MeetingAudioMath.rms(samples[1_000..<15_000]), 0.25, accuracy: 0.001)
        XCTAssertEqual(MeetingAudioMath.rms(samples[17_000..<510_000]), 0, accuracy: 1e-6, "the slept interval")
        XCTAssertEqual(MeetingAudioMath.rms(samples[513_000..<527_000]), 0.25, accuracy: 0.001,
                       "speech after the sleep keeps its true position")
    }

    /// Muting is how a meeting's microphone is switched off: every buffer still lands with
    /// its own length and at its own time, only as silence, so gap filling and sleep
    /// padding work as before and speech after unmuting keeps its true position.
    func testAMutedWriterWritesSilenceOfTheSameLengthAtTheSameTime() throws {
        let url = temporary.appendingPathComponent("muted.caf")
        let origin = AudioGetCurrentHostTime()
        let second = AudioConvertNanosToHostTime(1_000_000_000)
        let writer = try MeetingTrackWriter(url: url, originHostTime: origin)
        let tone = [Float](repeating: 0.25, count: 16_000)

        writer.append(tone, hostTime: origin)
        writer.isMuted = true
        writer.append(tone, hostTime: origin + second)
        XCTAssertEqual(writer.frameCount, 2 * 16_000, "a muted buffer keeps its length")
        // A capture gap while muted is filled like any other.
        writer.append(tone, hostTime: origin + 4 * second)
        XCTAssertEqual(Double(writer.frameCount), 5 * 16_000, accuracy: 20)
        // So is a sleep.
        XCTAssertEqual(writer.padSilence(seconds: 2), 2 * 16_000)
        // A buffer without a capture time, like the resampler's tail, is gated too.
        writer.append([Float](repeating: 0.25, count: 160))
        writer.isMuted = false
        writer.append(tone, hostTime: origin + 5 * second)
        let frames = writer.finish()

        XCTAssertEqual(Double(frames), 8 * 16_000 + 160, accuracy: 20)
        let samples = try MeetingTrackReader(url: url).read(Int(frames))
        XCTAssertEqual(MeetingAudioMath.rms(samples[1_000..<15_000]), 0.25, accuracy: 0.001)
        XCTAssertEqual(MeetingAudioMath.rms(samples[16_000..<(7 * 16_000 + 160)]), 0, accuracy: 1e-6,
                       "nothing the capture delivered while muted reaches the file")
        XCTAssertEqual(MeetingAudioMath.rms(samples[(7 * 16_000 + 1_000)..<(8 * 16_000 - 1_000)]), 0.25, accuracy: 0.001,
                       "speech after unmuting keeps its place on the timeline")
    }

    func testSleepPadIsCappedByTheGapSafeguard() {
        XCTAssertEqual(MeetingTrackWriter.padSeconds(for: 12), 12)
        XCTAssertEqual(MeetingTrackWriter.padSeconds(for: 0), 0)
        XCTAssertEqual(MeetingTrackWriter.padSeconds(for: -30), 0)
        XCTAssertEqual(MeetingTrackWriter.padSeconds(for: MeetingTrackWriter.maximumGap + 600),
                       MeetingTrackWriter.maximumGap)
    }

    /// A queued pad lands where the sleep was, before any buffer appended after it, and is
    /// not bound by the gap safeguard, which is for timestamps rather than measured sleeps.
    func testAQueuedSleepPadKeepsLaterBuffersOnTheTimelineAndSoundIsTracked() throws {
        let url = temporary.appendingPathComponent("queued.caf")
        let origin = AudioGetCurrentHostTime()
        let second = AudioConvertNanosToHostTime(1_000_000_000)
        let writer = try MeetingTrackWriter(url: url, originHostTime: origin)
        let tone = [Float](repeating: 0.25, count: 16_000)

        writer.append([Float](repeating: 0, count: 16_000), hostTime: origin)
        XCTAssertFalse(writer.hasSound, "silence is not sound")
        writer.enqueueSilence(seconds: 5)
        writer.append(tone, hostTime: origin + second)
        XCTAssertTrue(writer.hasSound)
        let frames = writer.finish()

        XCTAssertEqual(Double(frames), 7 * 16_000, accuracy: 20)
        let samples = try MeetingTrackReader(url: url).read(Int(frames))
        XCTAssertEqual(MeetingAudioMath.rms(samples[0..<96_000]), 0, accuracy: 1e-6, "the slept interval")
        XCTAssertEqual(MeetingAudioMath.rms(samples[97_000..<111_000]), 0.25, accuracy: 0.001)
        XCTAssertGreaterThan(MeetingTrackWriter.maximumSleepPad, MeetingTrackWriter.maximumGap)
    }

    func testTheFirstWriteFailureStopsTheTrackAndIsReportedOnce() throws {
        let url = temporary.appendingPathComponent("full.caf")
        let origin = AudioGetCurrentHostTime()
        let writer = try MeetingTrackWriter(url: url, originHostTime: origin)
        let failures = FailureRecorder()
        writer.onWriteFailure = { failures.add($0) }
        writer.writeBuffer = { _, _ in throw SlovoError("the disk is full") }

        writer.append([Float](repeating: 0.2, count: 16_000), hostTime: origin)
        // A gap worth a hundred seconds of silence: once writing has failed the fill must
        // not run at all, rather than do ever more work for nothing.
        writer.append([Float](repeating: 0.2, count: 16_000),
                      hostTime: origin + AudioConvertNanosToHostTime(100_000_000_000))
        XCTAssertEqual(writer.padSilence(seconds: 30), 0, "a failed track is not padded either")
        XCTAssertEqual(writer.finish(), 0)

        XCTAssertEqual(failures.count, 1, "only the first failure is reported")
        XCTAssertEqual(failures.messages.first, "the disk is full")
    }

    func testReaderConvertsOtherRatesAndChannelsTo16kHzMono() throws {
        let url = temporary.appendingPathComponent("stereo48k.caf")
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: 48_000.0,
            AVNumberOfChannelsKey: 2,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true
        ]
        let format = try XCTUnwrap(AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 2,
                                                 interleaved: false))
        let frames = 96_000
        let buffer = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)))
        let channels = try XCTUnwrap(buffer.floatChannelData)
        for frame in 0..<frames {
            let value = Float(0.4 * sin(2 * Double.pi * 300 * Double(frame) / 48_000))
            channels[0][frame] = value
            channels[1][frame] = 0
        }
        buffer.frameLength = AVAudioFrameCount(frames)
        do {
            let file = try AVAudioFile(forWriting: url, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
            try file.write(from: buffer)
        }

        let reader = try MeetingTrackReader(url: url)
        var samples: [Float] = []
        while true {
            let block = try reader.read(5_000)
            if block.isEmpty { break }
            samples += block
        }
        XCTAssertEqual(Double(samples.count), 32_000, accuracy: 200)
        // One silent channel halves the amplitude: RMS 0.2 / sqrt(2).
        XCTAssertEqual(MeetingAudioMath.rms(samples[4_000..<28_000]), 0.1414, accuracy: 0.01)

        let converted = temporary.appendingPathComponent("converted.caf")
        let duration = try MeetingTrackReader.convertToMeetingTrack(from: url, to: converted)
        let file = try AVAudioFile(forReading: converted)
        XCTAssertEqual(file.fileFormat.sampleRate, 16_000)
        XCTAssertEqual(file.fileFormat.channelCount, 1)
        XCTAssertEqual(file.fileFormat.settings[AVLinearPCMBitDepthKey] as? Int, 16)
        XCTAssertEqual(file.length, Int64(samples.count))
        XCTAssertEqual(duration, 2, accuracy: 0.01)
        let written = try MeetingTrackReader(url: converted).read(28_000)
        XCTAssertEqual(MeetingAudioMath.rms(written[4_000..<28_000]), 0.1414, accuracy: 0.01)
    }

    func testResamplerStreamsChunksWithoutLosingLength() throws {
        let resampler = try MeetingResampler(sourceSampleRate: 48_000)
        var output: [Float] = []
        for chunk in 0..<10 {
            let input = (0..<4_800).map { Float(0.3 * sin(2 * Double.pi * 440 * Double(chunk * 4_800 + $0) / 48_000)) }
            output += resampler.process(input)
        }
        output += resampler.flush()
        XCTAssertEqual(Double(output.count), 16_000, accuracy: 64)
        XCTAssertEqual(MeetingAudioMath.rms(output[2_000..<14_000]), 0.2121, accuracy: 0.01)

        let passthrough = try MeetingResampler(sourceSampleRate: 16_000)
        XCTAssertEqual(passthrough.process([1, 2, 3]), [1, 2, 3])
        XCTAssertEqual(passthrough.flush(), [])
    }

    func testMixdownOfBufferListAveragesOrPicksTrailingChannels() {
        var left: [Float] = [1, 1, 1, 1]
        var right: [Float] = [0, 0.5, 0, 0.5]
        let list = AudioBufferList.allocate(maximumBuffers: 2)
        defer { free(list.unsafeMutablePointer) }
        left.withUnsafeMutableBufferPointer { leftPointer in
            right.withUnsafeMutableBufferPointer { rightPointer in
                list[0] = AudioBuffer(leftPointer, numberOfChannels: 1)
                list[1] = AudioBuffer(rightPointer, numberOfChannels: 1)
                XCTAssertEqual(MeetingAudioMath.mixdown(list.unsafePointer), [0.5, 0.75, 0.5, 0.75])
                XCTAssertEqual(MeetingAudioMath.mixdown(list.unsafePointer, trailingChannels: 1), [0, 0.5, 0, 0.5])
            }
        }

        var interleaved: [Float] = [0.2, 0.4, 0.6, 0.8]
        let single = AudioBufferList.allocate(maximumBuffers: 1)
        defer { free(single.unsafeMutablePointer) }
        interleaved.withUnsafeMutableBufferPointer { pointer in
            single[0] = AudioBuffer(pointer, numberOfChannels: 2)
            let mono = MeetingAudioMath.mixdown(single.unsafePointer)
            XCTAssertEqual(mono.count, 2)
            XCTAssertEqual(mono[0], 0.3, accuracy: 1e-6)
            XCTAssertEqual(mono[1], 0.7, accuracy: 1e-6)
        }
    }

    func testLevelMeter() {
        var meter = LevelMeter()
        XCTAssertEqual(meter.windowLength, 533)
        XCTAssertNil(meter.consume([Float](repeating: 0.5, count: 100)))
        XCTAssertEqual(meter.consume([Float](repeating: 0, count: 2_000)) ?? -1, 0)
        let loud = (0..<1_600).map { Float(sin(2 * Double.pi * 400 * Double($0) / 16_000)) }
        XCTAssertEqual(meter.consume(loud) ?? 0, 0.94, accuracy: 0.02)
        XCTAssertEqual(MeetingAudioMath.level(rms: 0.001), 0)
        XCTAssertEqual(MeetingAudioMath.level(rms: 1), 1)
    }

    func testMicrophoneBoostRaisesQuietSpeechWithoutHardClippingLoudSpeech() {
        XCTAssertEqual(MeetingAudioMath.microphoneBoost([0]), [0])
        // A quiet voice comes out close to the full +9 dB (×2.82) boost.
        let quiet = MeetingAudioMath.microphoneBoost([0.05])[0]
        XCTAssertEqual(quiet, 0.05 * 2.818, accuracy: 0.001)
        // A voice already near full scale is compressed, not clipped flat, and it and
        // everything louder still stay within ±1.
        let loud = MeetingAudioMath.microphoneBoost([0.9, 1, -1])
        XCTAssertLessThan(loud[0], 0.9 * 2.818)
        for sample in loud {
            XCTAssertLessThanOrEqual(abs(sample), 1)
        }
        // Odd function: boosting the negative of a signal negates the boosted result.
        XCTAssertEqual(MeetingAudioMath.microphoneBoost([-0.2])[0], -MeetingAudioMath.microphoneBoost([0.2])[0],
                       accuracy: 1e-6)
    }

    func testSilenceDetectorReportsOnlyZerosOnceAndRecovers() {
        var detector = SilenceDetector(timeout: 3)
        XCTAssertNil(detector.consume(frames: 139_200, sampleRate: 48_000, allZero: true))  // 2.9 s
        XCTAssertEqual(detector.consume(frames: 4_800, sampleRate: 48_000, allZero: true), .silence)
        XCTAssertNil(detector.consume(frames: 480_000, sampleRate: 48_000, allZero: true))
        XCTAssertEqual(detector.consume(frames: 480, sampleRate: 48_000, allZero: false), .signalAfterSilence)
        XCTAssertNil(detector.consume(frames: 480, sampleRate: 48_000, allZero: false))
        XCTAssertTrue(detector.heardSignal)
    }

    func testSilenceDetectorIgnoresQuietAfterRealAudio() {
        var detector = SilenceDetector(timeout: 3)
        XCTAssertNil(detector.consume(frames: 480, sampleRate: 48_000, allZero: false))
        XCTAssertNil(detector.consume(frames: 480_000, sampleRate: 48_000, allZero: true))
        XCTAssertNil(detector.noAudio(forSeconds: 10))

        var starved = SilenceDetector(timeout: 3)
        XCTAssertNil(starved.noAudio(forSeconds: 2))
        XCTAssertEqual(starved.noAudio(forSeconds: 3.5), .silence)
        XCTAssertNil(starved.consume(frames: 480_000, sampleRate: 48_000, allZero: true))
    }

    func testClipIsReadableByTheEngineLoader() throws {
        let url = temporary.appendingPathComponent("clip.wav")
        let samples = (0..<8_000).map { Float($0 % 100) / 200 }
        try MeetingClip.write(samples, to: url)
        let loaded = try AudioLoader.samples(from: url)
        XCTAssertEqual(loaded.count, samples.count)
        XCTAssertEqual(loaded[150], samples[150], accuracy: 1e-6)
    }

    func testSystemAudioTapSupportMatchesThisMac() {
        if #available(macOS 14.4, *) {
            XCTAssertTrue(SystemAudioTap.isSupported)
        } else {
            XCTAssertFalse(SystemAudioTap.isSupported)
        }
    }
}

/// Write failures reported from the writer's queue, read back on the test thread.
private final class FailureRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [String] = []

    func add(_ error: Error) {
        lock.withLock { recorded.append(error.localizedDescription) }
    }

    var count: Int {
        lock.withLock { recorded.count }
    }

    var messages: [String] {
        lock.withLock { recorded }
    }
}
