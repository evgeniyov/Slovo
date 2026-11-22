# Slovo app specification

Slovo is a macOS app for local voice input, modelled on Wispr Flow:
hold a key, speak, and the text appears in whatever field has focus. It also
records meetings and turns them into transcripts. Everything runs on the Mac;
there is no account, subscription, team, cloud, telemetry or network use except
explicit model downloads the user starts. When the user turns on Claude access,
text Claude reads through Slovo's local MCP server leaves the Mac as part of the
user's Claude conversation; Slovo's own process still sends nothing.

Speaker separation, important moments, speaker renaming and Claude access are
specified in `docs/SPEC-speakers-mcp.md`; where the two documents disagree, that
document wins.

The interface is fully bilingual: English and Russian, switchable at runtime.
Recognition handles Russian, English and Russian speech with English terms.

## 1. Scope

In scope for this version:

- Push-to-talk and hands-free dictation into any app, with a floating Flow bar.
- Home screen with dictation history, search, playback and statistics.
- Insights, Dictionary (terms and replacements), Snippets, Style (per app
  category) with Auto cleanup, Scratchpad notes, Meetings (Notetaker) with
  speaker separation, speaker renaming and important moments.
- An opt-in local MCP server for meetings (see `docs/SPEC-speakers-mcp.md`), so
  Claude Code and the Claude app can read them and, behind a second switch of its
  own, write their own summary, decisions and tasks back into a meeting.
- Settings: General, Recognition, System, Meetings, Integrations, Data and
  privacy, About.
- Onboarding: language, permissions, model check, a practice dictation.
- Menu bar item.

Out of scope (do not build, do not leave placeholders for): accounts, sign-in,
plans, billing, referrals, "get a free month", team sharing, mobile app promos,
connectors, cloud sync, telemetry, voice profiles and voice profile marketing,
MCP write access, MCP access to anything but meetings, network MCP transports,
Transforms and anything that needs an LLM. A later version may add LLM features
through a local server; keep the text pipeline open to one more stage.

## 2. Platform and project

- macOS 14.4 or newer, Apple Silicon only. Swift Package Manager, swift-tools 6.0,
  every Slovo target in Swift 5 language mode (`.swiftLanguageMode(.v5)`).
  Development toolchain: Xcode 27.0 and Swift 6.4 on macOS 27.
- Existing targets stay working: `SlovoCore` (engines: `GigaAMEngine`,
  `ParakeetEngine`, `AppleDictationEngine`, `AudioLoader`, `ModelStore`),
  `BenchKit`, `slovo-bench`, `BenchKitTests`.
- New targets: `Slovo` (executable app, `Sources/Slovo`, with resources) and
  `SlovoCoreTests` (`Tests/SlovoCoreTests`).
