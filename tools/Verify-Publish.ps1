# Does the launcher at the repo root actually serve updates?
#
#   tools\Verify-Publish.ps1
#   tools\Verify-Publish.ps1 -Expect 1.2.265.376
#
# Run this after every publish. It catches the one failure that is otherwise
# silent: a framework-dependent build sits at the root looking perfectly fine,
# answers /launcher/manifest with 404, and every other machine reports "found a
# skin server, but it cannot offer updates" — which nobody sees until rollout day.

param(
    [string]$Expect,          # version the manifest must carry; defaults to the exe's own
    [int]$Port = 25567
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'MinecraftLauncher.exe'
$root = [Windows.Automation.AutomationElement]::RootElement

if (-not (Test-Path $exe)) { Write-Host "No launcher at $exe" -ForegroundColor Red; exit 2 }
if (-not $Expect) { $Expect = (Get-Item $exe).VersionInfo.FileVersion }

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

$pass = 0; $fail = 0
function Check($what, $ok, $detail) {
    if ($ok) { $script:pass++; Write-Host "  ok    $what" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL  $what  -> $detail" -ForegroundColor Red }
}

Write-Host "verifying $exe (expecting $Expect)"

# A leftover launcher would hold the port and make this meaningless.
Get-Process MinecraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 16

$w = $root.FindFirst([Windows.Automation.TreeScope]::Children,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))

Check 'the published launcher starts' ($null -ne $w) 'no window appeared'
if (-not $w) { exit 1 }

if ((ById $w 'VersionLabel')) { Write-Host "  sidebar says: $((ById $w 'VersionLabel').Current.Name)" }

(ByName $w 'SKINS').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 2
(ById $w 'SkinServerToggle').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 5

$status = (ById $w 'SkinServerStatus').Current.Name
Write-Host "  $status"
Check 'the skin server started' ($status -match 'running') $status

try {
    $m = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/launcher/manifest" -TimeoutSec 10
    Check 'the manifest endpoint answers' $true ''
    Write-Host "  manifest: version $($m.version), $($m.files.Count) files, compression $($m.compression)"

    Check 'the manifest carries the expected version' ($m.version -eq $Expect) $m.version
    Check 'it advertises all 6 package files' ($m.files.Count -eq 6) $m.files.Count
    Check 'gzip is advertised' ($m.compression -eq 'gzip') $m.compression

    $listed = $m.files | Where-Object { $_.name -eq 'MinecraftLauncher.exe' }
    Check 'the exe is listed with a size' ($listed -and $listed.size -gt 100MB) `
          $(if ($listed) { $listed.size } else { 'missing' })

    # Pull it back and prove it is the real file, not a truncated or wrong one.
    $tmp = Join-Path $env:TEMP 'verify-publish-probe.bin'
    Invoke-WebRequest -Uri "http://127.0.0.1:$Port/launcher/file/MinecraftLauncher.exe" `
                      -OutFile $tmp -TimeoutSec 300
    $bytes = [IO.File]::ReadAllBytes($tmp)

    Check 'it serves its own exe back in full' ($bytes.Length -eq $listed.size) "$($bytes.Length) vs $($listed.size)"
    Check 'the served exe has an intact MZ header' (($bytes[0] -eq 0x4D) -and ($bytes[1] -eq 0x5A)) "$($bytes[0]),$($bytes[1])"
    Check 'the served exe is byte-for-byte the one on disk' `
          ((Get-FileHash $tmp -Algorithm SHA256).Hash -eq (Get-FileHash $exe -Algorithm SHA256).Hash) 'hash differs'

    Remove-Item $tmp -ErrorAction SilentlyContinue
}
catch {
    Check 'the manifest endpoint answers' $false $_.Exception.Message
}

Get-Process MinecraftLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host ''
Write-Host "$pass passed, $fail failed" -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
