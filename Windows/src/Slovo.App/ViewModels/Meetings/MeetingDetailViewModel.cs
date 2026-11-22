using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.App.ViewModels.Meetings;

public enum MeetingDetailTab
{
    Transcript,
    Highlights,
    Statistics,
    ClaudeNotes,
}

/// <summary>
/// One speaker in the speakers bar: colour, name and talk time, which toggles the filter, and
/// the pencil that opens the speaker's name and merge controls (the macOS <c>SpeakerChip</c>).
/// </summary>
public sealed partial class SpeakerChipViewModel(MeetingSpeaker speaker, string label, string talkTime, bool isActive) : ViewModelBase
{
    public MeetingSpeaker Speaker { get; } = speaker;
    public string Label { get; } = label;
    public string TalkTime { get; } = talkTime;
    public int ColorIndex => Speaker.ColorIndex;

    [ObservableProperty] public partial bool IsActive { get; set; } = isActive;
}

/// <summary>«All», first in the speakers bar: clears the filter, and stands out while nothing is filtered.</summary>
public sealed record AllSpeakersChip(bool IsSelected);

public sealed record ReassignChoice(TranscriptLineViewModel Line, string SpeakerId, string Label);

public sealed partial class TranscriptLineViewModel(TranscriptLine line, MeetingSpeaker speaker, string speakerLabel, bool isMe, bool isStarred,
                                                     string? note, IReadOnlyList<(string Id, string Label)> otherSpeakers) : ViewModelBase
{
    public TranscriptLine Line { get; } = line;
    public string Timestamp { get; } = TranscriptMerger.Timestamp(line.Start);
    /// <summary>The speaker the line is shown under; its name opens the speaker's editor.</summary>
    public MeetingSpeaker Speaker { get; } = speaker;
    public string SpeakerLabel { get; } = speakerLabel;
    public int ColorIndex => Speaker.ColorIndex;
    /// <summary>Whether the line's speaker already counts as «Я», which leaves "This is me" nothing to do.</summary>
    public bool IsMe { get; } = isMe;
    public string Text => Line.Text;
    public bool IsStarred { get; } = isStarred;
    public string? Note { get; } = note;
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public IReadOnlyList<ReassignChoice> ReassignChoices => otherSpeakers.Select(s => new ReassignChoice(this, s.Id, s.Label)).ToList();
    public bool CanReassign => otherSpeakers.Count > 0;
    public string StarTitle => Loc.Instance.Get(IsStarred ? "meetings.highlight.remove" : "meetings.highlight.add");

    [ObservableProperty] public partial bool IsFlashing { get; set; }
}

/// <summary>
/// A marked moment: its time, the speaker and the start of the line it belongs to, and its
/// note (the macOS <c>HighlightRow</c>).
/// </summary>
public sealed record HighlightRowViewModel(MeetingHighlight Highlight, string TimeText, string? LineText, int? LineIndex, bool CanPlay = false,
                                           string? SpeakerLabel = null, int SpeakerColorIndex = 0)
{
    public string? Note => Highlight.Note;
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool HasLine => LineText is not null;
    public bool HasSpeaker => SpeakerLabel is not null;
    /// <summary>The first 120 characters of the line in quotes: «…» / “…”.</summary>
    public string? Quote => LineText is { } text ? Loc.Instance.Format("meetings.highlight.quoted", TranscriptMerger.Excerpt(text)) : null;
    /// <summary>What clicking the moment does: shows its line (and plays from it), or only plays.</summary>
    public string? OpenTitle => HasLine ? Loc.Instance.Get("meetings.highlight.show") : CanPlay ? Loc.Instance.Get("meetings.playback.jump") : null;
}

/// <summary>One speaker on the Statistics tab: their share as a bar in their colour, and the numbers behind it.</summary>
public sealed record StatRow(string Label, int ColorIndex, double Share, string ShareText, string Detail);

/// <summary>A number of the "Speech and pauses" section: a title, the value and, for speech, its share.</summary>
public sealed record StatFigure(string Title, string Value, string? Caption = null)
{
    public bool HasCaption => Caption is not null;
}

/// <summary>The selected meeting (the macOS MeetingDetail).</summary>
public sealed partial class MeetingDetailViewModel : ViewModelBase
{
    readonly MeetingsContext context;
    readonly ISheetHost sheets;
    MeetingRecord meeting;
    readonly HashSet<string> filter = new(StringComparer.Ordinal);
    SpeechStats stats = new();

