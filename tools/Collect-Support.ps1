# Gathers everything needed to diagnose a launcher problem into ONE zip.
#
#   powershell -ExecutionPolicy Bypass -File Collect-Support.ps1
#   powershell -ExecutionPolicy Bypass -File Collect-Support.ps1 -Root D:\Ultimate-MC-1
#
# Normally started by double-clicking Collect-Support.cmd, which is the only way
# a .ps1 runs on double-click under default Windows settings.
#
# WHY THIS EXISTS. Diagnosing a problem on a machine I cannot see has meant
# asking for one file, waiting, then asking for another - and the asking is the
# slow part, not the reading. This takes one pass and collects the lot.
#
# IT CHANGES NOTHING. Every operation is a read or a copy into its own new
# folder. It starts nothing, stops nothing, installs nothing, and needs no
# administrator.
#
# WHAT IT DELIBERATELY DOES NOT TAKE, so this can be run without thinking about
# it:
#
#   saves\          worlds. Never. Not a sample, not a listing of their names.
#   screenshots\    pictures people took.
#   resourcepacks\  not needed to diagnose anything, and often large.
#   assets\ libraries\   hundreds of MB of files identical on every machine.
#   the launcher exe itself   126 MB, and I already have every build of it.
#
# It DOES take config.txt, which holds launcher settings including the Minecraft
# name last used on this machine. That is the point - it identifies the machine -
# but it is named here so nobody is surprised by it.

param(
    # The folder holding MinecraftLauncher.exe. Found automatically when this is
    # run from inside it or from its tools\ folder.
    [string]$Root,

    # Where to write the zip. Defaults to beside this script.
    [string]$OutDir,

    [switch]$NoPause
)

$ErrorActionPreference = 'Continue'

# ---------------------------------------------------------------- find the root
function FindRoot {
    if ($Root) {
        if (Test-Path -LiteralPath (Join-Path $Root 'MinecraftLauncher.exe')) { return (Resolve-Path $Root).Path }
        Write-Output "  -Root was given as '$Root' but there is no MinecraftLauncher.exe there."
        return $null
    }

    $here = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }

    # Beside the script, then up, then up again: covers the launcher folder, its
    # tools\ folder, and a copy one level deeper than expected.
    $tries = @($here, (Split-Path -Parent $here))
    $up2 = Split-Path -Parent (Split-Path -Parent $here)
    if ($up2) { $tries += $up2 }

    foreach ($t in $tries) {
        if ($t -and (Test-Path -LiteralPath (Join-Path $t 'MinecraftLauncher.exe'))) {
            return (Resolve-Path $t).Path
        }
    }
    return $null
}

$repo = FindRoot
if (-not $repo) {
    Write-Output ''
    Write-Output '  Could not find MinecraftLauncher.exe.'
    Write-Output ''
    Write-Output '  Put this file (and Collect-Support.cmd) in the SAME FOLDER as'
    Write-Output '  MinecraftLauncher.exe and run it again - or pass the folder:'
    Write-Output ''
    Write-Output '     Collect-Support.cmd D:\path\to\Ultimate-MC-1'
    Write-Output ''
    if (-not $NoPause) { Read-Host 'Press Enter to close' }
    exit 2
}

if (-not $OutDir) {
    # Beside the launcher, not beside this script. When started by the .cmd at
    # the launcher root this script lives in tools\, and a bundle hidden in
    # there is a bundle nobody finds.
    $OutDir = $repo
}

$stamp   = Get-Date -Format 'yyyyMMdd-HHmm'
$name    = "Ultimate-MC-support-$env:COMPUTERNAME-$stamp"
$work    = Join-Path $OutDir $name
$filesIn = Join-Path $work 'files'
$zipPath = Join-Path $OutDir "$name.zip"

New-Item -ItemType Directory -Force -Path $filesIn | Out-Null

# ---------------------------------------------------------------- recorder
$report = New-Object System.Collections.Generic.List[string]
function Say($text = '') {
    if ($null -eq $text) { $text = '' }
    foreach ($line in ([string]$text) -split "`r?`n") {
        $report.Add($line)
        Write-Output $line
    }
}

# Printed to the screen only. Keeps the progress chatter out of the report.
function Step($text) { Write-Output "  $text" }

# First 16 characters of a timestamp, or a placeholder. Exists so the fleet rows
# below need no conditional inside a format string.
function Short16($value, $whenEmpty) {
    $s = '' + $value
    if ([string]::IsNullOrWhiteSpace($s)) { return $whenEmpty }
    if ($s.Length -gt 16) { return $s.Substring(0, 16) }
    return $s
}

