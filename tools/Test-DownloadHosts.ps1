# Which hosts can THIS machine actually reach?
#
#   powershell -ExecutionPolicy Bypass -File Test-DownloadHosts.ps1
#   powershell -ExecutionPolicy Bypass -File Test-DownloadHosts.ps1 -HostingOnly
#   powershell -ExecutionPolicy Bypass -File Test-DownloadHosts.ps1 -MinecraftOnly
#
# Read-only, needs no admin, changes nothing. Two separate questions:
#
#   MINECRAFT  - which of the four hosts a Minecraft install needs are blocked.
#                Run this on a machine where downloading Minecraft fails.
#   HOSTING    - where could we put a ~350 MB payload so these machines can
#                fetch it? GitHub is reported blocked here; this finds what is
#                not. Run this before choosing anywhere to host anything.
#
# WHY PER-HOST. Installing Minecraft touches four hosts across TWO domains, and
# which of them are blocked decides the fix completely:
#
#   piston-meta.mojang.com            manifests, version JSON, asset index
#   piston-data.mojang.com            the client jar
#   libraries.minecraft.net           107 library jars
#   resources.download.minecraft.net  the assets, thousands of files
#
# "Minecraft won't download" is not one failure. If only mojang.com is blocked,
# the libraries and assets - the overwhelming bulk of it - are still reachable.
#
# HOW EACH HOST IS TESTED, and why it is three things and not one:
#
#   DNS    does the name resolve. Not resolving means DNS-level blocking, and
#          then no other hostname on the same domain will help either.
#   TLS    does a TLS handshake complete, sent with SNI. This is the signal that
#          matters, because an SNI-filtering proxy breaks the handshake for the
#          names it dislikes while leaving everything else alone. The remote
#          certificate is deliberately accepted whatever it is - we want to SEE
#          an interception certificate, not reject it.
#   ISSUER who signed the certificate we were given. If this is not a public CA,
#          the network is decrypting TLS, and SNI tricks cannot get round it -
#          but a category allow-list can.
#   HTTP   a 1 KB range request, where there is a stable URL to ask for. A status
#          is reported rather than judged: a content filter commonly answers 200
#          or 403 with its own block page, so the number alone proves little.
#
# Output is Write-Output throughout, not Write-Host, so this can be piped to a
# file or pasted somewhere. Write-Host bypasses redirection in PowerShell 5.1.

param(
    [switch]$HostingOnly,
    [switch]$MinecraftOnly,

    # Everything printed is also written here, so the results survive the window
    # closing. Double-clicking a .ps1 (or "Run with PowerShell") closes it the
    # instant the script ends, which lost a whole run once.
    [string]$Out,

    # Skip the "press Enter" at the end. Set automatically when the output is
    # being redirected, since nothing is waiting to read the window then.
    [switch]$NoPause
)

$ErrorActionPreference = 'Continue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ConnectTimeoutMs = 8000

if (-not $Out) {
    $Out = Join-Path (Split-Path -Parent $PSScriptRoot) 'download-hosts-report.txt'
}

# Every line goes through Say, which prints it AND keeps it, and the whole lot is
# written out at the end.
#
# NOT Start-Transcript, which was the first attempt: it only records what reaches
# the HOST, so the moment anything redirects the output the report is just a
# transcript header and nothing else. Tested, and that is exactly what happened.
# Recording the lines here instead works however the script is invoked.
$script:report = New-Object System.Collections.Generic.List[string]

function Say($text = '') {
    if ($null -eq $text) { $text = '' }
    foreach ($line in ([string]$text) -split "`r?`n") {
        $script:report.Add($line)
        Write-Output $line
    }
}

# Identifies the exact script that produced a report, so two runs of different
# vintages cannot be mistaken for each other.
function ScriptFingerprint {
    try {
        if ($PSCommandPath -and (Test-Path -LiteralPath $PSCommandPath)) {
            $h = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
            return "$(Split-Path -Leaf $PSCommandPath) SHA256 $($h.Substring(0,16))"
        }
    } catch { }
    return 'unknown (not run from a file)'
}

function SaveReport {
    try {
        Set-Content -LiteralPath $Out -Value $script:report -Encoding ASCII -ErrorAction Stop
        return $true
    } catch {
        Write-Output "  (could not write the report to $Out - $($_.Exception.Message))"
        return $false
    }
}

Say ''
Say 'Everything below is also being saved to:'
Say "  $Out"
Say ''

