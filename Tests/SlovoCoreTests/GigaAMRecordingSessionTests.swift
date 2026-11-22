import XCTest
@testable import SlovoCore

@MainActor
final class GigaAMRecordingSessionTests: XCTestCase {
    private final class Decoder: GigaAMChunkDecoding {
        let started = DictationFakes.Gate()
        var gate: DictationFakes.Gate?
        var windows: [Range<Int>] = []
        var tail: ArraySlice<Float>?
        var prefix: [String]?
        var chunkError: Error?
        var tailError: Error?

        func decodeFinalizedChunk(from capture: CaptureBuffer, window: Range<Int>) async throws -> GigaAMChunk? {
            guard capture.finalizedWindow(window) != nil else { return nil }
            windows.append(window)
            started.open()
            await gate?.wait()
            if let chunkError { throw chunkError }
            // Deliberately ignore cancellation to emulate a call already inside sherpa-onnx.
            return GigaAMChunk(end: window.upperBound - 100, text: "  Первая\t часть. ")
        }

        func decodeTail(_ raw: ArraySlice<Float>, after prefix: [String]) async throws -> String {
            tail = raw
            self.prefix = prefix
            if let tailError { throw tailError }
            return GigaAMEngine.join(prefix + [" English\nтекст. "])
        }
    }

    private func captureWindow() -> CaptureBuffer {
        let capture = CaptureBuffer()
        capture.append(Array(repeating: 0.25, count: GigaAMChunker.windowSamples + 300))
        return capture
    }

    func testFinishWaitsForOnePendingChunkAndJoinsItsTextWithTheExactTail() async throws {
        let capture = captureWindow()
        let decoder = Decoder()
        let gate = DictationFakes.Gate()
        decoder.gate = gate
        let session = GigaAMRecordingSession(capture: capture) { decoder }
        session.advance()
        await decoder.started.wait()
        session.advance()
        session.stop()
        let audio = RecordedAudio(samples: capture.takeSamples())
        let finish = Task { try await session.finish(audio: audio, decoder: decoder) }
        gate.open()

        let result = try await finish.value
        XCTAssertEqual(result, "Первая часть. English текст.")
        XCTAssertEqual(decoder.windows, [0..<GigaAMChunker.windowSamples])
        XCTAssertEqual(decoder.tail?.startIndex, GigaAMChunker.windowSamples - 100)
        XCTAssertEqual(decoder.tail?.count, 400)
        XCTAssertEqual(decoder.prefix, ["  Первая\t часть. "])
    }

    func testReleaseAtExactlyTwentySecondsUsesTheFullDecodePath() async throws {
        let capture = CaptureBuffer()
        capture.append(Array(repeating: 0.25, count: GigaAMChunker.windowSamples))
        let decoder = Decoder()
        let session = GigaAMRecordingSession(capture: capture) { decoder }
        session.advance()
        session.stop()

        let result = try await session.finish(audio: RecordedAudio(samples: capture.takeSamples()), decoder: decoder)
        XCTAssertNil(result)
        XCTAssertTrue(decoder.windows.isEmpty)
        XCTAssertNil(decoder.tail)
    }

    func testCancelDiscardsTheResultOfAnUninterruptibleBackgroundDecode() async throws {
        let capture = captureWindow()
        let decoder = Decoder()
        let gate = DictationFakes.Gate()
        decoder.gate = gate
        let session = GigaAMRecordingSession(capture: capture) { decoder }
        session.advance()
        await decoder.started.wait()
        session.cancel()
        gate.open()

        let result = try await session.finish(audio: RecordedAudio(samples: capture.takeSamples()), decoder: decoder)
        XCTAssertNil(result)
        XCTAssertNil(decoder.tail)
    }

    func testSampleRateChangeDiscardsBackgroundWork() async throws {
        let capture = captureWindow()
        let decoder = Decoder()
        let session = GigaAMRecordingSession(capture: capture) { decoder }
        session.advance()
        await decoder.started.wait()
        capture.append([0.5], sourceRate: 48_000)
        session.stop()

        let result = try await session.finish(audio: RecordedAudio(samples: capture.takeSamples()), decoder: decoder)
        XCTAssertNil(result)
        XCTAssertNil(decoder.tail)
    }

    func testEngineReplacementDiscardsBackgroundWork() async throws {
        let capture = captureWindow()
        let decoder = Decoder()
        let replacement = Decoder()
        let session = GigaAMRecordingSession(capture: capture) { decoder }
        session.advance()
        await decoder.started.wait()
        session.stop()

        let result = try await session.finish(audio: RecordedAudio(samples: capture.takeSamples()), decoder: replacement)
        XCTAssertNil(result)
        XCTAssertNil(decoder.tail)
        XCTAssertNil(replacement.tail)
    }

    func testEveryChunkAsksTheEngineCacheAndAReplacedEngineDiscardsThePrefix() async throws {
        let capture = CaptureBuffer()
        capture.append(Array(repeating: 0.25, count: 2 * GigaAMChunker.windowSamples + 300))
        let decoder = Decoder()
        let replacement = Decoder()
        var requests = 0
        let session = GigaAMRecordingSession(capture: capture) {
            requests += 1
            return requests == 1 ? decoder : replacement
        }
        for _ in 0..<1_000 where requests < 2 {
            session.advance()
            await Task.yield()
        }
        for _ in 0..<100 { await Task.yield() }
        session.stop()

        XCTAssertEqual(requests, 2)
        let result = try await session.finish(audio: RecordedAudio(samples: capture.takeSamples()), decoder: replacement)
        XCTAssertNil(result)
        XCTAssertEqual(decoder.windows.count, 1)
        XCTAssertTrue(replacement.windows.isEmpty)
        XCTAssertNil(replacement.tail)
    }

    func testBackgroundErrorUsesTheFullDecodePath() async throws {
        let capture = captureWindow()
        let decoder = Decoder()
        decoder.chunkError = SlovoError("chunk failed")
        let session = GigaAMRecordingSession(capture: capture) { decoder }
        session.advance()
        await decoder.started.wait()
        session.stop()

        let result = try await session.finish(audio: RecordedAudio(samples: capture.takeSamples()), decoder: decoder)
        XCTAssertNil(result)
        XCTAssertNil(decoder.tail)
    }

    func testTailErrorUsesTheFullDecodePath() async throws {
        let capture = captureWindow()
        let decoder = Decoder()
        decoder.tailError = SlovoError("tail failed")
        let session = GigaAMRecordingSession(capture: capture) { decoder }
        session.advance()
        await decoder.started.wait()
        session.stop()

        let result = try await session.finish(audio: RecordedAudio(samples: capture.takeSamples()), decoder: decoder)
        XCTAssertNil(result)
        XCTAssertNotNil(decoder.tail)
    }
}
