# Slovo for macOS design brief

This brief describes the app's behavior, screens, and states. Apply the design to the current app without changing its logic.

---

## 1. What Slovo does

Slovo is a personal Mac app for one user. It was built as an alternative to a paid app. It converts speech to text entirely on the Mac. Speech recognition needs no internet connection or servers. The app has no payments, sign-in, accounts, subscriptions, or cloud sync.

1. **Dictation.** Hold a key in any app, such as a browser, editor, or messenger. Speak, then release the key to paste the text at the cursor.
2. **Meetings.** Record a call or a conversation in person. After recording, Slovo transcribes the conversation, labels voices as "Speaker 1", "Speaker 2", and so on, and marks important moments with stars.

Claude can also read meetings through a local connection and turn them into tasks. This feature has no screen of its own.

Russian is the primary language, and English has equal support. Users can mix English technical terms into Russian speech.

## 2. Where Slovo appears

Slovo runs in the menu bar, with no Dock icon in this design. The app has four parts.

**2.1. Menu bar icon.** The icon is always present and changes during dictation or meeting recording. Click it for a short menu to dictate, record a meeting, mark a moment, open the main window or settings, or quit.

**2.2. Floating panel.** A small strip near the bottom of the screen, above all windows. It is the only visible interface during dictation. It shows when Slovo is recording, the voice level, and whether Slovo pasted text, copied it, or heard nothing. Keep the panel unobtrusive over other apps. Users can hide it when idle in Settings.

**2.3. Main window.** Opens on demand. The sidebar is on the left and the content is on the right. The window is about 1100×720 points.

**2.4. Settings.** A separate sheet over the main window, with seven sections in its own sidebar.

## 3. Main window screens

From top to bottom, the sidebar lists **Dictation, Meetings, Insights, Dictionary, Snippets, Style,** and **Scratchpad**. The engine readiness indicator, **Settings**, and **Help** sit at the bottom.

**3.1. Dictation.** The home screen greets the user and shows which key to hold. It shows three counts: total words, words per minute, and consecutive days. Each history entry shows the time, text, destination app, recognition engine, and duration. Users can replay, copy, or delete an entry.

**3.2. Meetings.** The list is on the left and the selected meeting is on the right. See section 4.

**3.3. Insights.** Shows how much the user dictated by day and week, where they dictated, and how much time they saved compared with typing.

**3.4. Dictionary.** Users add terms and names the engine gets wrong. For example, the dictionary can replace "клод" with "Claude". These replacements improve recognition accuracy. Make it easy to add an entry.

**3.5. Snippets.** A keyword expands to saved text. For example, say "address" to insert an address.

**3.6. Style.** Choose whether to remove filler sounds such as "э-э" and repetitions or add punctuation. Set different styles for messaging, code, email, and notes.

**3.7. Scratchpad.** A simple notepad for dictating without switching apps.

**3.8. Settings.** Seven sections.

- **General.** Dictation key, hands-free mode, recording limit, Esc to cancel, microphone, and language.
- **Recognition.** Engines, model downloads, and unloading models from memory.
- **System.** Launch at login, macOS permissions, Dock icon, and menu bar icon.
- **Meetings.** System audio, engine, speaker separation, number of people, speaker model, shortcut to mark a moment, and audio retention.
- **Integrations.** Claude access, setup commands, and connection check.
- **Data and privacy.** Retention periods and deletion.
- **About.**

**3.9. Help.** Troubleshooting for missing permissions, a microphone that picks up no sound, and text that does not paste.

## 4. Meeting details

Give meeting details the most design time. This is the most complex part of the app and a priority for users.

**List on the left.** Show the title, date and time, and duration. Mark the recording in progress.

**Details on the right.**

- Edit the title in the header.
- Show the date, time, duration, engine, speaker count, and source (this Mac or a phone).
- Offer Copy, Export Markdown, Process Again, Show in Finder, and Delete.
- Show a chip for each speaker, such as "Speaker 1" and "Speaker 2", with a color and speaking time. Let users rename a speaker, mark a speaker as themselves, or merge two speakers.
- Show tabs for Transcript, Important Moments, and Statistics. Statistics show speaking time per person, pace, and pauses. Show the "Claude's notes" tab only if Claude has written notes for that meeting.
- Each transcript line shows a timestamp, speaker name, and text. A star marks an important line. Users can filter by speaker or assign a line to another speaker.
- The important moments list shows each mark with a timestamp and any note. Click a mark to jump to its transcript line.

