using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Audio;

/// <summary>
/// A meeting track read from its CAF file as it plays, so an hour-long meeting does not
/// have to sit in memory. Reads come from the audio thread; a read error ends the track
/// instead of taking the output down.
/// </summary>
public sealed class CafAudioSource : IAudioSource, IDisposable
{
    readonly CafReader reader;
    readonly Lock gate = new();
    bool failed;

    CafAudioSource(CafReader reader)
    {
        this.reader = reader;
    }

    /// <summary>Opens a track; throws SlovoException when the file is missing or not a CAF file.</summary>
    public static CafAudioSource Open(string path) => new(CafFile.Open(path));

    public double SampleRate => reader.Format.SampleRate;
    public long Length => reader.FrameCount;

    public int Read(long position, Span<float> destination)
    {
        lock (gate)
        {
            if (failed || position < 0 || position >= reader.FrameCount)
            {
                return 0;
            }
            try
            {
                reader.Position = position;
                return reader.Read(destination);
            }
            catch (Exception error) when (error is SlovoException or IOException or ObjectDisposedException)
            {
                failed = true;
                Log.For("Playback").Error("A meeting track stopped reading: " + error.Message);
                return 0;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            failed = true;
            reader.Dispose();
        }
    }
}
