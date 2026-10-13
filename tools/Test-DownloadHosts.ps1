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
    [switch]$MinecraftOnly
)

$ErrorActionPreference = 'Continue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ConnectTimeoutMs = 8000

$targets = @(
    # ---- what a Minecraft install needs -------------------------------
    @{ Group='minecraft'; Host='piston-meta.mojang.com';           What='manifests + version JSON';      Needed=$true;  Url='https://piston-meta.mojang.com/mc/game/version_manifest_v2.json' }
    @{ Group='minecraft'; Host='launchermeta.mojang.com';          What='older alias for the same';      Needed=$false; Url='https://launchermeta.mojang.com/mc/game/version_manifest_v2.json' }
    @{ Group='minecraft'; Host='piston-data.mojang.com';           What='the client jar';                Needed=$true;  Url='https://piston-data.mojang.com/v1/objects/4e618f09a0c649dde3fdf829df443ce0b8831e65/client.jar' }
    @{ Group='minecraft'; Host='libraries.minecraft.net';          What='107 library jars';              Needed=$true;  Url='https://libraries.minecraft.net/com/mojang/brigadier/1.3.10/brigadier-1.3.10.jar' }
    @{ Group='minecraft'; Host='resources.download.minecraft.net'; What='the assets';                    Needed=$true;  Url='https://resources.download.minecraft.net/b6/b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356' }
    @{ Group='minecraft'; Host='repo1.maven.org';                  What='95 of 107 libs, as a fallback'; Needed=$false; Url='https://repo1.maven.org/maven2/com/google/code/gson/gson/2.11.0/gson-2.11.0.jar.sha1' }

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
    if ($t.Length -gt 28) { $t = $t.Substring(0, 28) + '...' }
    return $t
}

$results = @()

foreach ($t in $targets) {
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
    Write-Output ''
    Write-Output "---- $title ----"
    Write-Output (($rows | Format-Table Host, What, Dns, Tls, Issuer, Http -AutoSize | Out-String).TrimEnd())
}

Show 'control'   'CONTROLS (these should pass)'
Show 'minecraft' 'WHAT A MINECRAFT INSTALL NEEDS'
Show 'hosting'   'WHERE WE COULD HOST A PAYLOAD'

Write-Output ''
Write-Output '================================================================'

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
    Write-Output ' TLS IS BEING INSPECTED.'
    if ($named.Count -gt 0) {
        Write-Output "   The certificates name it: $($named -join ', ')"
    } else {
        Write-Output "   All $($withCert.Count) hosts presented a certificate from the same"
        Write-Output "   issuer ($($issuers -join ', ')), which cannot happen naturally - they"
        Write-Output '   are behind different CDNs with different CAs.'
    }
    Write-Output '   Something on this network decrypts HTTPS. Consequences:'
    Write-Output '     - swapping hostnames to dodge a block will NOT work; the proxy'
    Write-Output '       reads the whole request, not just the name.'
    Write-Output '     - whoever runs that proxy can allow a host by category or by'
    Write-Output '       name, which is the only thing that will help.'
    Write-Output '     - it also explains large downloads being slow or truncated.'
    Write-Output ''
}
elseif ($withCert.Count -ge 4) {
    Write-Output " No sign of TLS interception: $($issuers.Count) different certificate issuers"
    Write-Output '   across these hosts, which is what an unmolested connection looks like.'
    Write-Output ''
}

if ($ctrl.Count -gt 0 -and $ctrlOk.Count -eq 0) {
    Write-Output ' THE CONTROLS FAILED, so read nothing else here as meaningful.'
    Write-Output '   Even example.com and Modrinth could not be reached, which means'
    Write-Output '   this machine has no general internet right now - or the network'
    Write-Output '   is allow-list only. Nothing we host anywhere will be reachable.'
    Write-Output '   Fix that first; the LAN route (copy from the host) is the answer'
    Write-Output '   in the meantime, and it needs no internet at all.'
    Write-Output ''
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
        Write-Output ' MINECRAFT: everything an install needs is reachable from here.'
        Write-Output '   So a download failure is not this machine being blocked - look at'
        Write-Output '   whether java.exe itself is filtered (Diagnose-MinecraftNet.ps1).'
    }
    else {
        Write-Output " MINECRAFT blocked: $(($blocked | ForEach-Object { $_.Host }) -join ', ')"

        if ($mojangAll.Count -gt 0 -and $mojang.Count -eq 0 -and $mcnet.Count -gt 0) {
            Write-Output '   Shape: mojang.com is blocked, minecraft.net is not. The libraries'
            Write-Output '   and assets - nearly all of the bytes - are fine; only the manifest'
            Write-Output '   and the client jar need another route.'
        }
        elseif ($mcnetAll.Count -gt 0 -and $mcnet.Count -eq 0 -and $mojang.Count -gt 0) {
            Write-Output '   Shape: minecraft.net is blocked, mojang.com is not. The manifest and'
            Write-Output '   client jar are fine. The 107 libraries can come from Maven Central'
            Write-Output '   instead (95 byte-for-byte); the assets have no alternative source.'
        }
        elseif ($mojang.Count -eq 0 -and $mcnet.Count -eq 0) {
            Write-Output '   Shape: both domains are blocked. No alternative official source'
            Write-Output '   helps. Copy versions\ in by hand, or route through a machine that'
            Write-Output '   can reach them.'
        }

        $dnsBlocked = @($blocked | Where-Object { $_.Dns -eq 'FAILED' -or $_.Dns -eq 'no A record' })
        if ($dnsBlocked.Count -gt 0) {
            Write-Output '   At least one host does not resolve at all, so that block is at DNS.'
            Write-Output '   A different hostname on the same domain will not help.'
        } else {
            Write-Output '   The blocked hosts resolve but will not connect, so something is'
            Write-Output '   filtering rather than DNS - a firewall, a proxy or antivirus.'
        }
    }
    Write-Output ''
}

