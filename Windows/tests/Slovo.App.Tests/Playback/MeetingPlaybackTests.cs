using Avalonia.Headless.XUnit;
using Slovo.App.Composition;
using Slovo.App.Native;
using Slovo.App.Tests.Fakes;
using Slovo.Core.Audio;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.App.Tests.Playback;

public class MeetingPlaybackTests
{
    sealed class RecordingPlayer : IAudioPlayer
    {
        public List<(int Tracks, double From)> Plays { get; } = [];
        public int Resumes { get; private set; }
        public int Stops { get; private set; }
        public bool IsPlaying { get; private set; }
        public double Position { get; set; }
        public double Duration { get; set; } = 3;
        public double Rate { get; set; } = 1;
        public event Action? StateChanged;

        public void Play(IReadOnlyList<IAudioSource> tracks, double startSeconds = 0)
        {
            Plays.Add((tracks.Count, startSeconds));
            Position = startSeconds;
            IsPlaying = true;
        }

        public void Pause() => IsPlaying = false;

        public void Resume()
        {
            Resumes++;
            IsPlaying = true;
        }

        public void Stop()
        {
            Stops++;
            IsPlaying = false;
        }

        public void Seek(double seconds) => Position = seconds;
        public void Dispose() { }

        /// <summary>The tracks stop by themselves, at <paramref name="at"/> seconds.</summary>
        public void Finish(double at = 3)
        {
            Position = at;
            IsPlaying = false;
            StateChanged?.Invoke();
        }
    }

    static MeetingRecord WithTracks(MeetingFixture fixture, bool system)
    {
        var meeting = fixture.Store.Create("Созвон");
        foreach (var path in system
                     ? new[] { fixture.Store.MicrophoneAudioPath(meeting.Id), fixture.Store.SystemAudioPath(meeting.Id) }
                     : [fixture.Store.MicrophoneAudioPath(meeting.Id)])
        {
            using var writer = CafWriter.Create(path);
            writer.Append(new float[48_000]);
        }
        return meeting;
    }

    [AvaloniaFact]
    public void PlaysBothTracksTogetherAndResumesWherePaused()
    {
        using var fixture = new MeetingFixture();
        var player = new RecordingPlayer();
        using var playback = new MeetingPlayback(player, fixture.Store);
        var meeting = WithTracks(fixture, system: true);

        playback.Play(meeting);
        Assert.Equal((2, 0.0), Assert.Single(player.Plays));
        Assert.True(playback.IsPlaying);
        Assert.Equal(meeting.Id, playback.MeetingId);
        Assert.Equal(3, playback.Duration, 3);

        playback.Pause();
        Assert.False(playback.IsPlaying);
        playback.Play(meeting);
        Assert.Equal(1, player.Resumes);
        Assert.Single(player.Plays);
    }

    [AvaloniaFact]
    public void PlayingALoadedMeetingFromAMomentOnlyJumpsThere()
    {
        using var fixture = new MeetingFixture();
        var player = new RecordingPlayer();
        using var playback = new MeetingPlayback(player, fixture.Store);
        var meeting = WithTracks(fixture, system: true);
        playback.Play(meeting);
        playback.Pause();

        // A transcript timestamp or the scrubber: no second output, the playhead moves and it plays.
        playback.Play(meeting, from: 1.5);
        Assert.Single(player.Plays);
        Assert.Equal(1.5, player.Position);
        Assert.Equal(1.5, playback.Position);
        Assert.True(playback.IsPlaying);
        Assert.Equal(1, player.Resumes);

        // Beyond the end is the end.
        playback.Play(meeting, from: 99);
        Assert.Equal(3, player.Position, 3);
    }

    [AvaloniaFact]
    public void AnotherMeetingStartsFromTheMomentAsked()
    {
        using var fixture = new MeetingFixture();
        var player = new RecordingPlayer();
        using var playback = new MeetingPlayback(player, fixture.Store);
        var first = WithTracks(fixture, system: true);
        var second = WithTracks(fixture, system: false);
        playback.Play(first);

        playback.Play(second, from: 2);
        Assert.Equal((1, 2.0), player.Plays[^1]);
        Assert.Equal(second.Id, playback.MeetingId);
        Assert.True(player.Stops > 0);
    }

