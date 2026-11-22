<#
.SYNOPSIS
  Installs Slovo for the current user: no administrator rights, nothing outside your profile.

.DESCRIPTION
  Copies this folder to %LOCALAPPDATA%\Programs\Slovo (a path that stays the same across
  updates, which matters for the command that connects Slovo to Claude), adds Slovo to the
  Start menu, registers it under "Installed apps" so it can be removed from Windows
  Settings, and starts it. Run it again from a newer zip to update; your data in
  %LOCALAPPDATA%\Slovo is never touched.

  Usage: right-click install.ps1 -> Run with PowerShell, or in PowerShell:
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'

$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\Slovo'
$exe = Join-Path $target 'Slovo.exe'

if (-not (Test-Path (Join-Path $source 'Slovo.exe'))) {
    throw "Slovo.exe is not next to install.ps1. Unzip the whole archive first."
}

# A running Slovo keeps its files open; ask it to quit first.
$running = Get-Process -Name 'Slovo' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    Write-Host 'Closing the running Slovo...'
    $running | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Seconds 2
    $running | Where-Object { -not $_.HasExited } | Stop-Process -Force
}

if ((Resolve-Path $source).Path -ne $target) {
    Write-Host "Copying Slovo to $target"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    # Remove files of the previous version that this one no longer ships.
    Get-ChildItem -Path $target -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($target.Length + 1)
        if (-not (Test-Path (Join-Path $source $relative))) { Remove-Item $_.FullName -Force }
    }
    Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
}

# Start menu shortcut.
$programs = [Environment]::GetFolderPath('Programs')
$shortcutPath = Join-Path $programs 'Slovo.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $target
$shortcut.Description = 'Slovo: local dictation and meeting transcripts'
$shortcut.IconLocation = "$exe,0"
$shortcut.Save()

# "Installed apps" entry, per user.
$version = (Get-Item $exe).VersionInfo.ProductVersion
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Slovo'
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name 'DisplayName' -Value 'Slovo'
Set-ItemProperty -Path $uninstallKey -Name 'DisplayVersion' -Value $version
Set-ItemProperty -Path $uninstallKey -Name 'Publisher' -Value 'Slovo'
Set-ItemProperty -Path $uninstallKey -Name 'DisplayIcon' -Value $exe
Set-ItemProperty -Path $uninstallKey -Name 'InstallLocation' -Value $target
Set-ItemProperty -Path $uninstallKey -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name 'NoRepair' -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name 'UninstallString' -Value ("powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"" + (Join-Path $target 'uninstall.ps1') + "`"")

Write-Host "Slovo $version is installed in $target"
if (-not $NoStart) {
    Start-Process -FilePath $exe
}
