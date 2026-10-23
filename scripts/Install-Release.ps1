param([string]$PackageRoot = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$payload = Join-Path $PackageRoot 'payload'
$installer = Join-Path $PackageRoot 'Install.ps1'
if (!(Test-Path (Join-Path $payload 'Flip.exe'))) { throw 'Pacchetto non valido: manca payload\Flip.exe.' }
if (!(Test-Path $installer)) { throw 'Pacchetto non valido: manca Install.ps1.' }
& $installer -Source $payload
if ($LASTEXITCODE -ne 0) { throw 'Installazione non riuscita.' }
Write-Output 'FLIP installato. Puoi avviarlo dal collegamento Desktop o dal menu Start.'
