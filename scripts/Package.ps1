param([string]$OutputRoot, [string]$PdfiumArchive)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputRoot) { $OutputRoot = Join-Path $root 'dist' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$version = ([xml](Get-Content (Join-Path $root 'src/Flip/Flip.csproj') -Raw)).Project.PropertyGroup.Version
$name = 'Flip-' + $version + '-win-x64'
$stage = Join-Path $OutputRoot ($name + '-release')
if (Test-Path -LiteralPath $stage) { throw 'Cartella di staging gia esistente: scegli una nuova OutputRoot.' }

$payload = Join-Path $stage 'payload'
& (Join-Path $PSScriptRoot 'Build.ps1') -Output $payload
if ($LASTEXITCODE -ne 0) { throw 'Build fallita.' }

& dotnet (Join-Path $payload 'Flip.dll') --test-input --evidence (Join-Path $OutputRoot 'package-tests')
if ($LASTEXITCODE -ne 0) { throw 'Test InputTests falliti durante il packaging.' }

$licenses = Join-Path $payload 'licenses'
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $licenses -Recurse
foreach ($package in @('skiasharp','skiasharp.nativeassets.win32')) {
    $source = Join-Path $root ('packages/' + $package + '/4.157.0')
    $dest = Join-Path $licenses $package
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match 'LICENSE|NOTICE' } | Copy-Item -Destination $dest
    if (!(Test-Path (Join-Path $dest 'LICENSE.txt'))) { throw ('Licenza mancante per ' + $package) }
}
foreach ($package in @('microsoft.ml.onnxruntime','microsoft.ml.onnxruntime.managed')) {
    $source = Join-Path $root ('packages/' + $package + '/1.32.0')
    $dest = Join-Path $licenses $package
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    $license = Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -in @('LICENSE','LICENSE.txt') } | Select-Object -First 1
    if (!$license) { throw ('Licenza mancante per ' + $package) }
    Copy-Item -LiteralPath $license.FullName,(Join-Path $source 'ThirdPartyNotices.txt') -Destination $dest
}

# Pacchetto licenze ufficiale di PDFium
$nativeArchive = $PdfiumArchive
if (!$nativeArchive) {
    $nativeArchive = Join-Path $OutputRoot 'pdfium-8165-win-x64.tgz'
    Invoke-WebRequest 'https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8165/pdfium-win-x64.tgz' -OutFile $nativeArchive
}
if ((Get-FileHash $nativeArchive).Hash.ToLowerInvariant() -ne 'ca7338ae7779b8b1b82f4580305c4d762d214f8df1d54b47647fe3cc75989ded') { throw 'Checksum archivio PDFium non valido.' }
$native = Join-Path $OutputRoot 'pdfium-notices'
New-Item -ItemType Directory -Path $native -Force | Out-Null
& (Join-Path $env:SystemRoot 'System32\tar.exe') -xzf $nativeArchive -C $native
if ($LASTEXITCODE -ne 0) { throw 'Impossibile estrarre le licenze PDFium.' }
$noticeFiles = @(Get-ChildItem $native -Recurse -File | Where-Object { $_.FullName -match '[\\/]licenses?[\\/]|[\\/](LICENSE|NOTICE|COPYING)(\.|$)' })
if (!$noticeFiles.Count) { throw 'Bundle licenze PDFium mancante.' }
foreach ($file in $noticeFiles) {
    $relative = $file.FullName.Substring($native.Length).TrimStart('\','/')
    $target = Join-Path (Join-Path $licenses 'pdfium') $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}

# Esclusione rigorosa di simboli debug, collegamenti, log diagnostici e directory obj
# Windows PowerShell 5.1 ignora -Include insieme a -LiteralPath: il filtro deve stare in Where-Object.
Get-ChildItem -LiteralPath $stage -File -Recurse -Force | Where-Object { $_.Extension -in @('.pdb','.lnk') -or $_.Name -eq 'events.jsonl' } | Remove-Item -Force
Get-ChildItem -LiteralPath $stage -Directory -Recurse -Force | Where-Object { $_.Name -in @('logs','obj','evidence','package-tests') } | Remove-Item -Recurse -Force

# Verifica privacy automatica su Flip.dll impacchettata (UTF-8 e UTF-16LE)
$dllPath = Join-Path $payload 'Flip.dll'
if (!(Test-Path -LiteralPath $dllPath -PathType Leaf)) { throw 'Flip.dll mancante nel payload.' }
$dllBytes = [IO.File]::ReadAllBytes($dllPath)
$dllUtf8 = [Text.Encoding]::UTF8.GetString($dllBytes)
$dllUtf16 = [Text.Encoding]::Unicode.GetString($dllBytes)
$forbiddenPatterns = @('\bDictum\b','\bComfyUI\b','\bWisper\b','\bFilee\b','C:\\Users\\','E:\\')
foreach ($pat in $forbiddenPatterns) {
    if ($dllUtf8 -match $pat -or $dllUtf16 -match $pat) {
        throw ("Audit privacy fallito su Flip.dll: trovata occorrenza di '$pat'.")
    }
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Installa-FLIP.cmd'),
    (Join-Path $PSScriptRoot 'Disinstalla-FLIP.cmd'),
    (Join-Path $PSScriptRoot 'Install.ps1'),
    (Join-Path $PSScriptRoot 'Install-Release.ps1'),
    (Join-Path $PSScriptRoot 'Install-DocumentEngine.ps1') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'docs/INSTALL-PACKAGE.txt') -Destination (Join-Path $stage 'README.txt')

$forbiddenLeft = @(Get-ChildItem -LiteralPath $stage -File -Recurse -Force | Where-Object { $_.Extension -in @('.pdb','.lnk') -or $_.Name -eq 'events.jsonl' }) +
    @(Get-ChildItem -LiteralPath $stage -Directory -Recurse -Force | Where-Object { $_.Name -in @('logs','obj') })
if ($forbiddenLeft.Count -gt 0) {
    throw 'Il pacchetto di staging contiene file o cartelle vietate (.pdb, .lnk, events.jsonl, logs/ o obj/).'
}

$manifest = @(Get-ChildItem $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    ((Get-FileHash $_.FullName).Hash.ToLowerInvariant()) + '  ' + $_.FullName.Substring($stage.Length + 1).Replace('\','/')
})
[IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS.txt'), $manifest, [Text.UTF8Encoding]::new($false))

$zip = Join-Path $OutputRoot ($name + '.zip')
if (Test-Path -LiteralPath $zip) { throw 'Archivio ZIP gia esistente; rifiuto la sovrascrittura.' }
# Compress-Archive in Windows PowerShell 5.1 scrive separatori '\' non conformi allo standard ZIP.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $stage -File -Recurse -Force | Sort-Object FullName)) {
        $entry = $file.FullName.Substring($stage.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose() }
((Get-FileHash $zip).Hash.ToLowerInvariant() + '  ' + (Split-Path $zip -Leaf)) | Set-Content ($zip + '.sha256') -Encoding ascii
Write-Output ('Pacchetto pronto: ' + $zip)