function Safe($block, $label) {
    try { & $block } catch { Say "  (could not read $label - $($_.Exception.Message))" }
}

# Copies a file into the bundle, capped, and says what happened.
function Grab($path, $as, $maxKB = 2048) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try {
        $item = Get-Item -LiteralPath $path
        $dest = Join-Path $filesIn $as
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null

        if ($item.Length -gt ($maxKB * 1KB)) {
            # Tail rather than skip: the end of a log is the part that matters,
            # and a 40 MB latest.log is not worth posting around.
            $keep = Get-Content -LiteralPath $path -Tail 400 -ErrorAction Stop
            Set-Content -LiteralPath $dest -Value `
                (@("[TRUNCATED - original $([math]::Round($item.Length/1KB))KB, last 400 lines kept]") + $keep) `
                -Encoding UTF8
            Say ("  kept (tail only) {0,-52} {1} KB original" -f $as, [math]::Round($item.Length/1KB))
        } else {
            Copy-Item -LiteralPath $path -Destination $dest -Force
            Say ("  kept {0,-63} {1} KB" -f $as, [math]::Round($item.Length/1KB))
        }
        return $true
    } catch {
        Say "  (could not copy $as - $($_.Exception.Message))"
        return $false
    }
}

# ================================================================= the report
Say '================================================================'
Say ' ULTIMATE-MC SUPPORT BUNDLE'
Say '================================================================'
Say ''
Say '---- WHERE THIS CAME FROM ----'
Say "  Computer   : $env:COMPUTERNAME"
Say "  User       : $env:USERNAME"
Say "  When       : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')"
Say "  Windows    : $([Environment]::OSVersion.Version)"
Say "  PowerShell : $($PSVersionTable.PSVersion) on .NET $([Environment]::Version)"
Say "  Launcher at: $repo"
Safe {
    if ($PSCommandPath -and (Test-Path -LiteralPath $PSCommandPath)) {
        $h = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
        Say "  Collector  : $(Split-Path -Leaf $PSCommandPath) SHA256 $($h.Substring(0,16))"
    }
} 'this script'
Say ''

# ---------------------------------------------------------------- the launcher
Step 'reading the launcher...'
Say '---- THE LAUNCHER ON THIS MACHINE ----'
Safe {
    $exe = Get-Item -LiteralPath (Join-Path $repo 'MinecraftLauncher.exe')
    Say "  version    : $($exe.VersionInfo.FileVersion)"
    Say "  size       : $([math]::Round($exe.Length/1MB,1)) MB"
    Say "  modified   : $($exe.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))"
    Say "  sha256     : $((Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash.Substring(0,16))"

    # Self-contained or not decides whether this copy can serve updates at all.
    $fwDep = @('MinecraftLauncher.dll', 'MinecraftLauncher.runtimeconfig.json') |
             Where-Object { Test-Path -LiteralPath (Join-Path $repo $_) }
    if ($fwDep.Count -gt 0) {
        Say "  NOTE: $($fwDep -join ', ') present - this looks framework-dependent,"
        Say '        which cannot serve updates to other machines.'
    } else {
        Say '  self-contained: yes (no .dll/.runtimeconfig.json beside it)'
    }
} 'the launcher'

$flag = Join-Path $repo 'admin.flag'
if (Test-Path -LiteralPath $flag) {
    Say ''
    Say '  *** admin.flag IS PRESENT ON THIS MACHINE. ***'
    Say '      If this is not the host, it should not be here: it unlocks the'
    Say '      Admin tab and exempts the machine from pauses and blocks.'
} else {
    Say '  admin.flag  : not present (correct for a client)'
}
Say ''

# ---------------------------------------------------------------- state files
Step 'reading launcher state...'
Say '---- LAUNCHER STATE ----'
foreach ($f in 'config.txt', 'play-blocks-cache.json', 'loader-policy-cache.json',
                'crash-sent.txt', 'required-mods-declined.txt', 'computer_uuid.dat',
                'fleet.json', 'play-blocks.json', 'loader-policy.json',
                'download-hosts-report.txt') {
    $p = Join-Path $repo $f
    if (Test-Path -LiteralPath $p) { [void](Grab $p $f) }
    else { Say ("  absent {0}" -f $f) }
}
Say ''

Say '---- config.txt, in full ----'
Safe {
    $c = Join-Path $repo 'config.txt'
    if (Test-Path -LiteralPath $c) { foreach ($l in Get-Content -LiteralPath $c) { Say "  $l" } }
    else { Say '  (none)' }
} 'config.txt'
Say ''

