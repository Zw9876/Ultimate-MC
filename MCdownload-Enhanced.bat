@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

REM ================================
REM === CHECK FOR POWERSHELL ======
REM ================================
set "PSExe=powershell"
where powershell >nul 2>&1 || (
    set "PSExe=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
    if not exist "%PSExe%" (
        echo [ERROR] PowerShell is not installed or not found on the system.
        pause
        exit /b
    )
)
echo [INFO] Using PowerShell from: %PSExe%

REM ================================
REM === CONFIGURATION SETUP =======
REM ================================
set "manifestFile=version_manifest_v2.json"
set "manifestURL=https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"
set "currentDir=%cd%"

set "versionsDir=%currentDir%\versions"

REM ================================
REM === DOWNLOAD MANIFEST FILE ====
REM ================================
if not exist "%manifestFile%" (
    echo [INFO] Downloading manifest...
    %PSExe% -Command "Invoke-WebRequest -Uri '%manifestURL%' -OutFile '%manifestFile%'"
    if errorlevel 1 (
        echo [ERROR] Failed to download manifest.
        pause
        exit /b
    )
)

REM ================================
REM === DISPLAY VERSION OPTIONS ===
REM ================================
:MENU
cls
echo.
echo ========================================
echo    MINECRAFT VERSION DOWNLOADER
echo ========================================
echo.
echo Select an option:
echo   1. List LATEST versions (first 20)
echo   2. List ALL RELEASE versions
echo   3. List ALL SNAPSHOT versions
echo   4. List ALL versions (releases + snapshots)
echo   5. Search for a specific version
echo   6. Enter version number directly
echo   7. Exit
echo.
set /p menuChoice=Enter your choice (1-7): 

if "%menuChoice%"=="1" goto LIST_LATEST
if "%menuChoice%"=="2" goto LIST_RELEASES
if "%menuChoice%"=="3" goto LIST_SNAPSHOTS
if "%menuChoice%"=="4" goto LIST_ALL
if "%menuChoice%"=="5" goto SEARCH_VERSION
if "%menuChoice%"=="6" goto ENTER_VERSION
if "%menuChoice%"=="7" exit /b
goto MENU

REM ================================
REM === LIST LATEST VERSIONS ======
REM ================================
:LIST_LATEST
echo.
echo === LATEST 20 VERSIONS ===
%PSExe% -Command "($json = Get-Content -Raw '%manifestFile%' | ConvertFrom-Json).versions | Select-Object -First 20 | ForEach-Object { '{0,-20} {1,-10} {2}' -f $_.id, $_.type, $_.releaseTime.Substring(0,10) }"
echo ============================
echo.
pause
goto MENU

REM ================================
REM === LIST ALL RELEASES =========
REM ================================
:LIST_RELEASES
echo.
echo === ALL RELEASE VERSIONS ===
echo (This may take a moment...)
%PSExe% -Command "($json = Get-Content -Raw '%manifestFile%' | ConvertFrom-Json).versions | Where-Object { $_.type -eq 'release' } | ForEach-Object { '{0,-20} {1}' -f $_.id, $_.releaseTime.Substring(0,10) }" | more
echo ============================
echo.
pause
goto MENU

REM ================================
REM === LIST ALL SNAPSHOTS ========
REM ================================
:LIST_SNAPSHOTS
echo.
echo === ALL SNAPSHOT VERSIONS ===
echo (This may take a moment...)
%PSExe% -Command "($json = Get-Content -Raw '%manifestFile%' | ConvertFrom-Json).versions | Where-Object { $_.type -eq 'snapshot' } | ForEach-Object { '{0,-20} {1}' -f $_.id, $_.releaseTime.Substring(0,10) }" | more
echo ============================
echo.
pause
goto MENU

REM ================================
REM === LIST ALL VERSIONS =========
REM ================================
:LIST_ALL
echo.
echo === ALL VERSIONS (Releases + Snapshots + Old Versions) ===
echo (This may take a moment... press SPACE to continue, Q to quit)
%PSExe% -Command "($json = Get-Content -Raw '%manifestFile%' | ConvertFrom-Json).versions | ForEach-Object { '{0,-25} {1,-15} {2}' -f $_.id, $_.type, $_.releaseTime.Substring(0,10) }" | more
echo ============================
echo.
pause
goto MENU

REM ================================
REM === SEARCH FOR VERSION ========
REM ================================
:SEARCH_VERSION
echo.
set /p searchTerm=Enter search term (e.g., "1.20", "1.8", "snapshot"): 
echo.
echo === SEARCH RESULTS FOR: %searchTerm% ===
%PSExe% -Command "($json = Get-Content -Raw '%manifestFile%' | ConvertFrom-Json).versions | Where-Object { $_.id -like '*%searchTerm%*' } | ForEach-Object { '{0,-25} {1,-15} {2}' -f $_.id, $_.type, $_.releaseTime.Substring(0,10) }" | more
echo ============================
echo.
pause
goto MENU

