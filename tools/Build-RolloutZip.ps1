# Builds the two zips that leave this machine.
#
#   tools\Build-RolloutZip.ps1
#   tools\Build-RolloutZip.ps1 -OutDir C:\Users\Zach
#   tools\Build-RolloutZip.ps1 -UpdateOnly
#
#   1. Minecraft-Launcher-Update.zip   goes to the host, then spreads itself
#   2. Minecraft-Launcher-Admin.zip    stays with whoever runs the sessions
#
# Written as a script rather than done by hand because HANDOFF section 8 says to
# check the contents "every time", and a rule enforced by remembering is a rule
# that gets forgotten the once it matters. The update zip is built from an explicit
# list of files and is then checked again against the host-only names: admin.flag
# reaching the other machines would hand all of them the required-mods editor, the
# list of everyone's computers and usernames, and everyone's crash reports.
#
# ASCII only, no non-ASCII characters anywhere -- see HANDOFF section 11. Windows
# PowerShell 5.1 reads a BOM-less file using the ANSI code page, so a UTF-8 dash
# becomes a stray quote and the script fails at its last line for no visible
# reason.

param(
    [string]$OutDir = 'C:\Users\Zach',
    [switch]$UpdateOnly,
    [switch]$AdminOnly
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'MinecraftLauncher.exe'

function Fail($message) { Write-Output "  ERROR: $message"; exit 1 }

if (-not (Test-Path $exe)) { Fail "no launcher at $exe" }

$version = (Get-Item $exe).VersionInfo.FileVersion
$built   = Get-Date -Format 'yyyy-MM-dd'

Write-Output "building from $repo"
Write-Output "  launcher version $version"

# The self-contained publish is the only build that can serve updates to anyone
# else; a framework-dependent one leaves this DLL beside the exe and would be
# refused by every client with "found a skin server, but it cannot offer updates".
if (Test-Path (Join-Path $repo 'MinecraftLauncher.dll')) {
    Fail "MinecraftLauncher.dll is beside the exe, so this is a framework-dependent build. Publish self-contained (HANDOFF section 4) before building a zip."
}

# ----------------------------------------------------------------
# The host-only names. Kept here next to the thing that enforces them.
# ----------------------------------------------------------------

$hostOnly = @(
    'admin.flag'
    'fleet.json'
    'required-mods.json'
    'required-mods'
    'crash-inbox'
    'crash-sent.txt'
    'required-mods-declined.txt'
    'backups'
    'launcher_errors.txt'
    'update-watcher.log'
    'config.txt'
    'computer_uuid.dat'
)

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("mc-zip-" + [guid]::NewGuid().ToString('N').Substring(0, 8))

function Expand($text) {
    $text -replace '@VERSION@', $version -replace '@DATE@', $built
}

# ----------------------------------------------------------------
# 1. The update zip
# ----------------------------------------------------------------

function Build-UpdateZip {
    $folder = Join-Path $staging 'Launcher-Update'
    New-Item -ItemType Directory -Force $folder | Out-Null

    # Named one by one. A wildcard over the repo root is how a working folder's
    # accumulated host-only files end up in a zip.
    $files = @('MinecraftLauncher.exe') +
             (Get-ChildItem (Join-Path $repo '*_cor3.dll') | ForEach-Object { $_.Name })

    foreach ($name in $files) {
        $from = Join-Path $repo $name
        if (-not (Test-Path $from)) { Fail "missing $name" }
        Copy-Item $from (Join-Path $folder $name)
    }

    foreach ($helper in 'Why-was-this-blocked.cmd', 'Get-DefenderDetection.ps1') {
        $from = Join-Path $repo "tools\$helper"
        if (-not (Test-Path $from)) { Fail "missing tools\$helper" }
        Copy-Item $from (Join-Path $folder $helper)
    }

    $readme = Join-Path $repo 'UPDATE-README.txt'
    if (-not (Test-Path $readme)) { Fail 'missing UPDATE-README.txt' }
    Set-Content (Join-Path $folder 'README.txt') (Expand (Get-Content $readme -Raw)) -Encoding ASCII

    # Checked again on what is actually staged, not on what was intended. The
    # copying above is correct by construction; this catches the day it is edited.
    foreach ($bad in $hostOnly) {
        $found = Get-ChildItem $folder -Recurse -Force -Filter $bad -ErrorAction SilentlyContinue
        if ($found) { Fail "$bad is in the update zip. It must never be. HANDOFF section 8." }
    }

    $out = Join-Path $OutDir 'Minecraft-Launcher-Update.zip'
    if (Test-Path $out) { Remove-Item $out -Force }

    # Optimal, measured on this exe: 52.7 MB in 6.5s against 57.1 MB in 2.8s for
    # Fastest. 4.4 MB for under four seconds is worth it for a file that has to
    # cross a USB stick and be scanned at the other end. (HANDOFF section 8 says
    # "fastest" -- that is about the 2.2 GB full-deployment zip, which is almost
    # entirely jars and DLLs that are already compressed. This one is a single exe
    # that halves.)
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $staging, $out, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    return $out
}

# ----------------------------------------------------------------
# 2. The admin zip
# ----------------------------------------------------------------

function Build-AdminZip {
    $folder = Join-Path $staging 'Launcher-Admin'
    New-Item -ItemType Directory -Force $folder | Out-Null

    # admin.flag is the point of this zip. It is an empty file and is written
    # rather than copied, so the zip builds whether or not this machine has one.
    # No -Force: staging is fresh, and -Force on a file truncates an existing one.
    New-Item -ItemType File (Join-Path $folder 'admin.flag') | Out-Null

    Copy-Item (Join-Path $repo 'tools') (Join-Path $folder 'tools') -Recurse

    $docs = Join-Path $folder 'Docs'
    New-Item -ItemType Directory -Force $docs | Out-Null
    foreach ($doc in 'HANDOFF.md', 'CLAUDE.md', 'DEPLOY-README.md') {
        $from = Join-Path $repo $doc
        if (Test-Path $from) { Copy-Item $from $docs }
    }

    # Only if they exist. An empty required-mods.json would tell a host it has a
    # list when it has not.
    # Script scope rather than a return value: PowerShell flattens an array into
    # the output stream, so returning ($out, $carried) would hand the caller three
    # loose objects when two mods are carried and one when none are.
    $script:carried = @()
    foreach ($item in 'required-mods.json', 'required-mods') {
        $from = Join-Path $repo $item
        if (Test-Path $from) {
            Copy-Item $from $folder -Recurse
            $script:carried += $item
        }
    }

    $readme = Join-Path $repo 'ADMIN-README.txt'
    if (-not (Test-Path $readme)) { Fail 'missing ADMIN-README.txt' }
    Set-Content (Join-Path $folder 'README.txt') (Expand (Get-Content $readme -Raw)) -Encoding ASCII

    # The generated, machine-specific ones must not be in here either. Shipping
    # one copy of crash-sent.txt makes a machine think it has already handed over
    # crashes it has not, and it then never will.
    foreach ($bad in 'fleet.json', 'crash-inbox', 'crash-sent.txt',
                     'required-mods-declined.txt', 'backups',
                     'launcher_errors.txt', 'update-watcher.log',
                     'config.txt', 'computer_uuid.dat') {
        $found = Get-ChildItem $folder -Recurse -Force -Filter $bad -ErrorAction SilentlyContinue
        if ($found) { Fail "$bad is in the admin zip. It belongs to one machine; read it where it is." }
    }

    # And the launcher itself is not in here: it is in the other zip, and two
    # copies of a 126 MB exe differing by a rebuild is how the wrong one gets
    # copied onto the host.
    if (Test-Path (Join-Path $folder 'MinecraftLauncher.exe')) {
        Fail 'the launcher is in the admin zip; it belongs only in the update zip'
    }

    $out = Join-Path $OutDir 'Minecraft-Launcher-Admin.zip'
    if (Test-Path $out) { Remove-Item $out -Force }

    # Optimal here, unlike the update zip: this one is text and scripts, where
    # compression actually pays, and it is small enough that the time is nothing.
    # includeBaseDirectory so everything lands under Launcher-Admin\.
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $folder, $out, [System.IO.Compression.CompressionLevel]::Optimal, $true)

    return $out
}