- `build.py` at the repo root (the user runs it from VS Code's Run button) builds
  a release `Slovo.app` in the repo root, signs it and, with `--install`, copies
  it to `/Applications/Slovo.app`. Signing: identity from `SIGN_IDENTITY`, else
  "Slovo Local" if `security find-identity` lists it
  (`Scripts/make_signing_identity.sh` creates it once per Mac), else ad-hoc. Always hardened runtime plus `entitlements.plist` containing
  only `com.apple.security.device.audio-input` and
  `com.apple.security.cs.disable-library-validation`: library validation admits a
  framework only when it is Apple's or has the app's Team ID, and local
  signatures have no Team ID, so dyld would refuse the embedded
  `whisper.framework` ("different Team IDs") although it carries the app's own
  signature. A Developer ID build could drop it. A stable identity matters: macOS
  permission grants (Microphone, Accessibility, Input Monitoring, System Audio
  Recording) are tied to the signature and are lost on ad-hoc rebuilds.
- Info.plist: `CFBundleIdentifier` `local.slovo.Slovo`, name Slovo,
  `LSMinimumSystemVersion` 14.4, `LSUIElement` true (menu bar app; the main
  window switches the activation policy to `.regular` while it is open and back
  to `.accessory` when it closes), `NSMicrophoneUsageDescription`,
  `NSAudioCaptureUsageDescription` (both in English; see localization for
  `InfoPlist.strings`), `CFBundleDevelopmentRegion` en,
  `CFBundleLocalizations` [en, ru], an app icon.
- `build.py` copies the SwiftPM resource bundles to `Contents/Resources`,
  where Slovo's bundle locator looks for them.
- Engineering rules: no force unwraps, no `print` outside `slovo-bench`, use
  `os.Logger` (subsystem `local.slovo.Slovo`), no third-party dependencies beyond
  sherpa-onnx, FluidAudio and whisper.cpp — the official prebuilt
  `whisper.xcframework` of v1.9.4 (release asset of nightly build b5130, the same
  commit) as a SwiftPM binary target pinned by URL and checksum. It is a dynamic
  framework: SwiftPM puts it next to the executables, `build.py` copies it into
  `Contents/Frameworks`, adds the `@executable_path/../Frameworks` rpath and signs
  it with the app's identity before the app (see the entitlements above).
  WhisperKit (Core ML, MIT) would be the fallback if the framework ever stopped
  working. User-visible strings always come from the
  localization tables. Timing uses monotonic clocks (`ContinuousClock`,
  `ProcessInfo.systemUptime`), not `Date()` differences.

## 3. Architecture

```
Sources/SlovoCore/
  Engines (existing)        GigaAMEngine, ParakeetEngine, AppleDictationEngine, WhisperEngine (helper only), AudioLoader, ModelStore
  Model/                    plain data types and service protocols shared by everything
  Localization/             CoreStrings: localized strings for SlovoCore errors, reading the app language
  Audio/                    MicrophoneRecorder, AudioDevices, WAVFile
  Input/                    HotkeyMonitor, PasteLastShortcut, TextInserter, Permissions, FrontmostApp
  Text/                     TextPipeline and its stages: SnippetExpander, DictionaryCorrector, Transliterator, FillerCleaner, StyleFormatter; MixedLanguageMerger
  Storage/                  JSON stores: History, Dictionary, Snippets, Notes, AppCategories, Meetings; RetentionCleaner; InsightsCalculator
  Dictation/                EngineManager (choose, preload, idle-unload), ModelManager (status, download, delete), DictationController (the state machine), MixedLanguageHelper (English words in mixed dictation)
  Meetings/                 SystemAudioTap, MeetingRecorder, SpeechSegmenter, MeetingTranscriber, ClipPlanner, SpeakerRegistry, TranscriptMerger, MeetingEdits
  Diarization/              DiarizationService (speaker model status, download, loading), FluidSpeakerDiarizer, DiarizationTuning, SpeakerTurns, DiarizationScoring
  MCP/                      MCPServer (stdio), MCPHandler, JSONValue, MCPText, MCPSanitizer, MeetingArchive (read-only), MeetingNotesFile (the one write), SlovoPreferences
  Integration/              connections no single module owns: ServiceWiring, MeetingWiring
Sources/Slovo/
  SlovoApp.swift, AppDelegate, AppModel (wires services to views)
  Support/                  MCPServerMode (`Slovo --mcp`), ClaudeIntegration, diagnostics
  Design/                   Theme tokens, Localizer, reusable components
  FlowBar/                  non-activating overlay panel and its states
  MenuBar/
  Screens/                  Home, Meetings, Insights, Dictionary, Snippets, Style, Scratchpad
  Settings/                 modal sheet with sections
  Onboarding/
  Resources/                en.lproj, ru.lproj (Localizable.strings, InfoPlist.strings), assets
```

SlovoCore never imports SwiftUI or AppKit views; AppKit is allowed only where an
API requires it (NSPasteboard, NSWorkspace, NSEvent). Services publish state
through `@MainActor` `ObservableObject`s or `AsyncStream`s declared in `Model/`.

`Slovo --mcp` runs a headless MCP server over stdio before `NSApplication` starts:
no window, menu bar item, store, engine or capture session, and nothing under
Slovo's data folder or preferences is ever written by that process.

## 4. Dictation

### 4.1 Keys

- Push-to-talk: hold the dictation key, speak, release to finish. The key is the
  user's choice, made in onboarding and changeable at any time in Settings →
  General. Choices: Fn/Globe (keycode 63, Apple keyboards only), Right Option
  (61), Right Command (54), Left Control+Left Option pressed together. Default:
  Fn. Holds shorter than
  0.25 s are ignored (a tap is not a dictation).
- Fn conflicts are explained, never silently "fixed": when Fn is chosen and
  `AppleFnUsageType` in the `com.apple.HIToolbox` domain is not 0 (1 = change
  input source, 2 = emoji, 3 = start Apple
  dictation), show an inline warning next to the Fn choice with a button that
  opens System Settings → Keyboard, so the user can set "Press 🌐 key to: Do
  Nothing". Slovo never writes that setting. When Fn is chosen and Wispr Flow
  (`com.electron.wispr-flow`) is running, warn that both apps react to Fn.
  Re-check both conditions whenever the app becomes active.
- Hands-free: double-tap the dictation key (two taps within 0.4 s) starts a
  recording that continues after release; one more tap finishes it. Maximum
  length is a setting (default 6 minutes); the Flow bar shows a countdown in the
  last 30 s.
- Esc while recording cancels (nothing is transcribed or inserted). Esc also
  cancels a dictation that is still being transcribed.
- Paste last transcript: Control+Command+V by default (Carbon
  `RegisterEventHotKey`, needs no permission), configurable.
- Implementation: one listen-only `CGEventTap` for `flagsChanged` and `keyDown`,
  created only when Input Monitoring is granted. Re-enable after
  `tapDisabledByTimeout`/`tapDisabledByUserInput`; recreate on wake and on
  session activation. Modifier-only keys keep working under Secure Event Input.
  Secure Event Input hides other key presses from the tap, so a hold during which
  the system's key press counter moved counts as a shortcut and is discarded.

### 4.2 Recording

- `AVCaptureSession` with an `AVCaptureAudioDataOutput` that delivers 16 kHz mono
  Float32, accumulated in memory and written to a WAV file when recording stops.
  Not `AVAudioEngine`: selecting a non-default input on its input node posts a
  configuration change that stops the engine at random, which captured nothing.
- Input device: system default or a chosen device (by UID, persisted; fall back
  to the default when it is missing or disconnects mid-recording). When no input
  takes over, the recording ends and what was captured before the loss is still
  transcribed; with nothing captured, the Flow bar shows the microphone error.
  Never assume 48 kHz (AirPods switch the input sample rate).
- A level meter (RMS of the last ~50 ms, smoothed) at about 30 updates per
  second drives the Flow bar waveform.
- Recordings shorter than 0.3 s or with no speech energy are dropped with a
  quiet "Nothing heard" state instead of calling an engine. The message names the
  microphone that was used and opens Settings → General when clicked, because
  the usual cause is a silent input such as a headphone jack with no microphone.
- Optional start and stop sounds (setting, default on, subtle system sounds).

### 4.3 Transcription and insertion

1. Engine: see Recognition settings. The engine for the current language mode is
   preloaded in the background at launch and after settings change; if it is not
   ready when recording stops, the Flow bar shows "Loading model…". After a
   configurable idle time (default 15 minutes, options 5/15/60/never) the engine
   is released to free memory (GigaAM holds about 700 MB).

   English words in mixed dictation (`MixedLanguageHelper`, setting
   `recognition.englishHelper`). GigaAM knows only Russian: it writes English as
   Latin garble (Makos, Pod Vidos, Hithub) or as transliterations that are not
   words (мейн, ребейс). In the mixed language mode, when the dictation engine is
   GigaAM (RNNT or CTC), a multilingual helper fixes those words:
   1. GigaAM transcribes as always. `MixedLanguageMerger.needsHelper` looks for a
      suspect word: Latin letters, or a Cyrillic word the system's Russian spelling
      dictionary does not know (capitalised abbreviations up to 5 letters and
      words mixing scripts, such as Telegram-бот, are not suspect). Without one
      the helper never runs, so pure-Russian dictation keeps GigaAM's latency.
   2. Otherwise the helper transcribes the same recording, and
      `MixedLanguageMerger.merge(russian:helper:)` replaces only GigaAM's suspect
      words with the helper's English where the two sound alike (a sound key
      shared by both scripts, difflib-style alignment, runs of up to three
      words). GigaAM's Russian, punctuation and capitals are kept word for word.
      `Shared/fixtures/mixed-merge.json` holds the golden cases both apps run.
   3. The merged text is the raw text from then on: it goes through the text
      pipeline below and into history as `rawText`; the entry's engine stays
      GigaAM. "Run again" of an entry dictated in the mixed mode with a GigaAM
      engine uses the helper too. Meetings and the other modes never do.
   - Helper choice: `auto` (default) — Whisper large-v3 turbo when its model is
     downloaded, else Parakeet v3 when its model is, else none; `parakeet`;
     `whisper`; `off`. A helper chosen by name whose model is missing means no
     helper; only Auto falls back from one model to the other.
   - The helper can only improve a dictation. If it is missing, fails, or has not
     answered within max(3 s, 0.5 × recording length) — counting its load — the
     dictation continues with GigaAM's text, no error is shown and the log says
     why. The limit also covers Whisper's first load after each install or
     update of the app, when Metal compiles whisper.cpp's shaders for the new
     binary (15–30 s; later loads take under a second): that dictation keeps
     GigaAM's text and the load finishes for the next one. Cancelling the
     dictation stops waiting at once and cancels the helper (whisper.cpp checks
     between Metal command buffers, so it stops within about a second).
   - The helper is an ordinary engine of `EngineManager`: loaded on first need,
     never preloaded, released by the idle timeout with the others. Whisper is
     also released as soon as settings stop using it (it holds about 1 GB).
   - `slovo-bench run --engine hybrid-parakeet|hybrid-whisper` evaluates the
     same helper pipeline on benchmark clips with reference transcripts.
   - Whisper runs through whisper.cpp v1.9.4 (`whisper.xcframework`, Metal) with
     the model `ggml-large-v3-turbo-q5_0.bin`: language forced to Russian
     (detection sometimes picks English and translates), beam search of 5 (best
     of 5 on temperature fallback), 4 CPU threads, flash attention, and the
     Dictionary's terms as the initial prompt — comma-separated, ending with a
     full stop, newest terms first when they exceed 600 characters (Whisper reads
     at most 224 prompt tokens).
