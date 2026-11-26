@echo off
setlocal
title Fleet System - install scripts

rem Fleet System installer for Space Engineers.
rem  1. Clones (first run) or updates (later runs) the SEGROUND repo into
rem     Documents\SEGROUND using Git for Windows.
rem  2. Copies the scripts into Space Engineers' local scripts folder so they
rem     appear under "Browse Scripts" in any programmable block.
rem Without Git it falls back to a dist folder next to this file (ZIP download).
rem Safe to re-run any time: that is how you update.

set "REPO=https://github.com/dywtw72wvk-blip/SEGROUND.git"
set "CLONE=%USERPROFILE%\Documents\SEGROUND"
set "DEST=%APPDATA%\SpaceEngineers\IngameScripts\local"
set "HERE=%~dp0"
set "SRC="

rem If running from inside the repo copy, relaunch from a temp copy first:
rem "git pull" may rewrite this very file, and cmd reads .bat files as it goes.
if /i not "%~dp0"=="%CLONE%\" goto :start
copy /y "%~f0" "%TEMP%\fleet-system-install.bat" >nul
"%TEMP%\fleet-system-install.bat"

:start
echo ================================================
echo  Fleet System script installer
echo ================================================
echo Repo copy: %CLONE%
echo Target:    %DEST%
echo.

where git >nul 2>&1
if errorlevel 1 goto :nogit

rem ---------------------------------------------------------------- git path
if exist "%CLONE%\.git" goto :pull
if exist "%CLONE%" goto :clonebusy

echo Downloading the repo...
echo The repo is private: if a GitHub sign-in window appears, sign in with the
echo account that owns SEGROUND. This is only needed once.
echo.
git clone "%REPO%" "%CLONE%"
if errorlevel 1 goto :clonefail
goto :usegit

:pull
echo Updating the repo copy...
git -C "%CLONE%" pull --ff-only
if errorlevel 1 echo WARNING: update failed - installing the copy already on this PC.
goto :usegit

:usegit
if exist "%CLONE%\dist\HubCore.cs" set "SRC=%CLONE%\dist"
if defined SRC goto :found
echo ERROR: the repo copy has no dist folder: %CLONE%
goto :fail

:clonebusy
echo ERROR: %CLONE% already exists but is not a Git copy of the repo.
echo Rename or delete that folder, then run this installer again.
goto :fail

:clonefail
echo.
echo WARNING: could not download the repo. Check your internet connection and
echo that you signed in to GitHub with an account that can see SEGROUND.
echo Trying a local dist folder instead...
echo.
goto :localsearch

rem ---------------------------------------------------------------- no git
:nogit
echo Git for Windows is not installed, so the repo cannot be downloaded
echo automatically. Get it from https://git-scm.com/download/win and run this
echo installer again, or use a ZIP download. Looking for a local dist folder...
echo.

:localsearch
if exist "%HERE%dist\HubCore.cs" set "SRC=%HERE%dist"
if not defined SRC for /d %%D in ("%HERE%*") do if exist "%%~fD\dist\HubCore.cs" set "SRC=%%~fD\dist"
if not defined SRC if exist "%HERE%HubCore.cs" if exist "%HERE%HubUI.cs" if exist "%HERE%Drone.cs" set "SRC=%HERE%."
if defined SRC goto :found

echo ERROR: no scripts found next to this file either: %HERE%
echo "%HERE%" | find /i "\Temp\" >nul && echo It looks like this was opened straight from inside a ZIP.
echo Either install Git for Windows, or extract the whole ZIP with
echo "Extract All..." and run install.bat from the extracted folder.
goto :fail

rem ---------------------------------------------------------------- install
:found
echo.
echo Installing scripts from: %SRC%
call :install "Fleet Hub Core" HubCore.cs || goto :fail
call :install "Fleet Hub UI"   HubUI.cs   || goto :fail
call :install "Fleet Drone"    Drone.cs   || goto :fail
call :install "Fleet Factory"  Factory.cs || goto :fail

rem Put the scripts straight into programmable blocks in saved worlds and
rem blueprints (blocks named [Fleet Core] / [Fleet UI] / [Fleet Drone] / [Fleet Factory], or
rem already running a Fleet script). Paths go via env vars so spaces and
rem brackets in folder names are safe.
set "FLEET_DIST=%SRC%"
set "FLEET_PATCHER=%SRC%\..\tools\FleetPatch.cs"
if not exist "%FLEET_PATCHER%" goto :donemsg
powershell -NoProfile -ExecutionPolicy Bypass -Command "Add-Type -Path $env:FLEET_PATCHER -ReferencedAssemblies System, System.Xml; $r = [FleetPatch]::Run($env:FLEET_DIST, (Join-Path $env:APPDATA 'SpaceEngineers'), $false, $false); exit $r"
if errorlevel 1 if not errorlevel 2 echo WARNING: updating worlds/blueprints failed - see above. Browse Scripts still works.

:donemsg
echo.
echo ================================================
echo  Done.
echo ================================================
echo Programmable blocks named [Fleet Core], [Fleet UI], [Fleet Drone] or [Fleet Factory] in your
echo saved worlds and blueprints now run the latest scripts: just load the world.
echo For any other block: Edit, Browse Scripts, pick a "Fleet" script.
echo To update later, close the game and run this installer again.
echo.
pause
exit /b 0

:install
if not exist "%DEST%\%~1" mkdir "%DEST%\%~1" || exit /b 1
copy /y "%SRC%\%~2" "%DEST%\%~1\Script.cs" >nul || exit /b 1
echo   Installed: %~1
exit /b 0

:fail
echo.
echo Install FAILED. No scripts were changed.
echo.
pause
exit /b 1
