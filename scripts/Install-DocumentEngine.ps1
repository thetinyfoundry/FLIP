param([string]$Destination = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Flip'))
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath (Join-Path $Destination 'Flip.exe'))) { throw 'Install FLIP before adding the document engine.' }
if (Get-Process Flip -ErrorAction SilentlyContinue) { throw 'Exit FLIP before installing its document engine.' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('flip-engine-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $archive = Join-Path $work 'pandoc.zip'
    Invoke-WebRequest 'https://github.com/jgm/pandoc/releases/download/3.12.1/pandoc-3.12.1-windows-x86_64.zip' -OutFile $archive
    if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne '6ef431cb20b2c24a0371fc19200dc49d452be54b1b5c9239f3462841ef53e587') { throw 'Pandoc checksum mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $work 'unpacked')
    $engine = Get-ChildItem (Join-Path $work 'unpacked') -Filter pandoc.exe -Recurse | Select-Object -First 1
    if (!$engine) { throw 'The verified archive does not contain pandoc.exe.' }
    New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'tools'),(Join-Path $Destination 'licenses\pandoc') | Out-Null
    Copy-Item -LiteralPath $engine.FullName -Destination (Join-Path $Destination 'tools\pandoc.exe')
    foreach ($name in @('COPYING.rtf','COPYRIGHT.txt')) {
        $notice = Join-Path $engine.DirectoryName $name
        if (!(Test-Path -LiteralPath $notice)) { throw ('Missing Pandoc notice: ' + $name) }
        Copy-Item -LiteralPath $notice -Destination (Join-Path $Destination 'licenses\pandoc')
    }
    Write-Output 'Official Pandoc engine installed and checksum verified. LibreOffice is a separate optional installation for document rendering.'
} finally {
    $resolved = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf).StartsWith('flip-engine-')) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