2. Text pipeline (pure, synchronous, fully unit-tested), in this order:
   1. Snippets: a trigger phrase spoken as the whole dictation or as a phrase
      inside it is replaced by its expansion. Matching is case-insensitive,
      ignores punctuation, treats ё as е and ’ as ', and reads a hyphen as a
      word break («IP адрес» matches «IP-адрес»), but never matches part of a
      hyphenated compound («адрес» alone does not match inside «IP-адрес»).
   2. Dictionary replacements: explicit "from → to" rules, whole words or
      phrases, case-insensitive.
   3. Dictionary terms: fuzzy correction of terms written the wrong way. Every
      dictionary term containing Latin letters is compared with windows of 1–3
      consecutive transcript words; Cyrillic words are transliterated to Latin
      first (кубернетес → kubernetes, гитхаб экшенс → githabekshens, юзер
      дефолтс → yuzer defolts). Replace when the normalized edit distance is at
      most 0.34 and the term is at least 4 letters, preferring longer matches.
      Must turn these real outputs into the terms: «кибернетес» → Kubernetes,
      «Гитхаб экшенс» → GitHub Actions, «юзер Айди» → user id, «постгресс» →
      Postgres, «докер» → Docker, «User Defolts» → UserDefaults; and must not
      change ordinary Russian words (for example «деплой», «логи», «модель»,
      «проект», «мастер», «телеграмм»). A word the Russian spelling dictionary
      knows becomes a term only when it sounds exactly like it (without
      doubling a letter the term has once) and is either a borrowed product
      name from a short fixed list («докер», «телеграм») or written with a
      capital in mid-sentence for a term that is not an ordinary English word
      («в Редис» → Redis). Before that, a few names whose spelling does not
      sound the way they are said are matched as spoken words, in any case
      form, when their term is in the Dictionary: «Клод», «с Клодом» → Claude,
      «апи» → API, «айос» → iOS, «икскод» → Xcode (API is otherwise too short
      and Claude is spelled like «клауде»). Keep a counter of applied fixes per
      dictation.
      The spelling dictionaries are injectable, and
      `Shared/fixtures/text-pipeline.json` holds golden cases for the whole
      pipeline with fixed word lists that every port must reproduce.
   4. Auto cleanup (setting, default on): remove filler words (ru: э, ээ, эм,
      ммм; en: um, uh, erm, hmm) and immediate word repetitions («я я» → «я»),
      then fix spacing around punctuation.
   5. Style for the frontmost app's category (see 4.5).
