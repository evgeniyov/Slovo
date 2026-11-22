<#
.SYNOPSIS
  Removes Slovo installed by install.ps1. Your recordings, transcripts, history and models in
  %LOCALAPPDATA%\Slovo stay; pass -RemoveData to delete them too.
#>
[CmdletBinding()]
param(
    [switch]$RemoveData
)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Slovo'
$exe = Join-Path $target 'Slovo.exe'

Get-Process -Name 'Slovo' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-Item -Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'Slovo.lnk') -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Slovo' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Slovo' -Recurse -ErrorAction SilentlyContinue

# This script lives in the folder it removes, so the folder goes after it exits.
Start-Process -WindowStyle Hidden -FilePath 'cmd.exe' -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$target`""

if ($RemoveData) {
    Remove-Item -Path (Join-Path $env:LOCALAPPDATA 'Slovo') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Slovo and its data were removed.'
} else {
    Write-Host "Slovo was removed. Your data stays in $(Join-Path $env:LOCALAPPDATA 'Slovo')."
}