# ----------------------------------------------------------------

# The zip is useless if what came out is not what went in, and a truncated or
# altered 126 MB exe inside an archive is invisible until somebody runs it. Hashed
# through the zip rather than trusting the writer.
function Verify-Exe($path) {
    $onDisk = (Get-FileHash $exe -Algorithm SHA256).Hash

    $z = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $z.Entries | Where-Object { $_.Name -eq 'MinecraftLauncher.exe' }
        if (-not $entry) { Fail "no launcher inside $path" }

        $stream = $entry.Open()
        try {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $inZip = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '')
        } finally { $stream.Dispose() }
    } finally { $z.Dispose() }

    if ($inZip -ne $onDisk) { Fail "the launcher inside the zip is not the one on disk" }

    Write-Output "      the launcher inside it is byte-for-byte the one on disk"
    Write-Output "      sha256 $($onDisk.Substring(0, 16))..."
}

function Describe($path) {
    $z = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $mb = [math]::Round((Get-Item $path).Length / 1MB, 1)
        Write-Output ""
        Write-Output "  $(Split-Path -Leaf $path)  --  $mb MB, $($z.Entries.Count) entries"
        $z.Entries | Sort-Object FullName | ForEach-Object {
            Write-Output ("      {0,8}  {1}" -f
                $(if ($_.Length -ge 1MB) { "$([math]::Round($_.Length/1MB,1)) MB" }
                  elseif ($_.Length -gt 0) { "$([math]::Round($_.Length/1KB)) KB" }
                  else { '-' }),
                $_.FullName)
        }
    } finally { $z.Dispose() }
}

try {
    if (-not $AdminOnly) {
        $updateZip = Build-UpdateZip
        Describe $updateZip
        Verify-Exe $updateZip
    }

    if (-not $UpdateOnly) {
        # Staging is shared, so clear the update folder before the admin one is
        # zipped or CreateFromDirectory would include it.
        $updateFolder = Join-Path $staging 'Launcher-Update'
        if (Test-Path $updateFolder) { Remove-Item $updateFolder -Recurse -Force }

        $adminZip = Build-AdminZip
        Describe $adminZip

        if (@($script:carried).Count -gt 0) {
            Write-Output "      (carried this host's $($script:carried -join ' and '))"
        } else {
            Write-Output "      (no required-mods list on this machine, so none is carried)"
        }
    }
} finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Output ""
Write-Output "  Update zip -> the host. Everything else updates itself from there."
Write-Output "  Admin zip  -> nobody. HANDOFF section 8."
Write-Output "  Carry them on a USB stick if you can: a downloaded file is marked as"
Write-Output "  coming from the internet, and that mark is most of what Defender reacts to."