3. Insertion: if the result is empty, show "Nothing heard". Otherwise insert into
   the focused field: save the pasteboard items, write the text with
   `org.nspasteboard.TransientType` and `org.nspasteboard.AutoGeneratedType`
   markers, post Command+V as the physical V key (keycode 9) to
   `.cghidEventTap` with the chord held about 60 ms, then restore the previous
   pasteboard after 1 s only if `changeCount` still matches what Slovo wrote.
   Needs Accessibility. When `IsSecureEventInputEnabled()` is true or
   Accessibility is missing, do not paste: copy to the pasteboard and show
   "Copied — press ⌘V" in the Flow bar.
4. Save a history entry (text, raw engine text, engine id, language mode, app
   bundle id and name, category, duration, word count, fixes count, audio file
   name) and keep the WAV for playback according to retention settings.

### 4.4 Flow bar

A borderless, non-activating `NSPanel` (level `.statusBar`,
`[.canJoinAllSpaces, .fullScreenAuxiliary]`, never becomes key, never steals
focus) centered above the bottom of the screen with the focused window (or the
main screen), 3 pt above the Dock/visible frame, so it sits on the Dock and
covers as little as possible of the app below.

- Idle: a small grey pill (about 40×8 pt), shown while focus is in a text field;
  it grows in and out of view instead of popping. Hidden completely when the
  setting "Show Flow bar when idle" is off. The pointer arriving nudges it at once
  (a little larger, in the accent colour); resting on it for about 0.2 s opens it
  into the hint "Hold ⌥ to dictate" (key name follows the setting). Clicking the
  idle pill starts a hands-free recording.
