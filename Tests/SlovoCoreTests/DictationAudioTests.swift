import AppKit
import AVFoundation
import Foundation
import XCTest
@testable import SlovoCore

/// WAV files, speech detection, the capture converter and device listing. The
/// microphone itself is never opened: that would need a permission prompt.
final class DictationAudioTests: XCTestCase {
    private var temporary: URL!

    override func setUp() {
        super.setUp()
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoDictationAudioTests-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: temporary)
        super.tearDown()
    }

    private func tone(count: Int, rate: Double, frequency: Double = 220, amplitude: Float = 0.3) -> [Float] {
        (0..<count).map { Float(sin(Double($0) * 2 * .pi * frequency / rate)) * amplitude }
    }

    // MARK: - WAV

    func testCancelledGigaAMRequestDoesNotLoadTheModel() async throws {
        let engine = GigaAMEngine(id: "cancelled", variant: .ctc, modelsDirectory: temporary)
        let audio = temporary.appendingPathComponent("unused.wav")
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return try await engine.transcribe(fileURL: audio)
        }

        do {
            _ = try await task.value
            XCTFail("An already cancelled request completed")
        } catch is CancellationError {
        }
    }

    func testWAVRoundTrip() throws {
        let samples = tone(count: 16_000, rate: 16_000) + [1, -1, 1.5, -2, .nan]
        let url = temporary.appendingPathComponent("2026-09-15/clip.wav")
        try WAVFile.write(RecordedAudio(samples: samples), to: url)

        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        XCTAssertEqual(attributes[.size] as? Int, 44 + samples.count * 2)

        let audio = try WAVFile.read(from: url)
        XCTAssertEqual(audio.sampleRate, 16_000)
        XCTAssertEqual(audio.samples.count, samples.count)
        for (index, original) in samples.prefix(16_000).enumerated() {
            XCTAssertEqual(audio.samples[index], original, accuracy: 0.001)
        }
        XCTAssertEqual(audio.samples[16_002], 1, accuracy: 0.001, "Out-of-range samples are clipped")
        XCTAssertEqual(audio.samples[16_004], 0, "NaN is written as silence")
    }

    func testEnginesReadWrittenRecordings() throws {
        let samples = tone(count: 8_000, rate: 16_000)
        let url = temporary.appendingPathComponent("engine.wav")
        try WAVFile.write(RecordedAudio(samples: samples), to: url)

        let loaded = try AudioLoader.samples(from: url)
        XCTAssertEqual(loaded.count, samples.count)
        XCTAssertEqual(loaded[1_234], samples[1_234], accuracy: 0.001)
    }

    func testReadsFloatAndTwentyFourBitStereoFilesWrittenByAVFoundation() throws {
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        for (bits, isFloat) in [(32, true), (24, false)] {
            let url = temporary.appendingPathComponent("stereo-\(bits).wav")
            let settings: [String: Any] = [
                AVFormatIDKey: kAudioFormatLinearPCM,
                AVSampleRateKey: 44_100,
                AVNumberOfChannelsKey: 2,
                AVLinearPCMBitDepthKey: bits,
                AVLinearPCMIsFloatKey: isFloat,
                AVLinearPCMIsBigEndianKey: false,
                AVLinearPCMIsNonInterleaved: false
            ]
            do {
                let file = try AVAudioFile(forWriting: url, settings: settings)
                let format = try XCTUnwrap(AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 44_100,
                                                         channels: 2, interleaved: false))
                let buffer = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 4_410))
                let channels = try XCTUnwrap(buffer.floatChannelData)
                for frame in 0..<4_410 {
                    channels[0][frame] = 0.5
                    channels[1][frame] = -0.1
                }
                buffer.frameLength = 4_410
                try file.write(from: buffer)
            }

            let audio = try WAVFile.read(from: url)
            XCTAssertEqual(audio.sampleRate, 44_100, "\(bits)-bit")
            XCTAssertEqual(audio.samples.count, 4_410, "\(bits)-bit")
            XCTAssertEqual(audio.samples[100], 0.2, accuracy: 0.001, "\(bits)-bit channels are averaged")
        }
    }

    func testReadingSomethingElseFails() throws {
        let url = temporary.appendingPathComponent("notes.wav")
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        try Data("hello, this is not audio".utf8).write(to: url)
        XCTAssertThrowsError(try WAVFile.read(from: url)) { error in
            XCTAssertTrue(error.localizedDescription.contains("notes.wav"), error.localizedDescription)
        }
        XCTAssertThrowsError(try WAVFile.read(from: temporary.appendingPathComponent("missing.wav")))
    }

    // MARK: - Speech energy

    func testSpeechEnergyDecisions() {
        XCTAssertTrue(SpeechEnergy.isEffectivelySilent(.empty))
        XCTAssertTrue(SpeechEnergy.isEffectivelySilent(RecordedAudio(samples: tone(count: 4_600, rate: 16_000))),
                      "Shorter than 0.3 s")
        XCTAssertTrue(SpeechEnergy.isEffectivelySilent(RecordedAudio(samples: [Float](repeating: 0, count: 16_000))))
        XCTAssertTrue(SpeechEnergy.isEffectivelySilent(RecordedAudio(samples: (0..<16_000).map { $0 % 2 == 0 ? 0.002 : -0.002 })),
                      "Room noise")
        XCTAssertTrue(SpeechEnergy.isEffectivelySilent(RecordedAudio(samples: [Float](repeating: 0.1, count: 16_000))),
                      "A DC offset is not sound")

        var click = [Float](repeating: 0, count: 16_000)
        for index in 8_000..<8_200 {
            click[index] = 0.5
            if index % 2 == 0 { click[index] = -0.5 }
        }
        XCTAssertTrue(SpeechEnergy.isEffectivelySilent(RecordedAudio(samples: click)), "A single click is not speech")

        XCTAssertFalse(SpeechEnergy.isEffectivelySilent(RecordedAudio(samples: tone(count: 16_000, rate: 16_000, amplitude: 0.05))))
        XCTAssertFalse(SpeechEnergy.isEffectivelySilent(RecordedAudio(samples: tone(count: 96_000, rate: 48_000),
                                                                     sampleRate: 48_000)))
    }

    func testRMSAndMeterLevel() {
        XCTAssertEqual(SpeechEnergy.rms([3, 4][...]), Float(12.5).squareRoot(), accuracy: 0.0001)
        XCTAssertEqual(SpeechEnergy.rms([][...]), 0)
        XCTAssertEqual(SpeechEnergy.meterLevel(rms: 0), 0)
        XCTAssertEqual(SpeechEnergy.meterLevel(rms: 1), 1)
        XCTAssertEqual(SpeechEnergy.meterLevel(rms: 0.00001), 0)
        XCTAssertEqual(SpeechEnergy.meterLevel(rms: Float(pow(10, -35.0 / 20))), 0.5, accuracy: 0.001)
    }

    func testLevelRisesFasterThanItFalls() {
        let rising = MicrophoneRecorder.smoothedLevel(previous: 0, target: 1)
        let falling = 1 - MicrophoneRecorder.smoothedLevel(previous: 1, target: 0)
        XCTAssertGreaterThan(rising, falling)
        XCTAssertEqual(MicrophoneRecorder.smoothedLevel(previous: 0.5, target: 0.5), 0.5)
    }

    // MARK: - Capture conversion

    func testConverterTurnsStereo48kIntoMono16k() throws {
        let converter = try XCTUnwrap(CaptureConverter(targetRate: 16_000))
        let format = try XCTUnwrap(AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 2,
                                                 interleaved: false))
        let source = tone(count: 48_000, rate: 48_000, frequency: 440, amplitude: 0.5)
        var output: [Float] = []
        var offset = 0
        while offset < source.count {
            let count = min(4_800, source.count - offset)
            let buffer = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(count)))
            let channels = try XCTUnwrap(buffer.floatChannelData)
            for frame in 0..<count {
                channels[0][frame] = source[offset + frame]
                channels[1][frame] = source[offset + frame]
            }
            buffer.frameLength = AVAudioFrameCount(count)
            output += try converter.convert(buffer)
            offset += count
        }

        XCTAssertEqual(Double(output.count), 16_000, accuracy: 400)
        let middle = output[4_000..<12_000]
        XCTAssertEqual(SpeechEnergy.rms(middle), 0.5 / Float(2).squareRoot(), accuracy: 0.03)
    }

    func testConverterDownmixesInterleavedIntegersWithoutResampling() throws {
        let converter = try XCTUnwrap(CaptureConverter(targetRate: 16_000))
        let format = try XCTUnwrap(AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16_000, channels: 2,
                                                 interleaved: true))
        let buffer = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 100))
        let data = try XCTUnwrap(buffer.int16ChannelData)
        for frame in 0..<100 {
            data[0][frame * 2] = 16_384
            data[0][frame * 2 + 1] = 0
        }
        buffer.frameLength = 100

        let mono = try converter.convert(buffer)
        XCTAssertEqual(mono.count, 100)
        XCTAssertEqual(mono[50], 0.25, accuracy: 0.001)
    }

    func testCaptureBufferKeepsSamplesAndRecentLevel() {
        let buffer = CaptureBuffer()
        buffer.append([Float](repeating: 0.5, count: 2_000))
        XCTAssertEqual(buffer.recentRMS, 0.5, accuracy: 0.0001)
        buffer.append([])
        XCTAssertEqual(buffer.takeSamples().count, 2_000)
        XCTAssertTrue(buffer.takeSamples().isEmpty)
        XCTAssertEqual(buffer.recentRMS, 0)
    }

    @MainActor
    func testRecorderThatNeverStartedReturnsNothing() {
        let recorder = MicrophoneRecorder()
        XCTAssertFalse(recorder.isRecording)
        XCTAssertEqual(recorder.stop(), .empty)
        recorder.cancel()
        XCTAssertFalse(recorder.isRecording)
    }

    // MARK: - Devices and sounds

    func testDeviceListIsConsistent() {
        let devices = AudioDevices.inputDevices()
        XCTAssertEqual(Set(devices.map(\.uid)).count, devices.count)
        for device in devices {
            XCTAssertEqual(AudioDevices.device(uid: device.uid), device)
        }
        XCTAssertNil(AudioDevices.device(uid: "no-such-device-\(UUID().uuidString)"))
        if let fallback = AudioDevices.defaultInputDevice() {
            XCTAssertFalse(fallback.uid.isEmpty)
        }
    }

    func testSoundsExist() {
        for cue in [SoundCue.start, .stop, .cancel] {
            XCTAssertNotNil(NSSound(named: NSSound.Name(DictationSounds.soundName(for: cue))), cue.rawValue)
        }
    }
}
