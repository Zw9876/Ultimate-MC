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
$blocksPath = Join-Path $repo 'play-blocks.json'
$policyPath = Join-Path $repo 'loader-policy.json'
if (Test-Path $rosterPath) { Remove-Item $rosterPath -Force }
if (Test-Path $inboxPath)  { Remove-Item $inboxPath -Recurse -Force }
if (Test-Path $blocksPath) { Remove-Item $blocksPath -Force }
$hadPolicy = Test-Path $policyPath
if ($hadPolicy) { Move-Item $policyPath "$policyPath.verify-backup" -Force }

# A machine last seen five days ago, on a build far below both floors. Written
# before the launcher opens so it is genuinely older than this session -- the
# endpoint stamps its own timestamp, so it cannot be planted through a check-in.
$ancient = (Get-Date).ToUniversalTime().AddDays(-5).ToString('yyyy-MM-ddTHH:mm:ssZ')
@"
{
  "machines": [
    { "machine": "GONE-LAST-WEEK", "username": "Nobody", "launcher": "1.2.265.376",
      "seen": "$ancient" }
  ]
}
"@ | Set-Content $rosterPath -Encoding ASCII
Write-Output "  pre-seeded a machine last seen 5 days ago on 1.2.265.376"

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
    # Three, not two: the two that checked in plus the one pre-seeded from last
    # week. Asserted by name rather than by count, so pre-seeding more later
    # cannot quietly turn this into a check of nothing.
    $names = @($roster.machines | ForEach-Object { $_.machine })
    Check 'both machines that checked in are in it' `
          (($names -contains 'TEST-BENCH-01') -and ($names -contains 'TEST-BENCH-02')) ($names -join ', ')
    Check 'and the pre-seeded old one is kept rather than discarded' `
          ($names -contains 'GONE-LAST-WEEK') ($names -join ', ')

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

# ---- the block list, served to clients -------------------------

try {
    $b = Invoke-RestMethod -Uri "$base/launcher/blocks" -TimeoutSec 10
    Check 'the block endpoint answers' $true ''
    Check 'with nothing blocked yet' (@($b.blocks).Count -eq 0) (@($b.blocks).Count)
} catch {
    Check 'the block endpoint answers' $false $_.Exception.Message
}

# Written as the Admin tab writes it. The dialog itself is not driven here: it
# confirms with a MessageBox, which UI Automation cannot see (HANDOFF 13).
$soon = (Get-Date).ToUniversalTime().AddHours(3).ToString('yyyy-MM-ddTHH:mm:ssZ')
$past = (Get-Date).ToUniversalTime().AddHours(-2).ToString('yyyy-MM-ddTHH:mm:ssZ')
@"
{
  "blocks": [
    { "machine": "TEST-BENCH-01", "guid": "abc-123", "username": "Steve",
      "reason": "verification run", "since": "$past", "until": "$soon", "by": "VERIFY" },
    { "machine": "TEST-BENCH-09", "username": "Gone",
      "reason": "this one has run out", "since": "$past", "until": "$past", "by": "VERIFY" }
  ]
}
"@ | Set-Content $blocksPath -Encoding ASCII

try {
    $b = Invoke-RestMethod -Uri "$base/launcher/blocks" -TimeoutSec 10
    $names = @($b.blocks | ForEach-Object { $_.machine })
    Write-Output "  blocks served: $($names -join ', ')"

    Check 'a live block is served to clients' ($names -contains 'TEST-BENCH-01') ($names -join ', ')
    Check 'the reason travels with it' (@($b.blocks | Where-Object { $_.machine -eq 'TEST-BENCH-01' }).reason -eq 'verification run') 'reason missing'
    Check 'so does the machine identifier' (@($b.blocks | Where-Object { $_.machine -eq 'TEST-BENCH-01' }).guid -eq 'abc-123') 'guid missing'

    # Expired blocks are dropped on the way out and the file rewritten, so a
    # timed block tidies itself up without the host having to notice.
    Check 'an expired block is not served' (-not ($names -contains 'TEST-BENCH-09')) ($names -join ', ')

    $onDisk = @((Get-Content $blocksPath -Raw | ConvertFrom-Json).blocks | ForEach-Object { $_.machine })
    Check 'and it was removed from the file too' (-not ($onDisk -contains 'TEST-BENCH-09')) ($onDisk -join ', ')
} catch {
    Check 'a live block is served to clients' $false $_.Exception.Message
}

# ---- the Fabric loader policy ----------------------------------