- Recording: a dark pill (about 120×34 pt) with 9–13 animated bars driven by the
  input level; in hands-free mode also a cancel (×) and a stop (■) button.
- Processing: the same pill with a subtle animated dots/shimmer and a cancel (×)
  button; the menu bar menu offers Cancel too. A dictation whose engine takes too
  long (5 minutes to load a model, or 1 minute plus the recording's length to
  transcribe) is abandoned with a short error, so a new one can start.
- Result states for about 1.5 s: "Copied — press ⌘V", "Nothing heard",
  "Loading model…", a short error ("Microphone access is off" etc., clickable to
  open the relevant settings), "Meeting recording in progress" when dictation is
  blocked by a meeting.
- Every state is the same capsule: it reshapes (size, colour, shadow) on one
  spring of about 0.4 s with a small overshoot, and its content fades in once the
  shape opens, so the idle pill grows into the hint, the hint into a recording and
  so on, rather than one pill replacing another. Reduce Motion (Windows: animation
  effects off) turns this into short fades.

### 4.5 Style and app categories

Four categories, like Wispr Flow: Personal messages, Work messages, Email,
Other. Defaults by bundle id: personal — Telegram (`ru.keepcoder.Telegram`,
`org.telegram.desktop`), Messages (`com.apple.MobileSMS`), WhatsApp
(`net.whatsapp.WhatsApp`), Discord (`com.hnc.Discord`); work — Slack
(`com.tinyspeck.slackmacgap`), Microsoft Teams (`com.microsoft.teams2`),
Bitrix24, Zoom (`us.zoom.xos`), Яндекс Телемост; email — Mail
(`com.apple.mail`), Outlook (`com.microsoft.Outlook`), Spark; everything else —
Other. The Style screen lets the user move any app seen in history to another
category.

