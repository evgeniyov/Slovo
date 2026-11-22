using System.IO.Pipes;
using System.Text;
using Slovo.Core.Mcp;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Mcp;

/// <summary>McpServer framing: one reply line per request, oversized lines, end of input and failed writes.</summary>
public sealed class McpTransportTests
{
    static McpHandler MakeHandler() => new(new StaticArchive(), () => new McpAccess(true), () => AppLanguage.En, () => McpFixture.Now,
        "1.2.0", timeZone: McpFixture.TimeZone, clock: new FakeClock());

    /// <summary>Hands out its chunks one read at a time, the way a pipe delivers separate writes.</summary>
    sealed class ChunkedStream(IEnumerable<byte[]> chunks) : Stream
    {
        readonly Queue<byte[]> pending = new(chunks);
        byte[]? current;
        int offset;

        public override int Read(byte[] buffer, int start, int count)
        {
            while (current is null || offset >= current.Length)
            {
                if (!pending.TryDequeue(out current))
                {
                    return 0;
                }
                offset = 0;
            }
            var length = Math.Min(count, current.Length - offset);
            Array.Copy(current, offset, buffer, start, length);
            offset += length;
            return length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>An output whose reader went away.</summary>
    sealed class BrokenStream : Stream
    {
        public int Writes { get; private set; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            throw new IOException("Broken pipe");
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    static string Serve(params byte[][] chunks)
    {
        using var output = new MemoryStream();
        new McpServer(new ChunkedStream(chunks), output, MakeHandler()).Run();
        return Encoding.UTF8.GetString(output.ToArray());
    }

    static List<JsonValue> Replies(string output)
    {
        Assert.True(output.Length == 0 || output.EndsWith('\n'));
        Assert.DoesNotContain('\r', output);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JsonValue.Parse).ToList();
    }

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void OneReplyLinePerRequest()
    {
        string[] lines =
        [
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            "",
            "   ",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""" + "\r",
            """{"jsonrpc":"2.0","id":"a\nb","method":"ping"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_meetings","arguments":{}}}""",
        ];
        var replies = Replies(Serve(Bytes(string.Join("\n", lines) + "\n")));
        Assert.Equal([JsonValue.Integer(1), JsonValue.Integer(2), JsonValue.Str("a\nb"), JsonValue.Integer(3)],
            replies.Select(reply => reply["id"]!));
    }

    [Fact]
    public void OversizedLineIsAnsweredAndTheServerContinues()
    {
        var oversized = Enumerable.Repeat((byte)'a', McpServer.MaximumLineBytes + 10).Append((byte)'\n').ToArray();
        var ping = Bytes("""{"jsonrpc":"2.0","id":2,"method":"ping"}""" + "\n");
        var exactText = """{"jsonrpc":"2.0","id":3,"method":"ping"}""";
        var exact = Bytes(exactText + new string(' ', McpServer.MaximumLineBytes - exactText.Length) + "\n");
        Assert.Equal("""{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}""" + "\n"
            + """{"id":2,"jsonrpc":"2.0","result":{}}""" + "\n" + """{"id":3,"jsonrpc":"2.0","result":{}}""" + "\n",
            Serve(oversized, ping, exact));
    }

    [Fact]
    public void OversizedLineArrivingInSmallWrites()
    {
        var line = Enumerable.Repeat((byte)'b', McpServer.MaximumLineBytes * 2).ToArray();
        var chunks = line.Chunk(50_000).ToList();
        chunks.Add(Bytes("\n" + """{"jsonrpc":"2.0","id":7,"method":"ping"}""" + "\n"));
        var replies = Replies(Serve([.. chunks]));
        Assert.Equal([-32700, null], replies.Select(McpClient.ErrorCode));
        Assert.Equal(JsonValue.Integer(7), replies[^1]["id"]);
    }

    [Fact]
    public void EndOfInputEndsTheRunAndALastLineWithoutNewlineIsServed()
    {
        Assert.Equal("""{"id":9,"jsonrpc":"2.0","result":{}}""" + "\n", Serve(Bytes("""{"jsonrpc":"2.0","id":9,"method":"ping"}""")));
        Assert.Equal("", Serve());
        // An oversized last line without its newline is still answered.
        Assert.Equal(-32700, McpClient.ErrorCode(Replies(Serve(Enumerable.Repeat((byte)'c', McpServer.MaximumLineBytes + 1).ToArray())).Single()));
    }

    [Fact]
    public void FailedWriteEndsTheRun()
    {
        var output = new BrokenStream();
        // More requests are waiting: only the failed write can end the loop this early.
        new McpServer(new ChunkedStream([Bytes("""{"jsonrpc":"2.0","id":1,"method":"ping"}""" + "\n"),
            Bytes("""{"jsonrpc":"2.0","id":2,"method":"ping"}""" + "\n")]), output, MakeHandler()).Run();
        Assert.Equal(1, output.Writes);
    }

    /// <summary>Over real operating-system pipes, the way Claude talks to the process.</summary>
    [Fact]
    public async Task ServesOverOperatingSystemPipes()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverInput = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var fromServer = new AnonymousPipeServerStream(PipeDirection.In);
        using var serverOutput = new AnonymousPipeClientStream(PipeDirection.Out, fromServer.ClientSafePipeHandle);
        var server = Task.Run(() => new McpServer(serverInput, serverOutput, MakeHandler()).Run(), TestContext.Current.CancellationToken);

        var reader = new StreamReader(fromServer, new UTF8Encoding(false));
        toServer.Write(Bytes("""{"jsonrpc":"2.0","id":1,"method":"ping"}""" + "\n"));
        toServer.Flush();
        Assert.Equal("""{"id":1,"jsonrpc":"2.0","result":{}}""", reader.ReadLine());
        toServer.Write(Bytes("""{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n" + """{"jsonrpc":"2.0","id":2,"method":"ping"}"""));
        toServer.Dispose();
        await server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        serverOutput.Dispose();
        Assert.Equal("""{"id":2,"jsonrpc":"2.0","result":{}}""", reader.ReadLine());
        Assert.Null(reader.ReadLine());
    }
}
