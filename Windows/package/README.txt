Slovo for Windows
=================

Local dictation and meeting transcripts. Everything runs on this computer.

Install
  1. Unzip the whole archive anywhere.
  2. Right-click install.ps1 -> "Run with PowerShell"
     (or: powershell -ExecutionPolicy Bypass -File .\install.ps1).
  Slovo goes to %LOCALAPPDATA%\Programs\Slovo, appears in the Start menu and starts.
  To update, do the same with a newer zip. Your data is in %LOCALAPPDATA%\Slovo.

Use
  Hold Ctrl + Win (changeable in Settings), speak, release: the text is pasted where the
  cursor is. Double-tap for hands-free, Esc cancels. Slovo lives in the notification area.

Models
  Download GigaAM (Russian) and/or Parakeet (English) in Settings -> Recognition. If you
  also use Slovo on a Mac, the GigaAM folders from ~/Library/Application Support/Slovo/Models
  can be copied into %LOCALAPPDATA%\Slovo\Models as they are.

Remove
  Windows Settings -> Apps -> Installed apps -> Slovo -> Uninstall
  (your data stays; run uninstall.ps1 -RemoveData to delete it too).
