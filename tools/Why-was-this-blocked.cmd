@echo off
REM ============================================================================
REM  Why-was-this-blocked.cmd
REM
REM  Double-click this if Windows Defender blocked, deleted or quarantined the
REM  launcher. It asks Defender what it caught and writes the answer to a file
REM  you can send on.
REM
REM  YOU DO NOT NEED ADMINISTRATOR. Everything it asks for reads fine as an
REM  ordinary user.
REM
REM  IT CHANGES NOTHING. It only reads. It does not turn anything off, does not
REM  add an exclusion, and does not restore anything - a script that did any of
REM  that would be treated as a virus in its own right, which is exactly the
REM  problem this is here to diagnose.
REM
REM  It exists because a .ps1 will not run on double-click under the default
REM  Windows settings, so this calls it in a way that will.
REM ============================================================================

setlocal
set REPORT=%~dp0antivirus-report.txt

if not exist "%~dp0Get-DefenderDetection.ps1" (
  echo.
  echo   ERROR: Get-DefenderDetection.ps1 is missing.
  echo   It must sit in the same folder as this file.
  echo.
  pause
  exit /b 2
)

echo.
echo Asking Windows Defender what it blocked. This takes a few seconds.
echo.

REM Twice, on purpose. A detection's resource can be a whole command line running
REM to thousands of characters: that belongs in the file, where length is free,
REM but typing it to the screen buries the one line anybody needs to read.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Get-DefenderDetection.ps1" -Full > "%REPORT%" 2>&1

if not exist "%REPORT%" (
  echo.
  echo   Could not write the report. Copy this folder somewhere you can write
  echo   to - your Desktop will do - and run it again.
  echo.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Get-DefenderDetection.ps1"

echo.
echo ----------------------------------------------------------------
echo  Saved to: %REPORT%
echo  Send that file to whoever set the launcher up.
echo ----------------------------------------------------------------
echo.
pause