**Recording panel.** While recording, show the timer, microphone and system audio levels, the Mark Moment button with its shortcut and mark count, and Stop. Show warnings, for example when Slovo lacks permission to record system audio.

**Process Again sheet.** Offer speaker separation only or speaker separation and transcription. Include the number of people and the engine.

## 5. States to design

**5.1. Dictation.** Idle, recording with voice level, processing with a distinct model-loading state, and result.

**5.2. Floating panel messages.** Each lasts about 1.5 seconds. Show "Copied, press ⌘V", "Nothing heard" with the microphone name, "Meeting recording in progress" when dictation is blocked, or an error.

**5.3. Dictation errors.** No microphone permission, no Accessibility permission, no Input Monitoring permission, model missing, engine failure, and recording failure. The first three open the relevant System Settings section.

**5.4. Meeting.** Recording, recorded and awaiting transcription, transcribing, transcribed, or failed.

**5.5. Processing phases.** Show preparation and speaker model loading. Show the percentage and track name during speaker separation, and the number of chunks processed during transcription. An hour-long meeting takes minutes to process. Show actual progress so users can tell the app is still working.

**5.6. Recognition model.** Show the size before download, the percentage and a Cancel button during download, the space used on disk when ready, and a damaged state.

**5.7. Empty states.** Design first-use states for empty dictation history, meetings, dictionary, snippets, and notes. The current screens need work.

## 6. Example data

**Dictation history:** `8:31 · «Да, скачай модель, проверь как работает» · Claude · GigaAM
v3 RNNT · 0:26`

**Meeting list:** `Встреча 16 сент. 2026 г., 10:52 · Сегодня, 10:52 · 1:42`

**Meeting details:** `16 сентября 2026 г. · 10:52 · 1:42 · GigaAM v3 CTC ·
2 спикера`

**Transcript line:** `[00:29] ★ Спикер 2: Да, на этой неделе всё`

**Speakers:** `Спикер 1 · 0:09` and `Спикер 2 · 0:02`, each in its own color.

**Home screen counts:** `602 слова · 111 слов в минуту · 2 дня подряд`

## 7. Current visual style

The app uses a warm, paper-like palette with a light sand background, thin borders, and little accent color. It uses no shadows or gradients. Both themes are required.

**Light.** Window background `#F2F0EB`, panel `#FBFAF7`, border `#E7E3DA`, primary text `#1C1B19`, secondary text `#6B675F`, and accent `#7C5CFA`.

**Dark.** Window background `#1B1A18`, panel `#232220`, border `#34322E`, primary text `#EEEAE3`, secondary text `#A39E94`, and accent `#8F74FB`.

In both themes, key hints use a distinctive orange keycap `#F4A259` with a dark outline. The star for important moments uses `#E9A94B`.

Corner radii are 16 for the panel, 12 for cards, and 8 for rows and buttons. Body text and page subheadings use 13 points. Captions use 11 points.

You can change these values. Keep both themes and a restrained design for people who use the app for hours.

## 8. Constraints

- The app supports macOS 14.4 or later and uses SwiftUI with no third-party UI libraries. Layered blur, custom shadows, and animations along curves are costly or hard to reproduce. Use clear typography, a consistent grid, and well-designed empty states.
- The window is resizable. The layout must work down to about 900 points wide.
- The app uses two languages. Russian strings are about a third longer than English strings on average. Allow room for both.
- Users often work without the main window open. The floating panel and menu bar icon must communicate as much as the app screens.

## 9. What to leave out

Leave out payments, subscriptions, accounts, sign-in, cloud features, sharing, ads for other products, and telemetry. The interface should make clear that speech processing stays on this Mac.

The Integrations section is the exception. If Claude reads a meeting, tell the user that Claude sends what it reads to Anthropic.

## 10. Deliverables

- Complete Dictation and Meetings screens in both themes.
- Meeting details with the transcript, important moments, and a processing progress state.
- Floating panel states for idle, recording, processing, no speech heard, and errors.
- Empty states for dictation and meetings.
- One complete settings section to use as a pattern for the others.
- A spacing and type size scale.
- Optional app icon to replace the current quick draft.

Provide the Russian designs and at least one English screen to check string lengths.
