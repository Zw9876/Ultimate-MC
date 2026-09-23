# Stops Microsoft Defender quarantining the launcher on a machine you own.
#
#   Run as Administrator, on each machine:
#     tools\Set-LauncherDefenderPolicy.ps1
#
#   Point it somewhere else, or undo it:
#     tools\Set-LauncherDefenderPolicy.ps1 -Path 'D:\Minecraft'
#     tools\Set-LauncherDefenderPolicy.ps1 -Remove
#     tools\Set-LauncherDefenderPolicy.ps1 -Show
#
# WHY THIS IS NEEDED
#   The launcher is a 125 MB unsigned executable that downloads a new copy of itself
#   over the network and replaces itself with it. That is an honest description of
#   what it does and also an honest description of a dropper, so Defender's machine
#   learning scores it as one. Nothing is wrong with the file.
#
#   It is worse than a one-off, because the version is generated per build: every
#   update is a brand-new binary with a hash Defender has never seen and no
#   reputation anywhere in the world. Reporting one build as a false positive clears
#   that build. The next one starts from zero again.
#
# WHAT THIS COSTS YOU
#   Defender stops scanning everything in that folder — including versions\, mods
#   downloaded from Modrinth, and anything else that lands there. That is a real
#   reduction in protection and it is the reason this is a script you run knowingly
#   rather than something the launcher does to your machine.
#
#   Keep the folder one nobody drops random downloads into, and keep the exclusion
#   as deep as you can: excluding C:\Users\Zach\Ultimate-MC is fine, excluding
#   C:\ or C:\Users is not.
#
#   The durable alternative is a real code-signing certificate, which carries
#   reputation across rebuilds. See HANDOFF.md section 15.

param(
    [string]$Path = (Split-Path -Parent $PSScriptRoot),
    [switch]$Remove,
    [switch]$Show
)

$admin = ([Security.Principal.WindowsPrincipal] `
          [Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Show-Current {
    $current = @((Get-MpPreference).ExclusionPath)
    if ($current.Count -eq 0) { Write-Host 'Defender has no path exclusions.' }
    else {
        Write-Host 'Defender path exclusions:'
        $current | ForEach-Object { Write-Host "  $_" }
    }
}

if ($Show) { Show-Current; exit 0 }

if (-not $admin) {
    Write-Host 'Run this in an Administrator PowerShell - Defender settings need it.' -ForegroundColor Red
    exit 2
}

# Not $Path = (...)?.Path - these machines run Windows PowerShell 5.1, which has no
# null-conditional operator and fails to parse it.
$resolved = Resolve-Path $Path -ErrorAction SilentlyContinue
if (-not $resolved) { Write-Host 'That folder does not exist.' -ForegroundColor Red; exit 2 }
$Path = $resolved.Path

# A shallow exclusion would cover far more than the launcher. Refuse rather than
# quietly turn most of the machine's scanning off.
$depth = ($Path.TrimEnd('\') -split '\').Count
if ($depth -lt 3) {
    Write-Host "Refusing to exclude '$Path' - too broad. Use the launcher's own folder." -ForegroundColor Red
    exit 2
}

if ($Remove) {
    Remove-MpPreference -ExclusionPath $Path
    Write-Host "Removed the exclusion for $Path" -ForegroundColor Yellow
    Write-Host 'Defender will scan this folder again, and may quarantine the launcher.'
    Show-Current
    exit 0
}

Add-MpPreference -ExclusionPath $Path
Write-Host "Excluded $Path from Defender scanning." -ForegroundColor Green
Write-Host ''
Show-Current

# A file already quarantined stays quarantined; the exclusion only stops it happening
# again. This is the step people miss and then report that the script did nothing.
$threats = @(Get-MpThreat -ErrorAction SilentlyContinue | Where-Object { $_.IsActive })
if ($threats.Count -gt 0) {
    Write-Host ''
    Write-Host 'There are still active detections on this machine:' -ForegroundColor Yellow
    $threats | ForEach-Object { Write-Host "  $($_.ThreatName)" }
    Write-Host 'Restore them from Windows Security > Protection history, or run:' -ForegroundColor Yellow
    Write-Host '  & "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -Restore -All'
}
