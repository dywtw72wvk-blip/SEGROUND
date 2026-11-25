@echo off
setlocal
title Fleet System - install scripts

rem Copies the paste-ready scripts from dist\ into Space Engineers' local
rem scripts folder, so they appear under "Browse Scripts" in any
rem programmable block. Safe to re-run: updates overwrite the old copies.

set "SRC=%~dp0dist"
set "DEST=%APPDATA%\SpaceEngineers\IngameScripts\local"

echo Fleet System script installer
echo Target: %DEST%
echo.

if not exist "%SRC%\HubCore.cs" (
    echo ERROR: the dist folder was not found next to this file.
    echo Extract the whole ZIP first, then run install.bat from inside that folder.
    goto :fail
)

if not exist "%APPDATA%\SpaceEngineers" (
    echo WARNING: %APPDATA%\SpaceEngineers does not exist yet.
    echo Has Space Engineers been started on this PC? Installing anyway...
    echo.
)

call :install "Fleet Hub Core" HubCore.cs || goto :fail
call :install "Fleet Hub UI"   HubUI.cs   || goto :fail
call :install "Fleet Drone"    Drone.cs   || goto :fail

echo.
echo Done. In game: open a programmable block, click Edit, then Browse Scripts,
echo and pick a "Fleet" script. Then Check Code, then Remember and Exit.
echo If the game was already running and the scripts are not listed, restart it.
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
echo Install FAILED. Nothing else was changed.
echo.
pause
exit /b 1