# WHY THIS BLOCK EXISTS. A report from a workstation was once read next to the
# summary from the build machine, and the two contradicted each other flatly:
# the table said seven hosts refused while the summary called all twelve
# reachable. Neither half was wrong. They were different runs, and nothing in
# the file said which machine either came from, so there was no way to notice.
# An hour went into looking for a bug in this script that was never there.
Say '---- WHERE THIS REPORT CAME FROM ----'
Say "  Computer   : $env:COMPUTERNAME"
Say "  User       : $env:USERNAME"
Say "  When       : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')"
Say "  Windows    : $([Environment]::OSVersion.Version)"
Say "  PowerShell : $($PSVersionTable.PSVersion) on .NET $([Environment]::Version)"
Say "  Script     : $(ScriptFingerprint)"
Say ''
Say '  That .NET version is not a detail. This script runs on .NET Framework;'
Say '  the launcher runs on .NET 10, and they do not handle proxies alike.'
Say '  See the PROXY section below.'
Say ''

$targets = @(
    # ---- what a Minecraft install needs -------------------------------
    @{ Group='minecraft'; Host='piston-meta.mojang.com';           What='manifests + version JSON';      Needed=$true;  Url='https://piston-meta.mojang.com/mc/game/version_manifest_v2.json' }
    @{ Group='minecraft'; Host='launchermeta.mojang.com';          What='older alias for the same';      Needed=$false; Url='https://launchermeta.mojang.com/mc/game/version_manifest_v2.json' }
    @{ Group='minecraft'; Host='piston-data.mojang.com';           What='the client jar';                Needed=$true;  Url='https://piston-data.mojang.com/v1/objects/4e618f09a0c649dde3fdf829df443ce0b8831e65/client.jar' }
    @{ Group='minecraft'; Host='libraries.minecraft.net';          What='107 library jars';              Needed=$true;  Url='https://libraries.minecraft.net/com/mojang/brigadier/1.3.10/brigadier-1.3.10.jar' }
    @{ Group='minecraft'; Host='resources.download.minecraft.net'; What='the assets';                    Needed=$true;  Url='https://resources.download.minecraft.net/b6/b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356' }
    @{ Group='minecraft'; Host='repo1.maven.org';                  What='95 of 107 libs, as a fallback'; Needed=$false; Url='https://repo1.maven.org/maven2/com/google/code/gson/gson/2.11.0/gson-2.11.0.jar.sha1' }

    # ---- what updating the Fabric loader needs ------------------------
    # Decides whether a client can install a loader itself, or whether the host
    # has to make a pack and serve it. The Mods tab's MAKE A PACK flow exists
    # because that was once in doubt.
    @{ Group='fabric';    Host='meta.fabricmc.net';                What='loader versions + profile JSON'; Needed=$true;  Url='https://meta.fabricmc.net/v2/versions/installer' }
    @{ Group='fabric';    Host='maven.fabricmc.net';               What='the loader library jars';        Needed=$true;  Url='' }

    # ---- controls. If these fail, read the summary before anything else.
    @{ Group='control';   Host='api.modrinth.com';                 What='CONTROL: mods, known to work here'; Needed=$false; Url='https://api.modrinth.com/v2/tag/loader' }
    @{ Group='control';   Host='example.com';                      What='CONTROL: an ordinary uncategorised site'; Needed=$false; Url='https://example.com/' }

    # ---- could we host a payload here? --------------------------------
    # GitHub first: reported blocked, and knowing HOW matters more than that it is.
    # Release assets do not come from github.com - they redirect to
    # objects.githubusercontent.com, so both have to pass for Releases to work.
    @{ Group='hosting';   Host='github.com';                       What='GitHub web + API';              Needed=$false; Url='https://github.com/robots.txt' }
    @{ Group='hosting';   Host='objects.githubusercontent.com';    What='where Release ASSETS serve from'; Needed=$false; Url='' }
    @{ Group='hosting';   Host='raw.githubusercontent.com';        What='raw files (the AV helper scripts)'; Needed=$false; Url='https://raw.githubusercontent.com/Zw9876/Ultimate-MC/main/tools/Why-was-this-blocked.cmd' }
    @{ Group='hosting';   Host='codeload.github.com';              What='GitHub zip/tarball downloads';  Needed=$false; Url='' }

    # Cloudflare. R2 is the candidate; the apex tells us whether Cloudflare-hosted
    # content passes at all, which is the more general question.
    @{ Group='hosting';   Host='cloudflare.com';                   What='is Cloudflare-hosted OK at all'; Needed=$false; Url='https://cloudflare.com/robots.txt' }
    @{ Group='hosting';   Host='r2.cloudflarestorage.com';         What='Cloudflare R2 endpoint';        Needed=$false; Url='' }
    @{ Group='hosting';   Host='pub-00000000000000000000000000000000.r2.dev'; What='an R2 public bucket name'; Needed=$false; Url='' }

    # The big object stores. Usually spared by category filters because blocking
    # them breaks too much legitimate traffic.
    @{ Group='hosting';   Host='s3.amazonaws.com';                 What='AWS S3';                        Needed=$false; Url='https://s3.amazonaws.com/' }
    @{ Group='hosting';   Host='storage.googleapis.com';           What='Google Cloud Storage';          Needed=$false; Url='https://storage.googleapis.com/' }
    @{ Group='hosting';   Host='azureopendatastorage.blob.core.windows.net'; What='Azure Blob Storage';   Needed=$false; Url='' }

    # Free and usually filed as reference rather than file sharing.
    @{ Group='hosting';   Host='archive.org';                      What='Internet Archive';              Needed=$false; Url='https://archive.org/robots.txt' }

    # Known-good download CDN, as a second control: this one definitely works,
    # because the Mods tab downloads from it on these machines.
    @{ Group='hosting';   Host='cdn.modrinth.com';                 What='CONTROL: mod downloads land here'; Needed=$false; Url='' }
)

