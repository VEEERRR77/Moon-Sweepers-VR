@echo off
title Moon Sweepers VR - installer
rem Runs the PowerShell installer that sits next to this file.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0files\install.ps1"
echo.
pause
