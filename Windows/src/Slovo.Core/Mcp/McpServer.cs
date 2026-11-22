namespace Slovo.Core.Mcp;

/// <summary>
/// MCP over stdio: one JSON-RPC message per line in, at most one reply line out, handled one
/// at a time in arrival order (port of the macOS <c>MCPServer</c>). The server never starts
/// requests or notifications of its own.
/// </summary>
public sealed class McpServer(Stream input, Stream output, McpHandler handler)
{
    /// <summary>A longer line is drained to its newline and answered with a parse error.</summary>
    public const int MaximumLineBytes = 1 << 20;

    const int ReadChunkBytes = 64 * 1024;
    const byte Newline = (byte)'\n';

    /// <summary>Serves requests until input ends, reading fails, or a reply cannot be written.</summary>
    public void Run()
    {
        var pending = new MemoryStream();
        // True while the rest of an oversized line is being thrown away.
        var discarding = false;
        var chunk = new byte[ReadChunkBytes];
        while (true)
        {
            int count;
            try
            {
                count = input.Read(chunk, 0, chunk.Length);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // A client that closed its end mid-read is gone: the same as the end of input.
                return;
            }
            if (count == 0)
            {
                // A last message without a trailing newline is still a message.
                if (discarding)
                {
                    Send(McpHandler.OversizedLineReply());
                }
                else if (pending.Length > 0)
                {
                    Process(pending.ToArray());
                }
                return;
            }

            var start = 0;
            while (start < count)
            {
                var newline = Array.IndexOf(chunk, Newline, start, count - start);
                if (newline < 0)
                {
                    break;
                }
                var length = pending.Length + (newline - start);
                if (discarding || length > MaximumLineBytes)
                {
                    discarding = false;
                    pending.SetLength(0);
                    if (!Send(McpHandler.OversizedLineReply()))
                    {
                        return;
                    }
                }
                else
                {
                    pending.Write(chunk, start, newline - start);
                    if (!Process(pending.ToArray()))
                    {
                        return;
                    }
                    pending.SetLength(0);
                }
                start = newline + 1;
            }
            if (start < count && !discarding)
            {
                if (pending.Length + (count - start) > MaximumLineBytes)
                {
                    // Keeping the line would let one client grow memory without bound.
                    pending = new MemoryStream();
                    discarding = true;
                }
                else
                {
                    pending.Write(chunk, start, count - start);
                }
            }
        }
    }

    /// <summary>Returns false when the reply could not be written.</summary>
    bool Process(byte[] line)
    {
        var length = line.Length;
        if (length > 0 && line[length - 1] == (byte)'\r')
        {
            length--;
        }
        var bytes = line.AsSpan(0, length);
        var blank = true;
        foreach (var value in bytes)
        {
            if (value != (byte)' ' && value != (byte)'\t')
            {
                blank = false;
                break;
            }
        }
        if (blank)
        {
            return true;
        }
        var reply = handler.Handle(bytes);
        return reply is null || Send(reply);
    }

    bool Send(byte[] reply)
    {
        var message = new byte[reply.Length + 1];
        reply.CopyTo(message, 0);
        message[^1] = Newline;
        try
        {
            output.Write(message, 0, message.Length);
            output.Flush();
            return true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or NotSupportedException)
        {
            // A broken pipe: the client is gone.
            return false;
        }
    }
}