if ($HostingOnly)   { $targets = @($targets | Where-Object { $_.Group -ne 'minecraft' }) }
if ($MinecraftOnly) { $targets = @($targets | Where-Object { $_.Group -ne 'hosting' }) }

# Shortens an X.500 issuer to something readable in a table.
function Short-Issuer($issuer) {
    if (-not $issuer) { return '-' }
    $cn = ($issuer -split ',' | Where-Object { $_.Trim() -like 'CN=*' } | Select-Object -First 1)
    if (-not $cn) { $cn = ($issuer -split ',' | Where-Object { $_.Trim() -like 'O=*' } | Select-Object -First 1) }
    if (-not $cn) { return $issuer }
    $t = $cn.Trim() -replace '^(CN|O)=', ''
    if ($t.Length -gt 18) { $t = $t.Substring(0, 18) + '...' }
    return $t
}

$results = @()

Say ("Probing " + $targets.Count + " hosts. Each one does a DNS lookup, a TLS")
Say "handshake and sometimes a small request, so a blocked one takes a few"
Say "seconds to time out."
Say ''

$done = 0
foreach ($t in $targets) {
    # Printed BEFORE the probe, not after: the whole point is that the window
    # shows something while a blocked host is timing out.
    $done++
    Say ("  [{0,2}/{1}] {2}" -f $done, $targets.Count, $t.Host)

    $row = [ordered]@{
        Group = $t.Group; Host = $t.Host; What = $t.What
        Dns = '-'; Tls = '-'; Issuer = '-'; Http = '-'
        Needed = $t.Needed; Reached = $false
    }

    # ---- DNS ----
    try {
        $ip = [Net.Dns]::GetHostAddresses($t.Host) |
              Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1
        $row.Dns = if ($ip) { $ip.IPAddressToString } else { 'no A record' }
    } catch { $row.Dns = 'FAILED' }

    $resolved = ($row.Dns -ne 'FAILED' -and $row.Dns -ne 'no A record')

    # ---- TLS handshake, with SNI ----
    if ($resolved) {
        $tcp = $null; $ssl = $null
        try {
            $tcp = New-Object Net.Sockets.TcpClient
            if (-not $tcp.ConnectAsync($t.Host, 443).Wait($ConnectTimeoutMs)) {
                $row.Tls = 'timeout'
            } else {
                # Any certificate is accepted on purpose: an interception proxy's
                # own certificate is exactly what we are trying to observe.
                $accept = [Net.Security.RemoteCertificateValidationCallback] { param($a, $b, $c, $d) $true }
                $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false, $accept)
                $ssl.AuthenticateAsClient($t.Host)
                $row.Tls = 'ok'
                $row.Reached = $true
                if ($ssl.RemoteCertificate) { $row.Issuer = Short-Issuer $ssl.RemoteCertificate.Issuer }
            }
        } catch {
            $row.Tls = 'refused'
        } finally {
            if ($ssl) { $ssl.Dispose() }
            if ($tcp) { $tcp.Dispose() }
        }
    }

    # ---- HTTP, where there is a stable thing to ask for ----
    if ($resolved -and $t.Url) {
        try {
            $req = [Net.HttpWebRequest]::Create($t.Url)
            $req.Timeout = $ConnectTimeoutMs
            $req.UserAgent = 'MinecraftPortableLauncher connectivity check'
            $req.AddRange(0, 1023)      # a kilobyte, not a 30 MB client jar
            $resp = $req.GetResponse()
            $row.Http = [int]$resp.StatusCode
            $resp.Close()
        } catch [Net.WebException] {
            $r = $_.Exception.Response
            if ($r) { $row.Http = [int]$r.StatusCode }
            else    { $row.Http = $_.Exception.Status.ToString() }
        } catch {
            $row.Http = 'error'
        }
    }

    $results += [pscustomobject]$row
}

