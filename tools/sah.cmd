@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0sah.ps1" %*
exit /b %ERRORLEVEL%
