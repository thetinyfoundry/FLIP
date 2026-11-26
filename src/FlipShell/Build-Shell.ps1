param([Parameter(Mandatory)][string]$Output)
# Compila i due componenti nativi del pacchetto Store: menu contestuale e avvio automatico.
$ErrorActionPreference='Stop'
$installer=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere=Join-Path $installer 'vswhere.exe'
if(!(Test-Path -LiteralPath $vswhere)){throw 'Visual Studio Build Tools con C++ non trovati.'}
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$vs){throw 'Componente C++ x64 di Visual Studio non trovato.'}
$vcvars=Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$work=Join-Path ([IO.Path]::GetTempPath()) ('flip-shell-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try{
    $common='/nologo /utf-8 /std:c++17 /EHsc /W4 /WX /O2 /MT /guard:cf'
    $link='/DYNAMICBASE /NXCOMPAT /guard:cf /INCREMENTAL:NO'
    $batch=Join-Path $work 'build.cmd'
    Set-Content -LiteralPath $batch -Encoding ASCII -Value @"
@echo off
set "PATH=%PATH%;$installer"
call "$vcvars" >nul || exit /b 1
cd /d "$work"
cl $common /LD "$PSScriptRoot\FlipShell.cpp" /Fe:FlipShell.dll /link $link /DEF:"$PSScriptRoot\FlipShell.def" runtimeobject.lib shlwapi.lib shell32.lib ole32.lib user32.lib || exit /b 1
cl $common "$PSScriptRoot\FlipStartup.cpp" /Fe:FlipStartup.exe /link $link /SUBSYSTEM:WINDOWS || exit /b 1
"@
    & cmd.exe /d /c $batch
    if($LASTEXITCODE -ne 0){throw 'Compilazione dei componenti nativi non riuscita.'}
    foreach($name in 'FlipShell.dll','FlipStartup.exe'){Copy-Item -LiteralPath (Join-Path $work $name) -Destination $Output -Force}
}
finally{Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue}