function Show($group, $title) {
    $rows = @($results | Where-Object { $_.Group -eq $group })
    if ($rows.Count -eq 0) { return }
    Say ''
    Say "---- $title ----"
    # Reached is printed even though Tls implies it, because every verdict below
    # is computed from THIS field and nothing else. When a summary and a table
    # appear to disagree, the first question is whether they are even the same
    # data - and now that is answerable by looking.
    # 'OK' rather than 'Reached', and an explicit Out-String width: at the
    # default console width Format-Table ran out of room and rendered the
    # header as a vertical column of single letters.
    Say (($rows | Format-Table Host, What, Dns, Tls, Issuer, Http,
        @{ n='OK'; e = { if ($_.Reached) { 'yes' } else { 'NO' } } } -AutoSize |
        Out-String -Width 160).TrimEnd())
}

Show 'control'   'CONTROLS (these should pass)'
Show 'minecraft' 'WHAT A MINECRAFT INSTALL NEEDS'
Show 'fabric'    'WHAT UPDATING THE FABRIC LOADER NEEDS'
Show 'hosting'   'WHERE WE COULD HOST A PAYLOAD'

# ===========================================================================
# PROXY.
#
# This section exists because of one specific contradiction in a real report:
# the Mojang rows came back TLS 'refused' on a direct socket and HTTP '200' at
# the same time. That is not noise, and it is not a flaky network. A direct
# connection being refused while an HTTP request to the SAME host and port
# succeeds means that request did not go direct - it went through a proxy.
#
# Which matters enormously, because the two stacks are not the same one:
#
#   this script    PowerShell 5.1, which is .NET FRAMEWORK. HttpWebRequest
#                  picks up the WinINET/IE proxy settings by itself.
#   the launcher   .NET 10, and Core\Downloader.cs builds a bare HttpClient
#                  with no proxy and no proxy credentials.
#
# So "the probe reached Mojang" does NOT establish that the launcher can, and
# on these machines the Setup tab cannot download versions while this script
# reports 200. The three fetches below are the whole point of the section:
# same URL, same machine, same moment, asked three different ways.
# ===========================================================================

Say ''
Say '---- PROXY: CONFIGURATION ----'

function RegVal($path, $name) {
    try {
        $v = Get-ItemProperty -LiteralPath $path -Name $name -ErrorAction Stop
        $t = [string]$v.$name
        if ([string]::IsNullOrWhiteSpace($t)) { return '(empty)' }
        return $t
    } catch { return '(not set)' }
}

$ieKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
Say "  ProxyEnable   : $(RegVal $ieKey 'ProxyEnable')"
Say "  ProxyServer   : $(RegVal $ieKey 'ProxyServer')"
Say "  ProxyOverride : $(RegVal $ieKey 'ProxyOverride')"
Say "  AutoConfigURL : $(RegVal $ieKey 'AutoConfigURL')   <- a PAC file, if set"

# WinHTTP keeps its own machine-wide proxy, separate from the per-user WinINET
# settings above. Some stacks read one and some the other, which is exactly the
# kind of difference that makes one program work and another fail.
try {
    $wh = (netsh winhttp show proxy | Out-String).Trim()
    if (-not $wh) { $wh = '(no output)' }
} catch { $wh = "(could not read - $($_.Exception.Message))" }
Say ''
Say '  netsh winhttp show proxy:'
foreach ($line in ($wh -split "`r?`n")) { Say "    $line" }

# WPAD autodiscovery: if this name resolves, a proxy can be imposed without any
# of the registry values above being set at all.
try {
    $wp = @([Net.Dns]::GetHostAddresses('wpad') |
            Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
            ForEach-Object { $_.IPAddressToString })
    $wpad = if ($wp.Count -gt 0) { ($wp -join ', ') + '  <- WPAD is live' } else { 'no A record' }
} catch { $wpad = 'does not resolve (good - no WPAD)' }
Say ''
Say "  wpad          : $wpad"

