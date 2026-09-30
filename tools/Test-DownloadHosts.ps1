# Which of the hosts a Minecraft install needs can THIS machine actually reach?
#
#   powershell -ExecutionPolicy Bypass -File Test-DownloadHosts.ps1
#
# Run this on a machine where downloading Minecraft fails. It is read-only and
# needs no admin.
#
# WHY IT IS PER-HOST. Installing Minecraft touches four hosts across TWO separate
# domains, and which of them are blocked decides the fix completely:
#
#   piston-meta.mojang.com            manifests, version JSON, asset index
#   piston-data.mojang.com            the client jar
#   libraries.minecraft.net           107 library jars
#   resources.download.minecraft.net  the assets, thousands of files
#
# "Minecraft won't download" is not one failure. If only mojang.com is blocked, the
# libraries and assets - the overwhelming bulk of it - are still reachable. If only
# one hostname is blocked, a different official hostname may serve the same bytes.
#
# The probe URLs are content-addressed objects, so they are immutable and will keep
# working; the two manifests are live and byte-identical to each other (checked).
#
# Each row separates DNS from the connection on purpose. Resolving but not
# connecting is a firewall or filter; not resolving at all is DNS-level blocking,
# and then no alternative hostname on the same domain will help either.

$ErrorActionPreference = 'Continue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$targets = @(
    @{ Host='piston-meta.mojang.com';           What='manifests + version JSON';  Url='https://piston-meta.mojang.com/mc/game/version_manifest_v2.json'; Needed=$true }
    @{ Host='launchermeta.mojang.com';          What='older alias for the same';   Url='https://launchermeta.mojang.com/mc/game/version_manifest_v2.json'; Needed=$false }
    @{ Host='piston-data.mojang.com';           What='the client jar';             Url='https://piston-data.mojang.com/v1/objects/4e618f09a0c649dde3fdf829df443ce0b8831e65/client.jar'; Needed=$true }
    @{ Host='libraries.minecraft.net';          What='107 library jars';           Url='https://libraries.minecraft.net/com/mojang/brigadier/1.3.10/brigadier-1.3.10.jar'; Needed=$true }
    @{ Host='resources.download.minecraft.net'; What='the assets';                 Url='https://resources.download.minecraft.net/b6/b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356'; Needed=$true }
    @{ Host='repo1.maven.org';                  What='95 of 107 libs, as a fallback'; Url='https://repo1.maven.org/maven2/com/google/code/gson/gson/2.11.0/gson-2.11.0.jar.sha1'; Needed=$false }
    @{ Host='api.modrinth.com';                 What='mods - known to work here';  Url='https://api.modrinth.com/v2/tag/loader'; Needed=$false }
)

$results = @()

foreach ($t in $targets) {
    $row = [ordered]@{ Host=$t.Host; What=$t.What; Dns='-'; Http='-'; Needed=$t.Needed; Reached=$false }

    try {
        $ip = [Net.Dns]::GetHostAddresses($t.Host) |
              Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1
        $row.Dns = if ($ip) { $ip.IPAddressToString } else { 'no A record' }
    } catch { $row.Dns = 'FAILED' }

    if ($row.Dns -ne 'FAILED' -and $row.Dns -ne 'no A record') {
        try {
            # Range so this costs a kilobyte, not a 30 MB client jar.
            $req = [Net.HttpWebRequest]::Create($t.Url)
            $req.Timeout = 15000
            $req.UserAgent = 'MinecraftPortableLauncher connectivity check'
            $req.AddRange(0, 1023)
            $resp = $req.GetResponse()
            $row.Http = [int]$resp.StatusCode
            $row.Reached = $true
            $resp.Close()
        } catch [Net.WebException] {
            $r = $_.Exception.Response
            if ($r) {
                # A status, even a rejection, means the connection got there.
                $row.Http = [int]$r.StatusCode
                $row.Reached = ([int]$r.StatusCode -lt 400)
            } else {
                $row.Http = $_.Exception.Status.ToString()
            }
        } catch {
            $row.Http = 'error'
        }
    }

    $results += [pscustomobject]$row
}

Write-Host ''
$results | Format-Table Host, What, Dns, Http, Reached -AutoSize

# ---- what it means ----
Write-Host ''
Write-Host '================================================================'

$mojang    = @($results | Where-Object { $_.Host -like '*.mojang.com' -and $_.Reached })
$mcnet     = @($results | Where-Object { $_.Host -like '*.minecraft.net' -and $_.Reached })
$mojangAll = @($results | Where-Object { $_.Host -like '*.mojang.com' })
$mcnetAll  = @($results | Where-Object { $_.Host -like '*.minecraft.net' })
$needed    = @($results | Where-Object { $_.Needed })
$blocked   = @($needed | Where-Object { -not $_.Reached })

if ($blocked.Count -eq 0) {
    Write-Host ' Everything a Minecraft install needs is reachable from here.' -ForegroundColor Green
    Write-Host ' So a download failure is not this machine being blocked - look at'
    Write-Host ' whether java.exe itself is being filtered (Diagnose-MinecraftNet.ps1).'
}
else {
    Write-Host " Blocked: $(($blocked | ForEach-Object { $_.Host }) -join ', ')" -ForegroundColor Yellow
    Write-Host ''

    if ($mojangAll.Count -gt 0 -and $mojang.Count -eq 0 -and $mcnet.Count -gt 0) {
        Write-Host ' Shape: mojang.com is blocked, minecraft.net is not.' -ForegroundColor Cyan
        Write-Host ' That means the libraries and assets - nearly all of the bytes - are'
        Write-Host ' fine, and only the manifest and the client jar need another route.'
    }
    elseif ($mcnetAll.Count -gt 0 -and $mcnet.Count -eq 0 -and $mojang.Count -gt 0) {
        Write-Host ' Shape: minecraft.net is blocked, mojang.com is not.' -ForegroundColor Cyan
        Write-Host ' The manifest and client jar are fine. The 107 libraries can come from'
        Write-Host ' Maven Central instead (95 of them are there byte-for-byte); the assets'
        Write-Host ' have no alternative source and must be copied in.'
    }
    elseif ($mojang.Count -eq 0 -and $mcnet.Count -eq 0) {
        Write-Host ' Shape: both domains are blocked.' -ForegroundColor Cyan
        Write-Host ' No alternative official source helps. Copy versions\ in by hand, or'
        Write-Host ' route through a machine that can reach them.'
    }

    $dnsBlocked = @($blocked | Where-Object { $_.Dns -eq 'FAILED' -or $_.Dns -eq 'no A record' })
    if ($dnsBlocked.Count -gt 0) {
        Write-Host ''
        Write-Host ' At least one host does not resolve at all, so the block is at DNS.' -ForegroundColor Cyan
        Write-Host ' Swapping to a different hostname on the same domain will not help.'
    }
    elseif ($blocked.Count -gt 0) {
        Write-Host ''
        Write-Host ' The blocked hosts resolve but will not connect, so something is'  -ForegroundColor Cyan
        Write-Host ' filtering rather than DNS. A firewall or antivirus rule, and a'
        Write-Host ' different hostname on the same domain is worth trying.'
    }
}

Write-Host '================================================================'
Write-Host ''
Write-Host 'Copy all of the above. See HANDOFF.md section 12.'
