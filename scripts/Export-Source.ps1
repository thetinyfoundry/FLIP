param([string]$Output)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Push-Location $root
try{
    & (Join-Path $PSScriptRoot 'Audit-Repository.ps1')
    $changes=@(& git status --porcelain)
    if($LASTEXITCODE -ne 0){throw 'Git status failed.'}
    if($changes.Count){throw 'Commit reviewed source changes before exporting. Private ignored build files need not be added.'}
    if(!$Output){
        [xml]$project=Get-Content -LiteralPath (Join-Path $root 'src\Flip\Flip.csproj') -Raw
        $version=[string]$project.Project.PropertyGroup.Version
        $Output=Join-Path $root ('dist\Flip-'+$version+'-source.zip')
    }
    $Output=[IO.Path]::GetFullPath($Output)
    New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force | Out-Null
    & git archive --format=zip ('--output='+$Output) HEAD
    if($LASTEXITCODE -ne 0){throw 'Source export failed.'}
    Write-Output $Output
    Write-Output 'Source only: no Git database, local evidence, runtime packages or installed application.'
}
finally{Pop-Location}
