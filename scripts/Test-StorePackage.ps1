param([Parameter(Mandatory)][string]$Package,[Parameter(Mandatory)][string]$Evidence)
# Installa e prova il pacchetto Store. Aggiunge un certificato e una policy di Windows: solo macchine usa e getta.
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Questa prova modifica Windows: si esegue solo su una macchina temporanea di GitHub Actions.'}
$Package=(Resolve-Path -LiteralPath $Package).Path
New-Item -ItemType Directory -Path $Evidence -Force | Out-Null
$Evidence=(Resolve-Path -LiteralPath $Evidence).Path
$failures=[Collections.Generic.List[string]]::new()
function Check([string]$Name,[bool]$Ok,[string]$Detail=''){
    Write-Host (('OK   ','FAIL ')[!$Ok]+$Name+$(if($Detail){' - '+$Detail}))
    if(!$Ok){$failures.Add($Name)}
}
function Invoke-Flip([string]$Arguments,[int]$Seconds=600){
    $process=Start-Process -FilePath $alias -ArgumentList $Arguments -PassThru
    if(!$process.WaitForExit($Seconds*1000)){$process.Kill();throw ('FLIP non ha risposto: '+$Arguments)}
    $process.ExitCode
}

# Firma di prova al posto di quella dello Store e installazione
$sdk=Get-ChildItem -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Directory | Where-Object {Test-Path (Join-Path $_.FullName 'x64\signtool.exe')} | Sort-Object {[version]($_.Name -replace '^[^0-9]+','0')} | Select-Object -Last 1
$signtool=Join-Path $sdk.FullName 'x64\signtool.exe'
$subject='CN=F13DDC2D-F07A-47CA-98BD-801A90665EC0'
$certificate=New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
$cer=Join-Path $Evidence 'test.cer'
Export-Certificate -Cert $certificate -FilePath $cer | Out-Null
foreach($store in 'TrustedPeople','Root'){Import-Certificate -FilePath $cer -CertStoreLocation ('Cert:\LocalMachine\'+$store) | Out-Null}
$signed=Join-Path $Evidence 'signed.msix'
Copy-Item -LiteralPath $Package -Destination $signed
& $signtool sign /fd SHA256 /sha1 $certificate.Thumbprint $signed
if($LASTEXITCODE -ne 0){throw 'Firma di prova non riuscita.'}
Add-AppxPackage -Path $signed
$app=Get-AppxPackage -Name TheTinyFoundry.FLIPConverter
Check 'pacchetto installato' ($null -ne $app -and $app.Status -eq 'Ok') $app.PackageFullName
Check 'famiglia uguale a Partner Center' ($app.PackageFamilyName -eq 'TheTinyFoundry.FLIPConverter_brr9a4rdb0vdt')
$alias=Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\flip-converter.exe'
Check 'comando flip-converter.exe' (Test-Path -LiteralPath $alias)

# Policy di Smart App Control: stesse regole, in sola osservazione, con la firma di prova nel ruolo dello Store
$catalog=Join-Path $app.InstallLocation 'AppxMetadata\CodeIntegrity.cat'
Check 'catalogo di integrita del pacchetto' (Test-Path -LiteralPath $catalog)
$policyApplied=$false
$template=Join-Path $env:SystemRoot 'schemas\CodeIntegrity\ExamplePolicies\SmartAppControl.xml'
$control=Join-Path $env:RUNNER_TEMP 'controllo-non-firmato.exe'
if((Test-Path -LiteralPath $template) -and (Get-Command Set-RuleOption -ErrorAction SilentlyContinue)){
    $policy=Join-Path $Evidence 'sac.xml'
    Copy-Item -LiteralPath $template -Destination $policy
    (Get-Item -LiteralPath $policy).IsReadOnly=$false
    # Queste voci rendono la policy attiva solo dove Windows ha gia' acceso Smart App Control.
    $xml=[xml](Get-Content -LiteralPath $policy -Raw)
    foreach($rule in @($xml.SiPolicy.Rules.Rule)){if($rule.Option -in 'Enabled:Conditional Windows Lockdown Policy','Enabled:Inherit Default Policy','Enabled:Developer Mode Dynamic Code Trust'){[void]$rule.ParentNode.RemoveChild($rule)}}
    foreach($setting in @($xml.SiPolicy.Settings.Setting)){if($setting.Provider -eq 'Microsoft'){[void]$setting.ParentNode.RemoveChild($setting)}}
    $xml.Save($policy)
    Set-CIPolicyIdInfo -FilePath $policy -ResetPolicyID -PolicyName 'FLIP SAC audit' | Out-Null
    Set-RuleOption -FilePath $policy -Option 3
    # Su questa macchina non c'e' la reputazione nel cloud: la prova e' piu' severa di Smart App Control.
    Set-RuleOption -FilePath $policy -Option 14 -Delete
    Add-SignerRule -FilePath $policy -CertificatePath $cer -User
    $id=([xml](Get-Content -LiteralPath $policy -Raw)).SiPolicy.PolicyID
    $binary=Join-Path $Evidence ($id+'.cip')
    ConvertFrom-CIPolicy -XmlFilePath $policy -BinaryFilePath $binary | Out-Null
    & (Join-Path $env:SystemRoot 'System32\CiTool.exe') --update-policy $binary --json | Out-Host
    $policyApplied=$LASTEXITCODE -eq 0
}
Check 'policy di Smart App Control attiva' $policyApplied
$since=(Get-Date).AddSeconds(-2)

# Controllo: lo stesso file, fuori dal pacchetto, deve risultare da bloccare. Se no la prova non vale.
Copy-Item -LiteralPath (Join-Path $app.InstallLocation 'tools\ffmpeg.exe') -Destination $control
& $control -version 2>&1 | Out-Null

# FLIP con l'identita' del pacchetto
$tests=Join-Path $Evidence 'input'
$code=Invoke-Flip ('--test-input --evidence "'+$tests+'"')
$failed=@()
$report=Join-Path $tests 'input-tests.json'
if(Test-Path -LiteralPath $report){$failed=@([regex]::Matches((Get-Content -LiteralPath $report -Raw),'"([^"]+)":\s*false') | ForEach-Object {$_.Groups[1].Value})}
Check 'test di input nel pacchetto' ($code -eq 0) ('codice '+$code+'; falliti: '+($failed -join ', '))
$log=Join-Path $tests 'events.jsonl'
if($failed.Count -and (Test-Path -LiteralPath $log)){Get-Content -LiteralPath $log | Where-Object {$_ -match 'regression|error|fatal|exception'} | Select-Object -Last 25 | ForEach-Object {Write-Host $_.Substring(0,[Math]::Min(500,$_.Length))}}

$work=Join-Path $env:USERPROFILE 'Desktop\prova-store'
New-Item -ItemType Directory -Path $work -Force | Out-Null
& $control -hide_banner -loglevel error -f lavfi -i testsrc=size=160x120:rate=5:duration=1 -f lavfi -i sine=duration=1 -y (Join-Path $work 'v.mp4') 2>&1 | Out-Null
# Windows lascia avviare questi programmi solo a FLIP: si provano con l'identita' del pacchetto.
function Invoke-InPackage([string]$Relative,[string]$Arguments){
    $mark=Join-Path $Evidence ([IO.Path]::GetFileNameWithoutExtension($Relative)+'.txt')
    $target=Join-Path $app.InstallLocation $Relative
    Invoke-CommandInDesktopPackage -PackageFamilyName $app.PackageFamilyName -AppId Flip -Command $env:ComSpec -Args ('/d /c ""'+$target+'" '+$Arguments+' >"'+$mark+'" 2>&1 & echo codice=%errorlevel%>>"'+$mark+'""')
    for($i=0;$i -lt 60;$i++){
        Start-Sleep -Milliseconds 500
        if((Test-Path -LiteralPath $mark) -and (Select-String -LiteralPath $mark -Pattern '^codice=' -Quiet -ErrorAction SilentlyContinue)){break}
    }
    if(Test-Path -LiteralPath $mark){Get-Content -LiteralPath $mark -Raw}else{''}
}
$ffmpegText=Invoke-InPackage 'tools\ffmpeg.exe' '-hide_banner -version'
Check 'FFmpeg incluso si avvia' ($ffmpegText -match 'ffmpeg version') ($ffmpegText -split "`n" | Select-Object -First 1)
$whisperText=Invoke-InPackage 'tools\whisper\whisper-cli.exe' '--help'
Check 'Whisper incluso si avvia' ($whisperText -match 'usage') ($whisperText -split "`n" | Select-Object -First 2)
Set-Content -LiteralPath (Join-Path $work 'a.csv') -Value "nome;importo`r`nRossi;1,50" -Encoding utf8
foreach($case in @(@('v.mp4','MP3','v.converted.mp3'),@('a.csv','JSON','a.converted.json'),@('a.csv','PDF','a.converted.pdf'))){
    [void](Invoke-Flip ('--convert '+$case[1]+' --files "'+(Join-Path $work $case[0])+'"'))
    Check ('conversione '+$case[0]+' in '+$case[1]) (Test-Path -LiteralPath (Join-Path $work $case[2]))
}

# Dati nella cartella del pacchetto, non in quella della versione GitHub.
Check 'impostazioni nella cartella del pacchetto' (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA ('Packages\'+$app.PackageFamilyName+'\LocalState\FLIP')))
Check 'nessun dato nella cartella GitHub' (!(Test-Path -LiteralPath (Join-Path $env:APPDATA 'FLIP')))

# Avvio con Windows
Check 'avvio con Windows attivato' ((Invoke-Flip '--enable-startup' 60) -eq 0)
$state=Join-Path 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData' ($app.PackageFamilyName+'\FlipStartup')
Check 'stato di avvio registrato' ((Get-ItemProperty -LiteralPath $state -ErrorAction SilentlyContinue).State -eq 2)
Check 'avvio con Windows disattivato' ((Invoke-Flip '--disable-startup' 60) -eq 0)
Check 'stato di avvio tolto' ((Get-ItemProperty -LiteralPath $state -ErrorAction SilentlyContinue).State -ne 2)

# Avvio normale e chiusura
$running=Start-Process -FilePath $alias -ArgumentList '--background' -PassThru
Start-Sleep -Seconds 12
$live=@(Get-Process Flip -ErrorAction SilentlyContinue)
Check 'FLIP resta aperto nell''area di notifica' ($live.Count -eq 1) (($live | ForEach-Object Path) -join ', ')
[void](Invoke-Flip '--stop' 60)
Start-Sleep -Seconds 4
Check 'FLIP si chiude su richiesta' (@(Get-Process Flip -ErrorAction SilentlyContinue).Count -eq 0)

# Menu contestuale: Windows crea la classe nel processo surrogato dichiarato dal pacchetto.
$source='[System.Runtime.InteropServices.DllImport("ole32.dll")] public static extern int CoCreateInstance(ref System.Guid c, System.IntPtr o, int x, ref System.Guid i, out System.IntPtr p);'
$ole=Add-Type -MemberDefinition $source -Name Ole -Namespace FlipTest -PassThru
$clsid=[Guid]'7C0B6C53-6C1E-4B5C-9B35-2E9C5D7E4A11'; $iid=[Guid]'A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9'; $pointer=[IntPtr]::Zero
$result=$ole::CoCreateInstance([ref]$clsid,[IntPtr]::Zero,4,[ref]$iid,[ref]$pointer)
Check 'menu contestuale registrato' ($result -eq 0 -and $pointer -ne [IntPtr]::Zero) ('0x{0:X8}' -f $result)

# Esito della policy
Start-Sleep -Seconds 3
$events=@(Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-CodeIntegrity/Operational';Id=3076,3077;StartTime=$since} -ErrorAction SilentlyContinue)
$events | ForEach-Object {$_.Message} | Set-Content -LiteralPath (Join-Path $Evidence 'code-integrity.txt') -Encoding utf8
Write-Host ('Eventi di controllo raccolti: '+$events.Count)
$blockedControl=@($events | Where-Object {$_.Message -match 'controllo-non-firmato'})
$blockedFlip=@($events | Where-Object {$_.Message -match 'TheTinyFoundry\.FLIPConverter'})
Check 'controllo: FFmpeg fuori dal pacchetto verrebbe bloccato' ($blockedControl.Count -gt 0)
Check 'nessun file di FLIP verrebbe bloccato' ($blockedFlip.Count -eq 0) (($blockedFlip | Select-Object -First 5 | ForEach-Object {($_.Message -split "`n")[0]}) -join ' | ')

# Disinstallazione pulita
Remove-AppxPackage -Package $app.PackageFullName
Check 'disinstallato' ($null -eq (Get-AppxPackage -Name TheTinyFoundry.FLIPConverter))
Check 'dati rimossi' (!(Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA ('Packages\'+$app.PackageFamilyName+'\LocalState\FLIP'))))
Check 'file dell''utente conservati' (Test-Path -LiteralPath (Join-Path $work 'a.converted.json'))

Write-Host ('Esito: '+$failures.Count+' problemi')
if($failures.Count){throw ('Prove non riuscite: '+($failures -join ', '))}
Write-Host 'Pacchetto Store: tutte le prove superate.'
