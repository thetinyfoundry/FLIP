param(
    [string]$Source,
    [string]$Destination,
    [switch]$CreateShortcuts=$true,
    [switch]$EnableStartup=$true,
    [switch]$NoShortcuts,
    [switch]$NoStartup,
    [switch]$NoLaunch
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot

if(!$Source){
    # 1. Pacchetto ZIP distribuito: payload\ accanto a Install.ps1
    if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'payload\Flip.exe') -PathType Leaf){
        $Source = Join-Path $PSScriptRoot 'payload'
    }
    # 2. Cartella scripts\ del repository sorgente: app\ alla radice
    elseif(Test-Path -LiteralPath (Join-Path $root 'app\Flip.exe') -PathType Leaf){
        $Source = Join-Path $root 'app'
    }
    # 3. app\ nella stessa cartella dello script
    elseif(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'app\Flip.exe') -PathType Leaf){
        $Source = Join-Path $PSScriptRoot 'app'
    }
    elseif(Test-Path -LiteralPath (Join-Path (Get-Location) 'payload\Flip.exe') -PathType Leaf){
        $Source = Join-Path (Get-Location) 'payload'
    }
    elseif(Test-Path -LiteralPath (Join-Path (Get-Location) 'app\Flip.exe') -PathType Leaf){
        $Source = Join-Path (Get-Location) 'app'
    }
    else {
        $Source = if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'payload') -PathType Container){ Join-Path $PSScriptRoot 'payload' } else { Join-Path $root 'app' }
    }
}
if(!$Destination){$Destination=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Flip'}
$Source=[IO.Path]::GetFullPath($Source).TrimEnd('\')
$Destination=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
$packageRoot=Split-Path -Parent $Source
$backup=$null

if($Source -eq $Destination -or $Destination.StartsWith($Source+'\',[StringComparison]::OrdinalIgnoreCase) -or $Source.StartsWith($Destination+'\',[StringComparison]::OrdinalIgnoreCase)){
    throw 'Origine e destinazione devono essere cartelle distinte e non annidate.'
}

# Verifica preventiva degli hash SHA256SUMS.txt prima di toccare la cartella di destinazione
$sumsPath = @((Join-Path $packageRoot 'SHA256SUMS.txt'), (Join-Path $PSScriptRoot 'SHA256SUMS.txt')) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if(!$sumsPath -and (Split-Path -Leaf $Source) -eq 'payload'){
    throw 'Manca SHA256SUMS.txt: estrai di nuovo l''intero archivio ZIP.'
}
if($sumsPath){
    $sumsRoot = Split-Path -Parent $sumsPath
    $checked = 0
    $listed = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($line in Get-Content -LiteralPath $sumsPath -Encoding UTF8){
        if([string]::IsNullOrWhiteSpace($line)){continue}
        if($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$'){
            $expectedHash = $matches[1].ToLowerInvariant()
            $rel = $matches[2].Trim()
            if($rel -eq 'SHA256SUMS.txt'){continue}
            $candidate = [IO.Path]::GetFullPath((Join-Path $sumsRoot ($rel.Replace('/', '\'))))
            if(!$candidate.StartsWith(($sumsRoot.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)){
                throw ('Voce non valida in SHA256SUMS.txt: ' + $rel)
            }
            if(!(Test-Path -LiteralPath $candidate -PathType Leaf)){
                throw ('File del pacchetto mancante per verifica SHA256: ' + $rel)
            }
            $actualHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
            if($actualHash -ne $expectedHash){
                throw ('Hash SHA256 non corrispondente per ' + $rel + ' (pacchetto corrotto o modificato).')
            }
            [void]$listed.Add($candidate)
            $checked++
        }
        else{
            throw 'SHA256SUMS.txt contiene una riga non valida (pacchetto corrotto o modificato).'
        }
    }
    if($checked -eq 0){throw 'SHA256SUMS.txt e'' vuoto (pacchetto corrotto o modificato).'}
    if($Source.StartsWith(($sumsRoot.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)){
        foreach($file in Get-ChildItem -LiteralPath $Source -File -Recurse -Force){
            if(!$listed.Contains($file.FullName)){throw ('File non previsto nel pacchetto: ' + $file.FullName.Substring($sumsRoot.Length).TrimStart('\'))}
        }
    }
    Write-Host ("Integrita SHA256SUMS.txt verificata ({0} file)." -f $checked) -ForegroundColor Cyan
}

foreach($name in @('Flip.exe','Flip.dll','Flip.runtimeconfig.json','Flip.deps.json','Flip.ico','LICENSE','THIRD_PARTY_NOTICES.md','libSkiaSharp.dll','pdfium.dll')){
    if(!(Test-Path -LiteralPath (Join-Path $Source $name) -PathType Leaf)){
        throw ("Pacchetto o build incompleta: manca '$name' in '$Source'. Estrai l'intero archivio ZIP prima di eseguire Installa-FLIP.cmd, oppure esegui '.\scripts\Build.ps1'.")
    }
}

$runtime=@(& dotnet --list-runtimes 2>$null)
if($LASTEXITCODE -ne 0 -or !($runtime -match '^Microsoft\.WindowsDesktop\.App 10\.')){
    throw 'Installa prima Microsoft .NET 10 Windows Desktop Runtime x64 (https://dotnet.microsoft.com/download/dotnet/10.0).'
}
$newest=@($runtime | ForEach-Object { if($_ -match '^Microsoft\.WindowsDesktop\.App (10\.\d+\.\d+) '){[version]$matches[1]} } | Sort-Object)[-1]
if($newest -and $newest -lt [version]'10.0.15'){
    Write-Warning ('.NET ' + $newest + ' non ha gli ultimi aggiornamenti di sicurezza: installa la versione piu'' recente da https://dotnet.microsoft.com/download/dotnet/10.0')
}

$running=@(Get-Process Flip -ErrorAction SilentlyContinue)
if($running.Count){
    throw 'Chiudi FLIP dal menu dell''icona vicino all''orologio prima di installare o aggiornare.'
}

if(Test-Path -LiteralPath $Destination){
    if(!(Test-Path -LiteralPath (Join-Path $Destination 'Flip.exe') -PathType Leaf)){
        throw 'La cartella di destinazione esiste ma non contiene una installazione valida di FLIP.'
    }
    foreach($name in @('Flip.exe','Flip.dll')){
        try{$probe=[IO.File]::Open((Join-Path $Destination $name),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probe.Dispose()}
        catch{throw 'I file in destinazione risultano in uso. Chiudi FLIP prima di aggiornare.'}
    }
    if((Get-Item -LiteralPath $Destination -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){
        throw 'La cartella di destinazione e'' un collegamento a un''altra cartella.'
    }
    $backup=$Destination+'.backup-'+[Guid]::NewGuid().ToString('N')
    Copy-Item -LiteralPath $Destination -Destination $backup -Recurse
    Write-Output ('Backup precedente creato: '+$backup)
}

try{
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source | Copy-Item -Destination $Destination -Recurse -Force
    foreach($file in Get-ChildItem -LiteralPath $Source -File -Recurse){
        $relative=$file.FullName.Substring($Source.Length).TrimStart('\')
        $srcHash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $dstHash=(Get-FileHash -LiteralPath (Join-Path $Destination $relative) -Algorithm SHA256).Hash
        if($srcHash -ne $dstHash){throw ('Checksum post-copia non corrispondente: '+$relative)}
    }
}
catch{
    # Copia incompleta: ripristina i file della versione precedente dal backup appena creato
    if($backup){
        Write-Host 'Copia non riuscita: ripristino della versione precedente.' -ForegroundColor Yellow
        Get-ChildItem -LiteralPath $backup | Copy-Item -Destination $Destination -Recurse -Force -ErrorAction Continue
    }
    throw
}

# Conserva solo il backup appena creato: rimuove i precedenti di questa stessa cartella
if($backup){
    $pattern='^'+[regex]::Escape((Split-Path -Leaf $Destination))+'\.backup-[0-9a-f]{32}$'
    foreach($old in Get-ChildItem -LiteralPath (Split-Path -Parent $Destination) -Directory){
        if($old.Name -notmatch $pattern -or $old.FullName -eq $backup){continue}
        if($old.Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
        if(!(Test-Path -LiteralPath (Join-Path $old.FullName 'Flip.exe') -PathType Leaf)){continue}
        try{Remove-Item -LiteralPath $old.FullName -Recurse -Force; Write-Output ('Backup precedente rimosso: '+$old.FullName)}
        catch{Write-Warning ('Backup non rimosso: '+$old.FullName)}
    }
}

$exe=Join-Path $Destination 'Flip.exe'
if($CreateShortcuts -and !$NoShortcuts){
    & $exe --install-shortcuts
    if($LASTEXITCODE -ne 0){throw 'Creazione collegamenti Desktop e Menu Start non riuscita.'}
}
if($EnableStartup -and !$NoStartup){
    & $exe --enable-startup
    if($LASTEXITCODE -ne 0){throw 'Abilitazione avvio con Windows non riuscita.'}
}

Write-Host ''
Write-Host ("FLIP installato e verificato in: " + $Destination) -ForegroundColor Green
Write-Host ('Collegamenti Desktop/Menu Start: ' + [bool]($CreateShortcuts -and !$NoShortcuts) + ' | Avvio con Windows: ' + [bool]($EnableStartup -and !$NoStartup))

if(!$NoLaunch){
    Start-Process -FilePath $exe
    Write-Host 'FLIP avviato! Trovi l''icona nella barra di sistema vicino all''orologio.' -ForegroundColor Green
}
