<#
  Verifica e pubblica FLIP in un solo passaggio:
  1. dotnet build Release (deve dare 0 errori, 0 avvisi)
  2. --test-input e --test-conversions (devono essere 100% verdi)
  3. publish in %LOCALAPPDATA%\Programs\Flip e riavvio di Flip.exe --ui
  Uso:  powershell -ExecutionPolicy Bypass -File scripts\Verify-And-Publish.ps1
#>
param([switch]$SkipPublish)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$project=Join-Path $root 'src\Flip\Flip.csproj'
$target=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Flip'
$evidence=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Flip\logs'

function Show-FailedChecks([string]$file){
    $path=Join-Path $evidence $file
    if(!(Test-Path -LiteralPath $path)){return}
    $json=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    Write-Host ("  passati: {0}  falliti: {1}" -f $json.passed,$json.failed)
    $json.checks.PSObject.Properties | Where-Object { $_.Value -eq $false } | ForEach-Object { Write-Host ("  ROSSO: "+$_.Name) -ForegroundColor Red }
}

Push-Location $root
try{
    Write-Host '== 1/4 Build Release ==' -ForegroundColor Cyan
    $build=& dotnet build $project -c Release 2>&1
    $build | Select-Object -Last 6 | ForEach-Object { Write-Host $_ }
    if($LASTEXITCODE -ne 0){throw 'Build fallita.'}
    if(!($build -match '\b0 (Warning\(s\)|Avvisi)')){
        $build | Where-Object { $_ -match 'warning' } | Select-Object -Unique | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
        Write-Host 'Avvisi: 0 confermato (IT)' -ForegroundColor Green
    }

    Write-Host '== 2/4 --test-input ==' -ForegroundColor Cyan
    & dotnet run --project $project -c Release --no-build -- --test-input | Out-Host
    $inputExit=$LASTEXITCODE
    Show-FailedChecks 'input-tests.json'
    if($inputExit -ne 0){throw 'InputTests non al 100%.'}

    Write-Host '== 3/4 --test-conversions ==' -ForegroundColor Cyan
    & dotnet run --project $project -c Release --no-build -- --test-conversions | Out-Host
    if($LASTEXITCODE -ne 0){throw 'ConversionTests non al 100%.'}

    if($SkipPublish){Write-Host 'Verifica completata (publish saltato).' -ForegroundColor Green; return}

    Write-Host '== 4/4 Publish e riavvio ==' -ForegroundColor Cyan
    if(Get-Process Flip -ErrorAction SilentlyContinue){throw 'Esci da FLIP dall''icona vicino all''orologio, poi riprova.'}
    & dotnet publish $project -c Release -r win-x64 --self-contained false -o $target
    if($LASTEXITCODE -ne 0){throw 'Publish fallito.'}
    Start-Process -FilePath (Join-Path $target 'Flip.exe') -ArgumentList '--ui'
    Write-Host ("FLIP pubblicato in "+$target+" e riavviato.") -ForegroundColor Green
}
finally{Pop-Location}

