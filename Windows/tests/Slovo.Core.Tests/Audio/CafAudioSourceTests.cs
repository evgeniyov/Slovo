using Slovo.Core.Audio;

namespace Slovo.Core.Tests.Audio;

public class CafAudioSourceTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "slovo-caf-source-" + Guid.NewGuid().ToString("N"));

    public CafAudioSourceTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void ReadsAnyRangeOfTheTrackAsItPlays()
    {
        var path = Path.Combine(folder, "microphone.caf");
        var samples = Enumerable.Range(0, 48_000).Select(i => (float)Math.Sin(i / 20.0) * 0.5f).ToArray();
        using (var writer = CafWriter.Create(path))
        {
            writer.Append(samples);
        }

        using var source = CafAudioSource.Open(path);
        Assert.Equal(16_000, source.SampleRate);
        Assert.Equal(samples.Length, source.Length);

        var buffer = new float[1000];
        Assert.Equal(1000, source.Read(30_000, buffer));
        for (var i = 0; i < buffer.Length; i++)
        {
            Assert.Equal(samples[30_000 + i], buffer[i], 1e-4f);
        }
        // Jumping back (a seek) and reading past the end.
        Assert.Equal(1000, source.Read(0, buffer));
        Assert.Equal(samples[999], buffer[999], 1e-4f);
        Assert.Equal(500, source.Read(samples.Length - 500, buffer));
        Assert.Equal(0, source.Read(samples.Length, buffer));
        Assert.Equal(0, source.Read(-1, buffer));
    }

    [Fact]
    public void ADisposedTrackReadsAsEnded()
    {
        var path = Path.Combine(folder, "system.caf");
        using (var writer = CafWriter.Create(path))
        {
            writer.Append(new float[100]);
        }
        var source = CafAudioSource.Open(path);
        source.Dispose();
        Assert.Equal(0, source.Read(0, new float[10]));
    }

    [Fact]
    public void AMissingTrackFailsToOpenWithAMessage() =>
        Assert.Throws<Slovo.Core.Model.SlovoException>(() => CafAudioSource.Open(Path.Combine(folder, "missing.caf")));
}
