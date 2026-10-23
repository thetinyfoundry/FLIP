param(
    [Parameter(Mandatory=$true)][int]$X,
    [Parameter(Mandatory=$true)][int]$Y,
    [ValidateRange(320,7680)][int]$Width=1280,
    [ValidateRange(240,4320)][int]$Height=720,
    [ValidateRange(5,60)][int]$Seconds=25,
    [string]$Output
)
$ErrorActionPreference='Stop'
if($Width % 2 -or $Height % 2){throw 'Use even capture dimensions for H.264.'}
$ffmpeg=Get-Command ffmpeg -ErrorAction SilentlyContinue
if(!$ffmpeg){throw 'FFmpeg must already be installed. This script does not download software.'}
$root=Split-Path -Parent $PSScriptRoot
if(!$Output){$Output=Join-Path $root ('_local\demo\flip-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.mp4')}
$Output=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $Output){throw 'Output already exists. Choose a new filename.'}
New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force | Out-Null
Write-Output ('Recording rectangle: '+$X+','+$Y+' '+$Width+'x'+$Height+' for '+$Seconds+' seconds.')
Write-Output 'Only synthetic demo files should be visible. Hide notifications and private windows. No microphone/system audio is captured.'
Write-Output 'Capture starts in 5 seconds. Press Ctrl+C to cancel.'
Start-Sleep -Seconds 5
& $ffmpeg.Source -hide_banner -loglevel warning -n -f gdigrab -framerate 30 -draw_mouse 1 -offset_x $X -offset_y $Y -video_size ($Width.ToString()+'x'+$Height) -i desktop -t $Seconds -an -c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -movflags +faststart $Output
if($LASTEXITCODE -ne 0){throw 'Recording failed. Review or remove any incomplete local output.'}
Write-Output ('Recorded locally: '+$Output)
Write-Output 'Review the full video for private content and actual results before uploading. Nothing was published.'
