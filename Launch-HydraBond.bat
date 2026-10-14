@echo off
title HydraBond Launcher
cd /d "%~dp0"

start "" node server.js
timeout /t 1 >nul

if exist "C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe" (
    start "" "C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe" --app="http://127.0.0.1:4545" --window-size=1080,720 --user-data-dir="%TEMP%\hydrabond_app_profile"
) else if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    start "" "C:\Program Files\Google\Chrome\Application\chrome.exe" --app="http://127.0.0.1:4545" --window-size=1080,720 --user-data-dir="%TEMP%\hydrabond_app_profile"
) else (
    start http://127.0.0.1:4545
)
