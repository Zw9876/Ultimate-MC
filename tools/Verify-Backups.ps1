# Do the world backup buttons actually produce a usable zip?
#
#   tools\Verify-Backups.ps1
#
# Driven through the real launcher, because the thing worth checking is not that
# ZipFile works - the offline suite covers that - but that the two buttons reach
# the right worlds and that what lands on disk opens again.
#
# The client path is exercised against a throwaway world placed in the real saves
# folder and removed afterwards. The server path is exercised against whichever
# server world is smallest, since one of the real ones is 110 MB and this script
# should not spend minutes or gigabytes proving a point.
#
# Nothing here opens a MessageBox: UI Automation cannot see one, the modal loop
# keeps pumping, and a status read then returns the text from before the dialog
# appeared. That cost a whole session once. Delete is therefore not tested here.

param(
    # Off by default: the real overworld is 110 MB and this script should stay quick.
    # Pass it to prove the large path and to leave a genuine backup behind -- which is
    # worth doing before pre-generating, since Chunky rewrites every region file.
    [switch]$IncludeServerWorld
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'MinecraftLauncher.exe'
$root = [Windows.Automation.AutomationElement]::RootElement
$backups = Join-Path $repo 'backups'

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

$pass = 0; $fail = 0
function Check($what, $ok, $detail) {
    if ($ok) { $script:pass++; Write-Output "  ok    $what" }
    else { $script:fail++; Write-Output "  FAIL  $what  -> $detail" }
}

Write-Output "verifying world backups in $exe"

# -- a throwaway single-player world ------------------------------

$version = (Get-ChildItem (Join-Path $repo 'versions') -Directory | Select-Object -First 1).Name
$saves = Join-Path $repo "versions\$version\saves"
$fake = Join-Path $saves 'Backup Test World'

New-Item -ItemType Directory -Force (Join-Path $fake 'region') | Out-Null
Set-Content (Join-Path $fake 'level.dat') 'pretend level data' -NoNewline
Set-Content (Join-Path $fake 'region\r.0.0.mca') ('x' * 4096) -NoNewline
Set-Content (Join-Path $fake 'session.lock') 'held' -NoNewline
Write-Output "  made a throwaway world in $version"

# Anything already here would make 'a new zip appeared' meaningless.
$before = @()
if (Test-Path $backups) { $before = (Get-ChildItem $backups -Filter *.zip).Name }

Get-Process MinecraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 16

$w = $root.FindFirst([Windows.Automation.TreeScope]::Children,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))

Check 'the launcher starts' ($null -ne $w) 'no window appeared'
if (-not $w) { Remove-Item $fake -Recurse -Force; exit 1 }

# -- the client tab -----------------------------------------------

Pick (ByName $w 'CLIENT')
Start-Sleep -Seconds 2

# The dialog backs up whatever the Client tab has selected, so the version has to
# be the one the throwaway world is in.
$combo = ById $w 'VersionCombo'
$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Start-Sleep -Seconds 1
$item = ByName $combo $version
if ($item) { Pick $item } else { Write-Output "  (could not select $version)" }
$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
Start-Sleep -Seconds 1

$button = ById $w 'BackupClientButton'
Check 'the Client tab has a backup button' ($null -ne $button) 'not found'
if (-not $button) { Stop-Process -Id $p.Id -Force; Remove-Item $fake -Recurse -Force; exit 1 }

Click $button
Start-Sleep -Seconds 3

$dlg = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, 'Back up worlds')))

Check 'the backup window opens' ($null -ne $dlg) 'no window named "Back up worlds"'
if (-not $dlg) { Stop-Process -Id $p.Id -Force; Remove-Item $fake -Recurse -Force; exit 1 }

$detail = (WaitById $dlg 'WorldDetail').Current.Name
$flat = $detail -replace "`r`n", ' | ' -replace "`n", ' | '
Write-Output "  world detail: $flat"

Check 'it found the throwaway world' ($detail -match 'Backup Test World') $detail
Check 'it says how big the world is' ($detail -match 'KB|MB|bytes') $detail
Check 'and how much room is left' ($detail -match 'free on the drive') $detail

Click (ById $dlg 'BackupButton')

# Small world, but give it room to finish.
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $status = (ById $dlg 'StatusLabel').Current.Name
    if ($status -match 'Backed up|failed|Not enough') { break }
}

Write-Output "  status: $status"
Check 'the backup reports success' ($status -match 'Backed up') $status
Check 'and says where it went' ($status -match 'backups') $status

Click (ById $dlg 'CloseButton')
Start-Sleep -Seconds 2

# -- what landed on disk ------------------------------------------

$after = @((Get-ChildItem $backups -Filter *.zip -ErrorAction SilentlyContinue).Name)

# @() matters: with exactly one new file the pipeline yields a bare string, and
# $new[0] then indexes into the characters of the file name rather than the list.
$new = @($after | Where-Object { $before -notcontains $_ })

Check 'a new zip appeared' ($new.Count -ge 1) "before $($before.Count), after $($after.Count)"