# What .NET Framework decides to do for a given URL, which is what the probe
# rows above actually used.
function ProxyFor($url) {
    try {
        $sys = [Net.WebRequest]::GetSystemWebProxy()
        $u   = [Uri]$url
        $pr  = $sys.GetProxy($u)
        if ($null -eq $pr)                  { return 'direct' }
        if ($pr.Authority -eq $u.Authority) { return 'direct' }
        return $pr.Authority
    } catch { return "error: $($_.Exception.Message)" }
}

$lanProbe = 'http://192.168.1.10:25565/launcher/manifest'
Say ''
Say '  Resolved route per URL:'
Say "    mojang.com            -> $(ProxyFor 'https://launchermeta.mojang.com/')"
Say "    a LAN host (192.168.) -> $(ProxyFor $lanProbe)"

# ---- the three-way fetch --------------------------------------------------
# Reuses URLs from the target list above, so these are the same requests that
# produced the Http column rather than new ones invented here.
$proxyTargets = @($targets | Where-Object { $_.Group -eq 'minecraft' -and $_.Url } |
                  Select-Object -First 2)

function TryFetch($mode, $hostName, $url) {
    $row = [ordered]@{ Host = $hostName; How = $mode; Via = '-'; Result = '-'; Ok = $false }
    try {
        $req = [Net.HttpWebRequest]::Create($url)
        $req.Timeout   = 6000
        $req.UserAgent = 'MinecraftPortableLauncher connectivity check'
        $req.AddRange(0, 1023)

        switch ($mode) {
            'direct' {
                # No proxy at all - the closest this script can get to what a
                # bare HttpClient does.
                $req.Proxy = $null
                $row.Via = 'no proxy'
            }
            'proxy' {
                $req.Proxy = [Net.WebRequest]::GetSystemWebProxy()
                $row.Via = ProxyFor $url
            }
            'proxy+creds' {
                $pr = [Net.WebRequest]::GetSystemWebProxy()
                $pr.Credentials = [Net.CredentialCache]::DefaultNetworkCredentials
                $req.Proxy = $pr
                $row.Via = ProxyFor $url
            }
        }

        $resp = $req.GetResponse()
        $row.Result = [string][int]$resp.StatusCode
        $row.Ok = $true
        $resp.Close()
    } catch [Net.WebException] {
        $r = $_.Exception.Response
        if ($r) {
            $code = [int]$r.StatusCode
            $row.Result = "HTTP $code"
            # A 407 is the proxy demanding credentials, and is the single most
            # actionable result this whole section can produce.
            if ($code -eq 407) { $row.Result = '407 proxy auth' }
        } else {
            $row.Result = $_.Exception.Status.ToString()
        }
    } catch {
        $row.Result = 'error'
    }
    return [pscustomobject]$row
}

Say ''
Say '---- PROXY: THE SAME REQUEST, ASKED THREE WAYS ----'
$pfRows = @()
foreach ($t in $proxyTargets) {
    foreach ($mode in @('direct', 'proxy', 'proxy+creds')) {
        $pfRows += TryFetch $mode $t.Host $t.Url
    }
}
Say (($pfRows | Format-Table Host, How, Via, Result -AutoSize | Out-String).TrimEnd())

$directOk = @($pfRows | Where-Object { $_.How -eq 'direct'      -and $_.Ok }).Count
$proxyOk  = @($pfRows | Where-Object { $_.How -eq 'proxy'       -and $_.Ok }).Count
$credsOk  = @($pfRows | Where-Object { $_.How -eq 'proxy+creds' -and $_.Ok }).Count
$auth407  = @($pfRows | Where-Object { $_.Result -eq '407 proxy auth' }).Count

