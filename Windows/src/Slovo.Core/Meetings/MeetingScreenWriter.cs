using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Video;

namespace Slovo.Core.Meetings;

/// <summary>
/// Writes a meeting's screen video to <c>screen.mov</c> on the timeline the audio tracks share
/// (port of the macOS <c>MeetingScreenWriter</c>).
/// </summary>
/// <remarks>
/// <para>
/// The file is a fragmented MP4 (<see cref="FragmentedMp4Writer"/>) under the name the macOS app
/// uses, so a meeting folder copied between the two opens on either: AVFoundation reads an
/// ISO MP4 in a <c>.mov</c> the same as its own QuickTime files, and Windows plays it with the
/// MPEG-4 source of Media Foundation. Like the macOS writer's <c>movieFragmentInterval</c>, the
/// fragments make the file self-finalizing every few seconds: a crash, a force-quit or a failed
/// write keeps everything up to the last fragment playable.
/// </para>
/// <para>
/// The file is created at the first key frame, whose parameter sets the file header needs;
/// pictures before it cannot be decoded and are dropped. Screen video is supplementary, so a
/// failed write ends this track only, reported once through the failure callback.
/// </para>
/// <para>
/// Threading: <see cref="Append"/> is called serially by the capture; <see cref="Finish"/> once
/// the capture has stopped. A lock keeps a late call from overlapping either.
/// </para>
/// </remarks>
public sealed class MeetingScreenWriter
{
    static readonly Logger Logger = Log.For("Meetings");

    readonly Lock gate = new();
    readonly Action<Exception>? onWriteFailure;
    readonly DateTimeOffset creationTime;
    FragmentedMp4Writer? file;
    AvcDecoderConfiguration? configuration;
    bool writeFailed;
    bool finished;

    /// <summary>Creates the folder; the file itself comes with the first key frame. Throws SlovoException.</summary>
    public MeetingScreenWriter(string path, int width, int height, Action<Exception>? onWriteFailure = null, DateTimeOffset? creationTime = null)
    {
        Path = path;
        Width = width;
        Height = height;
        this.onWriteFailure = onWriteFailure;
        this.creationTime = creationTime ?? DateTimeOffset.UtcNow;
        try
        {
            if (System.IO.Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw MeetingErrors.FileFailed(error);
        }
    }

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// Whether any picture was accepted into the file; an empty or immediately-failed track is
    /// deleted rather than kept, the same way a silent <c>system.caf</c> is.
    /// </summary>
    public bool WroteAnySamples
    {
        get
        {
            lock (gate)
            {
                return file is { AcceptedSamples: > 0 };
            }
        }
    }

    /// <summary>
    /// Adds one encoded picture. A picture before the first key frame, or at a time not after
    /// the one before, is dropped; nothing is written after a failure.
    /// </summary>
    public void Append(EncodedVideoFrame frame)
    {
        lock (gate)
        {
            if (writeFailed || finished)
            {
                return;
            }
            var units = AvcNalUnits.Split(frame.Data);
            configuration ??= AvcDecoderConfiguration.From(units);
            if (AvcNalUnits.Sample(units) is not { } sample)
            {
                return;
            }
            try
            {
                if (file is null)
                {
                    if (!sample.IsSync || configuration is null)
                    {
                        return;
                    }
                    file = FragmentedMp4Writer.Create(Path, Width, Height, configuration, creationTime);
                }
                file.AddSample(sample.Data, FragmentedMp4Writer.Ticks(frame.Time), sample.IsSync);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Fail(error);
            }
        }
    }

    /// <summary>
    /// Writes what is pending and closes the file; later appends are ignored. Returns whether
    /// the file holds pictures, including those written before a failure: the fragments before
    /// it stay playable.
    /// </summary>
    public bool Finish()
    {
        lock (gate)
        {
            if (finished)
            {
                return file is { AcceptedSamples: > 0 };
            }
            finished = true;
            if (file is null)
            {
                return false;
            }
            var wrote = file.AcceptedSamples > 0;
            try
            {
                if (!writeFailed)
                {
                    file.Finish(FragmentedMp4Writer.Ticks(MeetingScreenCapture.FrameInterval));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logger.Error($"Closing {System.IO.Path.GetFileName(Path)} failed: {error.Message}");
                wrote = file.WrittenSamples > 0;
            }
            finally
            {
                file.Dispose();
            }
            return writeFailed ? file.WrittenSamples > 0 : wrote;
        }
    }

    void Fail(Exception error)
    {
        if (writeFailed)
        {
            return;
        }
        writeFailed = true;
        Logger.Error($"Writing {System.IO.Path.GetFileName(Path)} failed: {error.Message}");
        onWriteFailure?.Invoke(error);
    }
}
