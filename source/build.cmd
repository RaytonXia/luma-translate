@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Test
if errorlevel 1 exit /b 1
if /I "%~1"=="--no-run" exit /b 0
start "" "%~dp0..\LumaTranslate.exe" --show
endlocal