# fleet.json presence is itself the answer to "is this machine the host".
Safe {
    $fj = Join-Path $repo 'fleet.json'
    if (Test-Path -LiteralPath $fj) {
        $fleet = Get-Content -LiteralPath $fj -Raw | ConvertFrom-Json
        $ms = @($fleet.machines)
        Say "---- FLEET ROSTER ON THIS MACHINE ($($ms.Count) machines) ----"
        Say '  (its presence means this machine has been serving as the host)'
        # $m.machine, not $m.name. The field is called machine, so every row
        # printed a blank where the computer name should have been and the
        # roster in the report was unusable - the raw fleet.json in the bundle
        # is the only reason it was recoverable.
        # Everything worked out BEFORE the format string. A parenthesised if
        # inside a -f argument list parses perfectly and then fails at run time
        # with "The term 'if' is not recognized", because if is a statement -
        # caught by running this, not by parsing it.
        foreach ($m in ($ms | Sort-Object { $_.seen } -Descending)) {
            $seen = Short16 $m.seen 'never'
            $played = Short16 $m.played 'NEVER'
            Say ("  {0,-20} {1,-12} launcher {2,-13} seen {3,-16} played {4,-16} as {5}" -f `
                 $m.machine, $m.address, $m.launcher, $seen, $played, $m.username)
        }
        Say ''
    }
} 'fleet.json'

# ---------------------------------------------------------------- errors
Step 'reading launcher_errors.txt...'
Say '---- launcher_errors.txt ----'
$errFile = Join-Path $repo 'launcher_errors.txt'
if (Test-Path -LiteralPath $errFile) {
    [void](Grab $errFile 'launcher_errors.txt' 4096)
    Say ''
    Say '  last 80 lines:'
    Safe { foreach ($l in (Get-Content -LiteralPath $errFile -Tail 80)) { Say "    $l" } } 'the error log'
} else {
    Say '  NOT PRESENT - the launcher has recorded no unhandled error here.'
}
Say ''

# ---------------------------------------------------------------- versions
Step 'inventorying installed versions...'
Say '---- INSTALLED VERSIONS ----'
$versionsDir = Join-Path $repo 'versions'
if (-not (Test-Path -LiteralPath $versionsDir)) {
    Say '  no versions\ folder - nothing is installed'
} else {
    foreach ($v in (Get-ChildItem -LiteralPath $versionsDir -Directory | Sort-Object Name)) {
        Say ''
        Say "  $($v.Name)"
        Safe {
            $mb = [math]::Round((Get-ChildItem -LiteralPath $v.FullName -Recurse -File -ErrorAction SilentlyContinue |
                                 Measure-Object -Property Length -Sum).Sum / 1MB, 0)
            Say "    total: $mb MB"
        } "the size of $($v.Name)"

        # Which of these exist says whether the version is merely installed or
        # has actually been played, which is the difference that matters when
        # deciding what a LAN copy should send.
        foreach ($sub in 'versions', 'libraries', 'assets', 'natives', 'mods', 'config',
                          'saves', 'logs', 'crash-reports', 'screenshots', 'resourcepacks') {
            $p = Join-Path $v.FullName $sub
            if (Test-Path -LiteralPath $p) {
                $n = @(Get-ChildItem -LiteralPath $p -ErrorAction SilentlyContinue).Count
                Say ("      {0,-14} present, {1} entries" -f $sub, $n)
            }
        }

        # Mods, named and hashed. This is what a "is it the mods?" question needs.
        $modDir = Join-Path $v.FullName 'mods'
        if (Test-Path -LiteralPath $modDir) {
            Say '      mods:'

            # NO filename-based version check here, and that is a decision
            # rather than an omission. One was written and measured against
            # these two real instances: it produced ten flags of which about
            # one was interesting. cloth-config-26.1.154 is the mod's OWN
            # version numbering, packetfixer-3.3.2-1.18-1.20.4 declares a
            # RANGE that contains the installed version, and six more were
            # patch-level differences that are entirely normal. A diagnostic
            # that cries wolf nine times out of ten is worse than none.
            #
            # The launcher already does this properly: ModInspector reads each
            # jar's real metadata and the Mods tab checks loader family AND
            # version against it. The list below is the raw material; the
            # judgement belongs to the thing that can read a manifest.
            Safe {
                foreach ($m in (Get-ChildItem -LiteralPath $modDir -File | Sort-Object Name)) {
                    $hash = try { (Get-FileHash -LiteralPath $m.FullName -Algorithm SHA1).Hash.Substring(0,12) }
                            catch { '????????????' }
                    Say ("        {0,-52} {1,8} KB  {2}" -f `
                         $m.Name, [math]::Round($m.Length/1KB), $hash)
                }
            } "mods in $($v.Name)"
            Say ''
            Say '      (Open the Mods tab on this machine for the real compatibility'
            Say '       check - it reads each jar''s metadata rather than its name.)'
        }

        # Crash reports and logs: the newest few, which is what gets read.
        foreach ($pair in @(@('crash-reports', 5), @('logs', 2))) {
            $dir = Join-Path $v.FullName $pair[0]
            if (-not (Test-Path -LiteralPath $dir)) { continue }
            Safe {
                $newest = @(Get-ChildItem -LiteralPath $dir -File |
                            Sort-Object LastWriteTime -Descending | Select-Object -First $pair[1])
                foreach ($f in $newest) {
                    [void](Grab $f.FullName "$($v.Name)\$($pair[0])\$($f.Name)" 512)
                }
            } "$($pair[0]) in $($v.Name)"
        }
    }
}
Say ''

