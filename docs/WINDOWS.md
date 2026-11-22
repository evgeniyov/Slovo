# Slovo for Windows

Slovo runs on macOS and on Windows as two native apps that share one product: the
same features, the same data format, the same MCP server for Claude, the same copy in
both languages and the same speech models. This document is the architecture decision
and the contract for the Windows app; `docs/SPEC.md`, `docs/SPEC-speakers-mcp.md` and
`docs/CONTRACTS.md` stay the specification of *what* Slovo does, and where they name a
macOS mechanism this document says what Windows uses instead.

## 1. Decision

Slovo uses Swift, SwiftUI, AVFoundation and Core ML on macOS. The Windows app
uses C#, .NET 10 and Avalonia.

What keeps the two apps one product:

1. **One data format.** `meeting.json`, `history.json`, `dictionary.json`,
   `snippets.json`, `notes.json`, `app-categories.json` and `transcript.md` are written
   the way the macOS app writes them (Swift `JSONEncoder` defaults: dates as seconds
   since 2001-01-01, upper-case UUIDs, sorted keys; meeting audio as 16 kHz 16-bit CAF).
   A meeting's optional screen video is `screen.mov` with `hasScreenRecording` on both
   (§5). A meeting folder copied from one computer opens on the other.
2. **One copy.** The Windows app embeds the macOS string tables
   (`Sources/Slovo/Resources/*.lproj`, `Sources/SlovoCore/Resources/*.lproj`) and adds
   small Windows tables that only replace lines naming macOS (⌘, Finder, System
   Settings) and add Windows-only keys.
3. **One behaviour.** Golden fixtures in `Shared/fixtures/` are checked by the Swift tests
   and by the C# tests: text pipeline input → output, `meeting.json` decoding, MCP
   exchanges. When behaviour changes on one side, the fixture changes and the other side's
   test fails until it follows.
4. **Same models.** GigaAM v3 (RNNT and CTC) uses the very same sherpa-onnx folders on both
   systems. Parakeet v3 is FluidAudio's Core ML build on macOS and sherpa-onnx's int8 ONNX
   export on Windows. Speaker separation is pyannote community-1 on Core ML (macOS) and
   pyannote segmentation 3.0 + a speaker-embedding model on sherpa-onnx (Windows).

## 2. Layout

```
Windows/
  Slovo.slnx, Directory.Build.props, Directory.Packages.props, global.json
  src/Slovo.Core/              net10.0, no OS APIs: model + JSON format, text pipeline, stores,
                               meeting logic (screen track included), the screen video file
                               (Video/: H.264 parsing, fragmented MP4, scaling to NV12), MCP
                               server, dictation controller, settings, localization, phone
                               clips over adb, service interfaces (Platform/Services.cs)
  src/Slovo.Engines/           sherpa-onnx: GigaAM, Parakeet, speaker separation, model catalog
  src/Slovo.Platform.Windows/  WASAPI capture, keyboard hook, SendInput paste, UI Automation,
                               foreground app, autostart, sounds, Media Foundation decode,
                               screen capture (Screen/: Windows.Graphics.Capture, Direct3D 11,
                               the Media Foundation H.264 encoder)
  src/Slovo.App/               Avalonia UI (tray, Flow bar, main window, settings, onboarding),
                               composition root, `Slovo.exe --mcp`
  tests/Slovo.Core.Tests/      xUnit v3; runs on macOS and Windows
  tests/Slovo.Engines.Tests/   runs real models when present (SLOVO_MODELS_DIR), skips otherwise
  tests/Slovo.App.Tests/       Avalonia headless view-model and view tests
Shared/fixtures/               golden files both test suites read
```

Everything compiles on a Mac (`dotnet build Windows/Slovo.slnx`), and all Core and Engines
tests run there. Windows-only code is marked `[SupportedOSPlatform("windows")]`, guarded by
`OperatingSystem.IsWindows()`, and replaced by inert stand-ins elsewhere, so the app can be
started on a Mac to work on the interface.

## 3. Conventions

- C# latest, nullable on, warnings are errors. No third-party packages beyond those in
  `Directory.Packages.props` (sherpa-onnx, NAudio, Avalonia, CommunityToolkit.Mvvm, xUnit).
- Windows APIs without a package are called directly: Win32 through `LibraryImport`, COM and
  WinRT (UI Automation, Windows.Graphics.Capture, Direct3D 11, Media Foundation) through
  their vtables with function pointers, slot numbers and interface ids written next to the
  calls. There is no CsWinRT projection, so every project stays `net10.0`, the whole solution
  builds and tests on a Mac, and the published app carries no 25 MB projection assembly.
