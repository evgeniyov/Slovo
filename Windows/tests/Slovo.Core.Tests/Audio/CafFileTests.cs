using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Slovo.Core.Audio;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Audio;

/// <summary>
/// Meeting tracks: <see cref="CafWriter"/> must write what the macOS app's AVAudioFile writes,
/// byte for byte, and <see cref="CafReader"/> must read what Core Audio writes, including a
/// track cut off by a crash. Reference files written on a Mac are embedded; afconvert and the
/// user's own meetings, when this runs on a Mac that has them, are read too (read-only).
/// </summary>
public sealed class CafFileTests : IDisposable
{
    /// <summary>
    /// Floats AVAudioFile wrote with the meeting track settings (16 kHz mono 16-bit little-endian
    /// integers), then closed: <see cref="AppleClosed"/>. The same file before <c>close()</c>,
    /// as a crash leaves it: <see cref="AppleOpen"/> (data size -1, edit count 0).
    /// </summary>
    static readonly float[] Values =
    [
        0, 1, -1, 0.5f, -0.5f, 1.5f, -1.5f, 0.25f, 1f / 32768, 0.4f / 32768, 0.5f / 32768, 0.6f / 32768, 1.5f / 32768, 2.5f / 32768,
        -0.5f / 32768, -1.5f / 32768, 32766.5f / 32768, 32767.5f / 32768, 0.123456f, -0.987654f, float.NaN, float.PositiveInfinity,
    ];

    /// <summary>The 16-bit values Core Audio stored for <see cref="Values"/>.</summary>
    static readonly short[] AppleInts = [0, 32767, -32768, 16384, -16384, 32767, -32768, 8192, 1, 0, 1, 1, 2, 3, -1, -2, 32767, 32767, 4045, -32363, 0, 32767];

    const string AppleClosed = "H4sIAAAAAAACA+3XSw2AMBAE0CkIAAlIQEKtNKU9QUKAe0EEPpCAAYIjlk8rgsO8ZDJ7WANjjfdQQONGi6jSp/6OtrfdU1mKSin94Fx8LTYQERERERER0e80ZjLprOOel4AZGvvXVRz56hn8OUQukSDhKNbl/bsBUzZ9RCwQAAA=";
    const string AppleOpen = "H4sIAAAAAAACA+3XSw2AMBAE0CkIKBKQUitNaU+QEOBeEIEPJGCA4Ijl04rgMC/ZzGYzBtbZEKCAxo8OSW1O8y1t77onijwqTxUG71NVbyAiIiIiIiKi32nsZCVLF4mYYbB/WacnXz0PfwmRSyRKPPS6vL0bINa5iiwQAAA=";

    readonly string folder = Path.Combine(Path.GetTempPath(), "SlovoCafTests-" + Guid.NewGuid().ToString("N"));

