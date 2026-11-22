<#
.SYNOPSIS
    Collects voice clips the phone app recorded, over the USB cable, and drops them in
    Slovo's inbox, where the importer turns each one into a meeting.

.DESCRIPTION
    The Windows counterpart of Scripts/pull_phone_clips.sh:

        powershell -ExecutionPolicy Bypass -File Scripts\pull_phone_clips.ps1

    Nothing listens on the network: this runs only while a cable is connected and adb
    sees the phone. adb is taken from $env:ADB, then PATH, then the Android SDK's default
    place (%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe).

    The phone publishes a clip only once it is complete, with the sidecar that holds its
    marked moments already beside it. This side does not take that on trust: audio with no
    sidecar waits until it has sat still for a minute, which is far longer than publishing
    takes, so a recording another app left in the folder still arrives while a clip caught
    mid-publish never does.

    Each file is pulled under a hidden ".<name>.partial" name and renamed into the inbox
    only once it is complete, so the importer never sees half a file.

    Works in Windows PowerShell 5.1 and PowerShell 7. Kept ASCII-only so 5.1 reads it
    correctly without a byte-order mark.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Clips come over USB only; the adb server need not browse the network for phones.
$env:ADB_MDNS = '0'
$PhoneFolder = '/sdcard/Download/Slovo'
$DataFolder = if ($env:SLOVO_DATA_DIR) { $env:SLOVO_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'Slovo' }
$Inbox = Join-Path $DataFolder 'Inbox'
# Where the importer moves a clip once it has become a meeting.
$Imported = Join-Path $Inbox 'Imported'
$GraceSeconds = 60

function Find-Adb {
    if ($env:ADB -and (Test-Path -LiteralPath $env:ADB -PathType Leaf)) {
        return $env:ADB
    }
    $command = Get-Command 'adb' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        return $command.Path
    }
    if ($env:LOCALAPPDATA) {
        $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
        if (Test-Path -LiteralPath $sdk -PathType Leaf) {
            return $sdk
        }
    }
    return $null
}

$Adb = Find-Adb
if (-not $Adb) {
    [Console]::Error.WriteLine('adb not found. Install the Android SDK platform-tools or set $env:ADB to adb.exe.')
    exit 1
}

# Runs adb and returns its output lines without carriage returns. "shell -n" keeps the
# remote shell away from this console's input, which it would otherwise read and swallow.
function Invoke-Adb {
    param([string[]]$Arguments)
    # Windows PowerShell 5.1 turns a native command's stderr into errors, which 'Stop'
    # would make fatal; adb's complaints are only noise here, its exit code says enough.
    $ErrorActionPreference = 'Continue'
    $output = & $Adb @Arguments 2>$null
    $script:AdbExitCode = $LASTEXITCODE
    return @($output | ForEach-Object { "$_".TrimEnd("`r") })
}

function Invoke-PhoneShell {
    param([string]$Serial, [string]$Command)
    return Invoke-Adb @('-s', $Serial, 'shell', '-n', $Command)
}

$serials = @(Invoke-Adb @('devices') | ForEach-Object {
    $parts = $_ -split "`t"
    if ($parts.Count -eq 2 -and $parts[1] -eq 'device') { $parts[0] }
})
if ($serials.Count -eq 0) {
    [Console]::Error.WriteLine('No phone connected. Plug in the cable, unlock the phone and allow USB debugging.')
    exit 1
}

New-Item -ItemType Directory -Path $Inbox -Force | Out-Null
$script:Collected = 0