- Ports keep the Swift names and structure (`DictionaryCorrector` → `DictionaryCorrector`),
  and the Swift doc comments become XML comments, so a change on one side is easy to find on
  the other. Port Swift tests along with the code.
- String lengths that Swift counts in `Character`s are counted in grapheme clusters
  (`Slovo.Core.Text.TextElements`); Unicode scalar work uses `Rune`.
- Text operations that must match across machines use ordinal / invariant comparisons.
- Logging goes through `Slovo.Core.Logging.Log` (file + optional stderr). Nothing ever writes
  to stdout except the MCP transport. Transcribed text is never logged.
- Monotonic time comes from `IUptime`; wall-clock `DateTimeOffset` only for stored dates.
- Stores and controllers are used from the UI thread (the counterpart of `@MainActor`);
  heavy work runs on the thread pool and returns with `await`.
- User-visible strings come from the tables (`CoreStrings` in Core, the app's `Loc` in the UI).

## 4. What Windows uses instead

| macOS | Windows |
|---|---|
| `~/Library/Application Support/Slovo` | `%LOCALAPPDATA%\Slovo` (same layout inside) |
| UserDefaults `local.slovo.Slovo` | `settings.json` in the data folder, same key names |
| Fn / Right ⌥ / Right ⌘ / ⌃⌥ | Ctrl+Win (default) / Right Ctrl / Right Alt / Ctrl+Alt |
| CGEventTap (listen-only) | `WH_KEYBOARD_LL` hook (listen-only; masks Win/Alt with vkE8 so Start/menus don't open) |
| Carbon `RegisterEventHotKey` | `RegisterHotKey` |
| ⌘V via CGEvent + NSPasteboard transient types | Ctrl+V via `SendInput` + clipboard formats `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory=0`, `CanUploadToCloudClipboard=0` |
| Secure Event Input | foreground window elevated above Slovo (UIPI) or secure desktop → copy instead of paste |
| Accessibility focused element | UI Automation focused element |
| AVCaptureSession microphone | WASAPI shared-mode capture (NAudio), resampled to 16 kHz mono |
| Core Audio process tap (excludes Slovo) | WASAPI process loopback excluding Slovo's process tree (Windows 10 2004+), else endpoint loopback |
| ScreenCaptureKit screen recording of the main display | Windows.Graphics.Capture of the primary monitor (Windows 10 1903+), polled from a free-threaded frame pool |
| ScreenCaptureKit capture of one app's windows, with that app's audio in `system.caf` | the app's frontmost window (followed when another of its windows comes forward, dialogs included on Windows 11 24H2+); its sound through WASAPI process loopback *including* its process tree (2004+) |
| `AVAssetWriter` H.264 `.mov` with `movieFragmentInterval` | Media Foundation's software H.264 encoder MFT (Baseline) and Slovo's own fragmented MP4 writer (`Slovo.Core/Video`), same `screen.mov` |
| Screen Recording permission | none to ask for; Windows draws a yellow capture border, which Windows 11 lets Slovo turn off (`GraphicsCaptureAccess` borderless, `IsBorderRequired`) |
| `com.apple.screenIsLocked` / `…Unlocked`, `NSWorkspace.didWakeNotification` | `WTS_SESSION_LOCK` / `WTS_SESSION_UNLOCK`, `WM_POWERBROADCAST` resume |
| `AVPlayerView` inline screen player | "Open video": the default video player (Media Player, Films & TV) |
| `PhoneClipCollector` with adb from Android Studio or Homebrew | the same collector; `adb.exe` from `%ADB%`, `PATH` or `%LOCALAPPDATA%\Android\Sdk\platform-tools` |
| SMAppService launch at login | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |
| menu bar item | notification-area (tray) icon |
| NSPanel Flow bar | top-most, non-activating tool window (`WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`) |
| `/usr/bin/tar` for model archives | `tar.exe` that ships with Windows 10 1803+ |
| Apple Dictation engine | not available |
| `Slovo --mcp` | `Slovo.exe --mcp` |
| Claude app config `~/Library/Application Support/Claude/claude_desktop_config.json` | `%APPDATA%\Claude\claude_desktop_config.json` |

## 5. Screen recording

The same optional third track as on macOS (`MeetingScreenCapture.swift`,
`MeetingScreenWriter.swift`): off by default (`meetings.recordScreen`), the whole primary
display or one app (`meetings.screenCaptureApp`, the app's lower-case executable name on
Windows), chosen with the square button next to "New recording" or in Settings → Meetings.
The logic is in the core and tested on any system — `MeetingScreenCapture` (pacing,
timeline, key frames, lock/unlock, wake, one reconnect), `MeetingScreenWriter`,
`Video/FragmentedMp4Writer`, `Video/ScreenFrameConverter` — while the platform only hands
over BGRA pictures and encodes NV12 (`IScreenCapture`).

- **Never at the audio's expense.** Every failure (Windows before 1903, no encoder, the app
  not running, a lost capture, a full disk, the encoder failing) ends the screen track only,
  with its reason in the recording panel and an alert when it does not start. Scoped to one
  app, the app's sound replaces system audio when it starts, and system audio is recorded
  instead when it does not; unlike macOS, where the app's sound rides on the screen stream,
  it is a capture of its own here and keeps recording if the app's video ends.
- **Picture.** About four pictures a second, scaled (box filter) into one canvas sized from
  the primary display with its longer edge at most 1920 and both edges even, fixed for the
  file; a display swap or a resized window is fitted into it (an app's window is not blown
  up). When nothing changed the last picture is encoded again, which costs next to nothing
  and keeps fragments reaching the disk. Times come from the audio tracks' uptime origin plus
  any silence padded for sleep, so the video lines up with `mic.caf`/`system.caf`.
- **Encoder.** Media Foundation's software H.264 MFT (synchronous; no hardware MFT event
  model to get wrong), Baseline profile so there are no reordered frames, quality-based rate
  control, a key frame every 10 s. Windows N without the Media Feature Pack has no encoder:
  the track says so and the meeting records its audio.
