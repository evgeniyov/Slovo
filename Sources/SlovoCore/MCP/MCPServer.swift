import Foundation

/// MCP over stdio: one JSON-RPC message per line in, at most one reply line out, handled
/// one at a time in arrival order. The server never starts requests or notifications of
/// its own.
public final class MCPServer {
    /// A longer line is drained to its newline and answered with a parse error.
    public static let maximumLineBytes = 1 << 20

    private static let readChunkBytes = 64 * 1024
    private static let newline = UInt8(ascii: "\n")

    private let input: Int32
    private let output: Int32
    private let handler: MCPHandler

    public init(input: Int32, output: Int32, handler: MCPHandler) {
        self.input = input
        self.output = output
        self.handler = handler
    }

    /// Serves requests until input ends or a write fails.
    public func run() {
        var pending: [UInt8] = []
        // True while the rest of an oversized line is being thrown away.
        var discarding = false
        var chunk = [UInt8](repeating: 0, count: Self.readChunkBytes)
        while true {
            let count = chunk.withUnsafeMutableBytes { buffer in
                read(input, buffer.baseAddress, buffer.count)
            }
            if count < 0 {
                if errno == EINTR { continue }
                return
            }
            if count == 0 {
                // A last message without a trailing newline is still a message.
                if discarding {
                    _ = send(MCPHandler.oversizedLineReply())
                } else if !pending.isEmpty {
                    _ = process(pending)
                }
                return
            }

            var start = 0
            while let newline = chunk[start..<count].firstIndex(of: Self.newline) {
                let length = pending.count + (newline - start)
                if discarding || length > Self.maximumLineBytes {
                    discarding = false
                    pending.removeAll(keepingCapacity: true)
                    guard send(MCPHandler.oversizedLineReply()) else { return }
                } else {
                    pending.append(contentsOf: chunk[start..<newline])
                    guard process(pending) else { return }
                    pending.removeAll(keepingCapacity: true)
                }
                start = newline + 1
            }
            if start < count, !discarding {
                if pending.count + (count - start) > Self.maximumLineBytes {
                    // Keeping the line would let one client grow memory without bound.
                    pending.removeAll(keepingCapacity: false)
                    discarding = true
                } else {
                    pending.append(contentsOf: chunk[start..<count])
                }
            }
        }
    }

    /// Returns false when the reply could not be written.
    private func process(_ line: [UInt8]) -> Bool {
        var bytes = line[...]
        if bytes.last == UInt8(ascii: "\r") {
            bytes = bytes.dropLast()
        }
        guard bytes.contains(where: { $0 != UInt8(ascii: " ") && $0 != UInt8(ascii: "\t") }) else {
            return true
        }
        guard let reply = handler.handle(line: Data(bytes)) else { return true }
        return send(reply)
    }

    private func send(_ reply: Data) -> Bool {
        var message = [UInt8](reply)
        message.append(Self.newline)
        var offset = 0
        while offset < message.count {
            let written = message.withUnsafeBytes { buffer in
                write(output, buffer.baseAddress.map { $0 + offset }, buffer.count - offset)
            }
            if written < 0 {
                if errno == EINTR { continue }
                return false
            }
            offset += written
        }
        return true
    }
}
