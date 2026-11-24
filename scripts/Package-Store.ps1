param([string]$OutputRoot,[string]$RuntimeVersion='10.0.15')
# Crea il pacchetto MSIX da caricare in Partner Center. Non e' firmato: lo firma Microsoft Store.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$OutputRoot){$OutputRoot=Join-Path $root 'dist\store'}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
$project=Join-Path $root 'src\Flip\Flip.csproj'
$version=[version](([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object {$_} | Select-Object -First 1)
# Stesso numero di GitHub: lo Store aggiunge solo la quarta cifra, sempre 0, e non accetta versioni 0.x.
if($version.Major -lt 1){throw 'Microsoft Store richiede una versione da 1.0.0 in poi.'}
$storeVersion='{0}.{1}.{2}.0' -f $version.Major,$version.Minor,$version.Build
$layout=Join-Path $OutputRoot ('layout-'+$version)
$package=Join-Path $OutputRoot ('FLIP-Converter-'+$version+'-store-x64.msix')
if(Test-Path -LiteralPath $layout){throw ('Cartella gia esistente: '+$layout)}
if(Test-Path -LiteralPath $package){throw ('Pacchetto gia esistente: '+$package)}

$kits=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$sdk=Get-ChildItem -LiteralPath $kits -Directory | Where-Object {$_.Name -match '^10\.' -and (Test-Path (Join-Path $_.FullName 'x64\makeappx.exe')) -and (Test-Path (Join-Path $_.FullName 'x64\makepri.exe'))} |
    Sort-Object {[version]$_.Name} | Select-Object -Last 1
if(!$sdk){throw 'Windows SDK (makeappx, makepri) non trovato.'}
$tools=Join-Path $sdk.FullName 'x64'

# .NET incluso nel pacchetto: lo Store non puo' chiedere all'utente di installarlo.
New-Item -ItemType Directory -Path $layout -Force | Out-Null
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=$RuntimeVersion -p:PublishReadyToRun=false -p:DebugType=none -p:RestoreLockedMode=false -p:RestorePackagesWithLockFile=false -p:NuGetLockFilePath=none -p:SatelliteResourceLanguages="it%3Ben" -o $layout
if($LASTEXITCODE -ne 0){throw 'Pubblicazione non riuscita.'}
& (Join-Path $root 'src\FlipShell\Build-Shell.ps1') -Output $layout

$assets=Join-Path $layout 'Assets'
Copy-Item -LiteralPath (Join-Path $root 'src\Store\Assets') -Destination $assets -Recurse
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'NOTICE'),(Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $layout
$licenses=Join-Path $layout 'licenses'
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $licenses -Recurse
foreach($entry in @(@('skiasharp','4.150.1'),@('skiasharp.nativeassets.win32','4.150.1'),@('microsoft.ml.onnxruntime','1.32.0'),@('microsoft.ml.onnxruntime.managed','1.32.0'))){
    $source=Join-Path $root ('packages\'+$entry[0]+'\'+$entry[1])
    $target=Join-Path $licenses $entry[0]
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $notices=@(Get-ChildItem -LiteralPath $source -File | Where-Object {$_.Name -match 'LICENSE|NOTICE'})
    if(!$notices.Count){throw ('Licenza mancante per '+$entry[0])}
    $notices | Copy-Item -Destination $target
}
# Le licenze di PDFium sono le stesse del pacchetto GitHub.
$archive=Join-Path $OutputRoot 'pdfium-7961-win-x64.tgz'
if(!(Test-Path -LiteralPath $archive)){Invoke-WebRequest -UseBasicParsing 'https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/7961/pdfium-win-x64.tgz' -OutFile $archive}
if((Get-FileHash -LiteralPath $archive).Hash -ne '88276459349b291c41f10422dad0210f007c04d919c8fa56472b6b7c6406adf4'){throw 'Checksum archivio PDFium non valido.'}
$native=Join-Path $OutputRoot 'pdfium-notices'
New-Item -ItemType Directory -Path $native -Force | Out-Null
& (Join-Path $env:SystemRoot 'System32\tar.exe') -xzf $archive -C $native
if($LASTEXITCODE -ne 0){throw 'Licenze PDFium non estratte.'}
foreach($file in @(Get-ChildItem -LiteralPath $native -Recurse -File | Where-Object {$_.FullName -match '[\\/]licenses?[\\/]|[\\/](LICENSE|NOTICE|COPYING)(\.|$)'})){
    $target=Join-Path (Join-Path $licenses 'pdfium') $file.FullName.Substring($native.Length).TrimStart('\')
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}

# FFmpeg e Whisper fanno parte del pacchetto: scaricati a parte non avrebbero firma e Smart App Control potrebbe bloccarli.
function Get-Pinned([string]$Url,[string]$Sha256){
    $file=Join-Path $OutputRoot ([IO.Path]::GetFileName($Url))
    if(!(Test-Path -LiteralPath $file)){
        $ProgressPreference='SilentlyContinue'
        Invoke-WebRequest -UseBasicParsing $Url -OutFile $file
    }
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $Sha256){throw ('Checksum non valido: '+$file)}
    $file
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Expand-Wanted([string]$Archive,[string]$Target,[scriptblock]$Filter){
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    $zip=[IO.Compression.ZipFile]::OpenRead($Archive)
    try{foreach($entry in $zip.Entries){if($entry.Name -and (& $Filter $entry)){[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $Target $entry.Name),$true)}}}
    finally{$zip.Dispose()}
}
$ffmpeg=Get-Pinned 'https://github.com/GyanD/codexffmpeg/releases/download/9.1/ffmpeg-9.1-essentials_build.zip' 'b9bbbb3acece000cbeec994f856649354e52c773807841e78908374b24d658fd'
Expand-Wanted $ffmpeg (Join-Path $layout 'tools') {param($e) $e.Name -eq 'ffmpeg.exe'}
Expand-Wanted $ffmpeg (Join-Path $licenses 'ffmpeg') {param($e) $e.Name -in 'LICENSE','LICENSE.txt','README.txt'}
$whisper=Get-Pinned 'https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.2/whisper-bin-x64.zip' '49dcc16de826f20bd53d44f947a1ae49dfa81f86cad67a64d80820cb192d674a'
Expand-Wanted $whisper (Join-Path $layout 'tools\whisper') {param($e) $e.Name -eq 'whisper-cli.exe' -or $e.Name -eq 'whisper.dll' -or $e.Name -like 'ggml*.dll'}
foreach($tool in 'tools\ffmpeg.exe','tools\whisper\whisper-cli.exe','tools\whisper\whisper.dll'){if(!(Test-Path -LiteralPath (Join-Path $layout $tool))){throw ('Componente mancante: '+$tool)}}
New-Item -ItemType Directory -Path (Join-Path $licenses 'whisper.cpp') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'licenses\whisper.cpp\LICENSE') -Destination (Join-Path $licenses 'whisper.cpp') -Force

Get-ChildItem -LiteralPath $layout -Recurse -File -Force | Where-Object {$_.Extension -in '.pdb','.lnk' -or $_.Name -in 'events.jsonl','createdump.exe'} | Remove-Item -Force

$manifest=(Get-Content -LiteralPath (Join-Path $root 'src\Store\AppxManifest.xml') -Raw -Encoding UTF8).Replace('__VERSION__',$storeVersion)
# Il commento contiene il segnaposto solo nel sorgente.
$manifest=[regex]::Replace($manifest,'<!--.*?-->\r?\n','',[Text.RegularExpressions.RegexOptions]::Singleline)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'),$manifest,[Text.UTF8Encoding]::new($false))

# resources.pri collega i nomi dei loghi alle varie dimensioni.
$priConfig=Join-Path $OutputRoot 'priconfig.xml'
Remove-Item -LiteralPath $priConfig -Force -ErrorAction SilentlyContinue
& (Join-Path $tools 'makepri.exe') createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
if($LASTEXITCODE -ne 0){throw 'makepri createconfig non riuscito.'}
# Senza questa sezione makepri crea un file separato per ogni scala, utile solo nei bundle.
$config=[xml](Get-Content -LiteralPath $priConfig -Raw)
$packaging=$config.SelectSingleNode('//packaging')
if($packaging){[void]$packaging.ParentNode.RemoveChild($packaging)}
$config.Save($priConfig)
$priRoot=Join-Path $OutputRoot 'pri'
Remove-Item -LiteralPath $priRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $priRoot -Force | Out-Null
Copy-Item -LiteralPath $assets -Destination (Join-Path $priRoot 'Assets') -Recurse
& (Join-Path $tools 'makepri.exe') new /pr $priRoot /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
if($LASTEXITCODE -ne 0){throw 'makepri new non riuscito.'}

& (Join-Path $tools 'makeappx.exe') pack /d $layout /p $package /h SHA256 | Out-Null
if($LASTEXITCODE -ne 0){throw 'makeappx non riuscito.'}
((Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant()+'  '+(Split-Path $package -Leaf)) | Set-Content -LiteralPath ($package+'.sha256') -Encoding ascii
Write-Output ('Pacchetto Store '+$storeVersion+': '+$package)
Write-Output ('Cartella per la prova locale: '+$layout)
