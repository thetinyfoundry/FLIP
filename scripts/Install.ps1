param(
    [string]$Source,
    [string]$Destination,
    [switch]$CreateShortcuts=$true,
    [switch]$EnableStartup=$true,
    [switch]$NoShortcuts,
    [switch]$NoStartup
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$Source){$Source=Join-Path $root 'app'}
if(!$Destination){$Destination=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Flip'}
$Source=[IO.Path]::GetFullPath($Source).TrimEnd('\')
$Destination=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
if($Source -eq $Destination -or $Destination.StartsWith($Source+'\',[StringComparison]::OrdinalIgnoreCase) -or $Source.StartsWith($Destination+'\',[StringComparison]::OrdinalIgnoreCase)){
    throw 'Source and destination must be separate, non-nested folders.'
}
foreach($name in @('Flip.exe','Flip.dll','Flip.runtimeconfig.json','Flip.deps.json','Flip.ico','LICENSE','THIRD_PARTY_NOTICES.md','libSkiaSharp.dll','pdfium.dll')){
    if(!(Test-Path -LiteralPath (Join-Path $Source $name) -PathType Leaf)){throw ('Incomplete build: missing '+$name+'. Run Build.ps1 first.')}
}
$runtime=@(& dotnet --list-runtimes)
if($LASTEXITCODE -ne 0 -or !($runtime -match '^Microsoft.WindowsDesktop.App 10\.')){throw 'Install Microsoft .NET Desktop Runtime 10 x64 first.'}
$running=@(Get-Process Flip -ErrorAction SilentlyContinue)
if($running.Count){throw 'Exit Flip from the tray menu before installing or updating.'}
if(Test-Path -LiteralPath $Destination){
    if(!(Test-Path -LiteralPath (Join-Path $Destination 'Flip.exe') -PathType Leaf)){throw 'Destination exists and is not a Flip installation. Choose an empty new path.'}
    foreach($name in @('Flip.exe','Flip.dll')){
        try{$probe=[IO.File]::Open((Join-Path $Destination $name),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probe.Dispose()}
        catch{throw 'Destination is in use or not writable. Exit that Flip installation before updating.'}
    }
    $backup=$Destination+'.backup-'+[Guid]::NewGuid().ToString('N')
    Copy-Item -LiteralPath $Destination -Destination $backup -Recurse
    Write-Output ('Backup: '+$backup)
}
# Copy, never recursively delete/move existing paths. Keep the build as rollback material.
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Get-ChildItem -LiteralPath $Source | Copy-Item -Destination $Destination -Recurse -Force
foreach($file in Get-ChildItem -LiteralPath $Source -File -Recurse){
    $relative=$file.FullName.Substring($Source.Length).TrimStart('\')
    $sha=[Security.Cryptography.SHA256]::Create()
    try{
        $a=[BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($file.FullName)))
        $b=[BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes((Join-Path $Destination $relative))))
    }finally{$sha.Dispose()}
    if($a -ne $b){throw ('Installed file checksum mismatch: '+$relative)}
}
$exe=Join-Path $Destination 'Flip.exe'
if($CreateShortcuts -and !$NoShortcuts){& $exe --install-shortcuts; if($LASTEXITCODE -ne 0){throw 'Shortcut installation failed.'}}
if($EnableStartup -and !$NoStartup){& $exe --enable-startup; if($LASTEXITCODE -ne 0){throw 'Startup configuration failed.'}}
Write-Output ('Installed and checksum-verified: '+$exe)
Write-Output ('Shortcuts requested: '+[bool]($CreateShortcuts -and !$NoShortcuts)+'; startup requested: '+[bool]($EnableStartup -and !$NoStartup))
Write-Output 'Use -NoShortcuts and/or -NoStartup to opt out of the installation defaults. Keep this path stable.'
