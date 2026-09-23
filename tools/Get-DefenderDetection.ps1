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

param([int]$Last = 5)

$line = '=' * 68

Write-Host $line
Write-Host " Defender state"
Write-Host $line

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

Write-Host ''
Write-Host $line
Write-Host " Named threats"
Write-Host $line

$threats = @(Get-MpThreat -ErrorAction SilentlyContinue)
if ($threats.Count -eq 0) {
    Write-Host ' none recorded on this machine'
} else {
    foreach ($t in $threats) {
        Write-Host ''
        '{0,-14}: {1}' -f 'Name', $t.ThreatName
        '{0,-14}: {1}' -f 'Severity', $t.SeverityID
        '{0,-14}: {1}' -f 'Still active', $t.IsActive
        '{0,-14}: {1}' -f 'Did it run', $t.DidThreatExecute
        foreach ($r in @($t.Resources)) { '{0,-14}: {1}' -f 'Resource', $r }
    }
}

Write-Host ''
Write-Host $line
Write-Host " Detections, newest first"
Write-Host $line

$det = @(Get-MpThreatDetection -ErrorAction SilentlyContinue |
         Sort-Object InitialDetectionTime -Descending |
         Select-Object -First $Last)

if ($det.Count -eq 0) {
    Write-Host ' none recorded'
    Write-Host ''
    Write-Host ' If a DOWNLOAD was blocked but nothing is listed here, it was'
    Write-Host ' SmartScreen in the browser, not Defender. Look in the browser''s'
    Write-Host ' own downloads list instead - that is a different fix.'
} else {
    foreach ($d in $det) {
        Write-Host ''
        '{0,-14}: {1}' -f 'When', $d.InitialDetectionTime
        '{0,-14}: {1}' -f 'Threat ID', $d.ThreatID
        '{0,-14}: {1}' -f 'Action ok', $d.ActionSuccess
        '{0,-14}: {1}' -f 'Process', $d.ProcessName
        foreach ($r in @($d.Resources)) {
            # The resource can be a whole command line, which is worth seeing in
            # full but is not worth 4000 characters of console.
            $text = if ($r.Length -gt 300) { $r.Substring(0, 300) + ' ...[truncated]' } else { $r }
            '{0,-14}: {1}' -f 'Resource', $text
        }
    }
}

Write-Host ''
Write-Host $line
Write-Host ' Copy everything above. The name and the resource type are what matter.'
Write-Host $line
