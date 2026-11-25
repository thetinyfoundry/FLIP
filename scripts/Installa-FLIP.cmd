@echo off
setlocal
cd /d "%~dp0"
echo Avvio installazione FLIP v1.0.2...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
if errorlevel 1 (
  echo.
  echo [ERRORE] Installazione non completata. Controlla il messaggio sopra.
  pause
  exit /b 1
)
echo.
echo [OK] FLIP e stato installato ed avviato!
echo Puoi avviarlo anche dal collegamento FLIP sul Desktop o dal Menu Start.
pause
