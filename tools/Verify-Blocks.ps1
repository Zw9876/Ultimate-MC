# Does turning playing off actually stop the game starting?
#
#   tools\Verify-Blocks.ps1
#
# This is the one check in the feature that counts. Everything else - the list
# format, the matching, the expiry, the admin exemption - is covered by the
# offline suite (tools\run-tests.ps1 blocks). What cannot be covered there is
# whether a real launcher, with a real block in front of it, refuses to start a
# real game. So that is what this does, and it asserts on BEHAVIOUR: no Java
# process appeared, and the launcher is still sitting there.
#
# TWO THINGS THAT MADE THIS AWKWARD, both already written down in HANDOFF
# section 13 and both hit again here:
#
#   * UI Automation cannot see a MessageBox. The modal loop keeps pumping, so a
#     status read succeeds and hands back the text from BEFORE the dialog. The
#     block dialog is a MessageBox, so this script finds it with the Win32
#     FindWindow instead - which does see it - and dismisses it with SendKeys.
#   * The machine holding admin.flag is never blocked, by design. So this script
#     has to run the launcher with that file ABSENT, which is the opposite of
#     what Verify-Admin.ps1 needs. That is why this is its own script.
#
# It uses the CACHE rather than a running host, deliberately: an unreachable host
# honouring the last answer it was given is the behaviour that stops somebody
# unplugging the network to get round a block, so it is the case worth proving.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

# Finding the MessageBox is done by enumerating this launcher's own windows, NOT
# with FindWindow(null, title). Measured here, because the obvious call silently
# does not work: against a live block dialog,
#
#   FindWindowW(null,      "Playing is turned off on this computer")  ->  0
#   FindWindowW("#32770",  "Playing is turned off on this computer")  ->  a handle
#
# A null class name does not match it. Enumerating by process id is used instead
# because it is also strictly more correct: a global title search could match
# some other application's dialog and report a pass that never happened.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Win32 {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

    /// Titles of the visible top-level windows belonging to one process.
    public static List<string> VisibleTitles(uint want) {
        var found = new List<string>();
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != want || !IsWindowVisible(h)) return true;
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            if (t.Length > 0) found.Add(t.ToString());
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

