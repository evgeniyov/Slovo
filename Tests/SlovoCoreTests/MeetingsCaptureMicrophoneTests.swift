import AVFoundation
import CoreAudio
import CoreMedia
import Foundation
import XCTest
@testable import SlovoCore

/// The microphone track's capture plumbing without a device: sample buffers built in
/// memory go through the same copy, timestamp and conversion code a capture session
/// feeds, into a real track file.
final class MeetingsCaptureMicrophoneTests: XCTestCase {
    private var temporary: URL!

    override func setUpWithError() throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoMeetingsCaptureTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: temporary)
    }

    // MARK: - Sample buffers

    func testSampleBufferCopiesIntoAPCMBufferOfTheSameFormat() throws {
        let samples = (0..<1_600).map { Float($0) / 3_200 }
        let sampleBuffer = try makeFloatSampleBuffer(samples, channels: 1, sampleRate: 16_000, presentation: .zero)

        let buffer = try XCTUnwrap(MeetingCaptureBuffers.pcmBuffer(from: sampleBuffer))

        XCTAssertEqual(buffer.frameLength, 1_600)
        XCTAssertEqual(buffer.format.sampleRate, 16_000)
        XCTAssertEqual(buffer.format.channelCount, 1)
        let channel = try XCTUnwrap(buffer.floatChannelData?[0])
        XCTAssertEqual(channel[0], 0)
        XCTAssertEqual(channel[800], 0.25, accuracy: 1e-6)
        XCTAssertEqual(channel[1_599], samples[1_599], accuracy: 1e-6)
    }

    func testConverterTakesIntegerStereoAtAnotherRateTo16kHzMono() throws {
        // 48 kHz interleaved Int16 stereo: a 300 Hz tone on the left, silence on the right.
        let frames = 9_600
        var interleaved = [Int16](repeating: 0, count: frames * 2)
        for frame in 0..<frames {
            interleaved[frame * 2] = Int16(16_384 * sin(2 * Double.pi * 300 * Double(frame) / 48_000))
        }
        let sampleBuffer = try makeInt16SampleBuffer(interleaved, channels: 2, sampleRate: 48_000)
        let buffer = try XCTUnwrap(MeetingCaptureBuffers.pcmBuffer(from: sampleBuffer))
        XCTAssertEqual(buffer.frameLength, AVAudioFrameCount(frames))

        let converter = MeetingCaptureConverter()
        let mono = converter.convert(buffer) + converter.flush()

        XCTAssertEqual(Double(mono.count), 3_200, accuracy: 64)
        // Amplitude 0.5 on one of two channels averages to 0.25: RMS 0.25 / sqrt(2).
        XCTAssertEqual(MeetingAudioMath.rms(mono[400..<2_800]), 0.1768, accuracy: 0.01)
    }

    func testConverterPassesTrackFormatThrough() throws {
        let samples: [Float] = [0.1, -0.2, 0.3, -0.4]
        let buffer = try XCTUnwrap(MeetingCaptureBuffers.pcmBuffer(
            from: makeFloatSampleBuffer(samples, channels: 1, sampleRate: 16_000, presentation: .zero)))
        let converter = MeetingCaptureConverter()
        XCTAssertEqual(converter.convert(buffer), samples)
        XCTAssertEqual(converter.flush(), [])
    }

    func testMixdownAveragesPlanarAndInterleavedIntegerBuffers() throws {
        let planarFormat = try XCTUnwrap(AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16_000,
                                                       channels: 2, interleaved: false))
        let planar = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: planarFormat, frameCapacity: 2))
        let planarData = try XCTUnwrap(planar.int16ChannelData)
        planarData[0][0] = 16_384
        planarData[1][0] = 0
        planarData[0][1] = -32_768
        planarData[1][1] = -32_768
        planar.frameLength = 2
        let planarMono = MeetingAudioMath.mixdown(planar)
        XCTAssertEqual(planarMono.count, 2)
        XCTAssertEqual(planarMono[0], 0.25, accuracy: 1e-6)
        XCTAssertEqual(planarMono[1], -1, accuracy: 1e-6)

        let interleavedFormat = try XCTUnwrap(AVAudioFormat(commonFormat: .pcmFormatInt32, sampleRate: 16_000,
                                                            channels: 2, interleaved: true))
        let interleaved = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: interleavedFormat, frameCapacity: 2))
        let interleavedData = try XCTUnwrap(interleaved.int32ChannelData)
        interleavedData[0][0] = 1_073_741_824
        interleavedData[0][1] = 1_073_741_824
        interleavedData[0][2] = 0
        interleavedData[0][3] = -1_073_741_824
        interleaved.frameLength = 2
        let interleavedMono = MeetingAudioMath.mixdown(interleaved)
        XCTAssertEqual(interleavedMono.count, 2)
        XCTAssertEqual(interleavedMono[0], 0.5, accuracy: 1e-6)
        XCTAssertEqual(interleavedMono[1], -0.25, accuracy: 1e-6)
    }

    // MARK: - Timestamps

    func testHostTimeComesFromThePresentationTimestamp() {
        let arrival = AudioGetCurrentHostTime()
        let captured = arrival - AudioConvertNanosToHostTime(80_000_000)
        let presentation = CMClockMakeHostTimeFromSystemUnits(captured)

        for clock in [CMClockGetHostTimeClock(), nil] {
            let hostTime = MeetingCaptureBuffers.hostTime(presentation: presentation, clock: clock,
                                                          arrivalHostTime: arrival, frames: 1_600, sampleRate: 16_000)
            XCTAssertEqual(milliseconds(hostTime, captured), 0, accuracy: 0.01)
        }
    }

    func testHostTimeFallsBackToArrivalForMissingOrImplausibleTimestamps() {
        let arrival = AudioGetCurrentHostTime()
        let hour = AudioConvertNanosToHostTime(3_600_000_000_000)
        let implausible = [CMTime.invalid, CMClockMakeHostTimeFromSystemUnits(arrival + hour),
                           CMClockMakeHostTimeFromSystemUnits(arrival > hour ? arrival - hour : 1)]
        for presentation in implausible {
            let hostTime = MeetingCaptureBuffers.hostTime(presentation: presentation, clock: nil,
                                                          arrivalHostTime: arrival, frames: 1_600, sampleRate: 16_000)
            // A 100 ms buffer that just arrived began 100 ms ago.
            XCTAssertEqual(milliseconds(arrival, hostTime), 100, accuracy: 0.01)
        }
    }

    // MARK: - Devices

    func testChosenMicrophoneComesFirstThenTheDefaultThenEveryOtherInput() {
        let connected: Set<String> = ["BuiltInMicrophoneDevice", "BuiltInHeadphoneInputDevice"]
        func preferred(_ chosen: String?, default defaultUID: String?) -> [String] {
            MeetingMicrophoneCapture.preferredDeviceUIDs(chosen: chosen, isConnected: { connected.contains($0) },
                                                         defaultUID: defaultUID)
        }
        /// Every connected input, as the recovery sees them when a device is handed over.
        func fromDevices(_ chosen: String?, default defaultUID: String?, connected devices: [String]) -> [String] {
            MeetingMicrophoneCapture.preferredDeviceUIDs(chosen: chosen, isConnected: { devices.contains($0) },
                                                         defaultUID: defaultUID, connected: devices)
        }
        XCTAssertEqual(preferred("BuiltInMicrophoneDevice", default: "BuiltInHeadphoneInputDevice"),
                       ["BuiltInMicrophoneDevice", "BuiltInHeadphoneInputDevice"])
        XCTAssertEqual(preferred("AirPods", default: "BuiltInHeadphoneInputDevice"), ["BuiltInHeadphoneInputDevice"])
        XCTAssertEqual(preferred(nil, default: "BuiltInHeadphoneInputDevice"), ["BuiltInHeadphoneInputDevice"])
        XCTAssertEqual(preferred("", default: "BuiltInHeadphoneInputDevice"), ["BuiltInHeadphoneInputDevice"])
        XCTAssertEqual(preferred("BuiltInMicrophoneDevice", default: "BuiltInMicrophoneDevice"), ["BuiltInMicrophoneDevice"])
        XCTAssertEqual(preferred("BuiltInMicrophoneDevice", default: nil), ["BuiltInMicrophoneDevice"])
        XCTAssertEqual(preferred("AirPods", default: nil), [])

        // A default that will not open — AirPods halfway through a handoff — must not take
        // every attempt the budget buys while another microphone sits unused. The built-in
        // one is the last resort, not a replacement for what the owner plugged in.
        let inputs = ["BuiltInMicrophoneDevice", "AirPods", "Scarlett"]
        XCTAssertEqual(fromDevices(nil, default: "AirPods", connected: inputs),
                       ["AirPods", "Scarlett", "BuiltInMicrophoneDevice"])
        XCTAssertEqual(fromDevices("Scarlett", default: "AirPods", connected: inputs),
                       ["Scarlett", "AirPods", "BuiltInMicrophoneDevice"])
        XCTAssertEqual(fromDevices(nil, default: nil, connected: ["BuiltInMicrophoneDevice"]),
                       ["BuiltInMicrophoneDevice"])
    }

    /// A Bluetooth handoff can leave the input missing for many seconds. The budget must
    /// cover that: five attempts 0.3 s apart gave up after 1.2 s and ended the meeting.
    func testTheRestartBudgetOutlastsADeviceHandoff() {
        var elapsed: TimeInterval = 0
        var delay = MeetingMicrophoneCapture.restartRetryDelay
        var attempts = 1
        while let next = MeetingMicrophoneCapture.nextRetryDelay(elapsed: elapsed, delay: delay) {
            elapsed += delay
            delay = next
            attempts += 1
        }

        XCTAssertGreaterThan(elapsed, 28, "the microphone is given about half a minute to come back")
        XCTAssertLessThanOrEqual(elapsed, MeetingMicrophoneCapture.restartBudget)
        XCTAssertGreaterThan(attempts, 15)
        XCTAssertEqual(delay, MeetingMicrophoneCapture.maximumRetryDelay, "the wait settles at its cap")
        XCTAssertEqual(MeetingMicrophoneCapture.nextRetryDelay(elapsed: 0, delay: 0.3) ?? 0, 0.45, accuracy: 1e-9)
        XCTAssertNil(MeetingMicrophoneCapture.nextRetryDelay(elapsed: MeetingMicrophoneCapture.restartBudget, delay: 0.3),
                     "a wait that would run past the budget ends the retries")
    }

    /// While the microphone is being handed over, the meeting keeps recording and the
    /// track is filled from the next buffer's own capture time.
    func testAMicrophoneOutageIsFilledWithSilenceOnTheSharedTimeline() throws {
        let url = temporary.appendingPathComponent("outage.caf")
        let origin = AudioGetCurrentHostTime()
        let second = AudioConvertNanosToHostTime(1_000_000_000)
        let writer = try MeetingTrackWriter(url: url, originHostTime: origin)
        let sink = MeetingMicrophoneSink(writer: writer, onLevel: nil, clock: CMClockGetHostTimeClock())
        let tone = (0..<16_000).map { Float(0.3 * sin(2 * Double.pi * 440 * Double($0) / 16_000)) }

        // A second of speech, twenty seconds with no device, then capture on the new one.
        let first = try makeFloatSampleBuffer(tone, channels: 1, sampleRate: 16_000,
                                              presentation: CMClockMakeHostTimeFromSystemUnits(origin))
        let afterOutage = try makeFloatSampleBuffer(tone, channels: 1, sampleRate: 16_000,
                                                    presentation: CMClockMakeHostTimeFromSystemUnits(origin + 21 * second))
        sink.process(first, arrivalHostTime: origin + second)
        sink.process(afterOutage, arrivalHostTime: origin + 22 * second)
        sink.finish()
        let frames = writer.finish()

        XCTAssertEqual(Double(frames), 22 * 16_000, accuracy: 20)
        let samples = try MeetingTrackReader(url: url).read(Int(frames))
        XCTAssertEqual(MeetingAudioMath.rms(samples[1_000..<15_000]), 0.512, accuracy: 0.01)
        XCTAssertEqual(MeetingAudioMath.rms(samples[17_000..<334_000]), 0, accuracy: 1e-6, "the outage")
        XCTAssertEqual(MeetingAudioMath.rms(samples[337_000..<351_000]), 0.512, accuracy: 0.01,
                       "speech after the outage keeps its place on the timeline")
    }

    func testStoppingACaptureThatNeverStartedIsHarmless() throws {
        let writer = try MeetingTrackWriter(url: temporary.appendingPathComponent("idle.caf"))
        let capture = MeetingMicrophoneCapture(writer: writer, deviceUID: "not-a-device")
        capture.stop()
        capture.stop()
        XCTAssertEqual(writer.finish(), 0)
    }

    // MARK: - Sink to track

    func testSinkWritesBuffersOnTheSharedTimelineAndReportsLevels() throws {
        let url = temporary.appendingPathComponent("mic.caf")
        let origin = AudioGetCurrentHostTime()
        let writer = try MeetingTrackWriter(url: url, originHostTime: origin)
        let levels = LevelRecorder()
        let sink = MeetingMicrophoneSink(writer: writer, onLevel: { levels.add($0) }, clock: CMClockGetHostTimeClock())

        let second = AudioConvertNanosToHostTime(1_000_000_000)
        let tone = (0..<16_000).map { Float(0.3 * sin(2 * Double.pi * 440 * Double($0) / 16_000)) }
        // One second of tone from 0.5 s, and after a device switch another from 3 s.
        let first = try makeFloatSampleBuffer(tone, channels: 1, sampleRate: 16_000,
                                              presentation: CMClockMakeHostTimeFromSystemUnits(origin + second / 2))
        let late = try makeFloatSampleBuffer(tone, channels: 1, sampleRate: 16_000,
                                             presentation: CMClockMakeHostTimeFromSystemUnits(origin + 3 * second))
        sink.process(first, arrivalHostTime: origin + 3 * second / 2)
        sink.process(late, arrivalHostTime: origin + 4 * second)
        sink.finish()
        let frames = writer.finish()

        XCTAssertEqual(Double(frames), 4 * 16_000, accuracy: 20)
        let samples = try MeetingTrackReader(url: url).read(Int(frames))
        XCTAssertEqual(MeetingAudioMath.rms(samples[0..<7_000]), 0, accuracy: 1e-6, "leading silence up to the first buffer")
        XCTAssertEqual(MeetingAudioMath.rms(samples[9_000..<23_000]), 0.512, accuracy: 0.01)
        XCTAssertEqual(MeetingAudioMath.rms(samples[25_000..<47_000]), 0, accuracy: 1e-6, "the switch-over gap")
        XCTAssertEqual(MeetingAudioMath.rms(samples[49_000..<63_000]), 0.512, accuracy: 0.01)
        // The meter reports the last window each buffer completes: one level per buffer here,
        // about 30 per second with the ~10 ms buffers a capture session delivers.
        XCTAssertEqual(levels.values.count, 2)
        for level in levels.values {
            XCTAssertEqual(level, MeetingAudioMath.level(rms: 0.512), accuracy: 0.02)
        }
    }

    // MARK: - Helpers

    private func milliseconds(_ later: UInt64, _ earlier: UInt64) -> Double {
        (Double(AudioConvertHostTimeToNanos(later)) - Double(AudioConvertHostTimeToNanos(earlier))) / 1_000_000
    }

    private func makeFloatSampleBuffer(_ samples: [Float], channels: Int, sampleRate: Double,
                                       presentation: CMTime) throws -> CMSampleBuffer {
        let bytes = samples.withUnsafeBytes { Data($0) }
        let description = AudioStreamBasicDescription(
            mSampleRate: sampleRate, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: UInt32(4 * channels), mFramesPerPacket: 1, mBytesPerFrame: UInt32(4 * channels),
            mChannelsPerFrame: UInt32(channels), mBitsPerChannel: 32, mReserved: 0)
        return try makeSampleBuffer(bytes, description: description, frames: samples.count / channels,
                                    presentation: presentation)
    }

    private func makeInt16SampleBuffer(_ samples: [Int16], channels: Int, sampleRate: Double) throws -> CMSampleBuffer {
        let bytes = samples.withUnsafeBytes { Data($0) }
        let description = AudioStreamBasicDescription(
            mSampleRate: sampleRate, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked,
            mBytesPerPacket: UInt32(2 * channels), mFramesPerPacket: 1, mBytesPerFrame: UInt32(2 * channels),
            mChannelsPerFrame: UInt32(channels), mBitsPerChannel: 16, mReserved: 0)
        return try makeSampleBuffer(bytes, description: description, frames: samples.count / channels, presentation: .zero)
    }

    private func makeSampleBuffer(_ bytes: Data, description: AudioStreamBasicDescription, frames: Int,
                                  presentation: CMTime) throws -> CMSampleBuffer {
        var streamDescription = description
        var format: CMAudioFormatDescription?
        let formatStatus = CMAudioFormatDescriptionCreate(allocator: kCFAllocatorDefault, asbd: &streamDescription,
                                                          layoutSize: 0, layout: nil, magicCookieSize: 0, magicCookie: nil,
                                                          extensions: nil, formatDescriptionOut: &format)
        XCTAssertEqual(formatStatus, noErr)
        let formatDescription = try XCTUnwrap(format)

        var blockBuffer: CMBlockBuffer?
        let blockStatus = CMBlockBufferCreateWithMemoryBlock(allocator: kCFAllocatorDefault, memoryBlock: nil,
                                                             blockLength: bytes.count, blockAllocator: kCFAllocatorDefault,
                                                             customBlockSource: nil, offsetToData: 0,
                                                             dataLength: bytes.count,
                                                             flags: kCMBlockBufferAssureMemoryNowFlag,
                                                             blockBufferOut: &blockBuffer)
        XCTAssertEqual(blockStatus, noErr)
        let block = try XCTUnwrap(blockBuffer)
        let copied = bytes.withUnsafeBytes { raw -> OSStatus in
            guard let base = raw.baseAddress else { return -1 }
            return CMBlockBufferReplaceDataBytes(with: base, blockBuffer: block, offsetIntoDestination: 0,
                                                 dataLength: bytes.count)
        }
        XCTAssertEqual(copied, noErr)

        var sampleBuffer: CMSampleBuffer?
        let sampleStatus = CMAudioSampleBufferCreateReadyWithPacketDescriptions(
            allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: formatDescription,
            sampleCount: frames, presentationTimeStamp: presentation, packetDescriptions: nil,
            sampleBufferOut: &sampleBuffer)
        XCTAssertEqual(sampleStatus, noErr)
        return try XCTUnwrap(sampleBuffer)
    }
}

/// Levels reported from the sink, read back on the test thread.
private final class LevelRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [Float] = []

    func add(_ level: Float) {
        lock.withLock { recorded.append(level) }
    }

    var values: [Float] {
        lock.withLock { recorded }
    }
}
