# What did Defender actually flag, and by which mechanism?
#
#   tools\Get-DefenderDetection.ps1
#   tools\Get-DefenderDetection.ps1 -Last 10
#
# Run this on the machine that flagged something, as soon after as you can.
# READ-ONLY: it changes nothing, so it will not trip the behaviour classifier the
# way a script that adds an exclusion does (see HANDOFF.md section 15).
#
# The detection NAME decides the fix, and the RESOURCE type decides where to look:
#
#   containerfile:_ / file:_   a file was scanned - the usual case
#   CmdLine:_                  a command line was scanned, not a file at all
#   webfile:_                  it was caught as it downloaded
#
# A name ending !ml or !MTB came from a classifier rather than a signature, which
# is what an unsigned self-updating binary attracts. A name starting Behavior:
# means it was caught doing something at run time, so excluding the folder helps
# but reporting it as a false positive will not.

# ADMIN IS NOT NEEDED. Everything that matters — the threat name, what it caught
# and where — reads fine as an ordinary user. Only the list of existing exclusions
# needs elevation, and this does not print it.
#
# -Full keeps whole resource strings instead of trimming them for the console. The
# .cmd wrapper passes it, because its output goes to a file where length is free.

param(
    [int]$Last = 5,
    [switch]$Full
)

$line = '=' * 68

Write-Output $line
Write-Output " Defender state"
Write-Output $line

$s = Get-MpComputerStatus
'{0,-26}: {1}' -f 'Tamper protection', $s.IsTamperProtected
'{0,-26}: {1}' -f 'Real-time protection', $s.RealTimeProtectionEnabled
'{0,-26}: {1}' -f 'Behaviour monitoring', $s.BehaviorMonitorEnabled
'{0,-26}: {1}' -f 'Signature version', $s.AntivirusSignatureVersion
'{0,-26}: {1}' -f 'Signatures last updated', $s.AntivirusSignatureLastUpdated

$p = Get-MpPreference
'{0,-26}: {1}' -f 'Cloud protection (MAPS)', $p.MAPSReporting
'{0,-26}: {1}' -f 'Cloud block level', $p.CloudBlockLevel
'{0,-26}: {1}' -f 'PUA protection', $p.PUAProtection

Write-Output ''
Write-Output $line
Write-Output " Named threats"
Write-Output $line

$threats = @(Get-MpThreat -ErrorAction SilentlyContinue)
if ($threats.Count -eq 0) {
    Write-Output ' none recorded on this machine'
} else {
    foreach ($t in $threats) {
        Write-Output ''
        '{0,-14}: {1}' -f 'Name', $t.ThreatName
        '{0,-14}: {1}' -f 'Severity', $t.SeverityID
        '{0,-14}: {1}' -f 'Still active', $t.IsActive
        '{0,-14}: {1}' -f 'Did it run', $t.DidThreatExecute
        foreach ($r in @($t.Resources)) { '{0,-14}: {1}' -f 'Resource', $r }
    }
}

Write-Output ''
Write-Output $line
Write-Output " Detections, newest first"
Write-Output $line

$det = @(Get-MpThreatDetection -ErrorAction SilentlyContinue |
         Sort-Object InitialDetectionTime -Descending |
         Select-Object -First $Last)

if ($det.Count -eq 0) {
    Write-Output ' none recorded'
    Write-Output ''
    Write-Output ' If a DOWNLOAD was blocked but nothing is listed here, it was'
    Write-Output ' SmartScreen in the browser, not Defender. Look in the browser''s'
    Write-Output ' own downloads list instead - that is a different fix.'
} else {
    foreach ($d in $det) {
        Write-Output ''
        '{0,-14}: {1}' -f 'When', $d.InitialDetectionTime
        '{0,-14}: {1}' -f 'Threat ID', $d.ThreatID
        '{0,-14}: {1}' -f 'Action ok', $d.ActionSuccess
        '{0,-14}: {1}' -f 'Process', $d.ProcessName
        foreach ($r in @($d.Resources)) {
            # A resource can be a whole command line — thousands of characters. Worth
            # keeping in a file, not worth scrolling past in a console.
            $text = if (-not $Full -and $r.Length -gt 300) {
                $r.Substring(0, 300) + ' ...[truncated, use -Full]'
            } else { $r }
            '{0,-14}: {1}' -f 'Resource', $text
        }
    }
}

Write-Output ''
Write-Output $line
Write-Output ' Copy everything above. The name and the resource type are what matter.'
Write-Output $line
