# Slovo for Windows

Slovo has native apps for macOS and Windows. They share features, data formats,
the MCP server for Claude, interface text in both languages, and speech models.
`docs/SPEC.md`, `docs/SPEC-speakers-mcp.md`, and `docs/CONTRACTS.md` specify the
shared behavior. This document describes the Windows implementation and the
features specific to each platform.

## 1. Decision

Slovo uses Swift, SwiftUI, AVFoundation and Core ML on macOS. The Windows app
uses C#, .NET 10 and Avalonia.

The apps share these formats, strings, tests, and models.

1. **One data format.** `meeting.json`, `history.json`, `dictionary.json`,
   `snippets.json`, `notes.json`, `app-categories.json` and `transcript.md` follow the macOS format. The Swift `JSONEncoder` settings write dates as seconds
   since 2001-01-01, use uppercase UUIDs, and sort keys. Both apps store meeting
   audio as 16 kHz, 16-bit CAF.
   A meeting's optional screen video is `screen.mov` with `hasScreenRecording` on both
   (§5). A meeting folder copied from one computer opens on the other.
2. **Shared interface text.** The Windows app embeds the macOS string tables
   (`Sources/Slovo/Resources/*.lproj`, `Sources/SlovoCore/Resources/*.lproj`) and adds
   small Windows tables that only replace lines naming macOS (⌘, Finder, System
   Settings) and add Windows-only keys.
3. **Shared fixtures.** Swift and C# tests use `Shared/fixtures/` to check text
   pipeline inputs and outputs, `meeting.json` decoding, and MCP exchanges. When
   behavior changes in one app, update the fixture. The other app's test fails
   until its behavior matches.
4. **Same models.** GigaAM v3 (RNNT and CTC) uses the same sherpa-onnx folders on both
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
- Call Windows APIs without a package directly. Use `LibraryImport` for Win32.
  Call COM and WinRT (UI Automation, Windows.Graphics.Capture, Direct3D 11,
  and Media Foundation) through their vtables. Keep the function pointers,
  slot numbers, and interface IDs next to the calls. Omitting CsWinRT keeps
  every project on `net10.0`, lets the whole solution build and test on a Mac,
  and saves the 25 MB projection assembly in the published app.
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

As on macOS (`MeetingScreenCapture.swift`, `MeetingScreenWriter.swift`), screen
recording is an optional third track and defaults to off (`meetings.recordScreen`).
Use the square button beside "New recording" or Settings → Meetings to choose
between the primary display and one app. On Windows, `meetings.screenCaptureApp`
stores the app's lowercase executable name.

The core handles pacing, the timeline, keyframes, lock and unlock, wake, and one
reconnect in `MeetingScreenCapture`. It also handles conversion and file writing
in `MeetingScreenWriter`, `Video/FragmentedMp4Writer`, and
`Video/ScreenFrameConverter`. Tests can exercise this logic on any system. The
platform provides BGRA frames and encodes NV12 through `IScreenCapture`.

- **Audio on capture failure.** Only the screen track ends if Windows is older
  than 1903, the encoder is unavailable or fails, the selected app is closed,
  capture is lost, or the disk is full. The recording panel shows the reason.
  If capture does not start, show an alert. When capturing one app, record its
  audio in place of system audio if the app capture starts. Otherwise record
  system audio. macOS captures the app's audio in the screen stream. Windows
  captures it separately, so audio recording continues if the video ends.
- **Frames and size.** Record about four frames per second. Scale frames with a
  box filter into a canvas based on the primary display. Both edges have even
  dimensions and the longer edge is at most 1920 pixels. Keep the canvas size
  fixed for the file. Fit a replacement display or a resized window without
  upscaling app windows. When nothing changes, encode the last frame again at
  minimal cost so fragments keep reaching disk. Use the audio tracks' uptime
  origin plus any silence added for sleep to align video with `mic.caf`/`system.caf`.
- **Encoder.** Use Media Foundation's synchronous software H.264 MFT to avoid the
  hardware MFT event model. Use Baseline to avoid frame reordering, quality-based
  rate control, and a keyframe every 10 s. Windows N needs the Media Feature Pack
  for the encoder. If it is missing, report that on the screen track and record audio.
- **File.** `screen.mov`, as on macOS, holding a fragmented ISO MP4 (`ftyp iso5`, a `moov`
  without samples, then `moof`+`mdat` fragments of about 10 s, each flushed to disk; a
  finished file also gets `mehd` and `mfra`). A crash or force-quit leaves every fragment
  written so far playable. The name keeps meetings portable: AVFoundation opens an ISO MP4
  in a `.mov` like its own QuickTime files (verified with `AVURLAsset`,
  `AVAssetImageGenerator` and `AVPlayer`, for a finished file, one cut off mid-meeting and one
  torn mid-fragment; FFmpeg decodes them too), and Windows plays `.mov` through the MPEG-4
  source of Media Foundation, which reads movie fragments since Windows 8. Every fragment
  starts with a key frame, which is also where that source assumes one when seeking. The
  meeting card opens the file in the default player because Avalonia has no video
  view. The muted reference video has its own timeline.
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

- Apple Dictation is available only on macOS.
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
- The Engines tests run GigaAM with models downloaded by the macOS app. The
  speaker separation tests use the ONNX models.
- The app starts on the stand-in platform services (`SLOVO_DATA_DIR=… dotnet Slovo.dll`), and
  a headless test builds the whole app, opens every screen and settings section, records into
  a microphone that fails, and quits saving everything. Screens are rendered to PNG in both
  languages and themes (`SLOVO_SNAPSHOT_DIR`).
- Screen recording up to the encoder: the canvas, scaling and colours, pacing, key frames,
  lock/unlock, wake, one reconnect, a failing encoder and every recorder path run over a fake
  platform; `screen.mov` files written by the MP4 writer from real x264 output play in
  AVFoundation (`AVPlayer`, `AVAssetImageGenerator`). Both AVFoundation and FFmpeg
  handle finished files, files cut off during recording, and files torn mid-fragment. The raw COM/WinRT calls were checked against the Windows metadata
  (interface ids, vtable slots, signatures, struct layouts); struct sizes are also tested.

The following compiled and passed unit tests on Mac. They still need checks on Windows hardware.

- the `WH_KEYBOARD_LL` hook next to Avalonia, the Win/Alt masking and `GetAsyncKeyState`
  when an elevated window has focus;
- the Ctrl+V paste through `SendInput` in Electron apps, terminals and remote desktops, and
  the clipboard formats that keep dictations out of clipboard history;
- UI Automation's focused element (the idle Flow bar shows only over text fields);
- WASAPI capture and process loopback on real devices, sleep and device changes during a
  meeting, and playback of meeting tracks;
- the tray icon, the non-activating Flow bar window and the installer;
- screen recording on Windows: Windows.Graphics.Capture of the display and of an app's window,
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
