# Do the Admin tab, the fleet roster and the crash inbox actually work?
#
#   tools\Verify-Admin.ps1
#
# Everything here is driven through the real launcher: the Admin tab and its three
# sub-tabs are opened by clicking them, and the fleet and crash endpoints are
# exercised by posting to the running skin server exactly as another machine would.
#
# Two things this exists to catch, because neither shows up in a unit test:
#
#   * The sub-tabs use a retemplated TabControl. A broken template still compiles
#     and still passes every Core check - the panel simply comes up empty.
#   * A client's check-in and crash upload are HTTP calls between two machines.
#     The only honest test is to make the call and then look at what the host's
#     own UI shows afterwards.
#
# Reminder from a previous session, written down because it cost real time: UI
# Automation cannot see a MessageBox. The modal loop keeps pumping, so reading a
# status label still succeeds and returns the text from *before* the dialog
# appeared. Nothing here opens one.

param(
    [int]$Port = 25567
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'MinecraftLauncher.exe'
$root = [Windows.Automation.AutomationElement]::RootElement

if (-not (Test-Path $exe)) { Write-Output "No launcher at $exe"; exit 2 }

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
function Click($e) { $e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Pick($e) { $e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }

# FindFirst can answer null while the provider is mid-update - populating the fleet
# DataGrid is enough to do it, and a tab that is plainly there then looks missing.
# Retrying is a harness concern, not a product one: the element exists throughout.
function WaitByName($w, $n, $seconds = 8) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $e = ByName $w $n
        if ($e) { return $e }
        Start-Sleep -Milliseconds 500
    }
    return $null
}
function WaitById($w, $id, $seconds = 8) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $e = ById $w $id
        if ($e) { return $e }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

$pass = 0; $fail = 0
function Check($what, $ok, $detail) {
    if ($ok) { $script:pass++; Write-Output "  ok    $what" }
    else { $script:fail++; Write-Output "  FAIL  $what  -> $detail" }
}

Write-Output "verifying the Admin tab in $exe"

# admin.flag is what makes the tab exist at all. Remembered so a machine that was
# not an admin machine before this ran is not left as one.
$flag = Join-Path $repo 'admin.flag'
$hadFlag = Test-Path $flag
if (-not $hadFlag) { New-Item -ItemType File $flag | Out-Null; Write-Output "  (created admin.flag for this run)" }

# Anything left over would hold the port and make all of this meaningless.
Get-Process MinecraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

# A roster from an earlier run would make the check-in test pass without proving
# anything, so start from nothing.
$rosterPath = Join-Path $repo 'fleet.json'
$inboxPath  = Join-Path $repo 'crash-inbox'
if (Test-Path $rosterPath) { Remove-Item $rosterPath -Force }
if (Test-Path $inboxPath)  { Remove-Item $inboxPath -Recurse -Force }

$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 16

$w = $root.FindFirst([Windows.Automation.TreeScope]::Children,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))

Check 'the launcher starts' ($null -ne $w) 'no window appeared'
if (-not $w) { exit 1 }

# -- the tab exists and is reachable ------------------------------

$nav = ByName $w 'ADMIN'
Check 'the sidebar has an ADMIN entry' ($null -ne $nav) 'no ADMIN in the sidebar'
if (-not $nav) { Stop-Process -Id $p.Id -Force; exit 1 }

Pick $nav
Start-Sleep -Seconds 2

foreach ($tab in 'REQUIRED MODS', 'FLEET', 'CRASH REPORTS') {
    Check "the $tab sub-tab is there" ($null -ne (ByName $w $tab)) 'not found'
}

# -- the skin server, which is what clients talk to ---------------

Pick (ByName $w 'SKINS')
Start-Sleep -Seconds 2
Click (ById $w 'SkinServerToggle')
Start-Sleep -Seconds 5

$status = (ById $w 'SkinServerStatus').Current.Name
Check 'the skin server is running' ($status -match 'running') $status

# -- a machine checks in, exactly as another computer would -------

$base = "http://127.0.0.1:$Port"