Say ''
Say ' PROXY VERDICT'
if ($directOk -eq 0 -and ($proxyOk -gt 0 -or $credsOk -gt 0)) {
    Say '   FOUND IT. Mojang is reachable ONLY through the proxy, and a direct'
    Say '   connection fails. That is exactly the launcher''s situation: its'
    Say '   HttpClient is built with no proxy, so it goes direct and fails,'
    Say '   while this script succeeded by using the system proxy.'
    Say ''
    Say '   The fix is in the launcher, not the network: one shared HTTP'
    Say '   factory in Core\Downloader.cs that uses the system proxy.'
    if ($credsOk -gt 0 -and $proxyOk -eq 0) {
        Say '   It also needs DEFAULT CREDENTIALS - the proxy only answered when'
        Say '   the logged-on user was presented to it.'
    }
}
elseif ($directOk -gt 0 -and $proxyOk -gt 0) {
    Say '   Not the proxy. Mojang answers both directly and through a proxy, so'
    Say '   whatever stops the Setup tab is something else. Read'
    Say '   launcher_errors.txt beside the launcher before theorising further -'
    Say '   a binding error behind an invisible MessageBox looks identical to a'
    Say '   network failure.'
}
elseif ($directOk -eq 0 -and $proxyOk -eq 0 -and $credsOk -eq 0) {
    Say '   Mojang is unreachable however it is asked. No launcher change will'
    Say '   fix that, so versions and Java runtimes have to come from the HOST'
    Say '   over the LAN: it downloads them once and serves them, which no'
    Say '   filter on this machine applies to.'
}
else {
    Say '   Mixed. Read the table above rather than trusting a one-line summary'
    Say '   of it - that is what produced a contradictory report once already.'
}

if ($auth407 -gt 0) {
    Say ''
    Say '   NOTE: a 407 appeared. This proxy wants authentication, which a bare'
    Say '   HttpClient will never supply on its own.'
}

$lanVia = ProxyFor $lanProbe
if ($lanVia -ne 'direct' -and $lanVia -notlike 'error*') {
    Say ''
    Say "   WARNING: LAN traffic resolves through $lanVia rather than going"
    Say '   direct. So a proxy must NOT be applied blindly in the launcher -'
    Say '   check-ins, self-update, play blocks and the skin server all talk to'
    Say '   the host on the LAN, and routing those through a proxy would break'
    Say '   every one of them. Any fix has to bypass local addresses.'
}

Say ''
Say '================================================================'

$ctrl    = @($results | Where-Object { $_.Group -eq 'control' })
$ctrlOk  = @($ctrl | Where-Object { $_.Reached })
$issuers = @($results | Where-Object { $_.Issuer -ne '-' } | Select-Object -ExpandProperty Issuer -Unique)

# ---- is the network inspecting TLS? ----
# NOT done by checking issuers against a list of known-good CAs. That was the
# first attempt and it reported inspection on a machine with none: the public CA
# population is large and full of short opaque names (WE2, YR1, Certainly, E6),
# so any hand-written allow-list produces false alarms - and a diagnostic that
# cries man-in-the-middle when there is none sends you somewhere expensive.
#
# The reliable signal needs no list, because it is structural. Unmolested, these
# hosts sit behind different CDNs and show MANY different issuers. Behind an
# inspecting proxy every single one is re-signed by that proxy's own CA, so the
# issuers collapse to one. Count them.
$withCert = @($results | Where-Object { $_.Issuer -ne '-' })

# Named vendors, used only to say WHICH product when we already suspect one.
$inspectors = 'Fortinet', 'FortiGate', 'Zscaler', 'Blue Coat', 'Bluecoat', 'Netskope',
              'Palo Alto', 'PAN-OS', 'Sophos', 'Untangle', 'Barracuda', 'McAfee', 'Forcepoint',
              'Websense', 'Umbrella', 'Kaspersky', 'ESET', 'Bitdefender', 'Avast', 'AVG',
              'NOD32', 'Proxy', 'SSL Inspection', 'Deep Packet', 'Charles', 'Fiddler', 'mitmproxy'
$named = @($issuers | Where-Object { $i = $_; ($inspectors | Where-Object { $i -like "*$_*" }) })

$collapsed = ($withCert.Count -ge 4 -and $issuers.Count -eq 1)

if ($collapsed -or $named.Count -gt 0) {
    Say ' TLS IS BEING INSPECTED.'
    if ($named.Count -gt 0) {
        Say "   The certificates name it: $($named -join ', ')"
    } else {
        Say "   All $($withCert.Count) hosts presented a certificate from the same"
        Say "   issuer ($($issuers -join ', ')), which cannot happen naturally - they"
        Say '   are behind different CDNs with different CAs.'
    }
    Say '   Something on this network decrypts HTTPS. Consequences:'
    Say '     - swapping hostnames to dodge a block will NOT work; the proxy'
    Say '       reads the whole request, not just the name.'
    Say '     - whoever runs that proxy can allow a host by category or by'
    Say '       name, which is the only thing that will help.'
    Say '     - it also explains large downloads being slow or truncated.'
    Say ''
}
elseif ($withCert.Count -ge 4) {
    Say " No sign of TLS interception: $($issuers.Count) different certificate issuers"
    Say '   across these hosts, which is what an unmolested connection looks like.'
    Say ''
}

