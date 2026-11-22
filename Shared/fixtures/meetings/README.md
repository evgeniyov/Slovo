# Meeting documents shared by both apps

Golden files that keep `transcript.md` identical on macOS and Windows. Both test suites
read this folder:

- Swift: `Tests/SlovoCoreTests/SharedMeetingFixtureTests.swift`
- C#: `Windows/tests/Slovo.Core.Tests/Meetings/SharedMeetingFixtureTests.cs`

## Files

For every meeting `<name>`:

| File | What it is |
|---|---|
| `<name>.meeting.json` | A `meeting.json` as the macOS app stores it (input, written by hand). |
| `<name>.en.md`, `<name>.ru.md` | `TranscriptMerger.markdown(meeting:language:)` for that record, written by the **Swift** code. The C# `TranscriptMerger.Markdown` must produce the same bytes. |
| `<name>.en.parsed.json`, `<name>.ru.parsed.json` | What the recovery parser (`MeetingStore.parseTranscript`) reads back from that document: title, segments, speakers and highlights, encoded by Swift's `JSONEncoder`. The C# `MeetingStore.ParseTranscript` must return equal values, highlight ids included. |

The meetings:

- `format1-call`: the first format (no `formatVersion`, only "me"/"others"): merged
  lines, whitespace in a title and a segment, an empty segment, the microphone first on
  equal start times, a timestamp past one hour. Gives the first format's document.
- `format2-cowork`: the second format with everything: a speaker marked «Я», a named and a
  numbered speaker, a renamed system track, moments on lines, near a line and on no line,
  a note with a line break, an excerpt cut at 120 characters, Claude's notes, duration.
- `format2-microphone`: another speaker marked «Я», so the unseparated microphone reads
  «Микрофон»; a `speakerID` that names no speaker; a name with extra spaces; a line cut
  at 60 seconds; a date at 23:59 on New Year's Eve; no duration.
- `damaged`: a hand-edited file both decoders must tolerate: unknown status, engine,
  track, source, highlight source and diarization state; malformed, duplicate and invalid
  speakers and highlights; negative and absurd times; a color out of range; Claude's notes
  with control characters, a `<transcript>` tag and a list of the wrong type.

## Fixed time zone

Documents show dates in the computer's time zone, so both suites pin it to **UTC+03:00**:
the Swift test sets `NSTimeZone.default = TimeZone(secondsFromGMT: 10800)` for the run,
the C# test passes `TimeZoneInfo.CreateCustomTimeZone("UTC+03", TimeSpan.FromHours(3), …)`
to `TranscriptMerger.Markdown`. Date text follows current macOS (`DateFormatter`, long date
and short time, with U+202F narrow no-break spaces), which the Windows app reproduces in
`MeetingDates`.

## Changing the behaviour

Change the Swift code, then rewrite the expected files from it and review the diff:

```sh
SLOVO_REGENERATE_FIXTURES=1 swift test --filter SharedMeetingFixture
```

The C# test fails until the Windows code follows. `.gitattributes` here turns off line-end
conversion, so the documents stay byte-identical in a Windows checkout.
