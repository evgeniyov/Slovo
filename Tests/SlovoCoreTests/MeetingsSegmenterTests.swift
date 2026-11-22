import Foundation
import XCTest
@testable import SlovoCore

/// `SpeechSegmenter` on synthetic signals: pauses, long speech, silence, clicks and
/// the streaming file path.
final class MeetingsSegmenterTests: XCTestCase {
    private let rate = 16_000
    private var temporary: URL!

    override func setUpWithError() throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoMeetingsSegmenterTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: temporary)
    }

    func testSilenceAndQuietNoiseHaveNoSegments() {
        let segmenter = SpeechSegmenter()
        XCTAssertEqual(segmenter.segments(for: []), [])
        XCTAssertEqual(segmenter.segments(for: silence(30)), [])
        // RMS 0.004 stays under the default 0.005 threshold.
        let hum = (0..<(rate * 5)).map { $0 % 2 == 0 ? Float(0.004) : Float(-0.004) }
        XCTAssertEqual(segmenter.segments(for: hum), [])
        XCTAssertEqual(SpeechSegmenter(silenceRMS: 0.5).segments(for: tone(3)), [])
    }

    func testPausesSeparateSegmentsWithPadding() throws {
        let signal = silence(2) + tone(3) + silence(4) + tone(2) + silence(1)
        let segments = SpeechSegmenter().segments(for: signal)
        XCTAssertEqual(segments.count, 2)
        let first = try XCTUnwrap(segments.first)
        let second = try XCTUnwrap(segments.last)
        XCTAssertEqual(first.start, 1.8, accuracy: 0.001)
        XCTAssertEqual(first.end, 5.2, accuracy: 0.001)
        XCTAssertEqual(second.start, 8.8, accuracy: 0.001)
        XCTAssertEqual(second.end, 11.2, accuracy: 0.001)
        XCTAssertEqual(first.samples, 28_800..<83_200)
        XCTAssertEqual(Double(second.samples.lowerBound) / Double(rate), second.start, accuracy: 1e-9)
    }

    func testShortPauseKeepsOneSegment() throws {
        let signal = tone(2) + silence(0.5) + tone(2)
        let segments = SpeechSegmenter().segments(for: signal)
        XCTAssertEqual(segments.count, 1)
        XCTAssertEqual(try XCTUnwrap(segments.first).samples, 0..<signal.count)
    }

    func testLongSpeechIsCutAtQuietMomentsIntoPiecesOfAtMostMaxSeconds() throws {
        // Continuous speech with two short dips, each shorter than a pause.
        let signal = tone(17) + silence(0.3) + tone(17.7) + silence(0.3) + tone(14.7)
        let segments = SpeechSegmenter().segments(for: signal)
        XCTAssertEqual(segments.count, 3)
        for segment in segments {
            XCTAssertLessThanOrEqual(segment.end - segment.start, 20 + 1e-9)
        }
        XCTAssertEqual(segments[0].samples.lowerBound, 0)
        XCTAssertEqual(segments[0].end, 17.15, accuracy: 0.1)
        XCTAssertEqual(segments[1].end, 35.15, accuracy: 0.1)
        XCTAssertEqual(try XCTUnwrap(segments.last).samples.upperBound, signal.count)
        for (left, right) in zip(segments, segments.dropFirst()) {
            XCTAssertEqual(left.samples.upperBound, right.samples.lowerBound)
        }
    }

    func testSteadySoundWithoutPausesIsChunkedCompletely() {
        let signal = tone(45)
        let segments = SpeechSegmenter().segments(for: signal)
        XCTAssertEqual(segments.count, 3)
        XCTAssertEqual(segments.first?.samples.lowerBound, 0)
        XCTAssertEqual(segments.last?.samples.upperBound, signal.count)
        XCTAssertTrue(segments.allSatisfy { $0.samples.count <= 20 * rate })
    }

    func testMaxSecondsIsConfigurable() {
        let segments = SpeechSegmenter(maxSeconds: 5).segments(for: tone(12))
        XCTAssertEqual(segments.count, 3)
        XCTAssertTrue(segments.allSatisfy { $0.end - $0.start <= 5 + 1e-9 })
    }

    func testClicksAreNotSpeech() {
        let signal = silence(2) + tone(0.05, amplitude: 0.8) + silence(2)
        XCTAssertEqual(SpeechSegmenter().segments(for: signal), [])
    }

    func testOtherSampleRatesUseTheirOwnTimeline() throws {
        let rate44 = 44_100
        let signal = [Float](repeating: 0, count: rate44 * 2)
            + (0..<(rate44 * 2)).map { Float(0.2 * sin(2 * Double.pi * 220 * Double($0) / Double(rate44))) }
        let segment = try XCTUnwrap(SpeechSegmenter().segments(for: signal, sampleRate: rate44).first)
        XCTAssertEqual(segment.start, 1.8, accuracy: 0.03)
        XCTAssertEqual(segment.end, 4, accuracy: 0.03)
    }

    func testFileSegmentsMatchInMemorySegments() throws {
        let signal = silence(1.5) + tone(4) + silence(3) + tone(25) + silence(2)
        let url = temporary.appendingPathComponent("track.caf")
        let writer = try MeetingTrackWriter(url: url)
        for start in stride(from: 0, to: signal.count, by: 4_000) {
            writer.append(Array(signal[start..<min(signal.count, start + 4_000)]))
        }
        writer.finish()

        let fromFile = try SpeechSegmenter().segments(inFileAt: url)
        let inMemory = SpeechSegmenter().segments(for: signal)
        XCTAssertEqual(fromFile, inMemory)
        XCTAssertEqual(fromFile.count, 3)
    }

    // MARK: - Signals

    private func tone(_ seconds: Double, amplitude: Float = 0.2, frequency: Double = 220) -> [Float] {
        (0..<Int(seconds * Double(rate))).map {
            amplitude * Float(sin(2 * Double.pi * frequency * Double($0) / Double(rate)))
        }
    }

    private func silence(_ seconds: Double) -> [Float] {
        [Float](repeating: 0, count: Int(seconds * Double(rate)))
    }
}