- **File.** `screen.mov`, as on macOS, holding a fragmented ISO MP4 (`ftyp iso5`, a `moov`
  without samples, then `moof`+`mdat` fragments of about 10 s, each flushed to disk; a
  finished file also gets `mehd` and `mfra`). A crash or force-quit leaves every fragment
  written so far playable. The name keeps meetings portable: AVFoundation opens an ISO MP4
  in a `.mov` like its own QuickTime files (verified with `AVURLAsset`,
  `AVAssetImageGenerator` and `AVPlayer`, for a finished file, one cut off mid-meeting and one
  torn mid-fragment; FFmpeg decodes them too), and Windows plays `.mov` through the MPEG-4
  source of Media Foundation, which reads movie fragments since Windows 8. Every fragment
  starts with a key frame, which is also where that source assumes one when seeking. The
  card opens it in the default video player — Avalonia has no
  video view, and the video is muted reference footage on its own timeline anyway.
- **Session.** A screen lock (`WTS_SESSION_LOCK`) closes the capture and says "paused"; the
  unlock reopens it. A wake (`PBT_APMRESUMEAUTOMATIC`) rebuilds it. A capture that ends by
  itself (window closed, display gone, device reset) gets one reconnect, then the track is
  lost for the meeting; what it recorded is kept.
- **What is recorded.** The cursor, as on macOS. Slovo's own windows (the Flow bar) are not
  excluded, as on macOS, whose content filter excludes nothing. Windows 10 draws a yellow
  border around what is captured; on Windows 11 Slovo asks for borderless capture
  (`GraphicsCaptureAccess`, which a desktop app is normally granted without a prompt) and turns
  the border off. The border is never part of the recording either way.
- **Frames.** A timer empties the free-threaded frame pool (three buffers) every 40 ms into the
  one newest picture, so Windows always has a buffer to compose into: otherwise a change right
  before the screen goes still could be dropped and the video would keep the older picture.
- Retention keeps `screen.mov` with "delete after transcription", as macOS does; an age
  limit removes it with the audio.

## 6. Scope of the Windows version

In: dictation (hold, double-tap hands-free, Esc, paste last), Flow bar, tray menu (with the
microphone-off and microphone-lost icons, models folder and onboarding in Help), keyboard
shortcuts sheet, the sidebar readiness card, text pipeline, history with playback and re-run,
insights, dictionary, snippets, style, scratchpad, meetings (microphone + system audio, the
screen as a third track, speaker separation, highlights, the speakers bar with «All» and
edits, reprocess, export, Claude notes picked up when the window comes forward, playback with
a scrubber and speeds that keep the pitch), the Inbox folder and phone clips collected over
adb, the MCP server, settings (a Microphone permission row included), onboarding, the alert
about damaged data files, both languages, light and dark themes.

