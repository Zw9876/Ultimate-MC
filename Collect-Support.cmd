@echo off
REM ============================================================================
REM  Collect-Support.cmd
REM
REM  DOUBLE-CLICK THIS when something is wrong with the launcher.
REM
REM  It gathers everything needed to diagnose the problem into one zip file and
REM  tells you where that file is. Send that one file - there is nothing else to
REM  go and find.
REM
REM  YOU DO NOT NEED ADMINISTRATOR.
REM
REM  IT CHANGES NOTHING. Every step is a read or a copy into a new folder of its
REM  own. It starts nothing, stops nothing, installs nothing and deletes nothing.
REM
REM  IT DOES NOT COLLECT your worlds, your screenshots or your resource packs.
REM  It does collect config.txt, which holds the Minecraft name last used on this
REM  machine - that is how the machine gets identified.
REM
REM  Optionally pass the launcher folder, if this file is not sitting in it:
REM      Collect-Support.cmd D:\Ultimate-MC-1
REM
REM  It exists because a .ps1 will not run on double-click under default Windows
REM  settings, so this calls it in a way that will.
REM ============================================================================

setlocal

REM Looks beside this file first, then in tools\ - so this works whether it sits
REM at the launcher root or has been copied around with the tools folder.
set PS1=%~dp0Collect-Support.ps1
if not exist "%PS1%" set PS1=%~dp0tools\Collect-Support.ps1

if not exist "%PS1%" (
  echo.
  echo   ERROR: Collect-Support.ps1 is missing.
  echo   It must sit either beside this file or in a tools\ folder next to it.
  echo.
  pause
  exit /b 2
)

echo.
echo  Collecting. This takes a few seconds - longer if there are many mods,
echo  because each one is hashed so they can be compared against the host.
echo.

if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -Root "%~1"
)

if errorlevel 2 (
  echo.
  echo   Nothing was collected - read the message above.
  echo.
)

endlocal
