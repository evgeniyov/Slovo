# Slovo for Android

The app records voice notes and meetings. Slovo on your computer imports the recordings over USB and creates transcripts with timestamps locally.

## Record on Android

- **Note** is for a single voice. On the computer, it appears under Meetings without speaker separation.
- **Meeting** is for a conversation with several participants. You can mark important moments while recording. The desktop app keeps the marks in the transcript and separates speakers if you have downloaded the speaker model.

Choose a recording type before you start. The app requires Android 10 or later. Grant microphone access on first launch.

## Transfer over USB

1. Install Android SDK Platform-Tools (`adb`) on the computer and enable USB debugging on the phone.
2. Connect the devices with a cable and approve the debugging prompt on the phone.
3. Open Slovo on the computer. Recordings queued on the phone appear under Meetings.

The phone keeps recordings until transfer. The desktop app transcribes the recordings and stores the transcripts. See the [README](../README.md) to install and set up the desktop app.
