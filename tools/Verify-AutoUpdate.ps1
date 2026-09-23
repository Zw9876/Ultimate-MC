# Does an out-of-date launcher update ITSELF, with nobody touching it?
#
#   tools\Verify-AutoUpdate.ps1
#
# Two real installs on one machine: a newer host serving over its skin server, and
# an older client that nobody clicks after it starts. This is the only test that
# exercises the whole mandatory-update path end to end — detect, download,
# countdown, close everything holding the exe, swap, relaunch.
#
# It needs:
#   - a Release publish in bin\Release\...\publish (that becomes the HOST, and must
#     be NEWER than the client, so publish it after building the client)
#   - a Debug build (that becomes the CLIENT)
# Both come from `dotnet build` then `dotnet publish`; the version is generated
# from the build minute, so publish at least a minute after building.
#
# Everything lands under -Work and is removed afterwards. No real install is touched.

param(
    [string]$Work = (Join-Path $env:TEMP 'mc-autoupdate-test'),
    [int]$Port = 25567
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$repo   = Split-Path -Parent $PSScriptRoot
$pub    = Join-Path $repo 'MinecraftLauncher\bin\Release\net10.0-windows\win-x64\publish'
$debug  = Join-Path $repo 'MinecraftLauncher\bin\Debug\net10.0-windows'
$client = Join-Path $Work 'client'
$hostDir = Join-Path $Work 'host'
$root   = [Windows.Automation.AutomationElement]::RootElement

$package = @('MinecraftLauncher.exe','D3DCompiler_47_cor3.dll','PenImc_cor3.dll',
             'PresentationNative_cor3.dll','vcruntime140_cor3.dll','wpfgfx_cor3.dll')

$pass = 0; $fail = 0
function Check($what, $ok, $detail) {
    if ($ok) { $script:pass++; Write-Host "  ok    $what" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL  $what  -> $detail" -ForegroundColor Red }
}
function ById($w, $id) {
    $w.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}
function ByName($w, $n) {
    $w.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::NameProperty, $n)))
}
function Ver($p) { (Get-Item $p).VersionInfo.FileVersion }
function Hash($p) { (Get-FileHash $p -Algorithm SHA256).Hash }

if (-not (Test-Path (Join-Path $pub 'MinecraftLauncher.exe'))) {
    Write-Host "No Release publish at $pub - run dotnet publish first." -ForegroundColor Red; exit 2
}
if (-not (Test-Path (Join-Path $debug 'MinecraftLauncher.exe'))) {
    Write-Host "No Debug build at $debug - run dotnet build first." -ForegroundColor Red; exit 2
}

Get-Process MinecraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

# ---- build the two installs ----
Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $client, $hostDir | Out-Null

Get-ChildItem $debug -File | Copy-Item -Destination $client -Force
foreach ($f in $package) { Copy-Item (Join-Path $pub $f) (Join-Path $hostDir $f) -Force }

# Junctions, not copies: runtime/ is hundreds of MB. Removed with rmdir below, which
# deletes the link rather than following it into the real folder.
foreach ($d in 'runtime', 'versions', 'servers') {
    $target = Join-Path $repo $d
    if (Test-Path $target) {
        cmd /c mklink /J (Join-Path $client $d) $target | Out-Null
        cmd /c mklink /J (Join-Path $hostDir $d) $target | Out-Null
    }
}

$clientBefore = Ver (Join-Path $client 'MinecraftLauncher.exe')
$hostVersion  = Ver (Join-Path $hostDir 'MinecraftLauncher.exe')
$hostHash     = Hash (Join-Path $hostDir 'MinecraftLauncher.exe')