    public CafFileTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    static byte[] Unzip(string base64)
    {
        using var input = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    string PathFor(string name) => Path.Combine(folder, name);

    // MARK: - Writing

    [Fact]
    public void AClosedTrackIsByteForByteWhatAVAudioFileWrites()
    {
        var path = PathFor("mic.caf");
        using (var writer = CafWriter.Create(path))
        {
            writer.Append(Values);
            Assert.Equal(Values.Length, writer.FrameCount);
            writer.Close();
        }
        Assert.Equal(Unzip(AppleClosed), File.ReadAllBytes(path));
    }

    [Fact]
    public void AnOpenTrackIsByteForByteWhatAVAudioFileLeavesBeforeClose()
    {
        var path = PathFor("mic.caf");
        using var writer = CafWriter.Create(path);
        writer.Append(Values);
        writer.Flush();
        Assert.Equal(Unzip(AppleOpen), ReadShared(path));
    }

    [Fact]
    public void SamplesAreConvertedLikeCoreAudio()
    {
        Assert.Equal(AppleInts, Values.Select(CafWriter.ToInt16));
        Assert.Equal(short.MinValue, CafWriter.ToInt16(float.NegativeInfinity));
    }

    [Fact]
    public void TheHeaderDescribesSixteenKilohertzMonoSixteenBitLittleEndianAndTheSamplesStartAt4096()
    {
        var path = PathFor("mic.caf");
        using (var writer = CafWriter.Create(path))
        {
            writer.Append([0.5f, -0.25f]);
        }
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("caff"u8.ToArray(), bytes[..4]);
        Assert.Equal(16_000, BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(20)));
        Assert.Equal("lpcm"u8.ToArray(), bytes[28..32]);
        Assert.Equal("data"u8.ToArray(), bytes[0xFF0..0xFF4]);
        Assert.Equal(2 * 2 + 4, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0xFF4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0xFFC)));
        Assert.Equal(4096 + 4, bytes.Length);
        Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(4096)));
        Assert.Equal(-8192, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(4098)));

        using var reader = CafReader.Open(path);
        Assert.Equal(CafFormat.MeetingTrack, reader.Format);
        Assert.Equal(2, reader.FrameCount);
    }

    [Fact]
    public void RoundTripsSilencePaddingAndMuting()
    {
        var path = PathFor("mic.caf");
        var tone = Enumerable.Range(0, 1600).Select(i => (float)(0.3 * Math.Sin(i / 7.0))).ToArray();
        using (var writer = CafWriter.Create(path))
        {
            writer.Append(tone);
            writer.AppendSilence(16_000 + 7);
            writer.IsMuted = true;
            writer.Append(tone);
            writer.IsMuted = false;
            writer.Append(tone.AsSpan(0, 10));
            Assert.Equal(1600 + 16_007 + 1600 + 10, writer.FrameCount);
            Assert.True(writer.HasSound);
            writer.Close();
            writer.Close();
        }

        using var reader = CafReader.Open(path);
        Assert.Equal(1600 + 16_007 + 1600 + 10, reader.FrameCount);
        Assert.Equal((1600 + 16_007 + 1600 + 10) / 16_000.0, reader.Duration, 9);
        var samples = reader.ReadAll();
        for (var index = 0; index < tone.Length; index++)
        {
            Assert.Equal(CafWriter.ToInt16(tone[index]) / 32768f, samples[index]);
        }
        Assert.All(samples[1600..(1600 + 16_007 + 1600)], sample => Assert.Equal(0, sample));
        Assert.Equal(CafWriter.ToInt16(tone[9]) / 32768f, samples[^1]);
    }

    [Fact]
    public void ATrackOfSilenceHasNoSound()
    {
        using var writer = CafWriter.Create(PathFor("mic.caf"));
        writer.AppendSilence(100);
        writer.IsMuted = true;
        writer.Append([0.5f, 0.5f]);
        Assert.False(writer.HasSound);
        Assert.Equal(102, writer.FrameCount);
    }

    [Fact]
    public void AFailedCreateIsALocalizedError()
    {
        var blocker = PathFor("file");
        File.WriteAllText(blocker, "x");
        Assert.Throws<SlovoException>(() => CafWriter.Create(Path.Combine(blocker, "mic.caf")));
    }

    // MARK: - Reading what a crash leaves

    [Fact]
    public void ACrashTruncatedTrackIsReadToTheEndOfTheFile()
    {
        var path = PathFor("mic.caf");
        var copy = PathFor("crashed.caf");
        var tone = Enumerable.Range(0, 48_000).Select(i => (float)(0.2 * Math.Sin(i / 10.0))).ToArray();
        using (var writer = CafWriter.Create(path))
        {
            writer.Append(tone);
            writer.Flush(toDisk: true);
            // The app dies here: what reached the disk is all there is.
            File.WriteAllBytes(copy, ReadShared(path));
        }
        Assert.Equal(-1, BinaryPrimitives.ReadInt64BigEndian(File.ReadAllBytes(copy).AsSpan(0xFF4)));

        using var reader = CafReader.Open(copy);
        Assert.Equal(48_000, reader.FrameCount);
        Assert.Equal(3, reader.Duration, 9);
        Assert.Equal(3, CafFile.Duration(copy), 9);
        var samples = reader.ReadAll();
        Assert.Equal(CafWriter.ToInt16(tone[47_999]) / 32768f, samples[^1]);
    }

    [Fact]
    public void AppleCrashedTrackReadsEverySample()
    {
        var path = PathFor("apple-open.caf");
        File.WriteAllBytes(path, Unzip(AppleOpen));
        using var reader = CafReader.Open(path);
        Assert.Equal(Values.Length, reader.FrameCount);
        Assert.Equal(AppleInts.Select(v => v / 32768f), reader.ReadAll());
    }

    [Fact]
    public void AHalfWrittenLastFrameAndASizePastTheEndAreIgnored()
    {
        var path = PathFor("torn.caf");
        var bytes = Unzip(AppleClosed).Concat(new byte[] { 0x01 }).ToArray();
        // The data chunk claims 1024 bytes more than the file holds, as a real macOS track did.
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(0xFF4), Values.Length * 2 + 4 + 1024);
        File.WriteAllBytes(path, bytes);
        using var reader = CafReader.Open(path);
        Assert.Equal(Values.Length, reader.FrameCount);
    }

    [Fact]
    public void AGrowingTrackCanBeReadWhileItIsWritten()
    {
        var path = PathFor("mic.caf");
        using var writer = CafWriter.Create(path);
        writer.Append(new float[1000]);
        writer.Flush();
        using (var reader = CafReader.Open(path))
        {
            Assert.Equal(1000, reader.FrameCount);
        }
        writer.Append(new float[500]);
        writer.Close();
        Assert.Equal(1500 / 16_000.0, CafFile.Duration(path), 9);
    }

    // MARK: - Other layouts Core Audio writes

    [Theory]
    [InlineData(false, true, 16, 1)]
    [InlineData(false, false, 16, 1)]
    [InlineData(false, true, 24, 2)]
    [InlineData(false, false, 32, 1)]
    [InlineData(true, true, 32, 1)]
    [InlineData(true, false, 32, 2)]
    [InlineData(true, false, 64, 1)]
    public void ReadsIntegerAndFloatSamplesOfEitherByteOrderAndAveragesChannels(bool isFloat, bool littleEndian, int bits, int channels)
    {
        var frames = new[] { 0.5, -0.25, 0.125, 0, -1, 0.75 };
        var bytesPerSample = bits / 8;
        var data = new byte[frames.Length * bytesPerSample * channels];
        for (var frame = 0; frame < frames.Length; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                // The second channel holds the negated first, except in the last frame.
                var value = channel == 0 || frame == frames.Length - 1 ? frames[frame] : -frames[frame];
                var slot = data.AsSpan((frame * channels + channel) * bytesPerSample, bytesPerSample);
                WriteSample(slot, value, isFloat, littleEndian, bits);
            }
        }
        var flags = (isFloat ? CafFormat.FloatFlag : 0) | (littleEndian ? CafFormat.LittleEndianFlag : 0);
        var format = new CafFormat(44_100, "lpcm", flags, (uint)(bytesPerSample * channels), 1, (uint)channels, (uint)bits);
        var path = PathFor("layout.caf");
        File.WriteAllBytes(path, [.. CafFile.Header(format, data.Length + 4, 1), .. data]);

        using var reader = CafReader.Open(path);
        Assert.Equal(format, reader.Format);
        Assert.Equal(frames.Length, reader.FrameCount);
        var samples = reader.ReadAll();
        for (var frame = 0; frame < frames.Length; frame++)
        {
            var expected = channels == 1 || frame == frames.Length - 1 ? frames[frame] : 0;
            Assert.Equal(expected, samples[frame], 4);
        }
    }

    [Fact]
    public void ChunksBeforeTheDataAreSkippedAndOtherFormatsRefused()
    {
        var path = PathFor("chunks.caf");
        var header = CafFile.Header(CafFormat.MeetingTrack, 4 + 4, 1);
        // Put an info chunk where the free chunk was, the way afconvert does.
        "info"u8.CopyTo(header.AsSpan(0x34));
        File.WriteAllBytes(path, [.. header, 0x00, 0x40, 0x00, 0xC0]);
        using (var reader = CafReader.Open(path))
        {
            Assert.Equal([0.5f, -0.5f], reader.ReadAll());
        }

        var aac = CafFile.Header(new CafFormat(44_100, "aac ", 0, 0, 1024, 2, 0), 4, 1);
        File.WriteAllBytes(path, aac);
        Assert.Throws<SlovoException>(() => CafReader.Open(path));
        File.WriteAllText(path, "not a caf file at all");
        Assert.Throws<SlovoException>(() => CafReader.Open(path));
        Assert.False(CafFile.IsCaf(path));
        Assert.Equal(0, CafFile.Duration(path));
        Assert.Equal(0, CafFile.Duration(PathFor("missing.caf")));
        Assert.Throws<SlovoException>(() => CafReader.Open(PathFor("missing.caf")));
    }

    [Fact]
    public void RangesAreReadWithoutReadingTheRest()
    {
        var path = PathFor("mic.caf");
        var samples = Enumerable.Range(0, 32_000).Select(i => (i % 2000) / 4000f).ToArray();
        using (var writer = CafWriter.Create(path))
        {
            writer.Append(samples);
        }
        using var reader = CafReader.Open(path);
        var middle = reader.ReadRange(0.5, 0.75);
        Assert.Equal(4000, middle.Length);
        Assert.Equal(CafWriter.ToInt16(samples[8000]) / 32768f, middle[0]);
        Assert.Equal(12_000, reader.Position);
        Assert.Equal(16_000, reader.ReadRange(1, 5).Length);
        Assert.Empty(reader.ReadRange(3, 4));
        Assert.Empty(reader.ReadRange(0.5, 0.25));
        reader.Position = 31_990;
        Assert.Equal(10, reader.Read(100).Length);
        Assert.Empty(reader.Read(100));
    }

    // MARK: - MeetingTrackReader

    [Fact]
    public void TheTrackReaderHandsOutSixteenKilohertzInOrderAndSkipsForward()
    {
        var path = PathFor("mic.caf");
        var samples = Enumerable.Range(0, 48_000).Select(i => i / 65_536f).ToArray();
        using (var writer = CafWriter.Create(path))
        {
            writer.Append(samples);
        }
        using var reader = MeetingTrackReader.Open(path);
        Assert.Equal(1000, reader.Read(1000).Length);
        var range = reader.Samples(16_000, 16_010);
        Assert.Equal(Enumerable.Range(16_000, 10).Select(i => CafWriter.ToInt16(i / 65_536f) / 32768f), range);
        // An overlap with what was already read is clipped; ranges only go forward.
        Assert.Equal(5, reader.Samples(16_005, 16_015).Length);
        Assert.Equal(16_015, reader.Position);
        Assert.Equal(48_000 - 40_000, reader.Samples(40_000, 60_000).Length);
        Assert.Empty(reader.Read(10));
    }

    [Fact]
    public void TheTrackReaderResamplesOtherRatesAndReadsWav()
    {
        var path = PathFor("tone44.caf");
        var tone = Enumerable.Range(0, 44_100).Select(i => (float)(0.25 * Math.Sin(2 * Math.PI * 300 * i / 44_100.0))).ToArray();
        var data = new byte[tone.Length * 4];
        for (var index = 0; index < tone.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(index * 4), tone[index]);
        }
        var format = new CafFormat(44_100, "lpcm", CafFormat.FloatFlag | CafFormat.LittleEndianFlag, 4, 1, 1, 32);
        File.WriteAllBytes(path, [.. CafFile.Header(format, CafFile.UnknownSize, 0), .. data]);

        using (var reader = MeetingTrackReader.Open(path))
        {
            var resampled = new List<float>();
            while (reader.Read(5000) is { Length: > 0 } block)
            {
                resampled.AddRange(block);
            }
            Assert.Equal(16_000, resampled.Count);
            Assert.InRange(LevelMeter.Rms(resampled.ToArray().AsSpan(1000, 14_000)), 0.17, 0.19);
        }

        var wav = PathFor("clip.wav");
        WavFile.Write(new RecordedAudio(tone, 44_100), wav);
        using (var reader = MeetingTrackReader.Open(wav))
        {
            Assert.InRange(reader.Read(20_000).Length, 15_990, 16_010);
        }
    }

    // MARK: - Files written by macOS

    /// <summary>afconvert writes CAF the way Core Audio does everywhere; read each layout it offers.</summary>
    [Theory]
    [InlineData("LEI16@16000", 1, 16_000)]
    [InlineData("BEI16@16000", 1, 16_000)]
    [InlineData("LEI24@16000", 2, 16_000)]
    [InlineData("LEF32@16000", 1, 16_000)]
    [InlineData("BEF32@44100", 2, 44_100)]
    public void ReadsWhatAfconvertWrites(string dataFormat, int channels, int rate)
    {
        const string afconvert = "/usr/bin/afconvert";
        if (!File.Exists(afconvert))
        {
            Assert.Skip("afconvert exists only on macOS.");
        }
        var source = Enumerable.Range(0, 16_000).Select(i => (float)(0.25 * Math.Sin(2 * Math.PI * 440 * i / 16_000.0))).ToArray();
        var wav = PathFor("source.wav");
        WavFile.Write(new RecordedAudio(source), wav);
        var caf = PathFor("converted.caf");
        var start = new ProcessStartInfo(afconvert) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { "-f", "caff", "-d", dataFormat, "-c", channels.ToString(System.Globalization.CultureInfo.InvariantCulture), wav, caf })
        {
            start.ArgumentList.Add(argument);
        }
        using (var process = Process.Start(start)!)
        {
            Assert.True(process.WaitForExit(30_000));
            Assert.Equal(0, process.ExitCode);
        }

        using var reader = CafReader.Open(caf);
        Assert.Equal(rate, reader.Format.SampleRate);
        Assert.Equal((uint)channels, reader.Format.ChannelsPerFrame);
        Assert.Equal(rate, reader.FrameCount, 64.0);
        var samples = reader.ReadAll();
        if (rate == 16_000)
        {
            // The WAV holds 16-bit samples, which every one of these layouts carries exactly.
            var expected = WavFile.Read(wav).Samples;
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.Equal(expected[index], samples[index], 4);
            }
        }
        else
        {
            Assert.InRange(LevelMeter.Rms(samples), 0.17, 0.19);
            using var track = MeetingTrackReader.Open(caf);
            var resampled = track.Read(20_000);
            Assert.InRange(resampled.Length, 15_990, 16_010);
            Assert.InRange(LevelMeter.Rms(resampled.AsSpan(1000, 14_000)), 0.17, 0.19);
        }
    }

    /// <summary>
    /// The user's own meetings recorded by the macOS app, when this runs on that Mac. They are
    /// only opened for reading.
    /// </summary>
    [Fact]
    public void ReadsTheMacAppsOwnMeetingTracks()
    {
        var meetings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Slovo", "Meetings");
        var tracks = Directory.Exists(meetings)
            ? Directory.GetDirectories(meetings).SelectMany(d => new[] { Path.Combine(d, "mic.caf"), Path.Combine(d, "system.caf") }).Where(File.Exists).Take(4).ToList()
            : [];
        if (tracks.Count == 0)
        {
            Assert.Skip("No meetings of the macOS app on this computer.");
        }
        foreach (var track in tracks)
        {
            var length = new FileInfo(track).Length;
            using var reader = CafReader.Open(track);
            Assert.Equal(CafFormat.MeetingTrack, reader.Format);
            Assert.Equal((length - CafFile.DataOffset) / 2, reader.FrameCount);

            // A second from the middle, compared with the raw little-endian integers.
            var start = reader.FrameCount / 2;
            var range = reader.ReadRange(start / 16_000.0, start / 16_000.0 + 1);
            Assert.Equal(16_000, range.Length);
            var raw = new byte[32_000];
            using (var stream = new FileStream(track, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Position = CafFile.DataOffset + start * 2;
                stream.ReadExactly(raw);
            }
            for (var index = 0; index < range.Length; index++)
            {
                Assert.Equal(BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(index * 2)) / 32768f, range[index]);
            }
            Assert.Equal(reader.Duration, CafFile.Duration(track), 9);
        }
    }

    static void WriteSample(Span<byte> slot, double value, bool isFloat, bool littleEndian, int bits)
    {
        switch (isFloat, bits)
        {
            case (true, 32) when littleEndian: BinaryPrimitives.WriteSingleLittleEndian(slot, (float)value); break;
            case (true, 32): BinaryPrimitives.WriteSingleBigEndian(slot, (float)value); break;
            case (true, _) when littleEndian: BinaryPrimitives.WriteDoubleLittleEndian(slot, value); break;
            case (true, _): BinaryPrimitives.WriteDoubleBigEndian(slot, value); break;
            case (false, 16):
                var sixteen = (short)Math.Clamp(Math.Round(value * 32768), short.MinValue, short.MaxValue);
                if (littleEndian) BinaryPrimitives.WriteInt16LittleEndian(slot, sixteen); else BinaryPrimitives.WriteInt16BigEndian(slot, sixteen);
                break;
            case (false, 24):
                var twentyFour = (int)Math.Clamp(Math.Round(value * 8_388_608), -8_388_608, 8_388_607);
                var b0 = (byte)twentyFour;
                var b1 = (byte)(twentyFour >> 8);
                var b2 = (byte)(twentyFour >> 16);
                if (littleEndian) { slot[0] = b0; slot[1] = b1; slot[2] = b2; } else { slot[0] = b2; slot[1] = b1; slot[2] = b0; }
                break;
            default:
                var thirtyTwo = (int)Math.Clamp(Math.Round(value * 2_147_483_648.0), int.MinValue, int.MaxValue);
                if (littleEndian) BinaryPrimitives.WriteInt32LittleEndian(slot, thirtyTwo); else BinaryPrimitives.WriteInt32BigEndian(slot, thirtyTwo);
                break;
        }
    }

    static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
