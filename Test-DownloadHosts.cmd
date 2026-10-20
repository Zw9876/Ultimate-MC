@echo off
REM ============================================================================
REM  Test-DownloadHosts.cmd
REM
REM  DOUBLE-CLICK THIS to find out what this computer can actually download.
REM
REM  It checks the Minecraft download hosts, the Fabric hosts, and a handful of
REM  places a payload could be hosted - and then, which is the part that matters,
REM  it downloads a REAL file in full and checks its hash. Everything else in the
REM  report only asks for a kilobyte, and a network that scans large downloads
REM  will pass that and still ruin a real one.
REM
REM  It takes a few minutes. A blocked host has to time out before it can be
REM  called blocked, and the full download is a genuine download.
REM
REM  YOU DO NOT NEED ADMINISTRATOR.
REM
REM  IT CHANGES NOTHING. It reads, connects and downloads one file to memory.
REM  It installs nothing, saves no game files and alters no settings.
REM
REM  It writes download-hosts-report.txt next to itself and waits for you to
REM  press Enter, so the window cannot close on the results.
REM
REM  It exists because a .ps1 will not run on double-click under default Windows
REM  settings, so this calls it in a way that will.
REM ============================================================================

setlocal

set PS1=%~dp0Test-DownloadHosts.ps1
if not exist "%PS1%" set PS1=%~dp0tools\Test-DownloadHosts.ps1

if not exist "%PS1%" (
  echo.
  echo   ERROR: Test-DownloadHosts.ps1 is missing.
  echo   It must sit either beside this file or in a tools\ folder next to it.
  echo.
  pause
  exit /b 2
)

echo.
echo  Checking what this computer can reach. This takes a few minutes.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*

endlocal