if ($ctrl.Count -gt 0 -and $ctrlOk.Count -eq 0) {
    Say ' THE CONTROLS FAILED, so read nothing else here as meaningful.'
    Say '   Even example.com and Modrinth could not be reached, which means'
    Say '   this machine has no general internet right now - or the network'
    Say '   is allow-list only. Nothing we host anywhere will be reachable.'
    Say '   Fix that first; the LAN route (copy from the host) is the answer'
    Say '   in the meantime, and it needs no internet at all.'
    Say ''
}

# ---- Minecraft ----
$mcRows = @($results | Where-Object { $_.Group -eq 'minecraft' })
if ($mcRows.Count -gt 0) {
    $mojang    = @($mcRows | Where-Object { $_.Host -like '*.mojang.com' -and $_.Reached })
    $mcnet     = @($mcRows | Where-Object { $_.Host -like '*.minecraft.net' -and $_.Reached })
    $mojangAll = @($mcRows | Where-Object { $_.Host -like '*.mojang.com' })
    $mcnetAll  = @($mcRows | Where-Object { $_.Host -like '*.minecraft.net' })
    $needed    = @($mcRows | Where-Object { $_.Needed })
    $blocked   = @($needed | Where-Object { -not $_.Reached })

    if ($blocked.Count -eq 0) {
        Say ' MINECRAFT: every host an install needs answered THIS script.'
        Say '   Read that narrowly. It does not mean the launcher can reach them,'
        Say '   and on these machines the Setup tab cannot download versions while'
        Say '   this section says everything is fine. Two different programs:'
        Say ''
        Say '     the Setup tab   the launcher itself, .NET 10, Core\Downloader.cs'
        Say '     the game        java.exe, a separate process with its own rules'
        Say ''
        Say '   So check in this order:'
        Say '     1. the PROXY section above - different stacks, different proxies'
        Say '     2. launcher_errors.txt beside the launcher'
        Say '     3. whether java.exe is filtered (Diagnose-MinecraftNet.ps1)'
    }
    else {
        Say " MINECRAFT blocked: $(($blocked | ForEach-Object { $_.Host }) -join ', ')"

        if ($mojangAll.Count -gt 0 -and $mojang.Count -eq 0 -and $mcnet.Count -gt 0) {
            Say '   Shape: mojang.com is blocked, minecraft.net is not. The libraries'
            Say '   and assets - nearly all of the bytes - are fine; only the manifest'
            Say '   and the client jar need another route.'
        }
        elseif ($mcnetAll.Count -gt 0 -and $mcnet.Count -eq 0 -and $mojang.Count -gt 0) {
            Say '   Shape: minecraft.net is blocked, mojang.com is not. The manifest and'
            Say '   client jar are fine. The 107 libraries can come from Maven Central'
            Say '   instead (95 byte-for-byte); the assets have no alternative source.'
        }
        elseif ($mojang.Count -eq 0 -and $mcnet.Count -eq 0) {
            Say '   Shape: both domains are blocked. No alternative official source'
            Say '   helps. Copy versions\ in by hand, or route through a machine that'
            Say '   can reach them.'
        }

        $dnsBlocked = @($blocked | Where-Object { $_.Dns -eq 'FAILED' -or $_.Dns -eq 'no A record' })
        if ($dnsBlocked.Count -gt 0) {
            Say '   At least one host does not resolve at all, so that block is at DNS.'
            Say '   A different hostname on the same domain will not help.'
        } else {
            Say '   The blocked hosts resolve but will not connect, so something is'
            Say '   filtering rather than DNS - a firewall, a proxy or antivirus.'
        }
    }
    Say ''
}

# ---- fabric ----
$fabRows = @($results | Where-Object { $_.Group -eq 'fabric' })
if ($fabRows.Count -gt 0) {
    $fabOk = @($fabRows | Where-Object { $_.Reached })

    if ($fabOk.Count -eq $fabRows.Count) {
        Say ' FABRIC: this machine can install a loader by itself.'
        Say '   So keeping the loader up to date can happen on each machine'
        Say '   directly, and the host does not have to make and serve a pack.'
    }
    elseif ($fabOk.Count -eq 0) {
        Say ' FABRIC: blocked. This machine cannot install a loader on its own.'
        Say '   Updating the loader here has to go through a pack made on a'
        Say '   machine that can reach Fabric - which is what the Mods tab'
        Say '   MAKE A PACK / OPEN A PACK flow is for.'
    }
    else {
        Say " FABRIC: partly reachable - $(($fabOk | ForEach-Object { $_.Host }) -join ', ') only."
        Say '   That is the worst case to leave alone: the version list can be'
        Say '   read while the library jars cannot, so an install would start'
        Say '   and fail part-way. Treat it as blocked and use a pack.'
    }
    Say ''
}

