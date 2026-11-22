Slovo for Windows
=================

Local dictation and meeting transcripts. Everything runs on this computer.

Install
  1. Unzip the whole archive anywhere.
  2. Right-click install.ps1 and choose "Run with PowerShell". Or run this command:
     powershell -ExecutionPolicy Bypass -File .\install.ps1
  The script installs Slovo in %LOCALAPPDATA%\Programs\Slovo, adds it to the Start
  menu, and starts it. To update, repeat these steps with a newer ZIP. Your data
  is in %LOCALAPPDATA%\Slovo.

Use
  Hold Ctrl + Win while you speak. Release the keys to paste the text at the
  cursor. You can change the shortcut in Settings. Double-tap for hands-free
  dictation. Press Esc to cancel. The Slovo icon is in the notification area.

Models
  In Settings, open Recognition to download GigaAM for Russian, Parakeet for
  English, or both. If you also use Slovo on a Mac, copy the GigaAM folders from
  ~/Library/Application Support/Slovo/Models into %LOCALAPPDATA%\Slovo\Models.

Remove
  In Windows Settings, open Apps, then Installed apps. Choose Uninstall for
  Slovo. This keeps your data. To delete it too, run uninstall.ps1 -RemoveData.