try {
    $lp = Invoke-RestMethod -Uri "$base/launcher/loaders" -TimeoutSec 10
    Check 'the loader policy endpoint answers' $true ''

    # Off and empty to begin with: publishing a build must never be able to start
    # installing loaders on twenty machines because a default was wrong.
    Check 'it starts switched off' ($lp.enabled -ne $true) ($lp | ConvertTo-Json -Compress)
    Check 'and with nothing pinned' (@($lp.targets).Count -eq 0) (@($lp.targets).Count)
} catch {
    Check 'the loader policy endpoint answers' $false $_.Exception.Message
}

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

# ---- CHECK FABRIC, for real, against Fabric's own servers ----
#
# Pressed with "Keep everyone on it" left OFF on purpose. That resolves and pins
# the latest loader but installs nothing, so this verifies the whole host-side
# path without changing what is actually installed on this machine.
$loaderBtn = ById $w 'LoaderCheckButton'
Check 'the fleet tab offers a Fabric loader check' ($null -ne $loaderBtn) 'no CHECK FABRIC button'

if ($loaderBtn) {
    $before = (ById $w 'LoaderPolicySummary').Current.Name
    Write-Output "  before: $before"

    Click $loaderBtn

    $pinned = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $st = (ById $w 'LoaderPolicyStatus').Current.Name
        if ($st -match 'latest is|could not|Nothing to pin|Gave up') { $pinned = $true; break }
    }

    $status = (ById $w 'LoaderPolicyStatus').Current.Name
    Write-Output "  status: $status"
    Check 'it answers rather than hanging' $pinned $status

    # Reached Fabric and pinned something. If Fabric is unreachable from the
    # build machine this will fail honestly rather than look like a pass.
    Check 'it reached Fabric and pinned a version' ($status -match 'latest is') $status

    if (Test-Path $policyPath) {
        $pol = Get-Content $policyPath -Raw | ConvertFrom-Json
        $targets = @($pol.targets)
        Write-Output ("  pinned: " + (($targets | ForEach-Object { "$($_.minecraft) -> $($_.loader)" }) -join ', '))

        Check 'a policy file was written' ($targets.Count -ge 1) $targets.Count
        Check 'and it is still switched off, so nothing was installed' ($pol.enabled -ne $true) $pol.enabled

        # Only versions that already have Fabric. 1.20.1 here is Vanilla + Forge
        # and must never gain a Fabric target.
        $mcs = @($targets | ForEach-Object { $_.minecraft })
        Check 'it pinned the version that has Fabric' ($mcs -contains '26.1.2') ($mcs -join ', ')
        Check 'and NOT the Forge-only one' (-not ($mcs -contains '1.20.1')) ($mcs -join ', ')

        $t = $targets | Where-Object { $_.minecraft -eq '26.1.2' } | Select-Object -First 1
        Check 'the pinned loader looks like a version' ($t.loader -match '^\d+\.\d+') $t.loader
        Check 'and it is marked for client and server' (($t.client -eq $true) -and ($t.server -eq $true)) `
              "client=$($t.client) server=$($t.server)"

        # Served to clients, which is the point of pinning it here.
        try {
            $lp2 = Invoke-RestMethod -Uri "$base/launcher/loaders" -TimeoutSec 10
            Check 'the pinned version is served to clients' (@($lp2.targets).Count -ge 1) (@($lp2.targets).Count)
        } catch {
            Check 'the pinned version is served to clients' $false $_.Exception.Message
        }
    } else {
        Check 'a policy file was written' $false 'loader-policy.json not created'
    }
}

# ---- the block dialog: geometry and name lookup -----------------
#
# Geometry is asserted, not presence. The reason box WAS in the tree and
# unreachable: the window was a fixed 560 tall with the reason card on a star
# row, so the row was squeezed, the box kept its MinHeight and overflowed, and
# the confirm button was painted on top of it. Measured at the time: box at
# y=739 h=54, button at y=732. "The control exists" would have passed.

Click (ById $w 'FleetBlockButton')
Start-Sleep -Seconds 3

$bd = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, 'Turn playing off')))

Check 'the block dialog opens' ($null -ne $bd) 'no window titled "Turn playing off"'

if ($bd) {
    $box = ById $bd 'ReasonBox'
    $chk = ById $bd 'ServerBanCheck'
    $btn = ById $bd 'BlockButton'

    Check 'it has a reason box' ($null -ne $box) 'ReasonBox not in the tree'

    if ($box -and $chk -and $btn) {
        $r = $box.Current.BoundingRectangle
        $c = $chk.Current.BoundingRectangle
        $b = $btn.Current.BoundingRectangle
        $d = $bd.Current.BoundingRectangle

        Write-Output ("  reason box: y=" + [int]$r.Y + " h=" + [int]$r.Height +
                      "  checkbox y=" + [int]$c.Y + "  button y=" + [int]$b.Y)

        Check 'the reason box has real height' ($r.Height -ge 40) $r.Height
        Check 'nothing is drawn on top of it' `
              (($c.Y -ge ($r.Y + $r.Height)) -and ($b.Y -ge ($r.Y + $r.Height))) `
              ("box bottom " + [int]($r.Y + $r.Height) + ", checkbox " + [int]$c.Y + ", button " + [int]$b.Y)
        Check 'and it fits inside the window' `
              (($r.Y + $r.Height) -le ($d.Y + $d.Height)) 'it overflows the window'

        # Typeable, not merely present.
        $vp = $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue('verification run')
        Check 'a reason can actually be typed into it' ($vp.Current.Value -eq 'verification run') $vp.Current.Value
    }

    # Asks for a PLAYER name and finds the computer itself -- nobody knows their
    # colleagues' computer names, which is what it used to ask for.
    $nameBox = ById $bd 'PlayerNameBox'
    Check 'it asks for a player name' ($null -ne $nameBox) 'PlayerNameBox not in the tree'

    if ($nameBox) {
        $nv = $nameBox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)

        $nv.SetValue('Steve')
        Start-Sleep -Seconds 1
        $note = (ById $bd 'WhoNote').Current.Name
        Write-Output ("  'Steve' -> " + $note.Substring(0, [Math]::Min(120, $note.Length)))
        Check 'a player name resolves to their computer' ($note -match 'TEST-BENCH-01') $note

        $nv.SetValue('Nobody At All')
        Start-Sleep -Seconds 1
        $note = (ById $bd 'WhoNote').Current.Name
        Check 'and an unknown name says so plainly' ($note -match 'No computer has reported') $note
        Check 'rather than appearing to work' (-not ($note -match 'TEST-BENCH')) $note
    }

    # Cancel: this must not actually block anybody.
    Click (ById $bd 'CancelButton')
    Start-Sleep -Seconds 2
}

