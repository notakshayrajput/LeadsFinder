@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-LeadsFinder.ps1" %*
exit /b %ERRORLEVEL%