Write-Host "client starts on $clientBefore ; host serves $hostVersion"
Check 'the host really is newer' ([version]$hostVersion -gt [version]$clientBefore) `
      "$hostVersion vs $clientBefore - publish AFTER building, at least a minute later"
if ([version]$hostVersion -le [version]$clientBefore) { exit 1 }

# ---- host: start it and turn its skin server on ----
Write-Host ''
Write-Host '== host ==' -ForegroundColor Yellow
$hp = Start-Process (Join-Path $hostDir 'MinecraftLauncher.exe') -PassThru
Start-Sleep -Seconds 18
$hw = $root.FindFirst([Windows.Automation.TreeScope]::Children,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $hp.Id)))
if (-not $hw) { Write-Host 'host window never appeared' -ForegroundColor Red; exit 1 }

(ByName $hw 'SKINS').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 2
(ById $hw 'SkinServerToggle').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 5
$status = (ById $hw 'SkinServerStatus').Current.Name
Write-Host "  $status"
Check 'the host is serving' ($status -match 'running') $status

# ---- client: pinned at the host, then left completely alone ----
Write-Host ''
Write-Host '== client, left alone ==' -ForegroundColor Yellow

# Pinned because both installs are on one machine, so the host reads as local and
# UDP discovery (which deliberately prefers a remote host) will not find it.
$cfg = @(Get-Content (Join-Path $client 'config.txt') -ErrorAction SilentlyContinue |
         Where-Object { $_ -notmatch '^SKIN_SERVER=' })
($cfg + "SKIN_SERVER=127.0.0.1:$Port") | Set-Content (Join-Path $client 'config.txt') -Encoding ascii

Start-Process (Join-Path $client 'MinecraftLauncher.exe') | Out-Null
Write-Host '  started; nobody clicks anything from here on'

$sawCountdown = $false
$deadline = (Get-Date).AddMinutes(5)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    if ($root.FindFirst([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition(
                [Windows.Automation.AutomationElement]::NameProperty, 'Updating the launcher')))) {
        $sawCountdown = $true; Write-Host '  countdown window appeared'; break
    }
    if ((Ver (Join-Path $client 'MinecraftLauncher.exe')) -ne $clientBefore) { Write-Host '  already swapped'; break }
}
Check 'a countdown appeared without anyone asking' $sawCountdown 'never seen'

$swapped = $false
$deadline = (Get-Date).AddMinutes(5)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 4
    if ((Ver (Join-Path $client 'MinecraftLauncher.exe')) -eq $hostVersion) { $swapped = $true; break }
}

Write-Host ''
Write-Host '== result ==' -ForegroundColor Yellow
$after = Ver (Join-Path $client 'MinecraftLauncher.exe')
Write-Host "  client was $clientBefore, is now $after"

Check 'the client updated itself, unattended' $swapped "still $after"
Check 'it is byte-for-byte the host build' ((Hash (Join-Path $client 'MinecraftLauncher.exe')) -eq $hostHash) 'hash differs'
Check 'the staging folder was cleaned up' (-not (Test-Path (Join-Path $client 'update-staging'))) 'staging left behind'

Start-Sleep -Seconds 12
$mine = @(Get-CimInstance Win32_Process -Filter "Name='MinecraftLauncher.exe'" -ErrorAction SilentlyContinue |
          Where-Object { $_.ExecutablePath -like "$client*" })
$watchers = @($mine | Where-Object { $_.CommandLine -like '*--watch-updates*' })
Write-Host "  client processes afterwards: total=$($mine.Count) watcher=$($watchers.Count)"
Check 'it came back up on its own' ($mine.Count -ge 1) 'nothing relaunched'

Write-Host ''
Write-Host '  update-watcher.log:'
Get-Content (Join-Path $client 'update-watcher.log') -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "     $_" }

# ---- tidy up ----
Get-Process MinecraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
foreach ($d in 'runtime', 'versions', 'servers') {
    cmd /c rmdir (Join-Path $client $d) 2>$null | Out-Null
    cmd /c rmdir (Join-Path $hostDir $d) 2>$null | Out-Null
}
Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "$pass passed, $fail failed" -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
