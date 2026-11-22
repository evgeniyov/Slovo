using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;

namespace Slovo.App.ViewModels.Meetings;

/// <summary>
/// Play/pause, a draggable scrubber, current/duration time and a speed choice for a
/// meeting's recorded audio (the macOS PlaybackBar). Hidden entirely once the audio is gone.
/// Also what the rest of the card plays through: a transcript timestamp or a marked moment
/// jumps there and plays, and the left/right arrows nudge the playhead.
/// </summary>
public sealed partial class PlaybackBarViewModel : ViewModelBase
{
    /// <summary>The speeds on offer, as on the Mac; 1× is the default and the choice is not saved.</summary>
    public static readonly IReadOnlyList<double> Rates = [0.75, 1, 1.25, 1.5, 1.75, 2];

    /// <summary>How far a left/right arrow press moves the playhead.</summary>
    public const double SeekStep = 5;

    readonly IMeetingPlayback playback;
    readonly ISheetHost sheets;
    MeetingRecord meeting;

    public PlaybackBarViewModel(MeetingRecord meeting, IMeetingPlayback playback, ISheetHost sheets)
    {
        this.meeting = meeting;
        this.playback = playback;
        this.sheets = sheets;
        RateChoices = Rates.Select(rate => new ChoiceItem<double>(rate, SpeedLabel(rate))).ToList();
        playback.PropertyChanged += OnPlaybackChanged;
    }

    public IReadOnlyList<ChoiceItem<double>> RateChoices { get; }

    public ChoiceItem<double> SelectedRate
    {
        get => RateChoices.FirstOrDefault(choice => choice.Value == playback.Rate) ?? RateChoices.First(choice => choice.Value == 1);
        set
        {
            if (value is not null && value.Value != playback.Rate)
            {
                playback.SetRate(value.Value);
            }
        }
    }

    bool IsCurrent => playback.MeetingId == meeting.Id;

    /// <summary>Shown while the meeting has audio and is not still recording it.</summary>
    public bool IsVisible => meeting.HasAudio && meeting.Status != MeetingStatus.Recording;

    public bool IsPlaying => IsCurrent && playback.IsPlaying;

    public double Duration => IsCurrent && playback.Duration > 0 ? playback.Duration : meeting.Duration;

    public double CurrentTime => IsCurrent ? Math.Clamp(playback.Position, 0, Math.Max(0, Duration)) : 0;

    /// <summary>How far the playhead is through the meeting, 0–1.</summary>
    public double Fraction => Duration > 0 ? Math.Clamp(CurrentTime / Duration, 0, 1) : 0;

    public string CurrentTimeText => Formatting.Clock(CurrentTime);

    public string DurationText => Formatting.Clock(Duration);

    public string PlayPauseTitle => Loc.Instance.Get(IsPlaying ? "meetings.playback.pause" : "meetings.playback.play");

    /// <summary>"1.25×", written the same way in both languages, as on the Mac.</summary>
    public static string SpeedLabel(double rate) => rate.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    public void Update(MeetingRecord fresh)
    {
        meeting = fresh;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Stops following the app's player once the card is gone.</summary>
    public void Detach() => playback.PropertyChanged -= OnPlaybackChanged;

    /// <summary>Plays or pauses; switches to this meeting first if another one (or none) is loaded.</summary>
    [RelayCommand]
    void TogglePlay()
    {
        if (!meeting.HasAudio)
        {
            return;
        }
        Run(() =>
        {
            if (IsPlaying)
            {
                playback.Pause();
            }
            else
            {
                playback.Play(meeting);
            }
        });
    }

    /// <summary>
    /// Jumps to a moment and plays from there: the scrubber, a transcript timestamp or a
    /// marked moment, clicked to listen instead of only read.
    /// </summary>
    public void SeekAndPlay(double time)
    {
        if (!meeting.HasAudio)
        {
            return;
        }
        Run(() => playback.Play(meeting, Math.Clamp(time, 0, Math.Max(0, Duration))));
    }

    /// <summary>Where the scrubber was clicked or let go, as a fraction of its width.</summary>
    [RelayCommand]
    void Seek(double fraction) => SeekAndPlay(Math.Clamp(fraction, 0, 1) * Duration);

    /// <summary>
    /// The left/right arrows: nudges the moment already loaded without changing whether it
    /// plays, and does nothing to a meeting that is not loaded, so a stray arrow press cannot
    /// start playback by itself. Returns whether the key belongs to playback (the meeting has audio).
    /// </summary>
    public bool Nudge(double delta)
    {
        if (!meeting.HasAudio)
        {
            return false;
        }
        if (IsCurrent)
        {
            playback.Seek(playback.Position + delta);
        }
        return true;
    }

    void Run(Action action)
    {
        try
        {
            action();
        }
        catch (SlovoException error)
        {
            sheets.ShowAlert(Loc.Instance.Get("meetings.error.playback"), error.Message);
        }
    }

    void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
        }
        else
        {
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    void Refresh()
    {
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(CurrentTime));
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PlayPauseTitle));
        OnPropertyChanged(nameof(SelectedRate));
    }
}
