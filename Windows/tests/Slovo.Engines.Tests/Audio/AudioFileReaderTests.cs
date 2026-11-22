using System.Buffers.Binary;
using Slovo.Core.Model;
using Slovo.Engines.Audio;

namespace Slovo.Engines.Tests.Audio;

public class AudioFileReaderTests
{
    /// <summary>A RIFF WAV file with a padding chunk before the audio, like afconvert writes.</summary>
    static byte[] Wav(int channels, int rate, int bits, ushort format, byte[] audio, bool extensible = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(extensible ? 40 : 16);
        writer.Write(extensible ? (ushort)0xFFFE : format);
        writer.Write((ushort)channels);
        writer.Write(rate);
        writer.Write(rate * channels * bits / 8);
        writer.Write((ushort)(channels * bits / 8));
        writer.Write((ushort)bits);
        if (extensible)
        {
            writer.Write((ushort)22);
            writer.Write((ushort)bits);
            writer.Write(0);
            writer.Write(format);
            writer.Write(new byte[14]);
        }
        writer.Write("FLLR"u8);
        writer.Write(3);
        writer.Write(new byte[4]); // Three bytes plus the pad byte.
        writer.Write("data"u8);
        writer.Write(audio.Length);
        writer.Write(audio);
        writer.Flush();
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        return bytes;
    }

