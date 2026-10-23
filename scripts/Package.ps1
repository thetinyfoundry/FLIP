param([string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputRoot) { $OutputRoot = Join-Path $root 'dist' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$version = ([xml](Get-Content (Join-Path $root 'src/Flip/Flip.csproj') -Raw)).Project.PropertyGroup.Version
$name = 'Flip-' + $version + '-win-x64'
$stage = Join-Path $OutputRoot ($name + '-release')
if (Test-Path -LiteralPath $stage) { throw 'Package staging folder already exists. Choose a new OutputRoot.' }
$payload = Join-Path $stage 'payload'
& (Join-Path $PSScriptRoot 'Build.ps1') -Output $payload
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& (Join-Path $payload 'Flip.exe') --test-input --evidence (Join-Path $OutputRoot 'package-tests')
if ($LASTEXITCODE -ne 0) { throw 'Input checks failed.' }
$licenses = Join-Path $payload 'licenses'
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $licenses -Recurse
foreach ($package in @('skiasharp','skiasharp.nativeassets.win32')) {
    $source = Join-Path $root ('packages/' + $package + '/4.150.1')
    $dest = Join-Path $licenses $package
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match 'LICENSE|NOTICE' } | Copy-Item -Destination $dest
    if (!(Test-Path (Join-Path $dest 'LICENSE.txt'))) { throw ('Missing license for ' + $package) }
}
# Obtain the native library's full license bundle from its exact official release.
$nativeArchive = Join-Path $OutputRoot 'pdfium-7961-win-x64.tgz'
Invoke-WebRequest 'https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/7961/pdfium-win-x64.tgz' -OutFile $nativeArchive
if ((Get-FileHash $nativeArchive).Hash.ToLowerInvariant() -ne '88276459349b291c41f10422dad0210f007c04d919c8fa56472b6b7c6406adf4') { throw 'PDFium notice archive checksum mismatch.' }
$native = Join-Path $OutputRoot 'pdfium-notices'
New-Item -ItemType Directory -Path $native -Force | Out-Null
& tar -xzf $nativeArchive -C $native
if ($LASTEXITCODE -ne 0) { throw 'Cannot extract PDFium notice archive.' }
$noticeFiles = @(Get-ChildItem $native -Recurse -File | Where-Object { $_.FullName -match '[\\/]licenses?[\\/]|[\\/](LICENSE|NOTICE|COPYING)(\.|$)' })
if (!$noticeFiles.Count) { throw 'Missing native PDFium license bundle.' }
foreach ($file in $noticeFiles) {
    $relative = $file.FullName.Substring($native.Length).TrimStart('\','/')
    $target = Join-Path (Join-Path $licenses 'pdfium') $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
# Debug symbols are unnecessary for installation and may expose build metadata.
Get-ChildItem -LiteralPath $payload -Filter *.pdb -File -Recurse | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1'),(Join-Path $PSScriptRoot 'Install-Release.ps1'),(Join-Path $PSScriptRoot 'Install-DocumentEngine.ps1') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'docs/INSTALL-PACKAGE.txt') -Destination (Join-Path $stage 'README.txt')
Copy-Item -LiteralPath (Join-Path $root 'scripts/Install-FLIP.cmd') -Destination $stage
$manifest = @(Get-ChildItem $stage -Recurse -File | ForEach-Object { ((Get-FileHash $_.FullName).Hash.ToLowerInvariant()) + '  ' + $_.FullName.Substring($stage.Length + 1).Replace('\','/') })
$manifest | Set-Content (Join-Path $stage 'SHA256SUMS.txt') -Encoding utf8
$zip = Join-Path $OutputRoot ($name + '.zip')
if (Test-Path -LiteralPath $zip) { throw 'Archive already exists; refusing to replace it.' }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
((Get-FileHash $zip).Hash.ToLowerInvariant() + '  ' + (Split-Path $zip -Leaf)) | Set-Content ($zip + '.sha256') -Encoding ascii
Write-Output ('Package ready: ' + $zip)
