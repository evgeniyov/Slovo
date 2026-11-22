using System.Text.Json.Serialization;

namespace Slovo.Core.Model;

// Raw values match the Swift enums exactly: they are what the shared JSON files and the
// settings file store.

/// <summary>Every speech engine the app can use.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EngineId>))]
public enum EngineId
{
    [JsonStringEnumMemberName("gigaam-v3-rnnt")] GigaamRnnt,
    [JsonStringEnumMemberName("gigaam-v3-ctc")] GigaamCtc,
    [JsonStringEnumMemberName("parakeet-v3")] Parakeet,
    /// <summary>macOS only; Windows never offers it but reads it from shared files.</summary>
    [JsonStringEnumMemberName("apple-dictation")] AppleDictation,
}

[JsonConverter(typeof(JsonStringEnumConverter<LanguageMode>))]
public enum LanguageMode
{
    /// <summary>Russian speech with English terms, the default.</summary>
    [JsonStringEnumMemberName("mixed")] Mixed,
    [JsonStringEnumMemberName("russian")] Russian,
    [JsonStringEnumMemberName("english")] English,
}

[JsonConverter(typeof(JsonStringEnumConverter<AppLanguage>))]
public enum AppLanguage
{
    [JsonStringEnumMemberName("system")] System,
    [JsonStringEnumMemberName("en")] En,
    [JsonStringEnumMemberName("ru")] Ru,
}

[JsonConverter(typeof(JsonStringEnumConverter<AppCategory>))]
public enum AppCategory
{
    [JsonStringEnumMemberName("personal")] Personal,
    [JsonStringEnumMemberName("work")] Work,
    [JsonStringEnumMemberName("email")] Email,
    [JsonStringEnumMemberName("other")] Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<StylePreset>))]
public enum StylePreset
{
    /// <summary>Sentence case, punctuation kept, a final period added when missing.</summary>
    [JsonStringEnumMemberName("formal")] Formal,
    /// <summary>Sentence case, the final period of the last sentence dropped.</summary>
    [JsonStringEnumMemberName("casual")] Casual,
    /// <summary>Lowercase sentence starts except terms and names, final periods dropped.</summary>
    [JsonStringEnumMemberName("veryCasual")] VeryCasual,
}

[JsonConverter(typeof(JsonStringEnumConverter<RetentionPeriod>))]
public enum RetentionPeriod
{
    [JsonStringEnumMemberName("forever")] Forever,
    [JsonStringEnumMemberName("days30")] Days30,
    [JsonStringEnumMemberName("days7")] Days7,
    [JsonStringEnumMemberName("hours24")] Hours24,
    /// <summary>Dictation audio only: nothing is kept for playback.</summary>
    [JsonStringEnumMemberName("never")] Never,
    /// <summary>Meeting audio only: removed once the transcript is written.</summary>
    [JsonStringEnumMemberName("afterTranscription")] AfterTranscription,
}

[JsonConverter(typeof(JsonStringEnumConverter<DictionaryItemKind>))]
public enum DictionaryItemKind
{
    /// <summary>A word or phrase, usually English, that fuzzy correction restores.</summary>
    [JsonStringEnumMemberName("term")] Term,
    /// <summary>An explicit "from → to" rule.</summary>
    [JsonStringEnumMemberName("replacement")] Replacement,
}

[JsonConverter(typeof(JsonStringEnumConverter<DictationMode>))]
public enum DictationMode
{
    [JsonStringEnumMemberName("pushToTalk")] PushToTalk,
    [JsonStringEnumMemberName("handsFree")] HandsFree,
}

[JsonConverter(typeof(JsonStringEnumConverter<Speaker>))]
public enum Speaker
{
    /// <summary>The microphone track.</summary>
    [JsonStringEnumMemberName("me")] Me,
    /// <summary>The system audio track.</summary>
    [JsonStringEnumMemberName("others")] Others,
}

[JsonConverter(typeof(JsonStringEnumConverter<MeetingTrack>))]
public enum MeetingTrack
{
    [JsonStringEnumMemberName("microphone")] Microphone,
    [JsonStringEnumMemberName("system")] System,
}

[JsonConverter(typeof(JsonStringEnumConverter<HighlightSource>))]
public enum HighlightSource
{
    [JsonStringEnumMemberName("recording")] Recording,
    [JsonStringEnumMemberName("transcript")] Transcript,
}

[JsonConverter(typeof(JsonStringEnumConverter<DiarizationState>))]
public enum DiarizationState
{
    [JsonStringEnumMemberName("applied")] Applied,
    [JsonStringEnumMemberName("off")] Off,
    [JsonStringEnumMemberName("modelMissing")] ModelMissing,
    [JsonStringEnumMemberName("unsupportedSystem")] UnsupportedSystem,
    [JsonStringEnumMemberName("failed")] Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<MeetingOrigin>))]
public enum MeetingOrigin
{
    /// <summary>Recorded on a computer (the Swift raw value is "thisMac").</summary>
    [JsonStringEnumMemberName("thisMac")] ThisComputer,
    [JsonStringEnumMemberName("phone")] Phone,
}

[JsonConverter(typeof(JsonStringEnumConverter<MeetingStatus>))]
public enum MeetingStatus
{
    [JsonStringEnumMemberName("recording")] Recording,
    /// <summary>Audio is on disk, no transcript yet.</summary>
    [JsonStringEnumMemberName("recorded")] Recorded,
    [JsonStringEnumMemberName("transcribing")] Transcribing,
    [JsonStringEnumMemberName("transcribed")] Transcribed,
    /// <summary>Transcription failed or was cancelled; see ErrorMessage.</summary>
    [JsonStringEnumMemberName("failed")] Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<PermissionKind>))]
public enum PermissionKind
{
    [JsonStringEnumMemberName("microphone")] Microphone,
    [JsonStringEnumMemberName("accessibility")] Accessibility,
    [JsonStringEnumMemberName("inputMonitoring")] InputMonitoring,
    [JsonStringEnumMemberName("systemAudioRecording")] SystemAudioRecording,
    [JsonStringEnumMemberName("screenRecording")] ScreenRecording,
}

public enum PermissionStatus
{
    Granted,
    Denied,
    NotDetermined,
    Unknown,
}

public enum SoundCue
{
    Start,
    Stop,
    Cancel,
}

/// <summary>How long a loaded engine may stay idle before it is released (minutes; 0 = never).</summary>
public enum EngineIdleTimeout
{
    Minutes5 = 5,
    Minutes15 = 15,
    Minutes60 = 60,
    Never = 0,
}
