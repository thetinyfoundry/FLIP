@echo off
setlocal
cd /d "%~dp0"
echo Rimozione di FLIP in corso...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$target = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Flip';" ^
  "$exe = Join-Path $target 'Flip.exe';" ^
  "if (Test-Path -LiteralPath $exe -PathType Leaf) { foreach ($action in '--stop','--disable-startup','--remove-shortcuts','--remove-context-menu') { Start-Process -FilePath $exe -ArgumentList $action -Wait }; Start-Sleep -Milliseconds 700 };" ^
  "$desktopLnk = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Flip.lnk';" ^
  "$startLnk = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\Flip.lnk';" ^
  "Remove-Item -LiteralPath $desktopLnk,$startLnk -Force -ErrorAction SilentlyContinue;" ^
  "$pandoc = Join-Path $target 'tools\pandoc.exe';" ^
  "$keep = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'FLIP\Engines\pandoc';" ^
  "if ((Test-Path -LiteralPath $pandoc) -and -not (Test-Path -LiteralPath (Join-Path $keep 'pandoc.exe'))) { New-Item -ItemType Directory -Force -Path $keep | Out-Null; Move-Item -LiteralPath $pandoc -Destination $keep; $notice = Join-Path $target 'licenses\pandoc'; if (Test-Path -LiteralPath $notice) { Copy-Item -LiteralPath $notice -Destination (Join-Path $keep 'licenses') -Recurse -Force } };" ^
  "if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force };" ^
  "Write-Host 'FLIP disinstallato con successo.' -ForegroundColor Green"
if errorlevel 1 (
  echo.
  echo [ERRORE] Impossibile rimuovere tutti i file: chiudi prima FLIP dall'icona vicino all'orologio e riprova.
  pause
  exit /b 1
)
echo.
pause