# Refuses every name that must not reach a shell or a path: anything that leaves the folder
# or ends the quoting around it, anything Windows cannot store, and anything beginning with
# a dot - MediaStore parks a clip that is still being written under ".pending-<n>-<name>".
function Test-SafeName {
    param([string]$Name)
    if ([string]::IsNullOrEmpty($Name) -or $Name.StartsWith('.')) { return $false }
    if ($Name.IndexOfAny([char[]]'/\''"`$<>:|?*') -ge 0) { return $false }
    foreach ($character in $Name.ToCharArray()) {
        if ([int]$character -lt 32) { return $false }
    }
    if ($Name.EndsWith('.') -or $Name.EndsWith(' ')) { return $false }
    $stem = $Name.Split('.')[0].ToUpperInvariant()
    if ($stem -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') { return $false }
    return $true
}

# Copies one file under a hidden partial name, then renames it into place; leaves nothing
# behind when it fails.
function Copy-FromPhone {
    param([string]$Serial, [string]$Name)
    $partial = Join-Path $Inbox ".$Name.partial"
    $target = Join-Path $Inbox $Name
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    Invoke-Adb @('-s', $Serial, 'pull', "$PhoneFolder/$Name", $partial) | Out-Null
    $ok = $script:AdbExitCode -eq 0 -and (Test-Path -LiteralPath $partial -PathType Leaf) -and ((Get-Item -LiteralPath $partial).Length -gt 0)
    if ($ok) {
        try {
            Move-Item -LiteralPath $partial -Destination $target
            return $true
        }
        catch {
            $ok = $false
        }
    }
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    return $false
}

# Deletes one file from the phone, and fails when the phone refuses to.
function Remove-FromPhone {
    param([string]$Serial, [string]$Name)
    Invoke-PhoneShell $Serial "rm -f '$PhoneFolder/$Name'" | Out-Null
    return $script:AdbExitCode -eq 0
}

# True once audio with no sidecar has sat still long enough to be what it looks like. Both
# times come from the phone: a computer clock running ahead of it would make a clip written
# a second ago look a minute old.
function Test-HasWaited {
    param([string]$Serial, [string]$Name)
    $times = @(Invoke-PhoneShell $Serial "date +%s; stat -c %Y '$PhoneFolder/$Name'")
    if ($times.Count -lt 2) { return $false }
    $now = 0L
    $modified = 0L
    if (-not [long]::TryParse($times[0].Trim(), [ref]$now)) { return $false }
    if (-not [long]::TryParse($times[1].Trim(), [ref]$modified)) { return $false }
    return ($now - $modified) -ge $GraceSeconds
}

function Receive-Clips {
    param([string]$Serial)
    $names = @(Invoke-PhoneShell $Serial "ls -1 $PhoneFolder 2>/dev/null" | Where-Object { $_ -ne '' })
    $clips = @($names | Where-Object { $_ -match '\.m4a$' })
    if ($clips.Count -eq 0) {
        Write-Host "${Serial}: nothing waiting on the phone."
        return
    }

    foreach ($name in $clips) {
        if (-not (Test-SafeName $name)) {
            [Console]::Error.WriteLine("${Serial}: skipped, not a plain file name: $name")
            continue
        }
        # Already here: still in the inbox, or moved on to Imported once it became a
        # meeting. Without the second place to look, a clip the phone would not delete
        # would come back on the next run as a second meeting.
        if ((Test-Path -LiteralPath (Join-Path $Inbox $name)) -or (Test-Path -LiteralPath (Join-Path $Imported $name))) {
            Write-Host "${Serial}: skipped, already here: $name"
            continue
        }
        $sidecar = [System.IO.Path]::ChangeExtension($name, '.json')
        $hasSidecar = $names -ccontains $sidecar
        if (-not $hasSidecar -and -not (Test-HasWaited $Serial $name)) {
            Write-Host "${Serial}: skipped, too new to be sure it is finished: $name"
            continue
        }
        if ($hasSidecar) {
            # The sidecar carries the phone's time, title and marked moments. It goes first:
            # an import starts from the audio, so the metadata is there before the clip is.
            if (-not (Copy-FromPhone $Serial $sidecar)) {
                [Console]::Error.WriteLine("${Serial}: failed, left on the phone: $sidecar")
                continue
            }
        }
        if (-not (Copy-FromPhone $Serial $name)) {
            # The sidecar arrived and its audio did not: it goes, since nothing here belongs
            # to it, and the pair stays on the phone for a later run.
            if ($hasSidecar) {
                Remove-Item -LiteralPath (Join-Path $Inbox $sidecar) -Force -ErrorAction SilentlyContinue
            }
            [Console]::Error.WriteLine("${Serial}: failed, left on the phone: $name")
            continue
        }
        $script:Collected++
        Write-Host "${Serial}: collected: $name"
        # Removed only once both files are here and neither is empty. The audio goes first
        # so the sidecar outlives it: while the pair is whole the phone's own rule still
        # holds, so a delete that fails in between leaves a clip the next run pairs again,
        # not audio that would later be taken for a plain note. MediaStore keeps a stale row
        # until Android rescans, so the app's counter can lag by a few minutes.
        if (Remove-FromPhone $Serial $name) {
            if ($hasSidecar -and -not (Remove-FromPhone $Serial $sidecar)) {
                [Console]::Error.WriteLine("${Serial}: collected, but the sidecar stays on the phone: $sidecar")
            }
        }
        else {
            [Console]::Error.WriteLine("${Serial}: collected, but it stays on the phone: $name")
        }
    }
}

# Every phone that answered, not the first of them: a second phone on the cable carries its
# own clips, and adb needs to be told which one each command is for.
foreach ($serial in $serials) {
    if ($serial) {
        Receive-Clips $serial
    }
}

Write-Host "Done: $($script:Collected) clip(s) in the inbox. Slovo imports them within a few seconds."
