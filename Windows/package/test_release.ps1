param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'

function Test-Mcp([string]$Executable, [string]$Arguments) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($Executable, $Arguments)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $response = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"release-test","version":"1"}}}')
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'Packaged Slovo did not exit after stdin closed'
        }
        $reply = $response.GetAwaiter().GetResult() | ConvertFrom-Json
        if ($process.ExitCode -ne 0 -or $reply.id -ne 1 -or $reply.result.protocolVersion -ne '2025-11-25') {
            throw "Unexpected MCP reply from packaged Slovo ($($process.ExitCode)). $($errors.GetAwaiter().GetResult())"
        }
    } finally {
        $process.Dispose()
    }
}

$name = "Slovo-$Version-win-x64"
$archive = Join-Path $PackageDirectory "$name-portable.zip"
$installer = Join-Path $PackageDirectory "$name-setup.exe"
$portableRoot = Join-Path $env:RUNNER_TEMP 'Slovo portable test'
Expand-Archive -LiteralPath $archive -DestinationPath $portableRoot
$portable = Join-Path $portableRoot "$name-portable"
$portableData = Join-Path $portable 'Data'
New-Item -ItemType Directory -Path $portableData | Out-Null
'{"integrations.mcp.enabled":true}' | Set-Content -Encoding utf8NoBOM (Join-Path $portableData 'settings.json')
$launcher = Join-Path $portable 'Start Slovo.cmd'
$env:SLOVO_DATA_DIR = Join-Path $env:RUNNER_TEMP 'incorrect portable data path'
Test-Mcp $env:ComSpec ('/d /s /c ""' + $launcher + '" --mcp"')
Write-Host 'Portable launches with the Data folder beside the executable.'

$data = Join-Path $env:LOCALAPPDATA 'Slovo'
$env:SLOVO_DATA_DIR = $data
New-Item -ItemType Directory -Force -Path $data | Out-Null
$settings = Join-Path $data 'settings.json'
'{"integrations.mcp.enabled":true}' | Set-Content -Encoding utf8NoBOM $settings
$originalData = (Get-FileHash -LiteralPath $settings).Hash

$app = Join-Path $env:LOCALAPPDATA 'Programs\Slovo'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Slovo'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Slovo.lnk'
$setup = Start-Process -FilePath $installer -ArgumentList '/S' -PassThru -Wait
if ($setup.ExitCode -ne 0) { throw "Setup exited with $($setup.ExitCode)" }
if ((Get-ItemProperty -Path $uninstallKey).DisplayVersion -ne $Version) { throw 'Setup did not register the version' }
if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Setup did not create the Start menu shortcut' }
Test-Mcp (Join-Path $app 'Slovo.exe') '--mcp'

$remove = Start-Process -FilePath (Join-Path $app 'Uninstall Slovo.exe') -ArgumentList '/S' -PassThru -Wait
if ($remove.ExitCode -ne 0 -or (Test-Path (Join-Path $app 'Slovo.exe')) -or
    (Test-Path $uninstallKey) -or (Test-Path -LiteralPath $shortcut)) {
    throw 'Uninstall did not remove the application, registration, or shortcut'
}
if ((Get-FileHash -LiteralPath $settings).Hash -ne $originalData) { throw 'User data changed during install or uninstall' }
Write-Host 'Setup installs and uninstalls the app without changing user data.'