    /// <summary>A CAF file with linear PCM, as Slovo stores meeting audio.</summary>
    static byte[] Caf(int channels, double rate, int bits, bool isFloat, bool littleEndian, byte[] audio, bool openEnded = false)
    {
        var bytes = new List<byte>();
        void Add(ReadOnlySpan<byte> part) => bytes.AddRange(part.ToArray());
        void BigEndian32(uint value)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            Add(buffer);
        }
        void BigEndian64(long value)
        {
            var buffer = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buffer, value);
            Add(buffer);
        }
        Add("caff"u8);
        Add([0, 1, 0, 0]);
        Add("desc"u8);
        BigEndian64(32);
        var rateBytes = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(rateBytes, rate);
        Add(rateBytes);
        Add("lpcm"u8);
        BigEndian32((isFloat ? 1u : 0u) | (littleEndian ? 2u : 0u));
        BigEndian32((uint)(channels * bits / 8));
        BigEndian32(1);
        BigEndian32((uint)channels);
        BigEndian32((uint)bits);
        Add("free"u8);
        BigEndian64(4);
        Add([0, 0, 0, 0]);
        Add("data"u8);
        BigEndian64(openEnded ? -1 : audio.Length + 4);
        BigEndian32(0);
        Add(audio);
        return [.. bytes];
    }

    static byte[] Int16(params short[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2), values[index]);
        }
        return bytes;
    }

    static float[] Read(byte[] file)
    {
        using var temporary = new TemporaryFolder();
        var path = Path.Combine(temporary.Path, "audio");
        File.WriteAllBytes(path, file);
        return AudioFileReader.ReadMono16k(path, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ReadsTheRussianSpeechFixture()
    {
        var samples = AudioFileReader.ReadMono16k(TestSupport.Fixture("ru-hello.wav"), TestContext.Current.CancellationToken);
        Assert.Equal(84_376, samples.Length);
        Assert.InRange(samples.Max(Math.Abs), 0.1f, 1f);
    }

    [Fact]
    public void Reads16BitMonoExactly()
    {
        var samples = Read(Wav(1, 16_000, 16, 1, Int16(0, 16_384, -32_768, 32_767)));
        Assert.Equal([0f, 0.5f, -1f, 32_767f / 32_768f], samples);
    }

    [Fact]
    public void AveragesChannels()
    {
        var samples = Read(Wav(2, 16_000, 16, 1, Int16(16_384, 0, -16_384, -16_384)));
        Assert.Equal([0.25f, -0.5f], samples);
    }

    [Fact]
    public void ReadsFloat24BitAndEightBitWav()
    {
        var floats = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(floats, 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(floats.AsSpan(4), -0.75f);
        Assert.Equal([0.25f, -0.75f], Read(Wav(1, 16_000, 32, 3, floats)));
        Assert.Equal([0.25f, -0.75f], Read(Wav(1, 16_000, 32, 3, floats, extensible: true)));

        // 0x400000 is half of full scale; 0xC00000 is minus half.
        Assert.Equal([0.5f, -0.5f], Read(Wav(1, 16_000, 24, 1, [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0])));
        // 8-bit WAV is unsigned around 128.
        Assert.Equal([0f, 0.5f, -1f], Read(Wav(1, 16_000, 8, 1, [128, 192, 0])));
    }

    [Fact]
    public void ReadsCafAsMeetingsStoreIt()
    {
        var bigEndian = new byte[4];
        BinaryPrimitives.WriteInt16BigEndian(bigEndian, 16_384);
        BinaryPrimitives.WriteInt16BigEndian(bigEndian.AsSpan(2), -16_384);
        Assert.Equal([0.5f, -0.5f], Read(Caf(1, 16_000, 16, isFloat: false, littleEndian: false, bigEndian)));
        Assert.Equal([0.5f, -0.5f], Read(Caf(1, 16_000, 16, isFloat: false, littleEndian: true, Int16(16_384, -16_384))));
        // A recording cut off before its data size was written.
        Assert.Equal([0.5f, -0.5f], Read(Caf(1, 16_000, 16, isFloat: false, littleEndian: true, Int16(16_384, -16_384), openEnded: true)));

        var floats = new byte[8];
        BinaryPrimitives.WriteSingleBigEndian(floats, 0.25f);
        BinaryPrimitives.WriteSingleBigEndian(floats.AsSpan(4), 0.75f);
        Assert.Equal([0.5f], Read(Caf(2, 16_000, 32, isFloat: true, littleEndian: false, floats)));
    }

    [Fact]
    public void ResamplesOtherRatesTo16Kilohertz()
    {
        var tone = Enumerable.Range(0, 48_000).Select(index => (short)(10_000 * Math.Sin(index * 2 * Math.PI * 440 / 48_000))).ToArray();
        var samples = Read(Wav(1, 48_000, 16, 1, Int16(tone)));
        Assert.InRange(samples.Length, 15_990, 16_000);
        // The tone keeps its level: about 0.3 peak.
        Assert.InRange(samples.Max(), 0.28f, 0.31f);

        var low = Enumerable.Range(0, 8_000).Select(index => (short)(10_000 * Math.Sin(index * 2 * Math.PI * 440 / 8_000))).ToArray();
        var upsampled = Read(Wav(1, 8_000, 16, 1, Int16(low)));
        Assert.Equal(16_000, upsampled.Length);
        Assert.InRange(upsampled.Skip(100).Take(15_800).Max(), 0.29f, 0.32f);

        // CAF at 44.1 kHz, as an imported file might be.
        var caf = Enumerable.Range(0, 44_100).Select(index => (short)(10_000 * Math.Sin(index * 2 * Math.PI * 440 / 44_100))).ToArray();
        var fromCaf = Read(Caf(1, 44_100, 16, isFloat: false, littleEndian: true, Int16(caf)));
        Assert.Equal(16_000, fromCaf.Length);
    }

    [Fact]
    public void EightBitCafIsSigned()
    {
        Assert.Equal([0.5f, -0.5f], Read(Caf(1, 16_000, 8, isFloat: false, littleEndian: false, [64, 192])));
    }

    [Fact]
    public void AnythingElseIsALocalizedError()
    {
        var error = Assert.Throws<SlovoException>(() => Read("not audio at all"u8.ToArray()));
        Assert.Contains("audio", error.Message);
        var compressed = Assert.Throws<SlovoException>(() => Read(Wav(1, 16_000, 16, 0x55, Int16(1, 2))));
        Assert.Contains("audio", compressed.Message);
        var missing = Assert.Throws<SlovoException>(() =>
            AudioFileReader.ReadMono16k(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.wav"), TestContext.Current.CancellationToken));
        Assert.Contains("missing-", missing.Message);
    }

    [Fact]
    public void AnEmptyDataChunkIsSilenceOfNoLength()
    {
        Assert.Empty(Read(Wav(1, 16_000, 16, 1, [])));
    }
}
