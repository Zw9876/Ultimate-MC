# Does pausing everyone actually close a running game, with no way to decline?
#
#   tools\Verify-Pause.ps1
#
# The claim being tested is behavioural and cannot be reached from the offline
# suite: a host sets a pause, and within a second or two every game watcher on
# the network closes the game it is watching - gently, with no countdown, and
# with no button anybody can press to stay in.
#
# HOW A GAME IS SIMULATED. The watcher is told which process to watch
# (--watch-game <pid>), and it closes that process the way clicking its X does.
# So Notepad stands in for Minecraft: a real watcher, the real poll against a
# real skin server, the real close path - just a stand-in window at the end of
# it. Launching actual Minecraft would add minutes and prove nothing extra about
# the signal, and an empty Notepad closes cleanly rather than asking to save.
#
# admin.flag has to be absent: the machine holding it is exempt from pauses by
# design, which is the opposite of what is under test here.

param(
    [int]$Port = 25567,

    # Path to an OLDER MinecraftLauncher.exe, from before pausing existed. When
    # given, this also proves what such a client does with a pause -- which is
    # nothing, and is the likeliest reason a pause looks broken on a fleet that
    # has not finished updating. Extract a previous rollout zip to get one.
    [string]$OldLauncher
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class PauseWin {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

    // Every visible window title on the desktop. Not filtered by process: the
    // paused notice belongs to the WATCHER, which is a different process from
    // the launcher, and its pid is not known to the caller up front.
    public static List<string> AllTitles() {
        var found = new List<string>();
        EnumWindows((h, p) => {
            if (!IsWindowVisible(h)) return true;
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            if (t.Length > 0) found.Add(t.ToString());
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'MinecraftLauncher.exe'
$root = [Windows.Automation.AutomationElement]::RootElement
$port = $Port

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
function WaitById($w, $id, $seconds = 10) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $e = ById $w $id
        if ($e) { return $e }
        Start-Sleep -Milliseconds 500
    }
    return $null
}
function Click($e) { $e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Pick($e) { $e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }

function WaitForTitle($title, $seconds = 20) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        if ([PauseWin]::AllTitles() -contains $title) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$pass = 0; $fail = 0
function Check($what, $ok, $detail) {
    if ($ok) { $script:pass++; Write-Output "  ok    $what" }
    else { $script:fail++; Write-Output "  FAIL  $what  -> $detail" }
}

Write-Output "verifying the pause-everyone path in $exe"

$blocks = Join-Path $repo 'play-blocks.json'
$cache  = Join-Path $repo 'play-blocks-cache.json'
$flag   = Join-Path $repo 'admin.flag'

$hadFlag = Test-Path $flag
if ($hadFlag) { Remove-Item $flag -Force; Write-Output "  (moved admin.flag aside - that machine is exempt by design)" }

$hadBlocks = Test-Path $blocks
if ($hadBlocks) { Move-Item $blocks "$blocks.verify-backup" -Force }
Remove-Item $cache -Force -ErrorAction SilentlyContinue

Get-Process MinecraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

# SKIN_SERVER= makes this launcher behave as a CLIENT of the host on this same
# machine, which is the only way to exercise the client gate here: on the host
# itself, discovery deliberately prefers a REMOTE skin server and so finds
# nothing, leaving nothing to ask about the pause. Without this the launcher
# sailed past the gate and launched, while a loose check on the word "everyone"
# matched an unrelated status message and called it a pass.
$config = Join-Path $repo 'config.txt'
$configBackup = "$config.verify-backup"
$hadConfig = Test-Path $config
if ($hadConfig) {
    Copy-Item $config $configBackup -Force
    $lines = @(Get-Content $config | Where-Object { $_ -notmatch '^SKIN_SERVER=' })
    $lines += "SKIN_SERVER=127.0.0.1:$port"
    Set-Content $config $lines -Encoding ASCII
    Write-Output "  pinned this launcher to 127.0.0.1:$port so it acts as a client"
}

# Written before the server starts, because SkinServer.Start loads it: a pause is
# persisted precisely so restarting mid-maintenance does not let everyone back in.
@"
{
  "blocks": [],
  "pause": {
    "id": "verify-$(Get-Random)",
    "reason": "verification run - planted by tools\\Verify-Pause.ps1",
    "since": "$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))",
    "by": "VERIFY-SCRIPT"
  }
}
"@ | Set-Content $blocks -Encoding ASCII

$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 16

$w = $root.FindFirst([Windows.Automation.TreeScope]::Children,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))

Check 'the launcher starts' ($null -ne $w) 'no window appeared'
if (-not $w) { exit 1 }

# ---- the host announces it ----

Pick (ByName $w 'SKINS')
Start-Sleep -Seconds 2
Click (ById $w 'SkinServerToggle')
Start-Sleep -Seconds 5

$status = (ById $w 'SkinServerStatus').Current.Name
Check 'the skin server is running' ($status -match 'running') $status

try {
    $reply = Invoke-RestMethod -Uri "http://127.0.0.1:$port/launcher/shutdown" -TimeoutSec 10
    Write-Output "  shutdown endpoint says: $($reply | ConvertTo-Json -Compress)"

    Check 'the pause is announced on the channel watchers already poll' ($reply.paused -eq $true) `
          ($reply | ConvertTo-Json -Compress)
    Check 'with an id, so a watcher acts on it once' (-not [string]::IsNullOrWhiteSpace($reply.pauseId)) 'no id'
    Check 'and the reason to show the player' ($reply.pauseReason -match 'verification run') $reply.pauseReason

    # It must not be confused with a shutdown: that one has a countdown and can
    # be declined, which is exactly what a pause must not have.
    Check 'it is NOT announced as a shutdown' ($reply.shutdown -ne $true) `
          'the pause came through as a shutdown, which can be declined'
} catch {
    Check 'the pause is announced on the channel watchers already poll' $false $_.Exception.Message
}

# ---- a running game is closed ----

# NOT notepad. On Windows 11 it is a Store app, so Start-Process -PassThru hands
# back a stub whose pid dies at once and the real window belongs to another
# process -- the watcher then found nothing to watch and exited, and the two
# checks below "passed" against nothing running. A window this script owns
# outright avoids all of that: real window, known pid, closes on WM_CLOSE.
$standInCode = @'
Add-Type -AssemblyName System.Windows.Forms
$f = New-Object System.Windows.Forms.Form
$f.Text = "Stand-in for Minecraft"
$f.Width = 420; $f.Height = 160
$l = New-Object System.Windows.Forms.Label
$l.Text = "Pretending to be a running game."
$l.AutoSize = $true; $l.Left = 20; $l.Top = 40
$f.Controls.Add($l)
[System.Windows.Forms.Application]::Run($f)
'@

# Written to a file and run with -File. A multi-line script passed as a single
# -Command argument through Start-Process does not survive the argument parsing,
# and the symptom is simply no window.
$standInFile = Join-Path $env:TEMP "mc-standin-$PID.ps1"
Set-Content $standInFile $standInCode -Encoding ASCII

$stand = Start-Process powershell -PassThru -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $standInFile)

# Waited for by its window, not by its pid: a process exists the instant it is
# started, and this needs a window the watcher can actually close.
$standUp = WaitForTitle 'Stand-in for Minecraft' 20
Check 'a stand-in game is running, with a window' $standUp 'the stand-in never showed a window'

if (-not $standUp) {
    Write-Output '  SKIPPING the close checks -- with nothing running they would pass on nothing,'
    Write-Output '  which is exactly how this script reported success the first time.'
} else {
    # A real watcher, pointed at the stand-in and at this host.
    $watcher = Start-Process $exe -PassThru -ArgumentList @(
        '--watch-game', $stand.Id, '--host', "127.0.0.1:$port", '--mc', '26.1.2')

    Write-Output "  started a real game watcher (pid $($watcher.Id)) against the stand-in"

    # The notice belongs to the watcher process, and appears at the same moment the
    # close starts rather than after it: the point is that the player knows why,
    # not that they get asked.
    $notice = WaitForTitle 'Playing is paused' 25
    Check 'the player is told why, in a window of its own' $notice 'no window titled "Playing is paused"'

    # There must be nothing to decline. The shutdown countdown offers "Keep
    # playing"; this must not, or one click defeats the whole feature.
    Check 'it is not the dismissible countdown' `
          (-not ([PauseWin]::AllTitles() -contains 'Minecraft is closing')) `
          'the countdown window appeared instead, and that one can be declined'

    $closed = $false
    for ($i = 0; $i -lt 90; $i++) {
        Start-Sleep -Milliseconds 500
        if (-not (Get-Process -Id $stand.Id -ErrorAction SilentlyContinue)) { $closed = $true; break }
    }
    Check 'the running game was closed' $closed 'the stand-in is still running after 45s'

    # Gently, not killed: the window went away on its own request, which is what
    # saves a singleplayer world. A killed process leaves no such chance.
    Check 'and its window is gone with it' `
          (-not ([PauseWin]::AllTitles() -contains 'Stand-in for Minecraft')) 'the window is still up'

    # The watcher gets out of the way afterwards, because while one is alive
    # Windows keeps the exe locked and a self-update cannot replace it -- which is
    # one of the reasons for pausing in the first place.
    $watcherGone = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if (-not (Get-Process -Id $watcher.Id -ErrorAction SilentlyContinue)) { $watcherGone = $true; break }
    }
    Check 'the watcher then exits, releasing the exe for an update' $watcherGone `
          'the watcher is still running, so a self-update would be blocked'
}

# ---- what an OLD client does with the same pause ----
#
# The case that matters in the field, and the one easy to mistake for a broken
# feature: a launcher built before pausing existed polls the same endpoint, does
# not understand the "paused" field, and carries on. The game keeps running and
# the host has no idea.

if (-not $OldLauncher) {
    Write-Output ''
    Write-Output '  (no -OldLauncher given, so the version-floor case is not covered here.'
    Write-Output '   Extract a previous rollout zip and pass its MinecraftLauncher.exe to'
    Write-Output '   prove what an un-updated machine does with a pause.)'
}
elseif (-not (Test-Path $OldLauncher)) {
    Check 'the older launcher exists' $false $OldLauncher
}
else {
    $oldVersion = (Get-Item $OldLauncher).VersionInfo.FileVersion
    Write-Output ''
    Write-Output ("  comparing against an older client: " + $oldVersion)

    $standIn2 = Join-Path $env:TEMP "mc-standin-old-$PID.ps1"
    ($standInCode -replace 'Stand-in for Minecraft', 'Stand-in on an OLD client') |
        Set-Content $standIn2 -Encoding ASCII

    $oldStand = Start-Process powershell -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $standIn2)

    $oldUp = WaitForTitle 'Stand-in on an OLD client' 20
    Check 'a stand-in game is running for the old client' $oldUp 'no window'

    if ($oldUp) {
        $oldWatcher = Start-Process $OldLauncher -PassThru -ArgumentList @(
            '--watch-game', $oldStand.Id, '--host', "127.0.0.1:$port")

        Write-Output ("  started the OLD watcher (pid " + $oldWatcher.Id + ") against the same paused host")

        # Longer than the current watcher needed, which closed its game well
        # inside this.
        Start-Sleep -Seconds 25

        $survived = $null -ne (Get-Process -Id $oldStand.Id -ErrorAction SilentlyContinue)

        # This check PASSING means the explanation is understood, not that all is
        # well: an old client is expected to ignore the pause.
        Check 'an old client ignores the pause, as expected' $survived `
              'the old client closed the game, so the version floor is not what we think'

        Check 'and it shows no paused notice' `
              (-not ([PauseWin]::AllTitles() -contains 'Playing is paused')) `
              'it somehow showed the notice'

        Write-Output ''
        Write-Output '  SO: a machine on a build older than the pause feature plays straight'
        Write-Output ("     through it. " + $oldVersion + " does not understand the 'paused'")
        Write-Output '     field at all. Nothing is wrong with the host; the client needs the'
        Write-Output '     build, and a game already running needs restarting even after it'
        Write-Output '     updates, because its watcher is the old process.'

        Stop-Process -Id $oldWatcher.Id -Force -ErrorAction SilentlyContinue
        Stop-Process -Id $oldStand.Id -Force -ErrorAction SilentlyContinue
    }

    Remove-Item $standIn2 -Force -ErrorAction SilentlyContinue
}

# ---- and nobody can start a game while it lasts ----

# Back to the Client tab first: WPF drops a collapsed panel out of the UI
# Automation tree, so while the Skins tab is showing there is no PlayButton to
# find. Looking for it there returned null and the whole section skipped in
# silence, which reads exactly like a section that passed.
Pick (ByName $w 'CLIENT')
Start-Sleep -Seconds 2

$play = WaitById $w 'PlayButton'
Check 'the PLAY button can be reached' ($null -ne $play) 'PlayButton not in the tree'

if ($play) {
    $javaBefore = @(Get-Process java, javaw -ErrorAction SilentlyContinue).Count
    Click $play

    $gate = WaitForTitle 'Playing is paused' 20
    Check 'pressing PLAY is refused while paused' $gate 'no pause dialog on PLAY'

    if ($gate) {
        $shell = New-Object -ComObject WScript.Shell
        $null = $shell.AppActivate($p.Id); Start-Sleep -Milliseconds 500; $shell.SendKeys('{ENTER}')
        Start-Sleep -Seconds 3
    }

    $clientStatus = (ById $w 'ClientStatus').Current.Name
    Write-Output "  launcher says: $clientStatus"

    # Matched on the actual wording, not on a word that appears in it. "everyone"
    # also occurs in the message about hosting the skin server, and matching that
    # reported a pass for a launch that went ahead.
    Check 'the launcher says playing is paused for everyone' `
          ($clientStatus -match 'paused for everyone') $clientStatus

    # The assertion that cannot be fooled by wording: it must not have launched.
    Check 'it did NOT launch the game' ($clientStatus -notmatch 'Game launched') $clientStatus

    Start-Sleep -Seconds 3
    $javaAfter = @(Get-Process java, javaw -ErrorAction SilentlyContinue).Count
    Check 'and no Java process started' ($javaAfter -le $javaBefore) "java went from $javaBefore to $javaAfter"
}

# ---- tidy up ----

Get-Process MinecraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process notepad, java, javaw -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Remove-Item $blocks -Force -ErrorAction SilentlyContinue
Remove-Item $cache -Force -ErrorAction SilentlyContinue
if ($standInFile) { Remove-Item $standInFile -Force -ErrorAction SilentlyContinue }
if ($hadConfig) { Move-Item $configBackup $config -Force; Write-Output "  (restored config.txt)" }
if ($hadBlocks) { Move-Item "$blocks.verify-backup" $blocks -Force }
if ($hadFlag) { New-Item -ItemType File $flag | Out-Null; Write-Output "  (put admin.flag back)" }

$errors = Join-Path $repo 'launcher_errors.txt'
if (Test-Path $errors) {
    Write-Output ''
    Write-Output '  launcher_errors.txt exists - read it before concluding anything else:'
    Get-Content $errors | Select-String -Pattern '^\[20' | Select-Object -Last 3 |
        ForEach-Object { Write-Output "    $_" }
    $fail++
} else {
    $pass++
    Write-Output '  ok    nothing was written to launcher_errors.txt'
}

Write-Output ''
Write-Output "$pass passed, $fail failed"
exit $(if ($fail -gt 0) { 1 } else { 0 })