Not on Windows, on purpose or for now:

- Apple Dictation — macOS only.
- An inline video player on the meeting card: Avalonia has no video view, so "Open video"
  opens the system player (§5). The screen picker lists apps by name without their icons, and
  the Home list shows no app icon beside a dictation: Windows keeps only the executable name,
  and finding and drawing its icon cannot be checked from a Mac.
- The Voice Isolation notice (a macOS microphone mode) and the Fn-key conflict warnings
  (Windows uses other keys); a "Wispr Flow is running" check could still be ported.
- `--diagnose-microphone <file>`, a developer command of the macOS app.
- Small differences left as they are: a moment's note is edited in a sheet rather than in
  place; while a meeting records, its card does not list the live tracks; percentages read
  "25 %" in English.
- A screen recording scoped to an app that quits mid-meeting ends after one reconnect, as on
  macOS; the app's sound goes quiet with it (process loopback does not report the end), and
  starting the app again does not bring either back in that meeting.

## 7. Building

```powershell
# On Windows (or on a Mac for everything except running the app for real):
dotnet test Windows/Slovo.slnx
dotnet publish Windows/src/Slovo.App -c Release -r win-x64 --self-contained -o dist/win-x64
```

`python3 build.py --windows` does the publish from a Mac and zips it as
`Slovo-<version>-win-x64.zip`.

## 8. What has been verified

On a Mac (the whole solution builds there with zero warnings, and CI repeats it on
`windows-latest`):

- Core, Engines, Platform and App tests cover the text pipeline, both formats of
  `meeting.json` and `transcript.md`, and MCP request and response parity using
  fixtures shared with the Swift tests.
- GigaAM runs for real in the Engines tests on the models the macOS app downloaded; speaker
  separation runs on the ONNX models.
- The app starts on the stand-in platform services (`SLOVO_DATA_DIR=… dotnet Slovo.dll`), and
  a headless test builds the whole app, opens every screen and settings section, records into
  a microphone that fails, and quits saving everything. Screens are rendered to PNG in both
  languages and themes (`SLOVO_SNAPSHOT_DIR`).
- Screen recording up to the encoder: the canvas, scaling and colours, pacing, key frames,
  lock/unlock, wake, one reconnect, a failing encoder and every recorder path run over a fake
  platform; `screen.mov` files written by the MP4 writer from real x264 output play in
  AVFoundation (`AVPlayer`, `AVAssetImageGenerator`) — finished, cut off, and torn mid-fragment —
  and FFmpeg decodes them. The raw COM/WinRT calls were checked against the Windows metadata
  (interface ids, vtable slots, signatures, struct layouts); struct sizes are also tested.

Only a Windows machine can confirm (compiled and unit-tested here, never run for real):

- the `WH_KEYBOARD_LL` hook next to Avalonia, the Win/Alt masking and `GetAsyncKeyState`
  when an elevated window has focus;
- the Ctrl+V paste through `SendInput` in Electron apps, terminals and remote desktops, and
  the clipboard formats that keep dictations out of clipboard history;
- UI Automation's focused element (the idle Flow bar shows only over text fields);
- WASAPI capture and process loopback on real devices, sleep and device changes during a
  meeting, and playback of meeting tracks;
- the tray icon, the non-activating Flow bar window and the installer;
- screen recording for real: Windows.Graphics.Capture of the display and of an app's window,
  the Media Foundation encoder, `screen.mov` in Windows' players, the yellow border, lock,
  unlock and sleep, an app's own sound through process loopback, and Windows N without the
  Media Feature Pack;
- adb from a real Android SDK with a phone attached.

To evaluate Windows speaker separation on annotated recordings, set
both `SLOVO_DIARIZATION_MODELS_DIR` and `SLOVO_EVAL_MEETINGS`, then run
`MeetingEvaluationTests` in `Windows/tests/Slovo.Engines.Tests/Diarization`. Speaker count hints and
the clustering threshold in `SherpaDiarizationTuning` affect the result.


The H.264 video fixture in `Windows/tests/Slovo.Core.Tests/Video/Fixtures` comes
from FFmpeg's synthetic test pattern. To generate a compatible stream locally:

```bash
ffmpeg -f lavfi -i testsrc=size=64x48:rate=8 -frames:v 12 -pix_fmt yuv420p \
  -c:v libx264 -profile:v baseline -g 8 -keyint_min 5 -sc_threshold 0 \
  -x264-params aud=1 -f h264 testsrc-64x48.h264
```
