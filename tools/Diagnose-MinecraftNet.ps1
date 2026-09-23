# Why can't Minecraft reach the internet on this machine?
#
# The whole point is the comparison: Windows can reach the server, but can the
# *bundled java.exe* reach it? If Windows can and Java can't, something is
# filtering Java specifically -- which is a firewall/antivirus rule, not a
# network problem, and is fixed in a completely different place.
#
#   powershell -ExecutionPolicy Bypass -File Diagnose-MinecraftNet.ps1
#   powershell -ExecutionPolicy Bypass -File Diagnose-MinecraftNet.ps1 -Server play.example.net -Port 25565

param(
    [string]$Server = 'mc.hypixel.net',
    [int]$Port = 25565,
    [string]$LauncherRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Continue'

function Section($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Ok($t)      { Write-Host "  OK   $t" -ForegroundColor Green }
function Bad($t)     { Write-Host "  FAIL $t" -ForegroundColor Red }
function Info($t)    { Write-Host "  ..   $t" -ForegroundColor DarkGray }

Write-Host "Minecraft connectivity check - target $Server`:$Port" -ForegroundColor White

# ---------------------------------------------------------------- 1. the machine
Section 'Does Windows itself have internet?'

$gw = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
       Sort-Object RouteMetric | Select-Object -First 1)
if ($gw) { Ok "default gateway $($gw.NextHop) via interface $($gw.InterfaceAlias)" }
else     { Bad 'no default gateway - this machine has no route to the internet at all' }

$dnsServers = (Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
               Where-Object { $_.ServerAddresses } |
               Select-Object -ExpandProperty ServerAddresses -Unique)
if ($dnsServers) { Ok "DNS servers: $($dnsServers -join ', ')" }
else             { Bad 'no DNS servers configured - hostnames cannot resolve' }

try {
    $resolved = [System.Net.Dns]::GetHostAddresses($Server) |
                Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
                Select-Object -First 1
    if ($resolved) { Ok "Windows resolved $Server -> $($resolved.IPAddressToString)" }
    else           { Bad "$Server resolved to no IPv4 address" }
} catch {
    Bad "Windows could not resolve $Server - $($_.Exception.Message)"
    $resolved = $null
}

$windowsCanConnect = $false
if ($resolved) {
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $c.BeginConnect($resolved, $Port, $null, $null)
        if ($iar.AsyncWaitHandle.WaitOne(8000) -and $c.Connected) {
            Ok "Windows connected to $Server`:$Port"
            $windowsCanConnect = $true
        } else {
            Bad "Windows could NOT connect to $Server`:$Port (timed out)"
        }
    } catch {
        Bad "Windows could NOT connect to $Server`:$Port - $($_.Exception.Message)"
    } finally { $c.Close() }
}

# ---------------------------------------------------------------- 2. java itself
Section 'Can the bundled Java reach it? (this is the decisive test)'

$netTest = Join-Path $PSScriptRoot 'NetTest.java'
if (-not (Test-Path $netTest)) {
    Bad "NetTest.java not found next to this script - cannot test Java"
} else {
    foreach ($ver in @('17', '21', '25')) {
        $java = Join-Path $LauncherRoot "runtime\$ver\bin\java.exe"
        if (-not (Test-Path $java)) { Info "java $ver not present, skipping"; continue }

        Write-Host "  -- runtime\$ver\bin\java.exe" -ForegroundColor White
        & $java $netTest $Server $Port 2>&1 | ForEach-Object { Write-Host "  $_" }
    }
}

# ---------------------------------------------------------------- 3. the blockers
Section 'Things that block Java specifically'

$avNames = @()
try {
    $avNames = Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct `
                   -ErrorAction Stop | Select-Object -ExpandProperty displayName
} catch { }
if ($avNames) { Info "antivirus registered: $($avNames -join ', ')" }
else          { Info 'no third-party antivirus registered (Defender only, probably)' }

$javaRules = @()
try {
    $javaRules = Get-NetFirewallApplicationFilter -ErrorAction Stop |
        Where-Object { $_.Program -match 'java' } |
        ForEach-Object {
            $r = $_ | Get-NetFirewallRule -ErrorAction SilentlyContinue
            if ($r) {
                [pscustomobject]@{
                    Action  = $r.Action
                    Dir     = $r.Direction
                    Enabled = $r.Enabled
                    Name    = $r.DisplayName
                    Program = $_.Program
                }
            }
        }
} catch { Info "could not read firewall rules - $($_.Exception.Message)" }

$blocks = @($javaRules | Where-Object { $_.Action -eq 'Block' -and $_.Enabled -eq 'True' })
if ($blocks) {
    Bad "$($blocks.Count) enabled Windows Firewall BLOCK rule(s) mention java:"
    $blocks | ForEach-Object { Write-Host "       [$($_.Dir)] $($_.Name)  ->  $($_.Program)" -ForegroundColor Red }
} elseif ($javaRules) {
    Ok "$($javaRules.Count) java firewall rule(s), none of them blocking"
} else {
    Info 'no Windows Firewall rules mention java at all'
}

$hosts = "$env:SystemRoot\System32\drivers\etc\hosts"
$hostLines = @()
if (Test-Path $hosts) {
    $hostLines = @(Get-Content $hosts | Where-Object {
        $_ -notmatch '^\s*#' -and $_ -match '\S' -and
        $_ -match 'mojang|minecraft|microsoft|xbox'
    })
}
if ($hostLines) {
    Bad 'hosts file redirects Mojang/Minecraft domains:'
    $hostLines | ForEach-Object { Write-Host "       $_" -ForegroundColor Red }
} else {
    Ok 'hosts file does not redirect Mojang/Minecraft domains'
}

$proxy = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' `
             -ErrorAction SilentlyContinue
if ($proxy -and $proxy.ProxyEnable -eq 1) {
    Info "a system proxy is configured ($($proxy.ProxyServer)) - Java ignores this unless told to use it"
}

# ---------------------------------------------------------------- 4. the verdict
Section 'Verdict'
if (-not $windowsCanConnect) {
    Write-Host '  The machine itself cannot reach the server. This is not a Minecraft' -ForegroundColor Yellow
    Write-Host '  problem - check the gateway, DNS, and whether the server is actually up.' -ForegroundColor Yellow
} else {
    Write-Host '  Windows can reach the server. Compare that against the Java results above:' -ForegroundColor Yellow
    Write-Host '   - Java OK too      -> the network is fine; the problem is in-game (wrong' -ForegroundColor Yellow
    Write-Host '                         address/port, or the server is online-mode).' -ForegroundColor Yellow
    Write-Host '   - Java DNS FAIL    -> Java is not being allowed to resolve names.' -ForegroundColor Yellow
    Write-Host '   - Java TCP FAIL    -> something is filtering java.exe specifically.' -ForegroundColor Yellow
    Write-Host '                         That is the antivirus or a firewall block rule.' -ForegroundColor Yellow
}
Write-Host ''