Check 'there is a way to empty the list entirely' ($null -ne (ById $w 'FleetForgetAllButton')) 'no forget-all button'

Check 'the fleet tab offers a way to turn playing off' ($null -ne (ById $w 'FleetBlockButton')) 'no block button'
Check 'and a way to turn it back on' ($null -ne (ById $w 'FleetUnblockButton')) 'no unblock button'

# The blocked machine checked in earlier, so its row should say so rather than
# the block living only in a file nobody looks at.
Check 'the blocked machine is shown as off in the list' `
      ($summary -match 'off on 1' -or $null -ne (ByName $w 'OFF - until lifted')) $summary

# Both planted machines (1.2.265.376 and 1.2.280.260) predate play blocks, and
# one of them is blocked -- so the panel must say the block cannot reach it.
# A restriction that silently reaches nobody is worse than none, because it is
# trusted; that is exactly how a pause looked broken in the field.
$fleetNote = (ById $w 'FleetNote').Current.Name
Write-Output ("  fleet note: " + $fleetNote.Substring(0, [Math]::Min(150, $fleetNote.Length)))
Check 'the panel warns that machines are too old to obey a block' `
      ($fleetNote -match 'too old to obey') $fleetNote
Check 'and names one of them' ($fleetNote -match 'TEST-BENCH-0') $fleetNote

# The counts must be about tonight, while the row for last week is still listed.
Check 'the old machine is still listed' ($null -ne (ByName $w 'GONE-LAST-WEEK')) 'row missing'

Check 'but it is not counted in the summary' ($summary -match '2 machines') $summary
Check 'and the panel says so' ($fleetNote -match 'seen on earlier days|earlier day') $fleetNote
Check 'naming how many are excluded' ($fleetNote -match '1 more was seen') $fleetNote

# The point of all of it: an ancient machine must not drag the obedience warning
# with it. The two that checked in tonight are themselves below the block floor,
# so the warning should name THEM and not the one from last week.
Check 'the too-old warning is about tonight, not last week' `
      (-not ($fleetNote -match 'GONE-LAST-WEEK')) $fleetNote

$fleetAgain = (ById $w 'FleetSummary').Current.Name
Write-Output "  fleet summary: $fleetAgain"
Check 'the summary counts how many are turned off' ($fleetAgain -match 'off on') $fleetAgain

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
if (Test-Path $blocksPath) { Remove-Item $blocksPath -Force }
Remove-Item $policyPath -Force -ErrorAction SilentlyContinue
if ($hadPolicy) { Move-Item "$policyPath.verify-backup" $policyPath -Force }
if (-not $hadFlag) { Remove-Item $flag -Force; Write-Output "  (removed admin.flag again)" }

Write-Output ""
Write-Output "$pass passed, $fail failed"
exit $(if ($fail -gt 0) { 1 } else { 0 })
