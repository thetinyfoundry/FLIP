param([string]$OutputRoot)
# Crea insieme lo zip GitHub e l'MSIX Store dallo stesso commit, con lo stesso numero di versione.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Push-Location $root
try{
    if(@(& git status --porcelain).Count){throw 'Ci sono modifiche non registrate: le due versioni devono uscire dallo stesso commit.'}
    $commit=(& git rev-parse HEAD).Trim()
    $version=([xml](Get-Content -LiteralPath 'src\Flip\Flip.csproj' -Raw)).Project.PropertyGroup.Version | Where-Object {$_} | Select-Object -First 1
    if(!$OutputRoot){$OutputRoot=Join-Path $root ('dist\'+$version)}
    $OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
    if(Test-Path -LiteralPath $OutputRoot){throw ('Cartella gia esistente: '+$OutputRoot)}
    & (Join-Path $PSScriptRoot 'Package.ps1') -OutputRoot (Join-Path $OutputRoot 'github')
    & (Join-Path $PSScriptRoot 'Package-Store.ps1') -OutputRoot (Join-Path $OutputRoot 'store')
    $github=Get-ChildItem -LiteralPath (Join-Path $OutputRoot 'github') -Recurse -Filter Flip.dll | Select-Object -First 1
    $store=Get-ChildItem -LiteralPath (Join-Path $OutputRoot 'store') -Recurse -Filter Flip.dll | Select-Object -First 1
    foreach($file in $github,$store){
        $found=(Get-Item -LiteralPath $file.FullName).VersionInfo.ProductVersion
        if($found -ne ($version+'+'+$commit)){throw ('Versione non allineata: '+$file.FullName+' = '+$found)}
    }
    Write-Output ('GitHub e Store allineati: '+$version+' da '+$commit)
}
finally{Pop-Location}