# Waits for one of this launcher's windows to carry the given title.
# $processId, not $pid: $pid is a read-only automatic variable holding this
# process's own id, and using it as a parameter name throws on every call -- which
# then reads as "the dialog never appeared".
function WaitForDialog($processId, $title, $seconds = 15) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        foreach ($t in [Win32]::VisibleTitles([uint32]$processId)) {
            if ($t -eq $title) { return $true }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'MinecraftLauncher.exe'
$root = [Windows.Automation.AutomationElement]::RootElement

if (-not (Test-Path $exe)) { Write-Output "No launcher at $exe"; exit 2 }

function ById($w, $id) {
    $w.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
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

$pass = 0; $fail = 0
function Check($what, $ok, $detail) {
    if ($ok) { $script:pass++; Write-Output "  ok    $what" }
    else { $script:fail++; Write-Output "  FAIL  $what  -> $detail" }
}

Write-Output "verifying play blocks in $exe"

$cache = Join-Path $repo 'play-blocks-cache.json'
$flag  = Join-Path $repo 'admin.flag'
$me    = $env:COMPUTERNAME

# admin.flag has to be out of the way: that machine is never blocked, which is
# the point of the rail but the opposite of what we are testing here.
$hadFlag = Test-Path $flag
if ($hadFlag) { Remove-Item $flag -Force; Write-Output "  (moved admin.flag aside for this run)" }

$hadCache = Test-Path $cache
$cacheBackup = "$cache.verify-backup"
if ($hadCache) { Move-Item $cache $cacheBackup -Force }

Get-Process MinecraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

# What a host would have told this machine last time it could see one.
$until = (Get-Date).ToUniversalTime().AddHours(2).ToString('yyyy-MM-ddTHH:mm:ssZ')
@"
{
  "blocks": [
    {
      "machine": "$me",
      "username": "Zach",
      "reason": "verification run - this block was planted by tools\\Verify-Blocks.ps1",
      "since": "$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))",
      "until": "$until",
      "by": "VERIFY-SCRIPT"
    }
  ]
}
"@ | Set-Content $cache -Encoding ASCII

Write-Output "  planted a block for this computer ($me), expiring $until"

$javaBefore = @(Get-Process java, javaw -ErrorAction SilentlyContinue).Count

$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 16

$w = $root.FindFirst([Windows.Automation.TreeScope]::Children,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))

Check 'the launcher starts' ($null -ne $w) 'no window appeared'
if (-not $w) { exit 1 }

# A blocked machine must still be able to OPEN the launcher and read why. If it
# could not, somebody would conclude the launcher is broken and set about fixing
# it, which is the opposite of what a visible block is for.
Check 'a blocked machine can still open the launcher' $true ''

$play = WaitById $w 'PlayButton'
Check 'the PLAY button is there' ($null -ne $play) 'not found'
if (-not $play) { Stop-Process -Id $p.Id -Force; exit 1 }

$statusBefore = (ById $w 'ClientStatus').Current.Name

Click $play

# The dialog is a MessageBox, so UIA will not find it.
$dialog = WaitForDialog $p.Id 'Playing is turned off on this computer'

Check 'pressing PLAY puts a dialog up' $dialog `
      'no window titled "Playing is turned off on this computer"'

# Dismiss it. ENTER takes the default on an OK box; see HANDOFF section 13 for
# why {ESC} is not used (it does nothing on a Yes/No box, and that cost a
# session once).
if ($dialog) {
    $shell = New-Object -ComObject WScript.Shell
    $null = $shell.AppActivate($p.Id)
    Start-Sleep -Milliseconds 500
    $shell.SendKeys('{ENTER}')
    Start-Sleep -Seconds 2
}

# ---- the assertions that actually matter, on behaviour not on text ----

$stillOpen = $null -ne (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)
Check 'the launcher did NOT close, so it never got to launching' $stillOpen `
      'the launcher closed, which is what it does after a successful PLAY'

Start-Sleep -Seconds 3
$javaAfter = @(Get-Process java, javaw -ErrorAction SilentlyContinue).Count
Check 'no Java process started' ($javaAfter -le $javaBefore) `
      "java processes went from $javaBefore to $javaAfter"

if ($stillOpen) {
    $statusAfter = (ById $w 'ClientStatus').Current.Name
    Write-Output "  status now: $statusAfter"
    Check 'the launcher says playing is turned off' ($statusAfter -match 'turned off') $statusAfter
    Check 'and that is not just the text it had before' ($statusAfter -ne $statusBefore) `
          "unchanged: '$statusBefore'"
}

# ---- an expired block must not stop anybody ----

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

$expired = (Get-Date).ToUniversalTime().AddHours(-1).ToString('yyyy-MM-ddTHH:mm:ssZ')
@"
{
  "blocks": [
    {
      "machine": "$me",
      "reason": "already finished",
      "since": "$((Get-Date).ToUniversalTime().AddDays(-1).ToString('yyyy-MM-ddTHH:mm:ssZ'))",
      "until": "$expired",
      "by": "VERIFY-SCRIPT"
    }
  ]
}
"@ | Set-Content $cache -Encoding ASCII

$p2 = Start-Process $exe -PassThru
Start-Sleep -Seconds 16

$w2 = $root.FindFirst([Windows.Automation.TreeScope]::Children,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $p2.Id)))

if ($w2) {
    $play2 = WaitById $w2 'PlayButton'
    Click $play2

    # Nothing should appear. Waited for as long as the real check took, so this
    # does not pass merely by being quick.
    $stray = WaitForDialog $p2.Id 'Playing is turned off on this computer' 8

    Check 'a block that has run out does not stop anybody' (-not $stray) `
          'the dialog appeared for an expired block'

    # It will have gone on to launch, so clean that up.
    if ($stray) {
        $shell = New-Object -ComObject WScript.Shell
        $null = $shell.AppActivate($p2.Id); Start-Sleep -Milliseconds 500; $shell.SendKeys('{ENTER}')
    }
    Start-Sleep -Seconds 10
}

# ---- tidy up ----

Get-Process MinecraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process java, javaw -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Remove-Item $cache -Force -ErrorAction SilentlyContinue
if ($hadCache) { Move-Item $cacheBackup $cache -Force }
if ($hadFlag) { New-Item -ItemType File $flag | Out-Null; Write-Output "  (put admin.flag back)" }

$errors = Join-Path $repo 'launcher_errors.txt'
if (Test-Path $errors) {
    Write-Output ''
    Write-Output '  launcher_errors.txt exists - read it, a binding error behind an'
    Write-Output '  invisible MessageBox looks exactly like a panel that did not render:'
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
