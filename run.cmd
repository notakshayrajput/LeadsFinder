@echo off
rem Keep this terminal attached; Ctrl+C stops both services and their children.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-LeadsFinder.ps1" %*
exit /b %ERRORLEVEL%
