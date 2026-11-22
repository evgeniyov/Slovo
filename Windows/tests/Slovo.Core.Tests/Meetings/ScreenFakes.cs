using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Meetings;

/// <summary>A screen the test paints by hand: the display or one app's window.</summary>
sealed class FakeScreenStream(ScreenCaptureTarget target) : IScreenCaptureStream
{
    readonly Queue<(byte[] Pixels, int Width, int Height)> pictures = new();

    public ScreenCaptureTarget Target { get; } = target;
    public bool IsDisposed { get; private set; }
    /// <summary>Set to end the capture by itself (the window closed, the display went away).</summary>
    public bool IsLost { get; set; }
    /// <summary>Thrown by the next read.</summary>
    public Exception? ReadError { get; set; }
    public string? LostReason => IsLost ? "gone" : null;
    public int Reads { get; private set; }

    /// <summary>Queues a picture of one colour for the next read.</summary>
    public void Show(int width, int height, byte blue, byte green, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = green;
            pixels[i + 2] = red;
            pixels[i + 3] = 255;
        }
        pictures.Enqueue((pixels, width, height));
    }

    public ScreenFrameResult ReadFrame(BgraFrameHandler handler)
    {
        Reads++;
        if (ReadError is { } error)
        {
            throw error;
        }
        if (IsLost)
        {
            return ScreenFrameResult.Lost;
        }
        if (!pictures.TryDequeue(out var picture))
        {
            return ScreenFrameResult.Unchanged;
        }
        handler(picture.Pixels, picture.Width, picture.Height, picture.Width * 4);
        return ScreenFrameResult.Frame;
    }

    public void Dispose() => IsDisposed = true;
}

/// <summary>
/// An encoder that writes a small made-up H.264 stream: a key frame carries a sequence and a
/// picture parameter set and an IDR slice, any other frame one non-IDR slice. Enough for the
/// MP4 writer, which never decodes.
/// </summary>
sealed class FakeVideoEncoder(int width, int height) : IVideoEncoder
{
    public static readonly byte[] SequenceParameterSet = [0x67, 0x42, 0xC0, 0x1E, 0xDA, 0x02, 0x80, 0xBF, 0xE5];
    public static readonly byte[] PictureParameterSet = [0x68, 0xCE, 0x3C, 0x80];

    public int Width { get; } = width;
    public int Height { get; } = height;
    public List<(double Time, bool KeyFrame)> Encoded { get; } = [];
    /// <summary>The first luma byte of each picture, to tell what was drawn.</summary>
    public List<byte> FirstLuma { get; } = [];
    public Exception? EncodeError { get; set; }
    /// <summary>Holds each picture back by one call, as a real encoder with a little latency does.</summary>
    public bool Delays { get; set; }
    public bool IsDisposed { get; private set; }
    EncodedVideoFrame? held;

    public IReadOnlyList<EncodedVideoFrame> Encode(ReadOnlySpan<byte> nv12, double time, bool keyFrame)
    {
        if (EncodeError is { } error)
        {
            throw error;
        }
        Assert.Equal(Width * Height * 3 / 2, nv12.Length);
        Encoded.Add((time, keyFrame));
        FirstLuma.Add(nv12[0]);
        var frame = new EncodedVideoFrame(AccessUnit(keyFrame, Encoded.Count), time);
        if (!Delays)
        {
            return [frame];
        }
        var previous = held;
        held = frame;
        return previous is null ? [] : [previous];
    }

    public static byte[] AccessUnit(bool keyFrame, int number)
    {
        var bytes = new List<byte>();
        void Unit(params byte[] unit)
        {
            bytes.AddRange([0, 0, 0, 1]);
            bytes.AddRange(unit);
        }
        Unit(0x09, 0xF0);
        if (keyFrame)
        {
            Unit(SequenceParameterSet);
            Unit(PictureParameterSet);
            Unit(0x65, 0x88, (byte)number, 0x21);
        }
        else
        {
            Unit(0x41, 0x9A, (byte)number, 0x11);
        }
        return [.. bytes];
    }

    public void Dispose() => IsDisposed = true;
}

/// <summary>Screen capture of a pretend computer: a display, some apps, lock and wake on demand.</summary>
sealed class FakeScreenCapture : IScreenCapture
{
    public string? UnavailableReason { get; set; }
    public (int Width, int Height)? DisplaySize { get; set; } = (320, 200);
    public List<AppIdentity> Apps { get; } = [new("teams.exe", "Microsoft Teams"), new("zoom.exe", "Zoom")];
    public List<FakeScreenStream> Streams { get; } = [];
    public List<FakeVideoEncoder> Encoders { get; } = [];
    /// <summary>Thrown by the next Open, then cleared unless <see cref="KeepOpenError"/>.</summary>
    public Exception? OpenError { get; set; }
    public bool KeepOpenError { get; set; }
    public Exception? EncoderError { get; set; }
    /// <summary>Builds every stream; each starts with one picture on screen.</summary>
    public Func<ScreenCaptureTarget, FakeScreenStream> NewStream { get; set; } = target =>
    {
        var stream = new FakeScreenStream(target);
        stream.Show(320, 200, 255, 255, 255);
        return stream;
    };
    public bool EncoderDelays { get; set; }
    public int Subscribers => sessionChanged?.GetInvocationList().Length ?? 0;
    Action<ScreenSessionEvent>? sessionChanged;

    public FakeScreenStream Stream => Streams[^1];
    public FakeVideoEncoder Encoder => Encoders[^1];

    public (int Width, int Height)? MainDisplaySize() => DisplaySize;

    public IReadOnlyList<AppIdentity> CapturableApps() => Apps;

    public IScreenCaptureStream Open(ScreenCaptureTarget target)
    {
        if (OpenError is { } error)
        {
            if (!KeepOpenError)
            {
                OpenError = null;
            }
            throw error;
        }
        var stream = NewStream(target);
        Streams.Add(stream);
        return stream;
    }

    public IVideoEncoder CreateEncoder(int width, int height)
    {
        if (EncoderError is { } error)
        {
            throw error;
        }
        var encoder = new FakeVideoEncoder(width, height) { Delays = EncoderDelays };
        Encoders.Add(encoder);
        return encoder;
    }

    public event Action<ScreenSessionEvent>? SessionChanged
    {
        add => sessionChanged += value;
        remove => sessionChanged -= value;
    }

    public void Raise(ScreenSessionEvent change) => sessionChanged?.Invoke(change);
}

/// <summary>An uptime the test sets.</summary>
sealed class ManualUptime(double start = 500) : IUptime
{
    public double Now { get; set; } = start;
}