REM ================================
REM === ENTER VERSION DIRECTLY ====
REM ================================
:ENTER_VERSION
echo.
set /p versionInput=Enter the exact version you want to download: 
goto DOWNLOAD_VERSION

REM ================================
REM === DOWNLOAD VERSION ==========
REM ================================
:DOWNLOAD_VERSION
set "versionDir=%versionsDir%\%versionInput%"

REM ================================
REM === VALIDATE VERSION EXISTS ===
REM ================================
%PSExe% -Command "(Get-Content -Raw '%manifestFile%' | ConvertFrom-Json).versions.id -contains '%versionInput%'" > temp_check.txt
set /p exists=<temp_check.txt
del temp_check.txt

if /i "%exists%" NEQ "True" (
    echo [ERROR] Version "%versionInput%" was NOT found.
    echo.
    pause
    goto MENU
)
echo [INFO] Version "%versionInput%" was found in the manifest.

REM ================================
REM === PREPARE DIRECTORY STRUCT ===
REM ================================
echo [INFO] Preparing structure at: %versionDir%
for %%D in ("%currentDir%\runtime" "%versionDir%" "%versionDir%\assets" "%versionDir%\assets\indexes" "%versionDir%\libraries" "%versionDir%\natives" "%versionDir%\versions") do (
    if not exist "%%~D" mkdir "%%~D"
)

REM ================================
REM === GET VERSION JSON URL ======
REM ================================
echo [INFO] Getting version JSON URL...
for /f "tokens=1,2 delims=|" %%A in (
    '%PSExe% -Command "(Get-Content -Raw '%manifestFile%' | ConvertFrom-Json).versions | Where-Object { $_.id -eq '%versionInput%' } | ForEach-Object { $_.url + '|' + $_.sha1 }"'
) do (
    set "versionJsonURL=%%A"
    set "versionJsonSHA=%%B"
)

set "versionJsonFile=%versionDir%\versions\%versionInput%.json"
echo [INFO] JSON URL: %versionJsonURL%

REM ================================
REM === VERIFY OR DOWNLOAD JSON ===
REM ================================
if exist "%versionJsonFile%" (
    %PSExe% -Command "if ((Get-FileHash -Path '%versionJsonFile%' -Algorithm SHA1).Hash -ieq '%versionJsonSHA%') { exit 0 } else { exit 1 }"
    if errorlevel 1 (
        echo [WARNING] Local JSON is corrupted. Re-downloading...
        del "%versionJsonFile%"
    )
)

if not exist "%versionJsonFile%" (
    echo [INFO] Downloading version JSON...
    %PSExe% -Command "Invoke-WebRequest -Uri '%versionJsonURL%' -OutFile '%versionJsonFile%' -UseBasicParsing"
)

REM ================================
REM === DOWNLOAD client.jar FILE ==
REM ================================
echo [INFO] Getting client.jar URL...
for /f "tokens=1,2 delims=|" %%A in (
    '%PSExe% -Command "(Get-Content -Raw '%versionJsonFile%' | ConvertFrom-Json).downloads.client | ForEach-Object { $_.url + '|' + $_.sha1 }"'
) do (
    set "clientJarURL=%%A"
    set "clientJarSHA=%%B"
)

set "clientJarFile=%versionDir%\versions\%versionInput%-client.jar"
echo [INFO] client.jar URL: %clientJarURL%

if exist "%clientJarFile%" (
    %PSExe% -Command "if ((Get-FileHash -Path '%clientJarFile%' -Algorithm SHA1).Hash -ieq '%clientJarSHA%') { exit 0 } else { exit 1 }"
    if errorlevel 1 (
        echo [WARNING] Local client.jar is corrupted. Re-downloading...
        del "%clientJarFile%"
    )
)

if not exist "%clientJarFile%" (
    echo [INFO] Downloading client.jar...
    %PSExe% -Command "Invoke-WebRequest -Uri '%clientJarURL%' -OutFile '%clientJarFile%' -UseBasicParsing"
)

REM ================================
REM === EXECUTE LIBRARIES SCRIPT ==
REM ================================
echo [INFO] Downloading resources, this may take a while...

set "libScript=%currentDir%\runtime\downloader\downloader.ps1"
set "libJson=%versionJsonFile%"
set "librariesDir=%versionDir%\libraries"
set "runtimeDir=%currentDir%\runtime"

%PSExe% -NoProfile -ExecutionPolicy Bypass -File "%libScript%" -jsonPath "%libJson%" -librariesDir "%librariesDir%" -versionDir "%versionDir%" -runtimeDir "%runtimeDir%"

if errorlevel 1 (
    echo [ERROR] There was a failure in download or verification.
    pause
    goto MENU
)

echo.
echo [SUCCESS] Libraries, assets, and natives processed successfully.
echo [SUCCESS] Files downloaded to:
echo         %versionDir%\versions
echo.
set /p returnMenu=Press D to download another version, or any other key to exit: 
if /i "%returnMenu%"=="D" goto MENU
exit /b
