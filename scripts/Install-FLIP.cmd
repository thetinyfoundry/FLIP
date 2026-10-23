@echo off
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Release.ps1"
if errorlevel 1 (
  echo Installation failed. See the message above and README.txt.
  pause
  exit /b 1
)
echo FLIP installed. Use the Desktop shortcut to start it.
pause
