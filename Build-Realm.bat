@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Build-Realm.bat: builds both Realm installers into the release folder. Read START-HERE.md first.
rem Double-click it. It needs Node.js 22 and Git for Windows. It never needs administrator rights, and it writes
rem only inside this folder and in npm's and electron-builder's download caches in your user profile.
rem   Build-Realm.bat /nopause   do not wait for a key at the end (also skipped when the CI variable is set)
title Realm - build the installers
cd /d "%~dp0"

set "NOPAUSE="
if /i "%~1"=="/nopause" set "NOPAUSE=1"
if defined CI set "NOPAUSE=1"
set "PROBLEM="
set "FIX="

echo.
echo  ============================================================
echo   Realm: build the installers
echo     Realm-Steward-Setup  for you, the owner
echo     Realm-Setup          for your players
echo  ============================================================
echo.
echo  The first build downloads about 300 MB and takes 5 to 10 minutes.
echo  Later builds take 2 to 3 minutes. Leave this window open.
echo.

if not exist "launcher\package.json" (
  set "PROBLEM=Build-Realm.bat is not next to the launcher folder."
  set "FIX=Keep Build-Realm.bat in the Realm folder you cloned or unzipped, and double-click it there."
  goto :fail
)

rem Realm refuses to put a server folder on C:, and the launcher tests make throwaway server folders. So this
rem folder must be on another drive, the same one you will use for the server.
if /i "%~d0"=="C:" (
  set "PROBLEM=This Realm folder is on drive C:. Realm keeps servers and its test folders off C:."
  set "FIX=Clone or move the Realm folder to another drive, for example G:\Realm, and double-click Build-Realm.bat there."
  goto :fail
)

fltmc >nul 2>nul
if not errorlevel 1 (
  echo  Note: this window runs as administrator. That is not needed. Next time just double-click
  echo  Build-Realm.bat, so the files it creates stay owned by your own Windows user.
  echo.
)

rem ---------------------------------------------------------------- 1. tools
echo [1/5] Checking Node.js and Git...
where node >nul 2>nul
if errorlevel 1 (
  set "PROBLEM=Node.js is not installed, or this window cannot find it."
  set "FIX=Install Node.js 22 LTS from https://nodejs.org (the Windows Installer), close this window, then double-click Build-Realm.bat again."
  goto :fail
)
set "NODE_VER="
for /f "delims=" %%v in ('node -p "process.versions.node" 2^>nul') do set "NODE_VER=%%v"
if not defined NODE_VER (
  set "PROBLEM=Node.js is installed but does not start from this window."
  set "FIX=Reinstall Node.js 22 LTS from https://nodejs.org, close this window, then double-click Build-Realm.bat again."
  goto :fail
)
set "NODE_MAJOR=0"
for /f "tokens=1 delims=." %%a in ("%NODE_VER%") do set "NODE_MAJOR=%%a"
if %NODE_MAJOR% LSS 22 (
  set "PROBLEM=Node.js %NODE_VER% is too old. Realm needs Node.js 22."
  set "FIX=Install Node.js 22 LTS from https://nodejs.org (it replaces the old one), close this window, then double-click Build-Realm.bat again."
  goto :fail
)
echo       Node.js %NODE_VER%
if %NODE_MAJOR% GTR 22 echo       Realm is tested with Node.js 22. Node.js %NODE_VER% should work; if the build fails, install 22 LTS.
where npm >nul 2>nul
if errorlevel 1 (
  set "PROBLEM=npm is missing. It comes with Node.js, so the Node.js install is incomplete."
  set "FIX=Reinstall Node.js 22 LTS from https://nodejs.org, close this window, then double-click Build-Realm.bat again."
  goto :fail
)
where git >nul 2>nul
if errorlevel 1 (
  set "PROBLEM=Git is not installed, or this window cannot find it."
  set "FIX=Install Git for Windows from https://git-scm.com/download/win (the default options are fine), close this window, then double-click Build-Realm.bat again."
  goto :fail
)
set "GIT_REV="
for /f "delims=" %%r in ('git rev-parse --short HEAD 2^>nul') do set "GIT_REV=%%r"
if defined GIT_REV (
  echo       Git, building commit %GIT_REV%
) else (
  echo       Git is installed, but this folder is not a git clone. The build still works.
)
echo.

cd /d "%~dp0launcher"

rem ---------------------------------------------------------------- 2. packages
echo [2/5] Installing the launcher's packages (npm ci)...
call npm ci --no-audit --no-fund
if errorlevel 1 (
  set "PROBLEM=npm ci failed. The lines above say why."
  set "FIX=Check the internet connection. If it says EPERM or EBUSY, close every Realm, Realm Steward or Electron window started from this folder, then run Build-Realm.bat again."
  goto :fail
)
echo.

rem ---------------------------------------------------------------- 3. checks
echo [3/5] Checking the launcher code (npm run check)...
call npm run check
if errorlevel 1 (
  set "PROBLEM=The launcher code check failed, so this copy of the code is broken."
  set "FIX=Do not build from it. Run git pull to get the latest version, or undo your own changes in the launcher folder, then try again."
  goto :fail
)
echo.

rem ---------------------------------------------------------------- 4. tests
echo [4/5] Running the launcher tests (npm test)...
rem The tests make throwaway server folders under TEMP, which is on C: on almost every PC. They get their own
rem temporary folder next to this file instead, removed again afterwards.
set "SAVED_TEMP=%TEMP%"
set "SAVED_TMP=%TMP%"
set "TEST_TMP=%~dp0launcher\.test-tmp"
if exist "%TEST_TMP%" rmdir /s /q "%TEST_TMP%"
mkdir "%TEST_TMP%"
set "TEMP=%TEST_TMP%"
set "TMP=%TEST_TMP%"
call npm test
set "TEST_RESULT=%ERRORLEVEL%"
set "TEMP=%SAVED_TEMP%"
set "TMP=%SAVED_TMP%"
rmdir /s /q "%TEST_TMP%" 2>nul
if not "%TEST_RESULT%"=="0" (
  set "PROBLEM=A launcher test failed. The lines above that start with 'not ok' name it."
  set "FIX=Do not build from this copy. Run git pull to get the latest version, or undo your own changes in the launcher folder, then try again."
  goto :fail
)
echo.

rem ---------------------------------------------------------------- 5. installers
echo [5/5] Building both installers...
node scripts\build-release.mjs
if errorlevel 1 (
  set "PROBLEM=Building the installers failed."
  set "FIX=The PROBLEM line just above says what went wrong and what to do."
  goto :fail
)
set "OUT_DIR="
for /f "delims=" %%d in ('node scripts\build-release.mjs --print-out-dir') do set "OUT_DIR=%%d"

echo.
echo  ============================================================
echo   Done. Your installers are in the release folder next to
echo   Build-Realm.bat. The full path is printed above.
echo.
echo   Next: run Realm-Steward-Setup on this PC (START-HERE.md step 2).
echo   Send Realm-Setup to friends only after you have published
echo   your server list (START-HERE.md step 7).
echo  ============================================================
if not defined NOPAUSE if defined OUT_DIR start "" "%OUT_DIR%"
call :maybe_pause
exit /b 0

:fail
echo.
echo  ------------------------------------------------------------
echo   PROBLEM: %PROBLEM%
echo   FIX:     %FIX%
echo  ------------------------------------------------------------
echo  Nothing was installed. Nothing outside this folder was changed.
call :maybe_pause
exit /b 1

:maybe_pause
if defined NOPAUSE goto :eof
echo.
echo Press any key to close this window.
pause >nul
goto :eof