    public MeetingDetailViewModel(MeetingRecord meeting, MeetingsContext context, ISheetHost sheets)
    {
        this.meeting = meeting;
        this.context = context;
        this.sheets = sheets;
        title = meeting.Title;
        Playback = new PlaybackBarViewModel(meeting, context.Playback, sheets);
        Rebuild();
    }

    public Guid Id => meeting.Id;
    public MeetingRecord Meeting => meeting;

    /// <summary>The playback bar, and what transcript lines and moments play through.</summary>
    public PlaybackBarViewModel Playback { get; }

    [ObservableProperty] public partial MeetingDetailTab Tab { get; set; } = MeetingDetailTab.Transcript;
    [ObservableProperty] public partial IReadOnlyList<SpeakerChipViewModel> Speakers { get; private set; } = [];
    /// <summary>The speakers bar: «All», then one chip per speaker (<see cref="Speakers"/>), wrapping together.</summary>
    [ObservableProperty] public partial IReadOnlyList<object> SpeakerBar { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<TranscriptLineViewModel> Lines { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<HighlightRowViewModel> Highlights { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<StatRow> StatRows { get; private set; } = [];
    [ObservableProperty] public partial TranscriptLineViewModel? FocusLine { get; private set; }

    string title;

    /// <summary>The editable title; committed with <see cref="CommitTitleCommand"/> (Enter or leaving the field).</summary>
    public string Title
    {
        get => title;
        set => SetProperty(ref title, value);
    }

    public void Update(MeetingRecord fresh)
    {
        var titleChangedElsewhere = fresh.Title != meeting.Title;
        meeting = fresh;
        if (titleChangedElsewhere)
        {
            Title = fresh.Title;
        }
        Playback.Update(fresh);
        Rebuild();
    }

    /// <summary>Lets go of the app-wide player once another meeting's card replaces this one.</summary>
    public void Detach() => Playback.Detach();

    // Header and status

    public string MetaText
    {
        get
        {
            var loc = Loc.Instance;
            var parts = new List<string> { MeetingDates.LongDateShortTime(meeting.CreatedAt), Formatting.Clock(meeting.Duration) };
            if (meeting.EngineId is { } engine)
            {
                parts.Add(engine.DisplayName());
            }
            var separated = meeting.Speakers.Count(s => s.Number is not null);
            if (separated > 0)
            {
                parts.Add(loc.Plural("plural.speakers", separated));
            }
            if (!meeting.HasAudio)
            {
                parts.Add(loc.Get("meetings.detail.audioDeleted"));
            }
            else if (!meeting.HasSystemAudio)
            {
                parts.Add(loc.Get("meetings.detail.micOnly"));
            }
            if (meeting.Source is { Origin: MeetingOrigin.Phone } source)
            {
                parts.Add(source.Device is { } device ? loc.Format("meetings.source.phone.device", device) : loc.Get("meetings.source.phone"));
            }
            return string.Join(" · ", parts);
        }
    }

    TranscriptionProgress? Progress => context.Processing.Progress(meeting.Id);

    public bool IsRecordingNow => meeting.Status == MeetingStatus.Recording;
    public bool IsProcessing => context.Processing.IsProcessing(meeting.Id) || meeting.Status == MeetingStatus.Transcribing;
    public bool IsRecorded => meeting.Status == MeetingStatus.Recorded && !IsProcessing;
    public bool IsFailed => meeting.Status == MeetingStatus.Failed && !IsProcessing;
    public bool IsTranscribed => meeting.Status == MeetingStatus.Transcribed && !IsProcessing;
    public bool CanTranscribe => meeting.HasAudio;
    public string FailureText => meeting.ErrorMessage ?? Loc.Instance.Get("meetings.failed.generic");
    public double ProgressValue => (Progress?.OverallFraction ?? 0) * 100;

    public string ProgressText
    {
        get
        {
            var loc = Loc.Instance;
            if (Progress is not { } progress)
            {
                return loc.Get("meetings.transcribing.title");
            }
            return progress.Phase.Kind switch
            {
                MeetingProcessingPhaseKind.Preparing => loc.Get("meetings.transcribing.preparing"),
                MeetingProcessingPhaseKind.LoadingSpeakerModel => loc.Get("meetings.transcribing.loadingSpeakerModel"),
                MeetingProcessingPhaseKind.SeparatingSpeakers => loc.Format("meetings.transcribing.separating",
                    $"{(int)Math.Round(progress.Phase.Fraction * 100)} %"),
                _ => progress.TotalSegments > 0
                    ? loc.Format("meetings.transcribing.progress", (long)progress.CompletedSegments, (long)progress.TotalSegments)
                    : loc.Get("meetings.transcribing.title"),
            };
        }
    }

    public bool ShowsSpeakerModelNotice => meeting.Diarization?.State == DiarizationState.ModelMissing && !context.Processing.IsSpeakerModelReady;
    public string? DiarizationFailure => meeting.Diarization is { State: DiarizationState.Failed } d
        ? d.Message ?? Loc.Instance.Get("meetings.diarization.failedGeneric") : null;
    public bool HasDiarizationFailure => DiarizationFailure is not null;

    /// <summary>Whether the separation-failed notice offers "Try again": speakers alone or a whole new run can start now.</summary>
    public bool CanRetrySpeakers => context.Processing.CanReprocessSpeakersOnly(meeting) || CanProcessFully;

    /// <summary>A whole new run can start: the audio is there and nothing records or processes the meeting.</summary>
    bool CanProcessFully => meeting.HasAudio && meeting.Status is not (MeetingStatus.Recording or MeetingStatus.Transcribing)
                            && !context.Processing.IsProcessing(meeting.Id);

    // Tabs

    public string HighlightsTabTitle => Loc.Instance.Format("meetings.tab.highlights", (long)meeting.Highlights.Count);
    public bool HasClaudeNotes => meeting.ClaudeNotes is not null;
    public bool IsTranscriptTab { get => Tab == MeetingDetailTab.Transcript; set { if (value) Tab = MeetingDetailTab.Transcript; } }
    public bool IsHighlightsTab { get => Tab == MeetingDetailTab.Highlights; set { if (value) Tab = MeetingDetailTab.Highlights; } }
    public bool IsStatisticsTab { get => Tab == MeetingDetailTab.Statistics; set { if (value) Tab = MeetingDetailTab.Statistics; } }
    public bool IsClaudeTab { get => Tab == MeetingDetailTab.ClaudeNotes; set { if (value) Tab = MeetingDetailTab.ClaudeNotes; } }

    partial void OnTabChanged(MeetingDetailTab value)
    {
        OnPropertyChanged(nameof(IsTranscriptTab));
        OnPropertyChanged(nameof(IsHighlightsTab));
        OnPropertyChanged(nameof(IsStatisticsTab));
        OnPropertyChanged(nameof(IsClaudeTab));
    }

    public bool HasLines => Lines.Count > 0;
    public string EmptyTranscriptText => Loc.Instance.Get(filter.Count > 0 ? "meetings.transcript.filtered" : "meetings.transcript.empty");
    public bool HasHighlights => Highlights.Count > 0;
    /// <summary>"4 moments" above the list, counting the moments the filter leaves.</summary>
    public string HighlightsCountText => Loc.Instance.Plural("plural.moments", Highlights.Count);
    public string EmptyHighlightsText => filter.Count > 0 && meeting.Highlights.Count > 0
        ? Loc.Instance.Get("meetings.highlights.filtered")
        : Loc.Instance.Format("meetings.highlights.empty", context.Settings.HighlightShortcut.DisplayText);
    public bool IsFiltered => filter.Count > 0;
    /// <summary>Whether «All» stands out: no speaker is picked, so every voice is shown.</summary>
    public bool ShowsAllSpeakers => filter.Count == 0;

    /// <summary>"Copy", or "Copy selected" while the filter narrows what is copied.</summary>
    public string CopyTranscriptTitle => Loc.Instance.Get(filter.Count > 0 ? "meetings.action.copyFiltered" : "meetings.action.copy");

    // Statistics (the macOS SpeechStatsView). The numbers describe the whole recording: the
    // speaker filter changes what the transcript shows, not what the meeting was.

    public bool HasStats => !stats.IsEmpty;

    /// <summary>The line worth reading before the numbers: who held the floor.</summary>
    public string StatsHeadline
    {
        get
        {
            var loc = Loc.Instance;
            if (stats.Speakers.Count <= 1 || meeting.SpeakerWithId(stats.Speakers[0].SpeakerId) is not { } speaker)
            {
                return loc.Get("meetings.stats.single");
            }
            // Naming one voice as "the one who talks the most" would be a coin flip when the
            // runner-up spoke exactly as long; say so plainly instead.
            if (stats.Speakers[1].SpeakingSeconds.Equals(stats.Speakers[0].SpeakingSeconds))
            {
                return loc.Get("meetings.stats.tied");
            }
            return TranscriptMerger.IsMe(speaker, meeting)
                ? loc.Get("meetings.stats.topIsYou")
                : loc.Format("meetings.stats.top", TranscriptMerger.SpeakerLabel(speaker, meeting));
        }
    }

    /// <summary>The owner's share, unless he is the only voice and it can only be all of it; then "Across the whole recording".</summary>
    public string StatsCaption
    {
        get
        {
            var loc = Loc.Instance;
            var parts = new List<string>();
            if (stats.Speakers.Count > 1 && stats.Speakers.FirstOrDefault(IsMe) is { } mine)
            {
                parts.Add(loc.Format("meetings.stats.yourShare", Percent(mine.Share)));
            }
            parts.Add(loc.Get("meetings.stats.wholeMeeting"));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>How much of the recording was speech, for the bar of the "Speech and pauses" section.</summary>
    public double SpeechShare => stats.SpeechShare;

    /// <summary>Speech with its share, silence, the longest pause and the pauses over the threshold.</summary>
    public IReadOnlyList<StatFigure> SpeechFigures
    {
        get
        {
            var loc = Loc.Instance;
            // The silence a gap has to beat to be counted, «5 с» / "5 s".
            var threshold = loc.Format("meetings.stats.seconds", Formatting.Number((long)Math.Round(stats.PauseSeconds, MidpointRounding.AwayFromZero)));
            return
            [
                new StatFigure(loc.Get("meetings.stats.speech"), Formatting.Clock(stats.SpeechSeconds), Percent(stats.SpeechShare)),
                new StatFigure(loc.Get("meetings.stats.silence"), Formatting.Clock(stats.SilenceSeconds)),
                new StatFigure(loc.Get("meetings.stats.longestPause"), Formatting.Clock(stats.LongestPauseSeconds)),
                new StatFigure(loc.Format("meetings.stats.pauses", threshold), Formatting.Number(stats.PauseCount)),
            ];
        }
    }

    bool IsMe(SpeakerSpeechStats speakerStats) =>
        meeting.SpeakerWithId(speakerStats.SpeakerId) is { } speaker && TranscriptMerger.IsMe(speaker, meeting);

    static string Percent(double fraction) => $"{(int)Math.Round(fraction * 100)} %";

    /// <summary>
    /// Talk time, turns, the average turn and the pace, as one quiet line. A single turn is its
    /// own average, and too little speech has no honest pace.
    /// </summary>
    static string StatDetail(SpeakerSpeechStats speakerStats)
    {
        var loc = Loc.Instance;
        var parts = new List<string> { Formatting.Clock(speakerStats.SpeakingSeconds), loc.Plural("plural.turns", speakerStats.Turns) };
        if (speakerStats.Turns > 1)
        {
            parts.Add(loc.Format("meetings.stats.averageTurn", Formatting.Clock(speakerStats.AverageTurnSeconds)));
        }
        if (speakerStats.WordsPerMinute is { } pace)
        {
            parts.Add(loc.Format("meetings.stats.pace", Formatting.Number((long)Math.Round(pace, MidpointRounding.AwayFromZero))));
        }
        return string.Join(" · ", parts);
    }

    // Claude's notes

    public string ClaudeSummary => meeting.ClaudeNotes?.Summary ?? "";
    public bool HasClaudeSummary => ClaudeSummary.Length > 0;
    public IReadOnlyList<string> ClaudeDecisions => meeting.ClaudeNotes?.Decisions ?? [];
    public IReadOnlyList<string> ClaudeTasks => meeting.ClaudeNotes?.Tasks ?? [];
    public bool HasClaudeDecisions => ClaudeDecisions.Count > 0;
    public bool HasClaudeTasks => ClaudeTasks.Count > 0;
    public string ClaudeWritten => meeting.ClaudeNotes is { } notes
        ? Loc.Instance.Format("meetings.claudeNotes.written", notes.Author, Formatting.RelativeDate(notes.WrittenAt)) : "";

    // Playback

    /// <summary>Transcript timestamps and lines play from their moment while the meeting has audio.</summary>
    public bool CanPlay => meeting.HasAudio && meeting.Status != MeetingStatus.Recording;

    void Rebuild()
    {
        var effective = meeting.EffectiveSpeakers;
        // A merged or reassigned speaker disappears; the filter forgets it.
        filter.RemoveWhere(id => effective.All(s => s.Id != id));
        Speakers = effective.Select(s => new SpeakerChipViewModel(s, TranscriptMerger.SpeakerLabel(s, meeting),
            Formatting.Clock(meeting.TalkTime(s.Id)), filter.Count == 0 || filter.Contains(s.Id))).ToList();
        SpeakerBar = [new AllSpeakersChip(filter.Count == 0), .. Speakers];

        var allLines = TranscriptMerger.Lines(meeting);
        var shown = filter.Count > 0 ? TranscriptMerger.Lines(meeting, filter) : allLines;
        var highlightsById = meeting.Highlights.ToDictionary(h => h.Id);
        Lines = shown.Select(line =>
        {
            var speaker = meeting.SpeakerWithId(line.SpeakerId) ?? MeetingSpeaker.Fallback(line.Track);
            var others = effective.Where(s => s.Track == line.Track && s.Id != line.SpeakerId)
                .Select(s => (s.Id, TranscriptMerger.SpeakerLabel(s, meeting))).ToList();
            var note = line.HighlightIds.Select(id => highlightsById.TryGetValue(id, out var h) ? h.Note : null).FirstOrDefault(n => !string.IsNullOrEmpty(n));
            return new TranscriptLineViewModel(line, speaker, TranscriptMerger.SpeakerLabel(speaker, meeting), TranscriptMerger.IsMe(speaker, meeting),
                line.HighlightIds.Count > 0, note, others);
        }).ToList();

        // Unfiltered lines, so every moment finds its own line; with a filter on, only the
        // moments on the chosen speakers' lines are listed, as Copy moments copies them.
        Highlights = meeting.Highlights.OrderBy(h => h.Time).Select(h =>
        {
            var line = TranscriptMerger.AnchoredLine(h, allLines);
            var speaker = line is null ? null : meeting.SpeakerWithId(line.SpeakerId);
            return new HighlightRowViewModel(h, TranscriptMerger.Timestamp(h.Time), line?.Text, line?.Id, meeting.HasAudio,
                speaker is null ? null : TranscriptMerger.SpeakerLabel(speaker, meeting), speaker?.ColorIndex ?? 0);
        }).Where(row => filter.Count == 0 || row.LineIndex is { } i && filter.Contains(allLines[i].SpeakerId)).ToList();

        stats = SpeechStatsCalculator.Compute(meeting);
        StatRows = stats.Speakers.Select(s =>
        {
            var speaker = meeting.SpeakerWithId(s.SpeakerId);
            return new StatRow(speaker is null ? s.SpeakerId : TranscriptMerger.SpeakerLabel(speaker, meeting), speaker?.ColorIndex ?? 0,
                s.Share, Percent(s.Share), StatDetail(s));
        }).ToList();

        if (Tab == MeetingDetailTab.ClaudeNotes && meeting.ClaudeNotes is null)
        {
            Tab = MeetingDetailTab.Transcript;
        }
        DeleteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(string.Empty);
    }

    // Commands

    [RelayCommand]
    void CommitTitle()
    {
        var trimmed = TranscriptMerger.NormalizedText(Title).Trim();
        if (trimmed.Length == 0)
        {
            Title = meeting.Title;
            return;
        }
        if (trimmed != meeting.Title)
        {
            Save(meeting with { Title = trimmed });
        }
    }

    [RelayCommand]
    void ToggleSpeaker(SpeakerChipViewModel chip)
    {
        if (!filter.Remove(chip.Speaker.Id))
        {
            filter.Add(chip.Speaker.Id);
        }
        if (filter.Count == Speakers.Count)
        {
            filter.Clear();
        }
        Rebuild();
    }

    [RelayCommand]
    void ShowAllSpeakers()
    {
        filter.Clear();
        Rebuild();
    }

    /// <summary>The pencil on a speaker chip (or its context menu): the speaker's name, «This is me» and merging.</summary>
    [RelayCommand]
    void EditSpeaker(SpeakerChipViewModel chip) => ShowSpeakerEditor(chip.Speaker);

    /// <summary>A line's speaker name, clicked, or "Rename speaker…" in the line's menu.</summary>
    [RelayCommand]
    void RenameLineSpeaker(TranscriptLineViewModel line) => ShowSpeakerEditor(line.Speaker);

    /// <summary>"This is me" in a line's menu: its speaker becomes «Я».</summary>
    [RelayCommand(CanExecute = nameof(CanMarkLineAsMe))]
    void MarkLineAsMe(TranscriptLineViewModel line) => Save(MeetingEdits.MarkAsMe(line.Line.SpeakerId, meeting));

    static bool CanMarkLineAsMe(TranscriptLineViewModel? line) => line is { IsMe: false };

    void ShowSpeakerEditor(MeetingSpeaker speaker) => sheets.ShowSheet(new SpeakerEditorViewModel(speaker, meeting, Save));

    [RelayCommand]
    void ToggleStar(TranscriptLineViewModel line) => Save(MeetingEdits.ToggleHighlight(line.Line, meeting, DateTimeOffset.UtcNow));

    [RelayCommand]
    void EditNote(TranscriptLineViewModel line)
    {
        var current = meeting;
        if (line.Line.HighlightIds.Count == 0)
        {
            current = MeetingEdits.ToggleHighlight(line.Line, meeting, DateTimeOffset.UtcNow);
            Save(current);
        }
        var fresh = TranscriptMerger.Lines(current).FirstOrDefault(l => l.Start == line.Line.Start && l.SpeakerId == line.Line.SpeakerId);
        if (fresh?.HighlightIds.FirstOrDefault() is { } id && id != Guid.Empty)
        {
            var highlight = current.Highlights.First(h => h.Id == id);
            sheets.ShowSheet(new HighlightNoteViewModel(highlight.Note, note => Save(MeetingEdits.SetHighlightNote(id, note, meeting))));
        }
    }

    [RelayCommand]
    void EditHighlightNote(HighlightRowViewModel row) =>
        sheets.ShowSheet(new HighlightNoteViewModel(row.Note, note => Save(MeetingEdits.SetHighlightNote(row.Highlight.Id, note, meeting))));

    [RelayCommand]
    void DeleteHighlight(HighlightRowViewModel row) => Save(MeetingEdits.DeleteHighlight(row.Highlight.Id, meeting));

    [RelayCommand]
    void ShowInTranscript(HighlightRowViewModel row)
    {
        filter.Clear();
        Tab = MeetingDetailTab.Transcript;
        Rebuild();
        if (row.LineIndex is { } index && index < Lines.Count)
        {
            FocusLine = Lines[index];
            FocusLine.IsFlashing = true;
        }
    }

    [RelayCommand]
    void Reassign(ReassignChoice choice) => Save(MeetingEdits.ReassignLine(choice.Line.Line, choice.SpeakerId, meeting));

    [RelayCommand]
    void CopyLine(TranscriptLineViewModel line) => context.Copy($"[{line.Timestamp}] {line.SpeakerLabel}: {line.Text}");

    /// <summary>The transcript as plain text, limited to the selected speakers while the filter is on.</summary>
    [RelayCommand]
    void CopyTranscript() => context.Copy(TranscriptMerger.PlainText(meeting, speakerFilter: filter.Count > 0 ? filter : null));

    /// <summary>The moments the list shows, as <c>transcript.md</c> lists them: <c>- [12:40] Аня: «…» — note</c>.</summary>
    [RelayCommand]
    void CopyHighlights() => context.Copy(TranscriptMerger.HighlightsText(meeting, speakerFilter: filter.Count > 0 ? filter : null));

    /// <summary>Saves <c>transcript.md</c>. Export always writes the whole document, whatever the speaker filter.</summary>
    [RelayCommand]
    async Task ExportAsync()
    {
        var fileName = string.Concat(meeting.Title.Split(Path.GetInvalidFileNameChars())) + ".md";
        if (await context.PickSaveFile(fileName) is not { } path)
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(path, TranscriptMerger.Markdown(meeting));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            sheets.ShowAlert(Loc.Instance.Get("meetings.error.export"), error.Message);
        }
    }

    [RelayCommand]
    void ShowInExplorer() => context.Shell.RevealFile(Path.Combine(context.Store.Folder(meeting.Id), "meeting.json"));

    // Screen recording

    /// <summary>
    /// Whether the card offers the meeting's screen recording: the file is still being written
    /// while the meeting records, so only afterwards, as on macOS.
    /// </summary>
    public bool HasScreenRecording => meeting.HasScreenRecording && meeting.Status != MeetingStatus.Recording;

    /// <summary>
    /// Opens <c>screen.mov</c> in the video player Windows has for it. Avalonia has no video view,
    /// so the macOS card's inline player becomes a button; the video is muted reference footage
    /// on its own timeline, which the audio player does not need to follow.
    /// </summary>
    [RelayCommand]
    void OpenScreenRecording()
    {
        var path = context.Store.ScreenRecordingPath(meeting.Id);
        if (!File.Exists(path))
        {
            sheets.ShowAlert(Loc.Instance.Get("meetings.detail.screenRecording"), Loc.Instance.Get("meetings.screenRecording.missing"));
            return;
        }
        context.Shell.OpenFile(path);
    }

    /// <summary>A meeting that is still recording cannot be deleted: its files are being written.</summary>
    public bool CanDelete => meeting.Status != MeetingStatus.Recording;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    async Task DeleteAsync()
    {
        if (!CanDelete)
        {
            return;
        }
        var loc = Loc.Instance;
        if (!await sheets.ConfirmAsync(loc.Get("meetings.delete.title"), loc.Format("meetings.delete.message", meeting.Title), loc.Get("common.delete")))
        {
            return;
        }
        if (context.Playback.MeetingId == meeting.Id)
        {
            context.Playback.Stop();
        }
        context.Processing.Cancel(meeting.Id);
        context.Store.Delete(meeting.Id);
    }

    [RelayCommand]
    void Transcribe() => context.Processing.Transcribe(meeting.Id);

    [RelayCommand]
    void CancelProcessing() => context.Processing.Cancel(meeting.Id);

    [RelayCommand]
    void Reprocess() => sheets.ShowSheet(new ReprocessViewModel(meeting, context));

    [RelayCommand]
    void OpenSpeakerSettings() => context.OpenSettings("Meetings");

    /// <summary>
    /// "Try again" on the separation-failed notice: speakers alone with the people count this
    /// meeting last asked for when that is possible, otherwise a whole run with the Meetings settings.
    /// </summary>
    [RelayCommand]
    void RetrySpeakers()
    {
        if (context.Processing.CanReprocessSpeakersOnly(meeting))
        {
            context.Processing.Reprocess(meeting.Id, new ReprocessOptions(SpeakersOnly: true, SeparateSpeakers: true,
                meeting.Diarization?.RequestedSpeakers ?? 0, meeting.EngineId ?? context.Settings.MeetingEngine));
        }
        else if (CanProcessFully)
        {
            context.Processing.Transcribe(meeting.Id);
        }
    }

    [RelayCommand]
    async Task DeleteClaudeNotesAsync()
    {
        var loc = Loc.Instance;
        if (await sheets.ConfirmAsync(loc.Get("meetings.claudeNotes.delete.title"), loc.Format("meetings.claudeNotes.delete.message", meeting.Title), loc.Get("common.delete")))
        {
            context.Store.SetClaudeNotes(null, meeting.Id);
        }
    }

    /// <summary>A transcript line's timestamp or text, clicked: plays from where the line starts.</summary>
    [RelayCommand]
    void PlayFrom(TranscriptLineViewModel line) => Playback.SeekAndPlay(line.Line.Start);

    /// <summary>A marked moment, clicked: plays from it and, when it belongs to a line, shows that line in the transcript.</summary>
    [RelayCommand]
    void OpenHighlight(HighlightRowViewModel row)
    {
        Playback.SeekAndPlay(row.Highlight.Time);
        if (row.HasLine)
        {
            ShowInTranscript(row);
        }
    }

    void Save(MeetingRecord edited)
    {
        meeting = edited;
        context.Store.Update(edited);
        Rebuild();
    }
}