function PostJson($path, $obj) {
    Invoke-RestMethod -Uri "$base$path" -Method Post -TimeoutSec 10 `
        -ContentType 'application/json' -Body ($obj | ConvertTo-Json -Compress)
}

try {
    $r = PostJson '/launcher/checkin' @{
        machine = 'TEST-BENCH-01'; username = 'Steve'
        launcher = '1.2.265.376'; minecraft = '26.1.2'; loader = 'Fabric'; playing = $true
    }
    Check 'a check-in is accepted' ($r.ok -eq $true) ($r | ConvertTo-Json -Compress)
} catch {
    Check 'a check-in is accepted' $false $_.Exception.Message
}

try {
    PostJson '/launcher/checkin' @{
        machine = 'TEST-BENCH-02'; username = 'Alex'
        launcher = '1.2.280.260'; playing = $false
    } | Out-Null
    Check 'a second machine is accepted' $true ''
} catch {
    Check 'a second machine is accepted' $false $_.Exception.Message
}

# Rubbish must be refused rather than stored.
#
# The status code is checked, not merely that it threw. An earlier version of this
# script "passed" here while the server was not running at all - a connection error
# is also an exception, and a check that cannot tell the two apart is worse than no
# check, because it reports success for a server that answered nothing.
function Refused($what, $uri, $type, $body) {
    try {
        Invoke-RestMethod -Uri $uri -Method Post -TimeoutSec 10 -ContentType $type -Body $body | Out-Null
        Check $what $false 'it was accepted'
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        Check $what ($code -ge 400 -and $code -lt 500) $(if ($code) { "answered $code" } else { "no answer at all: $($_.Exception.Message)" })
    }
}

Refused 'a report with no machine name is refused' `
        "$base/launcher/checkin" 'application/json' '{"nothing":"useful"}'

Start-Sleep -Seconds 1
Check 'the roster is written beside the launcher' (Test-Path $rosterPath) $rosterPath

if (Test-Path $rosterPath) {
    $roster = Get-Content $rosterPath -Raw | ConvertFrom-Json
    Check 'both machines are in it' ($roster.machines.Count -eq 2) $roster.machines.Count

    $one = $roster.machines | Where-Object { $_.machine -eq 'TEST-BENCH-01' }
    Check 'the username came through' ($one.username -eq 'Steve') $one.username
    Check 'so did the launcher version' ($one.launcher -eq '1.2.265.376') $one.launcher
    Check 'and what they were playing' ($one.minecraft -eq '26.1.2') $one.minecraft
    Check 'the address was filled in by the host' ($one.address -match '\d') $one.address
    Check 'and a real launch recorded a played time' ($null -ne $one.played) 'no played time'

    $two = $roster.machines | Where-Object { $_.machine -eq 'TEST-BENCH-02' }
    Check 'merely opening the launcher is not recorded as playing' ($null -eq $two.played) $two.played
}

# -- a crash report arrives ---------------------------------------

$crash = @"
---- Minecraft Crash Report ----
// I blame Dinnerbone.

Time: 2026-10-07 20:14:03
Description: Initializing game

java.lang.RuntimeException: Could not execute entrypoint stage 'client'
	at net.fabricmc.loader.impl.FabricLoaderImpl.invokeEntrypoints(FabricLoaderImpl.java:120)

-- MOD immersive_portals --
Details:
	Mod File: /mods/immersively-vibed-portals-6.1.0.jar
	Failure message: Mod resolution failed
		Requires fabric-api 0.154.2 or later

-- System Details --
Details:
	Minecraft Version: 26.1.2
	Java Version: 25.0.1, Oracle Corporation
"@

$q = '?machine=TEST-BENCH-01&username=Steve&minecraft=26.1.2&file=crash-2026-10-07_20.14.03-client.txt'

try {
    $r = Invoke-RestMethod -Uri "$base/launcher/crash$q" -Method Post -TimeoutSec 10 `
            -ContentType 'text/plain' -Body $crash
    Check 'a crash report is accepted' ($r.ok -eq $true) ($r | ConvertTo-Json -Compress)
    Check 'and stored' ($r.stored -eq $true) ($r | ConvertTo-Json -Compress)
} catch {
    Check 'a crash report is accepted' $false $_.Exception.Message
}

# The same crash again, which is what a client does after a host that was switched
# off. It must be taken and not stored twice.
try {
    $r = Invoke-RestMethod -Uri "$base/launcher/crash$q" -Method Post -TimeoutSec 10 `
            -ContentType 'text/plain' -Body $crash
    Check 'the same report twice is not stored twice' ($r.ok -eq $true -and $r.stored -eq $false) `
          ($r | ConvertTo-Json -Compress)
} catch {
    Check 'the same report twice is not stored twice' $false $_.Exception.Message
}

Refused 'an empty report is refused' "$base/launcher/crash$q" 'text/plain' ' '

Check 'the inbox folder was created' (Test-Path $inboxPath) $inboxPath

if (Test-Path $inboxPath) {
    $texts = Get-ChildItem $inboxPath -Filter *.txt
    $sides = Get-ChildItem $inboxPath -Filter *.json
    Check 'one report is on disk' ($texts.Count -eq 1) $texts.Count
    Check 'with one sidecar' ($sides.Count -eq 1) $sides.Count

    # Byte-for-byte, so it reads here as it read there and so anything pasted into
    # a mod's issue tracker is the real thing.
    $stored = Get-Content $texts[0].FullName -Raw
    Check 'the report is stored exactly as it arrived' `
          ($stored.Replace("`r`n","`n").Trim() -eq $crash.Replace("`r`n","`n").Trim()) 'it was altered'

    $meta = Get-Content $sides[0].FullName -Raw | ConvertFrom-Json
    Check 'the sidecar says who sent it' ($meta.machine -eq 'TEST-BENCH-01' -and $meta.username -eq 'Steve') `
          "$($meta.machine)/$($meta.username)"
}

# -- and the host's own UI shows all of it ------------------------

Pick (ByName $w 'ADMIN')
Start-Sleep -Seconds 1
Pick (WaitByName $w 'FLEET')
Start-Sleep -Seconds 2

$summary = (WaitById $w 'FleetSummary').Current.Name
Write-Output "  fleet says: $summary"
Check 'the fleet panel counts both machines' ($summary -match '2 machines') $summary
Check 'and says how many are behind this build' ($summary -match 'behind') $summary

# The rows themselves, which is what the person in the room actually reads.
$list = ById $w 'FleetList'
Check 'the fleet list has rows' ($null -ne $list -and $list.FindAll([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.Condition]::TrueCondition).Count -ge 2) 'no rows'

Check 'the username is shown on the host' ($null -ne (ByName $w 'Steve')) 'Steve not displayed'
Check 'and the other machine too' ($null -ne (ByName $w 'TEST-BENCH-02')) 'TEST-BENCH-02 not displayed'

$crashTab = WaitByName $w 'CRASH REPORTS'
Check 'the crash reports tab can be reached' ($null -ne $crashTab) 'never appeared'
if (-not $crashTab) { Stop-Process -Id $p.Id -Force; exit 1 }

Pick $crashTab
Start-Sleep -Seconds 2

$noteEl = WaitById $w 'InboxNote'
Check 'the crash reports panel is shown' ($null -ne $noteEl) 'InboxNote never appeared'

$note = $noteEl.Current.Name
Write-Output "  inbox says: $note"
Check 'the inbox panel sees the report' ($note -match '1 report') $note

$explain = (WaitById $w 'InboxExplain').Current.Name
$flatExplain = $explain -replace "`r`n", ' / ' -replace "`n", ' / '
Write-Output "  explains:   $flatExplain"
Check 'it names who it came from' ($explain -match 'Steve') $explain
Check 'and explains the crash in plain words' ($explain -match 'fabric-api' -or $explain -match 'immersive') $explain

# -- tidy up ------------------------------------------------------

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if (Test-Path $rosterPath) { Remove-Item $rosterPath -Force }
if (Test-Path $inboxPath)  { Remove-Item $inboxPath -Recurse -Force }
if (-not $hadFlag) { Remove-Item $flag -Force; Write-Output "  (removed admin.flag again)" }

Write-Output ""
Write-Output "$pass passed, $fail failed"
exit $(if ($fail -gt 0) { 1 } else { 0 })