    [AvaloniaFact]
    public void SeekingMovesThePlayheadWithoutStartingOrPausing()
    {
        using var fixture = new MeetingFixture();
        var player = new RecordingPlayer();
        using var playback = new MeetingPlayback(player, fixture.Store);
        var meeting = WithTracks(fixture, system: true);

        // Nothing loaded: nothing to move.
        playback.Seek(2);
        Assert.Empty(player.Plays);
        Assert.Equal(0, playback.Position);

        playback.Play(meeting);
        playback.Pause();
        var changes = new List<string?>();
        playback.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        playback.Seek(2);
        Assert.Equal(2, playback.Position);
        Assert.False(playback.IsPlaying);
        Assert.Contains(nameof(MeetingPlayback.Position), changes);

        playback.Seek(-4);
        Assert.Equal(0, playback.Position);
        playback.Seek(40);
        Assert.Equal(3, playback.Position, 3);
    }

    [AvaloniaFact]
    public void TheSpeedReachesThePlayerAndStaysForTheNextMeeting()
    {
        using var fixture = new MeetingFixture();
        var player = new RecordingPlayer();
        using var playback = new MeetingPlayback(player, fixture.Store);
        Assert.Equal(1, playback.Rate);

        var changes = new List<string?>();
        playback.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        playback.SetRate(1.5);
        Assert.Equal(1.5, playback.Rate);
        Assert.Equal(1.5, player.Rate);
        Assert.Contains(nameof(MeetingPlayback.Rate), changes);

        playback.Play(WithTracks(fixture, system: true));
        playback.Play(WithTracks(fixture, system: false));
        Assert.Equal(1.5, playback.Rate);
        Assert.Equal(1.5, player.Rate);
    }

    [AvaloniaFact]
    public void AMicrophoneOnlyMeetingPlaysItsOneTrack()
    {
        using var fixture = new MeetingFixture();
        var player = new RecordingPlayer();
        using var playback = new MeetingPlayback(player, fixture.Store);
        playback.Play(WithTracks(fixture, system: false));
        Assert.Equal(1, Assert.Single(player.Plays).Tracks);
    }

    [AvaloniaFact]
    public void AMeetingWithoutAudioSaysSo()
    {
        using var fixture = new MeetingFixture();
        using var playback = new MeetingPlayback(new RecordingPlayer(), fixture.Store);
        var meeting = fixture.Store.Create("Без звука");
        var error = Assert.Throws<SlovoException>(() => playback.Play(meeting));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Null(playback.MeetingId);
    }

    [AvaloniaFact]
    public void TheEndRewindsToTheStartAndTheNextPlayOpensTheOutputAgain()
    {
        using var fixture = new MeetingFixture();
        var player = new RecordingPlayer();
        using var playback = new MeetingPlayback(player, fixture.Store);
        var meeting = WithTracks(fixture, system: true);
        playback.Play(meeting, from: 2);
        player.Finish(at: 3);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(playback.IsPlaying);
        Assert.Equal(meeting.Id, playback.MeetingId);
        Assert.Equal(0, playback.Position);

        // An arrow press after the end moves the parked playhead; play starts from there.
        playback.Seek(1);
        Assert.Equal(1, playback.Position);
        playback.Play(meeting);
        Assert.Equal((2, 1.0), player.Plays[^1]);
        Assert.Equal(0, player.Resumes);
        Assert.True(playback.IsPlaying);

        playback.Stop();
        Assert.Null(playback.MeetingId);
    }

    [AvaloniaFact]
    public void ALostOutputKeepsItsPlace()
    {
        using var fixture = new MeetingFixture();
        var player = new RecordingPlayer();
        using var playback = new MeetingPlayback(player, fixture.Store);
        var meeting = WithTracks(fixture, system: true);
        playback.Play(meeting);
        player.Finish(at: 1.25);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(playback.IsPlaying);
        Assert.Equal(1.25, playback.Position);

        playback.Play(meeting);
        Assert.Equal((2, 1.25), player.Plays[^1]);
    }

    [AvaloniaFact]
    public void AnOutputThatWillNotStartLeavesNothingLoaded()
    {
        using var fixture = new MeetingFixture();
        using var playback = new MeetingPlayback(new UnavailableAudioPlayer(), fixture.Store);
        Assert.Throws<SlovoException>(() => playback.Play(WithTracks(fixture, system: true)));
        Assert.Null(playback.MeetingId);
        Assert.False(playback.IsPlaying);
        Assert.Equal(0, playback.Duration);
    }
}
