@echo off
REM ============================================================================
REM  Fix-Antivirus.cmd  -  stop Defender quarantining this launcher
REM
REM  Put this file in the SAME FOLDER as MinecraftLauncher.exe, then
REM  right-click it and choose "Run as administrator". Double-clicking works
REM  too - it asks for admin itself and Windows shows the usual prompt.
REM
REM  It excludes this folder from Defender, and restores launcher files that
REM  were already quarantined. It restores ONLY files from this folder: a
REM  blanket MpCmdRun -Restore -All would also bring back anything genuinely
REM  malicious that Defender had correctly caught.
REM
REM  What it costs: Defender stops scanning this folder, including versions\,
REM  servers\ and mods downloaded from Modrinth. That is a real reduction in
REM  protection, and the right trade only on a machine you own, and only while
REM  the launcher is unsigned. HANDOFF.md section 15 has the alternatives.
REM
REM  The PowerShell below is one line on purpose, with no pipe characters and no
REM  inner double quotes. It sits inside a cmd double-quoted string, where a |
REM  passes through literally, an escaped ^| arrives as two characters, and a \"
REM  flips cmd's own quote tracking. foreach and concatenation do the same jobs
REM  without tripping any of that.
REM ============================================================================

net session >nul 2>&1
if not errorlevel 1 goto :elevated

echo Asking for administrator rights...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b

:elevated
echo.
echo Launcher folder: %~dp0

if not exist "%~dp0MinecraftLauncher.exe" (
  echo.
  echo   ERROR: MinecraftLauncher.exe is not in this folder.
  echo   Put this file next to the launcher and run it again.
  echo.
  pause
  exit /b 2
)

echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$d = '%~dp0'.TrimEnd('\'); Add-MpPreference -ExclusionPath $d -ErrorAction Stop; Write-Host ('  excluded ' + $d) -ForegroundColor Green; $mp = ($env:ProgramFiles + '\Windows Defender\MpCmdRun.exe'); $hits = @(); foreach ($t in @(Get-MpThreatDetection -ErrorAction SilentlyContinue)) { foreach ($res in @($t.Resources)) { $p = $res -replace '^file:_', ''; if ($p.StartsWith($d, 'OrdinalIgnoreCase')) { $hits += $p } } }; if ($hits.Count -eq 0) { Write-Host '  nothing from this folder was quarantined' -ForegroundColor Green } else { foreach ($p in $hits) { Write-Host ('  restoring ' + $p) -ForegroundColor Yellow; $null = & $mp -Restore -FilePath $p } }"
if errorlevel 1 goto :failed

echo.
echo Done. Start the launcher normally.
echo.
pause
exit /b 0

:failed
echo.
echo That did not work. Check you answered Yes to the admin prompt.
echo.
pause
exit /b 1
