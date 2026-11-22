import Foundation
import XCTest
@testable import SlovoCore

/// Bytes collected on another thread.
final class MCPLockedBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()

    func append(_ bytes: ArraySlice<UInt8>) {
        lock.lock()
        data.append(contentsOf: bytes)
        lock.unlock()
    }

    var string: String {
        lock.lock()
        defer { lock.unlock() }
        return String(decoding: data, as: UTF8.self)
    }
}

/// Lets a server cross into the thread that runs it.
private final class MCPServerBox: @unchecked Sendable {
    let server: MCPServer

    init(_ server: MCPServer) {
        self.server = server
    }
}

/// `MCPServer` over real pipes: framing, oversized lines, end of input and failed writes.
final class MCPTransportTests: XCTestCase {
    private struct Run {
        let output: String
        let finished: Bool
    }

    private func makeHandler() -> MCPHandler {
        MCPTestClient.handler(archive: MCPStaticArchive([MCPFixture.coworkRecord()]))
    }

    private func writeAll(_ data: Data, to descriptor: Int32) {
        data.withUnsafeBytes { buffer in
            guard let base = buffer.baseAddress else { return }
            var offset = 0
            while offset < buffer.count {
                let written = write(descriptor, base + offset, buffer.count - offset)
                if written < 0 {
                    if errno == EINTR { continue }
                    return
                }
                offset += written
            }
        }
    }

    private func serve(_ chunks: [Data]) -> Run {
        var input: [Int32] = [0, 0]
        var output: [Int32] = [0, 0]
        guard pipe(&input) == 0, pipe(&output) == 0 else {
            XCTFail("pipe failed")
            return Run(output: "", finished: false)
        }
        let box = MCPServerBox(MCPServer(input: input[0], output: output[1], handler: makeHandler()))
        let serverDone = DispatchSemaphore(value: 0)
        let serverOutput = output[1]
        Thread.detachNewThread {
            box.server.run()
            close(serverOutput)
            serverDone.signal()
        }
        let collected = MCPLockedBuffer()
        let readerDone = DispatchSemaphore(value: 0)
        let readEnd = output[0]
        Thread.detachNewThread {
            var buffer = [UInt8](repeating: 0, count: 65_536)
            while true {
                let count = read(readEnd, &buffer, buffer.count)
                if count < 0, errno == EINTR { continue }
                if count <= 0 { break }
                collected.append(buffer[0..<count])
            }
            readerDone.signal()
        }
        for chunk in chunks {
            writeAll(chunk, to: input[1])
        }
        close(input[1])
        let finished = serverDone.wait(timeout: .now() + 30) == .success
        _ = readerDone.wait(timeout: .now() + 30)
        close(input[0])
        close(readEnd)
        return Run(output: collected.string, finished: finished)
    }

    private func replies(_ output: String) -> [JSONValue] {
        XCTAssertTrue(output.isEmpty || output.hasSuffix("\n"))
        return output.split(separator: "\n").map { line in
            let value = try? JSONValue(parsing: Data(line.utf8))
            XCTAssertNotNil(value, "not one JSON message per line: \(line)")
            return value ?? .null
        }
    }

    func testOneReplyLinePerRequest() {
        let lines = [
            #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}"#,
            #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#,
            "",
            "   ",
            #"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"# + "\r",
            #"{"jsonrpc":"2.0","id":"a\nb","method":"ping"}"#,
            #"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_transcript","arguments":{"meeting_id":"latest"}}}"#
        ]
        let run = serve([Data((lines.joined(separator: "\n") + "\n").utf8)])
        XCTAssertTrue(run.finished)
        let parsed = replies(run.output)
        XCTAssertEqual(parsed.count, 4)
        XCTAssertEqual(parsed.compactMap { $0["id"] }, [1, 2, "a\nb", 3])
        XCTAssertEqual(parsed.last?["result"]?["content"]?.arrayValue?.first?["text"]?.stringValue?.contains("\n"), true)
    }

    func testOversizedLineIsAnsweredAndTheServerContinues() {
        var oversized = Data(repeating: UInt8(ascii: "a"), count: MCPServer.maximumLineBytes + 10)
        oversized.append(UInt8(ascii: "\n"))
        let ping = Data((#"{"jsonrpc":"2.0","id":2,"method":"ping"}"# + "\n").utf8)
        var exact = Data(#"{"jsonrpc":"2.0","id":3,"method":"ping"}"#.utf8)
        exact.append(Data(repeating: UInt8(ascii: " "), count: MCPServer.maximumLineBytes - exact.count))
        exact.append(UInt8(ascii: "\n"))

        let run = serve([oversized, ping, exact])
        XCTAssertTrue(run.finished)
        XCTAssertEqual(run.output, #"{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"# + "\n"
                       + #"{"id":2,"jsonrpc":"2.0","result":{}}"# + "\n" + #"{"id":3,"jsonrpc":"2.0","result":{}}"# + "\n")
    }

    func testOversizedLineArrivingInSmallWrites() {
        let line = Data(repeating: UInt8(ascii: "b"), count: MCPServer.maximumLineBytes * 2)
        var chunks = stride(from: 0, to: line.count, by: 50_000).map { line.subdata(in: $0..<min($0 + 50_000, line.count)) }
        chunks.append(Data(("\n" + #"{"jsonrpc":"2.0","id":7,"method":"ping"}"# + "\n").utf8))
        let run = serve(chunks)
        XCTAssertTrue(run.finished)
        XCTAssertEqual(replies(run.output).map { MCPTestClient.errorCode($0) }, [-32700, nil])
        XCTAssertEqual(replies(run.output).last?["id"], 7)
    }

    func testEndOfInputEndsRunAndALastLineWithoutNewlineIsServed() {
        let run = serve([Data(#"{"jsonrpc":"2.0","id":9,"method":"ping"}"#.utf8)])
        XCTAssertTrue(run.finished)
        XCTAssertEqual(run.output, #"{"id":9,"jsonrpc":"2.0","result":{}}"# + "\n")
        let empty = serve([])
        XCTAssertTrue(empty.finished)
        XCTAssertEqual(empty.output, "")
    }

    func testFailedWriteEndsRun() {
        var input: [Int32] = [0, 0]
        var output: [Int32] = [0, 0]
        guard pipe(&input) == 0, pipe(&output) == 0 else { return XCTFail("pipe failed") }
        // The reader is gone, so the reply cannot be written; no SIGPIPE for the test runner.
        close(output[0])
        _ = fcntl(output[1], F_SETNOSIGPIPE, 1)
        let box = MCPServerBox(MCPServer(input: input[0], output: output[1], handler: makeHandler()))
        let serverDone = DispatchSemaphore(value: 0)
        Thread.detachNewThread {
            box.server.run()
            serverDone.signal()
        }
        writeAll(Data((#"{"jsonrpc":"2.0","id":1,"method":"ping"}"# + "\n").utf8), to: input[1])
        // Input stays open: only the failed write can end the loop.
        XCTAssertEqual(serverDone.wait(timeout: .now() + 30), .success)
        close(input[1])
        close(input[0])
        close(output[1])
    }
}
