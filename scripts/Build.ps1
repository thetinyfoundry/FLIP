param([switch]$DocumentEngine,[string]$PandocArchive,[string]$Output)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$project=Join-Path $root 'src\Flip\Flip.csproj'
if(!$Output){$Output=Join-Path $root 'app'}
$Output=[IO.Path]::GetFullPath($Output)
$existing=Join-Path $Output 'Flip.dll'
if(Test-Path -LiteralPath $existing){
    try{$probe=[IO.File]::Open($existing,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probe.Dispose()}
    catch{throw 'Output is in use or not writable. Exit Flip or choose -Output with a new folder before building.'}
}
if($DocumentEngine){
    $toolRoot=Join-Path $root 'tools'
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
    if(!$PandocArchive){
        $PandocArchive=Join-Path $toolRoot 'pandoc-3.12.1-windows-x86_64.zip'
        if(!(Test-Path -LiteralPath $PandocArchive)){
            Invoke-WebRequest -Uri 'https://github.com/jgm/pandoc/releases/download/3.12.1/pandoc-3.12.1-windows-x86_64.zip' -OutFile $PandocArchive
        }
    }
    $stream=[IO.File]::OpenRead($PandocArchive)
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}
    finally{$sha.Dispose();$stream.Dispose()}
    if($hash -ne '6ef431cb20b2c24a0371fc19200dc49d452be54b1b5c9239f3462841ef53e587'){throw 'Pandoc archive SHA256 mismatch.'}
    $destination=Join-Path $toolRoot 'pandoc'
    if(!(Test-Path -LiteralPath (Join-Path $destination 'pandoc-3.12.1\pandoc.exe'))){
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($PandocArchive,$destination)
    }
}
Push-Location $root
try{
    & dotnet restore $project --configfile (Join-Path $root 'NuGet.Config') --locked-mode -r win-x64
    if($LASTEXITCODE -ne 0){throw 'Restore failed.'}
    & dotnet publish $project -c Release -r win-x64 --self-contained false --no-restore -o $output
    if($LASTEXITCODE -ne 0){throw 'Publish failed. Exit Flip before rebuilding app/.'}
    $pandoc=Get-ChildItem -Path (Join-Path $root 'tools\pandoc') -Filter pandoc.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if($DocumentEngine -and $pandoc){
        New-Item -ItemType Directory -Path (Join-Path $output 'tools'),(Join-Path $output 'licenses\pandoc') -Force | Out-Null
        Copy-Item -LiteralPath $pandoc.FullName -Destination (Join-Path $output 'tools\pandoc.exe')
        foreach($name in @('COPYING.rtf','COPYRIGHT.txt')){
            $notice=Join-Path $pandoc.Directory.FullName $name
            if(Test-Path -LiteralPath $notice){Copy-Item -LiteralPath $notice -Destination (Join-Path $output 'licenses\pandoc')}
        }
    }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $output
    Write-Output ('Built '+$output+'\Flip.exe. Run with --ui for the file panel; --background for tray only.')
}
finally{Pop-Location}