# ---------------------------------------------------------------- java
Step 'checking bundled Java...'
Say '---- BUNDLED JAVA ----'
$runtime = Join-Path $repo 'runtime'
if (Test-Path -LiteralPath $runtime) {
    foreach ($d in (Get-ChildItem -LiteralPath $runtime -Directory | Sort-Object Name)) {
        $javaExe = Join-Path $d.FullName 'bin\java.exe'
        if (Test-Path -LiteralPath $javaExe) {
            Say ("  java {0,-18} present" -f $d.Name)
        } else {
            Say ("  {0,-23} (not a Java runtime)" -f $d.Name)
        }
    }
} else { Say '  no runtime\ folder' }
Say ''

# ---------------------------------------------------------------- machine
Step 'reading machine state...'
Say '---- MACHINE ----'
Safe {
    $procs = @(Get-Process MinecraftLauncher, java, javaw -ErrorAction SilentlyContinue)
    Say "  launcher/java processes running: $($procs.Count)"
    foreach ($p in $procs) { Say "    $($p.ProcessName) pid $($p.Id)" }
} 'processes'
Safe {
    $drive = (Get-Item -LiteralPath $repo).PSDrive.Name
    $free = [math]::Round((Get-PSDrive $drive).Free / 1GB, 2)
    Say "  free space on $($drive): $free GB"
} 'disk space'
Say ''

Safe {
    $w = Join-Path $repo 'update-watcher.log'
    if (Test-Path -LiteralPath $w) {
        [void](Grab $w 'update-watcher.log' 512)
        Say '  update-watcher.log, last 25 lines:'
        foreach ($l in (Get-Content -LiteralPath $w -Tail 25)) { Say "    $l" }
    }
} 'the update watcher log'
Say ''

Say '================================================================'
Say ' WHAT IS IN THIS BUNDLE'
Say '================================================================'
Safe {
    foreach ($f in (Get-ChildItem -LiteralPath $filesIn -Recurse -File | Sort-Object FullName)) {
        Say ("  {0,-66} {1} KB" -f `
             $f.FullName.Substring($filesIn.Length + 1), [math]::Round($f.Length/1KB))
    }
} 'the bundle listing'
Say ''
Say '  No worlds, screenshots or resource packs were collected.'
Say '  config.txt is included and holds the Minecraft name last used here.'
Say '================================================================'

# ================================================================= write it out
Set-Content -LiteralPath (Join-Path $work 'REPORT.txt') -Value $report -Encoding UTF8

$zipped = $false
try {
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zipPath -Force -ErrorAction Stop
    $zipped = $true
} catch {
    Write-Output "  (could not make the zip - $($_.Exception.Message))"
}

Write-Output ''
Write-Output '----------------------------------------------------------------'
if ($zipped) {
    $kb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB)
    Write-Output "  Bundle: $zipPath"
    Write-Output "          $kb KB"
    Write-Output ''
    Write-Output '  Send that one file. Nothing else is needed.'
    Write-Output ''
    Write-Output "  The loose copy is in $work if you want to look through it"
    Write-Output '  first - everything in it is readable text.'
} else {
    Write-Output "  Could not zip it, but the folder is complete:"
    Write-Output "    $work"
    Write-Output '  Send that folder, or just REPORT.txt inside it.'
}
Write-Output '----------------------------------------------------------------'
Write-Output ''

if (-not $NoPause -and -not [Console]::IsOutputRedirected) {
    Read-Host 'Press Enter to close'
}
