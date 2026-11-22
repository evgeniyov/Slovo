using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Tests.Video;

namespace Slovo.Core.Tests.Meetings;

/// <summary>screen.mov as MeetingScreenWriter.swift writes it: from the first key frame, kept up to a failure.</summary>
public sealed class ScreenWriterTests : IDisposable
{
    readonly TemporaryMeetingsFolder folder = new();

    public void Dispose() => folder.Dispose();

    string Path => System.IO.Path.Combine(folder.Paths.MeetingsFolder, "A", "screen.mov");

    static EncodedVideoFrame Frame(bool key, int number, double time) => new(FakeVideoEncoder.AccessUnit(key, number), time);

    [Fact]
    public void TheFileStartsAtTheFirstKeyFrameAndDropsWhatCameBefore()
    {
        var writer = new MeetingScreenWriter(Path, 320, 200);
        Assert.True(Directory.Exists(System.IO.Path.GetDirectoryName(Path)));
        writer.Append(Frame(key: false, 1, 0.1));
        Assert.False(File.Exists(Path));
        Assert.False(writer.WroteAnySamples);
        writer.Append(Frame(key: true, 2, 0.35));
        writer.Append(Frame(key: false, 3, 0.6));
        Assert.True(File.Exists(Path));
        Assert.True(writer.WroteAnySamples);
        Assert.True(writer.Finish());
        // Appends after the end are ignored.
        writer.Append(Frame(key: false, 4, 0.85));
        var samples = Mp4Reader.Samples(File.ReadAllBytes(Path));
        Assert.Equal([2, 3], samples.Select(sample => (int)sample.Data[6]));
        Assert.Equal([true, false], samples.Select(sample => sample.IsSync));
    }

    [Fact]
    public void AWriterThatNeverSawAKeyFrameLeavesNoFile()
    {
        var writer = new MeetingScreenWriter(Path, 320, 200);
        writer.Append(Frame(key: false, 1, 0.1));
        Assert.False(writer.Finish());
        Assert.False(File.Exists(Path));
    }

    [Fact]
    public void AFailedWriteIsReportedOnceAndWhatWasWrittenStays()
    {
        var failures = new List<Exception>();
        // A folder where the file should be: creating it fails the way a full or locked disk does.
        Directory.CreateDirectory(Path);
        var writer = new MeetingScreenWriter(Path, 320, 200, failures.Add);
        writer.Append(Frame(key: true, 1, 0));
        writer.Append(Frame(key: true, 2, 0.25));
        Assert.Single(failures);
        Assert.False(writer.Finish());
    }

    [Fact]
    public void AFolderThatCannotBeMadeFailsTheStartWithAFileError()
    {
        var blocker = System.IO.Path.Combine(folder.Paths.MeetingsFolder, "file");
        Directory.CreateDirectory(folder.Paths.MeetingsFolder);
        File.WriteAllText(blocker, "not a folder");
        Assert.Throws<SlovoException>(() => new MeetingScreenWriter(System.IO.Path.Combine(blocker, "screen.mov"), 320, 200));
    }
}
