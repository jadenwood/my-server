@echo off
title Realm - Reign of Kings community server
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0server\Realm.ps1" %*
pause
