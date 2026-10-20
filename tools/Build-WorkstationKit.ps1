# Assembles Workstation-Kit\ - the exact set of files to copy onto a USB stick
# for a trip to the workstations, and nothing else.
#
#   powershell -ExecutionPolicy Bypass -File tools\Build-WorkstationKit.ps1
#
# WHY THIS EXISTS. Asked which files to take, the honest answer was "two of the
# three .cmd files, one .ps1 from tools\, another .ps1 from the root, and the
# zip" - which is a question nobody should have to answer twice. The kit folder
# is the answer instead: copy the folder, take the zip, go.
#
# It is rebuilt rather than kept by hand because the folder necessarily holds
# COPIES. The copies are disposable and gitignored; these files are the
# originals:
#
#   Collect-Support.cmd         repo root
#   Collect-Support.ps1         tools\
#   Test-DownloadHosts.cmd      repo root
#   Test-DownloadHosts.ps1      tools\
#   WORKSTATION-STEPS.txt       repo root
#
# Each .cmd looks for its .ps1 beside itself before trying tools\, which is what
# lets the kit be one flat folder.
#
# Run this again after changing any of them. The hashes printed below are the
# check: Test-DownloadHosts.ps1 stamps its own SHA-256 into every report it
# writes, so a report can always be traced back to the copy that produced it.

param([switch]$Quiet)

$repo = Split-Path -Parent $PSScriptRoot
$kit  = Join-Path $repo 'Workstation-Kit'

$parts = @(
    @{ From = (Join-Path $repo  'Collect-Support.cmd');    As = 'Collect-Support.cmd' }
    @{ From = (Join-Path $repo  'tools\Collect-Support.ps1'); As = 'Collect-Support.ps1' }
    @{ From = (Join-Path $repo  'Test-DownloadHosts.cmd'); As = 'Test-DownloadHosts.cmd' }
    @{ From = (Join-Path $repo  'tools\Test-DownloadHosts.ps1'); As = 'Test-DownloadHosts.ps1' }
    @{ From = (Join-Path $repo  'WORKSTATION-STEPS.txt');  As = 'WORKSTATION-STEPS.txt' }
)

# Refuse rather than build half a kit: a kit missing one file is worse than no
# kit, because it looks complete on the stick.
$missing = @($parts | Where-Object { -not (Test-Path -LiteralPath $_.From) })
if ($missing.Count -gt 0) {
    Write-Output 'NOT BUILT - these are missing:'
    foreach ($m in $missing) { Write-Output "  $($m.From)" }
    exit 2
}

if (Test-Path -LiteralPath $kit) { Remove-Item -LiteralPath $kit -Recurse -Force }
New-Item -ItemType Directory -Force -Path $kit | Out-Null

foreach ($p in $parts) {
    Copy-Item -LiteralPath $p.From -Destination (Join-Path $kit $p.As) -Force
}

if (-not $Quiet) {
    Write-Output ''
    Write-Output "  Workstation-Kit built: $kit"
    Write-Output ''
    foreach ($f in (Get-ChildItem -LiteralPath $kit -File | Sort-Object Name)) {
        $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.Substring(0, 16)
        Write-Output ("    {0,-26} {1,6} KB  {2}" -f $f.Name, [math]::Round($f.Length / 1KB), $h)
    }
    Write-Output ''
    Write-Output '  Copy that whole folder to the stick, plus Minecraft-Launcher-Update.zip.'
    Write-Output '  Nothing else from the launcher folder is needed.'
    Write-Output ''
}
