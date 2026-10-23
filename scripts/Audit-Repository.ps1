param()
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Push-Location $root
try{
    $files=@(& git ls-files)
    if($LASTEXITCODE -ne 0 -or !$files.Count){throw 'No tracked Git source files found.'}
    $forbiddenPattern='(^|/)(_local|app|archives|evidence|fixtures|packages|tools|WORKING_PROTOTYPE|bin|obj)/|\.(bak|lnk|dll|exe|pdb|zip|pfx|p12|pem|key)$|(^|/)\.env'
    $forbidden=@($files | Where-Object {$_ -match $forbiddenPattern})
    if($forbidden.Count){throw ('Private/build files tracked: '+($forbidden -join ', '))}
    $untracked=@(& git ls-files --others --exclude-standard)
    if($LASTEXITCODE -ne 0){throw 'Untracked source inventory failed.'}
    $badUntracked=@($untracked | Where-Object {$_ -match $forbiddenPattern})
    if($badUntracked.Count){throw ('Private files waiting to be added: '+($badUntracked -join ', '))}
    $pattern='[A-Za-z]:[\\/]+Users[\\/]+|/Us'+'ers/|/ho'+'me/|github_pat_[A-Za-z0-9_]{15,}|gh[pousr]_[A-Za-z0-9]{20,}|sk-(proj-)?[A-Za-z0-9_-]{20,}|(AKIA|ASIA)[A-Z0-9]{16}|xox[baprs]-[A-Za-z0-9-]{10,}|-----BEGIN (RSA |OPENSSH |EC |ENCRYPTED )?PRIVATE KEY-----'
    $scopes=@('working-tree')+@(& git rev-list --all)
    foreach($file in $untracked){
        $hits=@(& git grep --no-index -I -l -E $pattern -- $file)
        if($LASTEXITCODE -gt 1){throw ('New source scan failed: '+$file)}
        if($hits.Count){throw ('Potential private content in new source: '+$file)}
    }
    foreach($scope in $scopes){
        if($scope -ne 'working-tree'){
            $historicalPaths=@(& git ls-tree -r --name-only $scope)
            if($LASTEXITCODE -ne 0){throw ('History path scan failed for '+$scope)}
            $badPaths=@($historicalPaths | Where-Object {$_ -match $forbiddenPattern})
            if($badPaths.Count){throw ('Private/build files in history '+$scope+': '+($badPaths -join ', '))}
        }
        $hits=if($scope -eq 'working-tree'){@(& git grep -I -l -E $pattern -- .)}else{@(& git grep -I -l -E $pattern $scope -- .)}
        if($LASTEXITCODE -gt 1){throw ('Git scan failed for '+$scope)}
        if($hits.Count){throw ('Potential private content in '+$scope+': '+($hits -join ', '))}
    }
    $metadata=@(& git log --all --format=fuller) -join "`n"
    if($LASTEXITCODE -ne 0){throw 'Commit metadata scan failed.'}
    if($metadata -match $pattern){throw 'Potential private content in commit metadata. Inspect locally; do not publish raw scan output.'}
    $identities=@(& git log --all --format='%an <%ae> | committer: %cn <%ce>' | Sort-Object -Unique)
    Write-Output ('Checked '+$files.Count+' tracked files, '+$untracked.Count+' new source files and '+($scopes.Count-1)+' revisions.')
    Write-Output 'No matches for the configured private-path/key patterns. This is not a guarantee of anonymity or secret absence.'
    Write-Output 'Review commit identities, images/audio, licenses and README before publishing:'
    $identities | Write-Output
}
finally{Pop-Location}