# ---- hosting ----
$hostRows = @($results | Where-Object { $_.Group -eq 'hosting' })
if ($hostRows.Count -gt 0) {
    $ok = @($hostRows | Where-Object { $_.Reached })
    $no = @($hostRows | Where-Object { -not $_.Reached })

    Say ' HOSTING'
    if ($ok.Count -gt 0) {
        Say "   Reachable: $(($ok | ForEach-Object { $_.Host }) -join ', ')"
    }
    if ($no.Count -gt 0) {
        Say "   Not reachable: $(($no | ForEach-Object { $_.Host }) -join ', ')"
    }
    Say ''

    $gh    = @($hostRows | Where-Object { $_.Host -like '*github*' })
    $ghOk  = @($gh | Where-Object { $_.Reached })
    $assets = @($hostRows | Where-Object { $_.Host -eq 'objects.githubusercontent.com' -and $_.Reached })

    if ($gh.Count -gt 0 -and $ghOk.Count -eq 0) {
        Say '   GitHub is blocked outright, as expected. Releases are out.'
    } elseif ($ghOk.Count -gt 0 -and $assets.Count -eq 0) {
        Say '   github.com works but objects.githubusercontent.com does not, so'
        Say '   Release ASSETS would still fail - that is where the bytes come from.'
        Say '   Worth knowing: this is the one case that looks like it works and'
        Say '   then does not.'
    } elseif ($assets.Count -gt 0) {
        Say '   GitHub Releases would work from this machine after all - both'
        Say '   github.com and the asset host are reachable. Re-test before relying'
        Say '   on it, since a filter can be changed at any time.'
    }

    # The thing they would actually use is a custom domain in front of R2, and
    # that cannot be tested before it exists. Say so rather than imply otherwise.
    $cf = @($hostRows | Where-Object { $_.Host -eq 'cloudflare.com' -and $_.Reached })
    $r2 = @($hostRows | Where-Object { $_.Host -eq 'r2.cloudflarestorage.com' -and $_.Reached })
    if ($cf.Count -gt 0 -or $r2.Count -gt 0) {
        Say ''
        Say '   Cloudflare answers here, which is a good sign for R2 - but note'
        Say '   what this does and does not prove. A real deployment would put'
        Say '   your own domain in front of the bucket, and a brand-new domain'
        Say '   cannot be tested until it exists. These rows say Cloudflare itself'
        Say '   is not blocked; they do not promise your eventual hostname is not.'
    }

    $stores = @($hostRows | Where-Object {
        $_.Reached -and ($_.Host -like '*amazonaws*' -or $_.Host -like '*googleapis*' -or $_.Host -like '*blob.core.windows.net')
    })
    if ($stores.Count -gt 0) {
        Say ''
        Say "   Big object stores reachable: $(($stores | ForEach-Object { $_.Host }) -join ', ')"
        Say '   These are the safest bet against a category filter, because'
        Say '   blocking them breaks a great deal of unrelated traffic.'
    }

    Say ''
    Say '   Remember the cheapest answer: once ONE machine has the payload, every'
    Say '   other machine can pull it from that host over the LAN, where no filter'
    Say '   applies. An internet source is only needed for the first machine at a'
    Say '   site - and a USB stick does that job today.'
}

Say '================================================================'
Say ''
Say 'See HANDOFF.md section 12.'
Say ''

# Added before the file is written, so the report says where it is too.
$script:report.Add('')
$script:report.Add('A copy of all of the above was saved to:')
$script:report.Add("  $Out")

if (SaveReport) {
    Write-Output 'A copy of all of that has been saved to:'
    Write-Output "  $Out"
    Write-Output ''
    Write-Output 'Send that file rather than retyping any of it.'
}

# Without this, running the script by double-clicking it closes the window the
# instant it finishes and the results are gone.
if (-not $NoPause -and -not [Console]::IsOutputRedirected) {
    Say ''
    Read-Host 'Press Enter to close'
}