Each category has one of three styles (Wispr's names in parentheses):

- Formal (Caps + Punctuation): sentence case, keep punctuation, add a final
  period if the text has none.
- Casual (Caps + Less punctuation): sentence case, drop the final period of the
  last sentence.
- Very casual (No caps + Less punctuation): lowercase first letter of each
  sentence except for words that are dictionary terms or look like names in the
  original casing (acronyms, inner capitals, a Latin word in Russian text, the
  same word capitalized mid-sentence, or a proper noun the spelling dictionary
  knows only capitalized, such as «Москва»), drop final periods.

A period after an abbreviation («т. е.», «и т. д.», «г.», «ч.», «руб.», "etc.",
"Inc.") does not start a new sentence unless the engine capitalized the next
word, and Casual and Very casual never drop it.

Defaults: personal — Casual, work — Formal, email — Formal, other — Formal.
Each style card shows a sample message in the current UI language.

## 5. Screens

Main window (about 1100×720, minimum 900×600): a sidebar on a warm light
background and a content panel with large rounded corners. Sidebar items with SF
Symbols: Home (Dictation), Meetings, Insights, Dictionary, Snippets, Style,
Scratchpad; at the bottom Settings and a Help menu (keyboard shortcuts cheat
sheet, "Open models folder", "Open logs"). Settings open as a modal sheet over
the window, like Wispr Flow.

- **Home**: greeting "Hi {first name}, hold [⌥] to dictate" with the key shown
  as an orange keycap badge; a dark banner card with a serif headline (for
  example "Slovo spells the way you do" → opens Dictionary) that can be
  dismissed; a statistics card (total words, words per minute, day streak) in a
  large serif numeral style; history grouped by day (Today, Yesterday, date),
  each row with time, text and hover actions: play audio (if kept), copy,
  re-run with another engine (menu of ready engines; replaces the entry's text),
  delete. Search field filters history. Empty state explains the key.
- **Meetings**: "New recording" button, list of recordings (title editable,
  date, duration, status). Recording view: timer, input level for mic and system
  audio, "Mark moment" with its shortcut and the number of moments marked, stop.
  After stop: the processing phase with progress (loading the speaker model,
  separating speakers, transcribing), then the transcript with timestamps and
  speakers: "Speaker 1, 2, 3" for separated voices, "Me" for an unseparated
  microphone track and "Others" for unseparated system audio. Speakers can be
  renamed, marked "This is me" and merged; a line can be moved to another
  speaker; any line can be starred with an optional note, and important moments
  have their own tab. Buttons Copy, Export Markdown, Process again…, Show in
  Finder, Delete. A consent reminder line under the record button ("Let people
  know you are recording"). Details: `docs/SPEC-speakers-mcp.md` section 6.
- **Insights**: words per minute (with a semicircle gauge), total words
  dictated, fixes made (dictionary replacements plus term corrections), current
  and longest streak with a GitHub-style calendar of the last 20 weeks, usage by
  category (percent bars), top apps.
- **Dictionary**: "Add new" button (sheet: either a term, or a replacement
  "from → to"), tabs All / Terms / Replacements, search, sort, list rows showing
  "term" or "from → to", edit and delete. Dismissible banner "Slovo spells the
  way you do" explaining that terms fix English words written in Cyrillic. Seed
  on first launch with the terms from `Bench/vocabulary.txt` (copied into the
  app resources). Terms added to the vocabulary later (`DictionarySeed.additions`,
  e.g. GitHub) reach a Dictionary seeded earlier once, tracked by
  `dictionary.seedVersion`; a term the user deleted never comes back.
- **Snippets**: "Add new" (trigger phrase and text), list "trigger → text"
  (truncated), search, edit, delete, dismissible banner with three examples.
- **Style**: tabs Personal messages / Work messages / Email / Other / Auto
  cleanup. Category tabs: three selectable style cards with a sample and, below,
  the list of apps in this category with a control to move an app. Auto cleanup
  tab: toggles for filler removal and repetition removal with before/after
  examples.
- **Scratchpad**: list of notes (title from the first line, date), a simple
  editor; "Dictate into note" button that starts hands-free dictation whose
  result goes into the open note instead of the frontmost app. Notes autosave.

## 6. Settings

- **General**: dictation key (with a live "press to test" indicator), hands-free
  double-tap on/off, cancel with Esc on/off, paste-last shortcut, microphone
  (list with "System default"), interface language (System, English, Русский),
  dictation language mode (Russian and English — default; Russian; English).
- **Recognition**: engine for Russian and mixed speech (GigaAM v3 RNNT —
  recommended; GigaAM v3 CTC; Parakeet v3; Apple Dictation), engine for English
  (Parakeet v3 — recommended; GigaAM v3 RNNT; Apple Dictation), engine for
  meetings (GigaAM v3 CTC — default; Parakeet v3). Each engine row shows status
  (Ready, Not downloaded with size, Downloading with progress, Damaged), disk
  size, a one-line trade-off from the benchmark (for example "Best Russian with
  punctuation · ~700 MB RAM while loaded"), Download/Delete buttons. Downloads
  happen only when the user presses Download. GigaAM archives come from
  `https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/` and are
  extracted with `/usr/bin/tar`; Parakeet downloads through FluidAudio into
  `ModelStore.root/parakeet-v3`. "English words in Russian speech" (Auto —
  default, naming the model it would use now; Parakeet v3; Whisper large-v3
  turbo; Off), with a line saying it works only for the Russian and English
  dictation language, and a note when the chosen model is not downloaded (see
  4.3). The model list also has Whisper large-v3 turbo, marked "English helper"
  and never offered as a dictation or meeting engine, nor in "Run again" or
  onboarding: 574 MB (`ggml-large-v3-turbo-q5_0.bin` from Hugging Face
  `ggerganov/whisper.cpp`, pinned to a commit, SHA-256
  `394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2`) into
  `ModelStore.root/whisper-v3-turbo/`. It downloads into `<file>.partial`, which
  a network failure or quitting leaves behind to resume with an HTTP range
  (the row shows "The download did not finish" and Download again); the file is
  renamed into place only after its checksum matched, and Cancel removes the
  partial file. Idle unload time.
- **System**: launch at login (`SMAppService.mainApp`), show Flow bar when idle,
  start/stop sounds, show Slovo in the Dock while the window is open, show the menu bar
  icon (hidden, it still appears while a meeting records; launching Slovo again opens the window).
- **Meetings**: record system audio (on by default; explains the System Audio
  Recording permission), engine (mirrors Recognition), keep meeting audio
  (forever, 30 days, 7 days, delete after transcription). Speakers: separate
  speakers (on by default), people in a meeting (Automatic, 2–8), the speaker
  model row (status, size, Download, Delete; needs macOS 15). Important moments:
  the mark-moment shortcut (⌃⌘M, ⌃⌥M, ⌃⇧M).
- **Integrations**: let Claude read meetings (off by default), which meetings
  Claude can see (all, 90, 30 or 7 days), let Claude write notes into meetings
  (off by default, separate from reading), copyable registration snippets for
  Claude Code and the Claude app, a read-only registration status and a Check
  button. The section says that anything Claude reads is sent to Anthropic.
- **Data and privacy**: a line "Everything stays on this Mac" (while Claude access
  is on, it adds that transcripts Claude reads are sent to Anthropic); keep dictation
  history (forever, 30 days, 7 days, 24 hours); keep dictation audio for
  playback (7 days default, 30 days, 24 hours, never); "Delete all history"
  with confirmation; "Open data folder".
- **About**: version, build, engines and their licenses (GigaAM — MIT; NVIDIA
  Parakeet TDT 0.6B v3 — CC-BY-4.0, attribution to NVIDIA; sherpa-onnx —
  Apache-2.0; FluidAudio — Apache-2.0; OpenAI Whisper large-v3 turbo — MIT;
  whisper.cpp — MIT, ggml-org; pyannote community-1 speaker model —
  CC-BY-4.0, pyannoteAI, Core ML conversion by FluidInference), link to the
  models folder.

## 7. Onboarding

Shown on first launch (and from Help → "Show onboarding"), in a centered window
with a progress indicator:

1. Welcome and interface language choice (English / Русский, preselected from
   the system).
2. Permissions, each row with live status that refreshes when the app becomes
   active: Microphone (request), Accessibility (open System Settings → Privacy &
   Security → Accessibility; explains it is needed to paste text), Input
   Monitoring (open the pane; explains it is needed to notice the dictation
   key). System Audio Recording is requested later, on the first meeting.
3. Dictation key choice: one selectable card per key (Fn preselected) with a
   live test ("Hold fn now" turns green while held) and the Fn warnings from
   section 4.1. The choice can be changed later in Settings → General.
4. Models: shows ready engines and Download buttons for missing ones. Users can continue with Apple
   Dictation if nothing is downloaded.
5. Practice: a text field; the user holds the key and says a sentence; shows the
   result. Finish.

## 8. Meetings (Notetaker)

- Microphone through its own `AVCaptureSession` (the same design as dictation),
  system audio through a Core Audio
  process tap (`CATapDescription` that excludes Slovo's own process, a private
  aggregate device, an IOProc), each written continuously as a 16 kHz mono CAF
  file on disk so hours of audio never sit in memory. If the tap delivers only
  zeros for 3 s, treat System Audio Recording as denied and say so.
- While a meeting records, dictation is disabled and the Flow bar says so.
- After stop, when speaker separation is on, the speaker model is downloaded and
  macOS is 15 or later, each track is diarized first (FluidAudio's offline
  pyannote community-1 pipeline on Core ML) and then transcribed one clip per
  speaker turn of at most 20 s, which works with every engine because none needs
  word timings. Otherwise `SpeechSegmenter` cuts each track into speech segments
  of at most 20 s at low-energy points (reuse `GigaAMChunker` logic) and the
  tracks keep the "Me" and "Others" speakers. Silent audio is skipped, clips are
  transcribed with the meetings engine, and segments of one speaker are merged
  into lines by start time for display (`[mm:ss] Speaker 2: …`). A separation
  failure never fails the meeting. Progress is reported per phase. Processing
  runs at utility priority and can be cancelled; while the audio exists a meeting
  can be processed again, speakers only or speakers and text.
- A moment marked while recording is stored at its position on the tracks'
  shared timeline.
- Stored under `Application Support/Slovo/Meetings/<id>/`: `meeting.json` (format
  2 adds speakers, important moments and the separation outcome as optional keys,
  so older builds still read it), `mic.caf`, `system.caf` and `transcript.md`,
  which the store writes whenever it saves a transcribed meeting. No speaker
  embedding or voice profile is stored.

## 9. Storage

All under `~/Library/Application Support/Slovo/` (models stay in `Models/`, the
speaker model in `Models/diarization/`):
`history.json`, `dictionary.json`, `snippets.json`, `notes.json`,
`app-categories.json`, `Recordings/<yyyy-mm-dd>/<uuid>.wav`, `Meetings/`.
Stores are `@MainActor` observable objects that load on launch, save atomically
(write to a temporary file and replace) on change with a short debounce, and
survive a corrupted file by moving it aside to `<name>.corrupt-<timestamp>.json`
and starting empty. Entries that cannot be read (for example written by a newer
build) are dropped one by one and the rest kept, after the original file is
copied to `<name>.corrupt-<timestamp>.json`. When a damaged file cannot be set
aside, the store starts without it and saves nothing, so the only copy is never
overwritten. Settings live in `UserDefaults` under keys declared in one
place. `RetentionCleaner` runs at launch and every 6 hours.

## 10. Localization

- Two tables, `en.lproj/Localizable.strings` and `ru.lproj/Localizable.strings`,
  with exactly the same keys (a unit test checks parity and that every key used
  in code exists), plus `InfoPlist.strings` for the permission prompts.
- Runtime switching without restart through Slovo's `Localizer`
  (System / English / Русский), stored under `app.language`. SlovoCore's
  user-visible errors (including the existing Russian messages in the engines)
  move to keys in SlovoCore's own tables read through `CoreStrings`, which
  follows the same `app.language` value.
- Russian copy is natural and short, not a literal translation. Glossary:
  Home/Dictation — Диктовка; Meetings — Встречи; Insights — Статистика;
  Dictionary — Словарь; Snippets — Сниппеты; Style — Стиль; Scratchpad —
  Заметки; Settings — Настройки; Flow bar — панель диктовки; hands-free — без
  рук; Auto cleanup — Автоочистка; Personal messages — Личные сообщения; Work
  messages — Рабочие сообщения; Email — Почта; Other — Другое; Formal —
  Официально; Casual — Обычно; Very casual — Совсем просто; term — термин;
  replacement — замена; engine — движок; model — модель; speaker — спикер;
  Speaker 2 — Спикер 2; speaker separation, Separate speakers — разделение по
  спикерам, Разделять по спикерам; speaker model — модель спикеров; This is me —
  Это я; Default name — Имя по умолчанию; Merge with… — Объединить с…; Assign to
  speaker — Отнести к спикеру; important moment (highlight) — важный момент; Mark
  moment — Отметить момент; Mark as important / Remove mark — Отметить как важное
  / Снять отметку; Important moments — Важные моменты; Participants — Участники;
  Process again… — Обработать заново…; Speakers only / Speakers and text — Только
  спикеры / Спикеры и текст; People in a meeting — Сколько человек на встрече;
  Automatic — Автоматически; Integrations — Интеграции; Claude access — Доступ
  Claude; Let Claude read meetings — Разрешить Claude читать встречи; Check —
  Проверить.
- "Diarization", "embedding", "cluster" and "MCP" never appear in the interface;
  "MCP" appears only inside the copyable registration snippets.
- Plurals through `.stringsdict` in both languages (Russian needs one/few/many).

## 11. Visual design

Wispr Flow's look, not its branding (own name, own icon, no copied images):

- Light: window background `#F2F0EB`; sidebar same; content panel `#FBFAF7`
  with a 1 pt border `#E7E3DA` and 16 pt corner radius; primary text `#1C1B19`,
  secondary `#6B675F`; selected sidebar row `#E7E3DA` with 8 pt radius; primary
  button black `#161514` with white text, 8 pt radius; secondary button
  `#ECE8E0`; accent purple `#7C5CFA` for toggles, progress and the recording
  dot; keycap badge orange `#F4A259` with a 1 pt dark border.
- Dark: background `#1B1A18`, panel `#232220`, border `#34322E`, text `#EEEAE3`,
  secondary `#A39E94`, primary button white with black text; the rest adjusted
  to keep WCAG AA contrast.
- Type: SF Pro for UI; New York (`.serif` design) for page banners, big
  statistics numbers and style card samples; page titles 26 pt semibold.
- Banners: dark warm gradient (`#1E1A17` → `#4A3A30`) with a serif headline,
  a short description and a light button.
- Every colour is a token in `Theme`; no literal colours in views.

## 12. Quality bar

- `swift build` with no warnings from Slovo targets, `swift test` green
  (text pipeline, term correction examples above, snippets, style formatter,
  filler cleaner, stores round-trip and corruption handling, retention,
  insights, localization parity, segmenter/merger, hotkey tap state machine as a
  pure function of events, `meeting.json` migration, speaker turns, clip
  planning, speaker numbering and edits, MCP protocol exchanges, the MCP
  process's guarantee that reading changes nothing, and the gated write tool).
- `slovo-bench score --postprocess` applies the Dictionary stage with
  `Bench/vocabulary.txt` and shows the before/after table on the supplied reference clips.
- `python3 build.py` produces a signed `Slovo.app` that launches, shows the menu
  bar item and the window, and switches language live.