if ($new.Count -ge 1) {
    $zip = Join-Path $backups $new[0]
    Write-Output "  wrote: $($new[0]) ($([math]::Round((Get-Item $zip).Length / 1KB)) KB)"

    Check 'the name carries the world and the version' `
          ($new[0] -match 'Backup_Test_World' -and $new[0] -match [regex]::Escape($version)) $new[0]

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Opening it is the check. An unconditional 'ok' here would pass even when the
    # archive could not be read at all, and then every check below it passes on an
    # empty list -- which is how a broken backup reports success.
    $archive = $null
    try { $archive = [System.IO.Compression.ZipFile]::OpenRead($zip) } catch { }

    Check 'the zip opens again' ($null -ne $archive) 'could not be opened'

    if ($archive) {
        try {
            $names = @($archive.Entries | ForEach-Object { $_.FullName })
            Check 'level.dat is in it' ($names -contains 'level.dat') ($names -join ', ')
            Check 'so is the region file' ($names -contains 'region/r.0.0.mca') ($names -join ', ')

            # The one exclusion. A restored stale lock is how "someone else is playing
            # in this world" appears out of nowhere.
            Check 'session.lock was left out' `
                  ($names.Count -gt 0 -and -not ($names -match 'session\.lock')) ($names -join ', ')

            $entry = $archive.GetEntry('level.dat')
            $reader = New-Object System.IO.StreamReader($entry.Open())
            $content = $reader.ReadToEnd()
            $reader.Dispose()
            Check 'and the contents survived' ($content -eq 'pretend level data') $content
        } finally {
            $archive.Dispose()
        }
    }
}

# -- the server tab -----------------------------------------------

Pick (ByName $w 'SERVER')
Start-Sleep -Seconds 2

$svButton = ById $w 'BackupServerButton'
Check 'the Server tab has a backup button' ($null -ne $svButton) 'not found'

if ($svButton) {
    Click $svButton
    Start-Sleep -Seconds 3

    $dlg2 = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::NameProperty, 'Back up worlds')))

    Check 'the server backup window opens' ($null -ne $dlg2) 'no window'

    if ($dlg2) {
        $intro = (WaitById $dlg2 'IntroLabel').Current.Name
        $flatIntro = $intro -replace "`r`n", ' | ' -replace "`n", ' | '
        Write-Output "  server intro: $flatIntro"

        # Either a world and the clean-copy wording, or an honest "no world yet".
        Check 'it describes the server world situation' `
              ($intro -match 'not running|running|no world') $intro

        $svDetail = (ById $dlg2 'WorldDetail').Current.Name
        $flatSv = $svDetail -replace "`r`n", ' | ' -replace "`n", ' | '
        Write-Output "  server world: $flatSv"

        if ($IncludeServerWorld) {
            Write-Output "  backing up the real server world (this takes a while)..."
            $started = Get-Date
            Click (ById $dlg2 'BackupButton')

            # A 110 MB world of region files; generous, and it reports progress.
            $svStatus = ''
            for ($i = 0; $i -lt 600; $i++) {
                Start-Sleep -Seconds 1
                $svStatus = (ById $dlg2 'StatusLabel').Current.Name
                if ($svStatus -match 'Backed up|failed|Not enough') { break }
            }

            $took = [math]::Round(((Get-Date) - $started).TotalSeconds)
            Write-Output "  status after ${took}s: $svStatus"
            Check 'the real server world backs up' ($svStatus -match 'Backed up') $svStatus

            $zips = @(Get-ChildItem $backups -Filter '*world*.zip' -ErrorAction SilentlyContinue |
                      Sort-Object LastWriteTime -Descending)
            Check 'it produced a zip' ($zips.Count -ge 1) 'none found'

            if ($zips.Count -ge 1) {
                $big = $zips[0]
                Write-Output ("  wrote {0}: {1} MB" -f $big.Name, [math]::Round($big.Length / 1MB, 1))

                Check 'the zip is a plausible size for the world' ($big.Length -gt 1MB) $big.Length

                $a = $null
                try { $a = [System.IO.Compression.ZipFile]::OpenRead($big.FullName) } catch { }
                Check 'the real backup opens again' ($null -ne $a) 'could not be opened'

                if ($a) {
                    try {
                        $n = @($a.Entries | ForEach-Object { $_.FullName })
                        Write-Output "  it holds $($n.Count) files"
                        Check 'level.dat is in the real backup' ($n -contains 'level.dat') 'missing'
                        Check 'and the region data' (($n -match '\.mca$').Count -gt 0) 'no region files'
                        Check 'session.lock is not' (-not ($n -match 'session\.lock')) 'it was included'
                    } finally { $a.Dispose() }
                }

                Write-Output "  KEEPING this one on purpose: it is a real backup of the real world."
                $script:keepBig = $big.Name
            }
        }

        Click (ById $dlg2 'CloseButton')
        Start-Sleep -Seconds 1
    }
}

# -- tidy up ------------------------------------------------------

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Remove-Item $fake -Recurse -Force -ErrorAction SilentlyContinue
foreach ($n in $new) { Remove-Item (Join-Path $backups $n) -Force -ErrorAction SilentlyContinue }
if ((Test-Path $backups) -and -not (Get-ChildItem $backups)) { Remove-Item $backups -Force }
Write-Output "  cleaned up the throwaway world and its backup"

$errors = Join-Path $repo 'launcher_errors.txt'
if (Test-Path $errors) {
    Write-Output ""
    Write-Output "  launcher_errors.txt exists - a dialog UIA cannot see may have appeared:"
    Get-Content $errors | Select-String -Pattern '^\[20' | Select-Object -Last 3 | ForEach-Object { Write-Output "    $_" }
    $fail++
} else {
    $pass++
    Write-Output "  ok    nothing was written to launcher_errors.txt"
}

Write-Output ""
Write-Output "$pass passed, $fail failed"
exit $(if ($fail -gt 0) { 1 } else { 0 })
