using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Audio;

/// <summary>
/// Reads a meeting track forward as 16 kHz mono floats, a block at a time, whatever the
/// file's own rate and channel count (port of the macOS <c>MeetingTrackReader</c>). CAF files
/// are streamed from disk; WAV files, which only an imported clip is, are read whole.
/// </summary>
public sealed class MeetingTrackReader : IDisposable
{
    const int BlockFrames = MeetingAudioFormat.SampleRate;

    readonly CafReader? caf;
    readonly float[]? whole;
    readonly Resampler? resampler;
    readonly List<float> pending = [];
    int pendingStart;
    long wholePosition;
    bool fileFinished;

    MeetingTrackReader(CafReader? caf, float[]? whole, int sourceRate)
    {
        this.caf = caf;
        this.whole = whole;
        resampler = sourceRate == MeetingAudioFormat.SampleRate ? null : new Resampler(sourceRate);
    }

    /// <summary>16 kHz frames handed out or skipped so far.</summary>
    public long Position { get; private set; }

    /// <summary>Opens a CAF or WAV track. Throws SlovoException.</summary>
    public static MeetingTrackReader Open(string path)
    {
        if (CafFile.IsCaf(path))
        {
            var reader = CafReader.Open(path);
            var rate = reader.Format.SampleRate;
            if (rate != Math.Round(rate) || rate > 1_536_000)
            {
                reader.Dispose();
                throw MeetingErrors.UnsupportedAudioFormat();
            }
            return new MeetingTrackReader(reader, null, (int)rate);
        }
        var audio = WavFile.Read(path);
        var sampleRate = audio.SampleRate;
        if (sampleRate != Math.Round(sampleRate) || sampleRate is <= 0 or > 1_536_000)
        {
            throw MeetingErrors.UnsupportedAudioFormat();
        }
        return new MeetingTrackReader(null, audio.Samples, (int)sampleRate);
    }

    /// <summary>Up to <paramref name="count"/> next samples; fewer only at the end of the file. Throws SlovoException.</summary>
    public float[] Read(int count)
    {
        if (count <= 0)
        {
            return [];
        }
        Fill(count);
        var available = Math.Min(count, pending.Count - pendingStart);
        var samples = pending.GetRange(pendingStart, available).ToArray();
        pendingStart += available;
        Position += available;
        Compact();
        return samples;
    }

    /// <summary>
    /// The samples of <c>start..&lt;end</c> on the 16 kHz timeline. The reader only moves
    /// forward, so ranges must come in order; an overlap with the previous range is clipped.
    /// Throws SlovoException.
    /// </summary>
    public float[] Samples(long start, long end)
    {
        if (start > Position)
        {
            Skip(start - Position);
        }
        var from = Math.Max(start, Position);
        if (end <= from)
        {
            return [];
        }
        return Read((int)Math.Min(int.MaxValue, end - from));
    }

    public void Dispose() => caf?.Dispose();

    void Skip(long count)
    {
        var fromPending = (int)Math.Min(count, pending.Count - pendingStart);
        pendingStart += fromPending;
        Position += fromPending;
        Compact();
        var remaining = count - fromPending;
        if (remaining <= 0)
        {
            return;
        }
        if (resampler is null)
        {
            // Same rate as the timeline: seek instead of decoding.
            if (caf is not null)
            {
                var target = Math.Min(caf.FrameCount, caf.Position + remaining);
                Position += target - caf.Position;
                caf.Position = target;
            }
            else if (whole is not null)
            {
                var target = Math.Min(whole.Length, wholePosition + remaining);
                Position += target - wholePosition;
                wholePosition = target;
            }
            return;
        }
        while (remaining > 0)
        {
            var chunk = Read((int)Math.Min(remaining, BlockFrames));
            if (chunk.Length == 0)
            {
                return;
            }
            remaining -= chunk.Length;
        }
    }

    void Fill(int count)
    {
        var block = new float[BlockFrames];
        while (pending.Count - pendingStart < count && !fileFinished)
        {
            var wanted = resampler is null ? Math.Max(1, Math.Min(BlockFrames, count - (pending.Count - pendingStart))) : BlockFrames;
            int read;
            if (caf is not null)
            {
                read = caf.Read(block.AsSpan(0, wanted));
            }
            else
            {
                read = (int)Math.Min(wanted, whole!.Length - wholePosition);
                whole.AsSpan((int)wholePosition, read).CopyTo(block);
                wholePosition += read;
            }
            if (read == 0)
            {
                fileFinished = true;
                if (resampler is not null)
                {
                    pending.AddRange(resampler.Flush());
                }
                return;
            }
            var samples = block.AsSpan(0, read);
            if (resampler is null)
            {
                pending.AddRange(samples);
            }
            else
            {
                pending.AddRange(resampler.Process(samples));
            }
        }
    }

    void Compact()
    {
        if (pendingStart > 0 && pendingStart >= pending.Count / 2)
        {
            pending.RemoveRange(0, pendingStart);
            pendingStart = 0;
        }
    }
}