# ---- hosting ----
$hostRows = @($results | Where-Object { $_.Group -eq 'hosting' })
if ($hostRows.Count -gt 0) {
    $ok = @($hostRows | Where-Object { $_.Reached })
    $no = @($hostRows | Where-Object { -not $_.Reached })

    Write-Output ' HOSTING'
    if ($ok.Count -gt 0) {
        Write-Output "   Reachable: $(($ok | ForEach-Object { $_.Host }) -join ', ')"
    }
    if ($no.Count -gt 0) {
        Write-Output "   Not reachable: $(($no | ForEach-Object { $_.Host }) -join ', ')"
    }
    Write-Output ''

    $gh    = @($hostRows | Where-Object { $_.Host -like '*github*' })
    $ghOk  = @($gh | Where-Object { $_.Reached })
    $assets = @($hostRows | Where-Object { $_.Host -eq 'objects.githubusercontent.com' -and $_.Reached })

    if ($gh.Count -gt 0 -and $ghOk.Count -eq 0) {
        Write-Output '   GitHub is blocked outright, as expected. Releases are out.'
    } elseif ($ghOk.Count -gt 0 -and $assets.Count -eq 0) {
        Write-Output '   github.com works but objects.githubusercontent.com does not, so'
        Write-Output '   Release ASSETS would still fail - that is where the bytes come from.'
        Write-Output '   Worth knowing: this is the one case that looks like it works and'
        Write-Output '   then does not.'
    } elseif ($assets.Count -gt 0) {
        Write-Output '   GitHub Releases would work from this machine after all - both'
        Write-Output '   github.com and the asset host are reachable. Re-test before relying'
        Write-Output '   on it, since a filter can be changed at any time.'
    }

    # The thing they would actually use is a custom domain in front of R2, and
    # that cannot be tested before it exists. Say so rather than imply otherwise.
    $cf = @($hostRows | Where-Object { $_.Host -eq 'cloudflare.com' -and $_.Reached })
    $r2 = @($hostRows | Where-Object { $_.Host -eq 'r2.cloudflarestorage.com' -and $_.Reached })
    if ($cf.Count -gt 0 -or $r2.Count -gt 0) {
        Write-Output ''
        Write-Output '   Cloudflare answers here, which is a good sign for R2 - but note'
        Write-Output '   what this does and does not prove. A real deployment would put'
        Write-Output '   your own domain in front of the bucket, and a brand-new domain'
        Write-Output '   cannot be tested until it exists. These rows say Cloudflare itself'
        Write-Output '   is not blocked; they do not promise your eventual hostname is not.'
    }

    $stores = @($hostRows | Where-Object {
        $_.Reached -and ($_.Host -like '*amazonaws*' -or $_.Host -like '*googleapis*' -or $_.Host -like '*blob.core.windows.net')
    })
    if ($stores.Count -gt 0) {
        Write-Output ''
        Write-Output "   Big object stores reachable: $(($stores | ForEach-Object { $_.Host }) -join ', ')"
        Write-Output '   These are the safest bet against a category filter, because'
        Write-Output '   blocking them breaks a great deal of unrelated traffic.'
    }

    Write-Output ''
    Write-Output '   Remember the cheapest answer: once ONE machine has the payload, every'
    Write-Output '   other machine can pull it from that host over the LAN, where no filter'
    Write-Output '   applies. An internet source is only needed for the first machine at a'
    Write-Output '   site - and a USB stick does that job today.'
}

Write-Output '================================================================'
Write-Output ''
Write-Output 'Copy all of the above. See HANDOFF.md section 12.'
