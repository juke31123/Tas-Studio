param(
    [string]$GameDirectory = "C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer",
    [string]$HarmonyPath = (Join-Path $PSScriptRoot '..\.packages\lib.harmony.2.3.3\lib\net472\0Harmony.dll'),
    [string]$TasPath,
    [string]$ReferenceTasPath,
    [int]$ReferenceEndFrame = -1,
    [ValidateRange(0,999999)][int]$ReferenceCaptureStartFrame = 0,
    [switch]$SearchSmoke,
    [switch]$SearchCheckpoints,
    [switch]$CheckpointBudgets,
    [string]$CustomMapId,
    [ValidateRange(0,2)][int]$VmanMode=0,
    [ValidateRange(0,20000)][int]$CaptureFrames=0,
    [switch]$MapSaveIsolation,
    [switch]$HotInstallMap,
    [switch]$TrajectorySmoke,
    [switch]$ActionStats,
    [switch]$TrajectoryStability,
    [switch]$TrajectoryLive,
    [switch]$TrajectoryPuSteps,
    [switch]$RuntimeBenchmark,
    [ValidateSet('reset','spike','spike-dash')][string]$PreviewInteraction,
    [ValidateRange(0,999999)][int]$TrajectoryAnchorFrame=0,
    [switch]$VerifyCompactMovie,
    [ValidateSet('chain','beam','wave')][string[]]$SearchModes = @('chain','beam'),
    [int]$SearchSmokeStartFrame = 100,
    [int]$SearchSmokeEndFrame = 130,
    [ValidateRange(1,32)][int]$SearchWorkers = 2,
    [switch]$ExperimentalWorkStealing,
    [int]$PhysicsDiagnosticsEndFrame = 0,
    [int[]]$FrameRates = @(),
    [string]$EngineHostPath,
    [string]$CaptureDirectory = (Join-Path $PSScriptRoot 'Engine.Host\artifacts\native')
)
$ErrorActionPreference = 'Stop'
$referencePath = $null
$referenceHash = $null
if (![string]::IsNullOrWhiteSpace($ReferenceTasPath)) {
    if (![string]::IsNullOrWhiteSpace($TasPath) -or $PSBoundParameters.ContainsKey('FrameRates')) { throw 'A reference TAS supplies its own frame rate; do not combine it with TasPath or FrameRates.' }
    $referencePath = (Resolve-Path -LiteralPath $ReferenceTasPath).Path
    $recording = Get-Content -LiteralPath $referencePath -Raw | ConvertFrom-Json
    if ($null -eq $recording.StartSetup -or $recording.Frames.Count -lt 1) { throw 'The reference TAS must contain a configured start and recorded frames.' }
    if ($ReferenceEndFrame -eq -1) { $ReferenceEndFrame = $recording.Frames.Count }
    if ($ReferenceEndFrame -lt 1 -or $ReferenceEndFrame -gt $recording.Frames.Count) { throw 'ReferenceEndFrame must be within the recording and is exclusive.' }
    $recordingRate = 0
    if (![int]::TryParse([string]$recording.FrameRate, [ref]$recordingRate)) { throw 'The reference TAS has no valid recording FrameRate.' }
    $FrameRates = @($recordingRate)
    $referenceHash = (Get-FileHash -LiteralPath $referencePath -Algorithm SHA256).Hash
} elseif (![string]::IsNullOrWhiteSpace($TasPath)) {
    if ($PSBoundParameters.ContainsKey('FrameRates')) { throw 'Use the TAS recording rate or explicit developer FrameRates, not both.' }
    $recording = Get-Content -LiteralPath $TasPath -Raw | ConvertFrom-Json
    $rateProperty = $recording.PSObject.Properties['FrameRate']
    $recordingRate = 0
    if ($null -eq $rateProperty -or ![int]::TryParse([string]$rateProperty.Value, [ref]$recordingRate)) { throw 'The TAS file has no valid recording FrameRate.' }
    $FrameRates = @($recordingRate)
} elseif (!$PSBoundParameters.ContainsKey('FrameRates')) {
    throw 'Pass -TasPath for a same-FPS fixture or -ReferenceTasPath to capture the actual recording. Explicit -FrameRates is available for developer regression tests.'
}
if ($null -eq $referencePath -and $PSBoundParameters.ContainsKey('ReferenceEndFrame')) { throw 'ReferenceEndFrame requires ReferenceTasPath.' }
if ($ReferenceCaptureStartFrame -gt 0 -and ($null -eq $referencePath -or $ReferenceCaptureStartFrame -ge $ReferenceEndFrame)) { throw 'ReferenceCaptureStartFrame requires a reference and must precede ReferenceEndFrame.' }
if ($ReferenceCaptureStartFrame -gt 0 -and $TrajectorySmoke -and $TrajectoryAnchorFrame -ne $ReferenceCaptureStartFrame) { throw 'A late reference trajectory must start at ReferenceCaptureStartFrame.' }
if($SearchSmoke -and ($null -eq $referencePath -or $SearchSmokeStartFrame -lt 0 -or $SearchSmokeEndFrame -le $SearchSmokeStartFrame -or $ReferenceEndFrame -le $SearchSmokeEndFrame)) { throw 'SearchSmoke requires a captured reference containing the entire selected start/end range.' }
if($SearchCheckpoints -and !$SearchSmoke){throw 'SearchCheckpoints requires SearchSmoke.'}
if($CheckpointBudgets -and !$SearchCheckpoints){throw 'CheckpointBudgets requires SearchCheckpoints.'}
if($MapSaveIsolation -and !$CustomMapId){throw 'MapSaveIsolation requires CustomMapId.'}
if($HotInstallMap -and (!$CustomMapId -or $referencePath)){throw 'HotInstallMap requires a prototype CustomMapId without ReferenceTasPath.'}
if($VerifyCompactMovie -and !$referencePath){throw 'VerifyCompactMovie requires ReferenceTasPath.'}
foreach ($fps in $FrameRates) { if ($fps -notin @(30,50,60,100,120,144,165,244,280)) { throw "Unsupported FPS: $fps" } }
if ($FrameRates.Count -eq 0) { throw 'Select at least one frame rate.' }
if (@($FrameRates | Sort-Object -Unique).Count -ne $FrameRates.Count) { throw 'Each frame rate may appear only once.' }
Write-Host ('Simulator test rate(s): ' + ($FrameRates -join ', ') + ' FPS; physics uses the TAS 50 Hz PU schedule.')
if (Get-Process IGTAPfullGame -ErrorAction SilentlyContinue) { throw 'Close IGTAP before running the controlled simulator test.' }
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
$managed = Join-Path $game 'IGTAPfullGame_Data\Managed'
$mods = Join-Path $game 'Recharge\Mods'
$installed = Join-Path $mods 'Tas.Studio\TasStudio.dll'
$probe = [IO.Path]::GetFullPath((Join-Path $mods 'Tas.Studio.SimulationProbe'))
$modsPrefix = [IO.Path]::GetFullPath($mods).TrimEnd('\') + '\'
if (!$probe.StartsWith($modsPrefix, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $probe) -ne 'Tas.Studio.SimulationProbe') { throw 'Invalid temporary mod path.' }
if (Test-Path -LiteralPath $probe) { throw "A temporary probe already exists: $probe" }
$engine = if ([string]::IsNullOrWhiteSpace($EngineHostPath)) { Join-Path $PSScriptRoot 'Engine.Host\bin\Release\net10.0\TasStudio.EngineHost.exe' } else { [IO.Path]::GetFullPath($EngineHostPath) }
if (!(Test-Path -LiteralPath $engine)) { throw 'Prepare the local host with prepare-engine-host.ps1 first.' }
if (!(Test-Path -LiteralPath $installed)) { throw 'An installed Tas Studio mod is required for the controlled test.' }
$run = [IO.Path]::GetFullPath((Join-Path $CaptureDirectory (Get-Date -Format 'yyyyMMdd-HHmmss-fff')))
if ($run.StartsWith($modsPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Store test captures outside the game Mods directory.' }
New-Item -ItemType Directory -Path $run -Force | Out-Null
if($ActionStats){Set-Content -LiteralPath (Join-Path $run 'action-stats.txt') -Value 'Read-only native action readiness and resource-consumption checks.'}
if($RuntimeBenchmark){Set-Content -LiteralPath (Join-Path $run 'runtime-benchmark.txt') -Value 'Paused capture and canonical digest performance/parity checks.'}
if($TrajectoryLive -and !$TrajectorySmoke){throw 'TrajectoryLive requires TrajectorySmoke.'}
if($TrajectoryPuSteps -and (!$TrajectorySmoke -or $TrajectoryLive)){throw 'TrajectoryPuSteps requires TrajectorySmoke without TrajectoryLive.'}
if($TrajectoryLive){Set-Content -LiteralPath (Join-Path $run 'trajectory-live.txt') -Value 'Continuous trajectory responsiveness test.'}
if($TrajectoryPuSteps){Set-Content -LiteralPath (Join-Path $run 'trajectory-pu-steps.txt') -Value 'Alternate frame and PU steps.'}
if($PreviewInteraction){Set-Content -LiteralPath (Join-Path $run 'preview-interaction.txt') -Value $PreviewInteraction}
if ($referencePath) {
    Copy-Item -LiteralPath $referencePath -Destination (Join-Path $run 'reference.tas.json')
    Set-Content -LiteralPath (Join-Path $run 'reference-end.txt') -Value $ReferenceEndFrame
    Set-Content -LiteralPath (Join-Path $run 'reference-start.txt') -Value $ReferenceCaptureStartFrame
    Write-Host "Reference recording: $($recording.Name), frames [0,$ReferenceEndFrame). Original file remains read-only."
}
$buildLog = Join-Path $run 'build.log'
& dotnet build (Join-Path $PSScriptRoot 'RechargeTas.csproj') -c Release --nologo "-p:ManagedDir=$managed" "-p:HarmonyPath=$HarmonyPath" *> $buildLog
if ($LASTEXITCODE -ne 0) { throw "Mod build failed: $buildLog" }
& dotnet build (Join-Path $PSScriptRoot 'Engine.Host\NativeProbe\Probe.csproj') -c Release --nologo "-p:ManagedDir=$managed" "-p:HarmonyPath=$HarmonyPath" *>> $buildLog
if ($LASTEXITCODE -ne 0) { throw "Native probe build failed: $buildLog" }
Copy-Item -LiteralPath $installed -Destination (Join-Path $run 'TasStudio.dll')
$originalHash = (Get-FileHash -LiteralPath $installed).Hash
$oldCapture = $env:TAS_STUDIO_SIM_CAPTURE
$movies = Join-Path $mods 'Tas.Studio\data\movies'
$existingMovies = @{}
if (Test-Path -LiteralPath $movies) { Get-ChildItem -LiteralPath $movies -File | ForEach-Object { $existingMovies[$_.FullName] = $true } }
$process = $null
$saveRoot=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\LocalLow\Pepper tango games\IGTAPfullGame'))
$savePrefix=$saveRoot.TrimEnd('\')+'\'
$saveBackup=Join-Path $run 'saved-progress-before'
function SavedFiles {
    if(Test-Path -LiteralPath $saveRoot){
        foreach($folder in Get-ChildItem -LiteralPath $saveRoot -Directory -Filter 'Savedata*'){
            Get-ChildItem -LiteralPath $folder.FullName -File -Recurse
        }
    }
}
$saveBefore=@{}
foreach($file in SavedFiles){
    $relative=$file.FullName.Substring($savePrefix.Length)
    $destination=Join-Path $saveBackup $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force|Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
    $saveBefore[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
try {
    Set-Content -LiteralPath (Join-Path $run 'rates.txt') -Value ($FrameRates -join ',')
    if($SearchSmoke) {
        Set-Content -LiteralPath (Join-Path $run 'search-smoke.txt') -Value $engine
        Set-Content -LiteralPath (Join-Path $run 'search-range.txt') -Value "$SearchSmokeStartFrame,$SearchSmokeEndFrame"
        Set-Content -LiteralPath (Join-Path $run 'search-modes.txt') -Value ($SearchModes -join ',')
        Set-Content -LiteralPath (Join-Path $run 'search-workers.txt') -Value $SearchWorkers
        if($ExperimentalWorkStealing){Set-Content -LiteralPath (Join-Path $run 'search-stealing.txt') -Value 'true'}
        if($SearchCheckpoints){Set-Content -LiteralPath (Join-Path $run 'search-checkpoints.txt') -Value 'true'}
        if($CheckpointBudgets){Set-Content -LiteralPath (Join-Path $run 'search-checkpoint-budgets.txt') -Value 'true'}
    }
    if($VerifyCompactMovie){Set-Content -LiteralPath (Join-Path $run 'compact-replay.txt') -Value 'true'}
    if($CustomMapId -or $VmanMode -ne 0){@{CustomMapId=$CustomMapId;VmanMode=$VmanMode}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'configured-start.json')}
    if($CaptureFrames -gt 0){Set-Content -LiteralPath (Join-Path $run 'capture-frames.txt') -Value $CaptureFrames}
    if($MapSaveIsolation){Set-Content -LiteralPath (Join-Path $run 'map-save-owner-test.txt') -Value $CustomMapId}
    if($HotInstallMap){@{Prototype=$CustomMapId;Id=('tas-studio-probe-'+[guid]::NewGuid().ToString('N'))}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'hot-install-map.json')}
    if($TrajectorySmoke){Set-Content -LiteralPath (Join-Path $run 'trajectory-smoke.txt') -Value $TrajectoryAnchorFrame}
    if($TrajectoryStability){Set-Content -LiteralPath (Join-Path $run 'trajectory-stability.txt') -Value 'true'}
    $timeoutSeconds = if ($SearchSmoke -or $VerifyCompactMovie -or $ReferenceEndFrame -gt 20000) { 480 } else { 240 }
    Set-Content -LiteralPath (Join-Path $run 'timeout-seconds.txt') -Value $timeoutSeconds
    if($PhysicsDiagnosticsEndFrame -gt 0) { Set-Content -LiteralPath (Join-Path $run 'physics-end.txt') -Value $PhysicsDiagnosticsEndFrame }
    $env:TAS_STUDIO_SIM_CAPTURE = $run
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\Release\netstandard2.1\TasStudio.dll') -Destination $installed -Force
    New-Item -ItemType Directory -Path $probe | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Engine.Host\NativeProbe\mod.json') -Destination $probe
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Engine.Host\NativeProbe\bin\Release\netstandard2.1\TasEngineProbe.dll') -Destination $probe
    $process = Start-Process -FilePath (Join-Path $game 'IGTAPfullGame.exe') -ArgumentList @('-batchmode','-nographics','-logFile', ('"' + (Join-Path $run 'unity.log') + '"')) -WorkingDirectory $game -WindowStyle Hidden -PassThru
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while (!$process.WaitForExit(5000)) {
        if ($elapsed.Elapsed.TotalSeconds -gt $timeoutSeconds + 15) { Stop-Process -Id $process.Id; throw 'Controlled simulator capture timed out.' }
    }
}
finally {
    $env:TAS_STUDIO_SIM_CAPTURE = $oldCapture
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    if (Get-Process IGTAPfullGame -ErrorAction SilentlyContinue) { throw 'Another game is running; refusing to replace its DLL.' }
    Copy-Item -LiteralPath (Join-Path $run 'TasStudio.dll') -Destination $installed -Force
    if ((Get-FileHash -LiteralPath $installed).Hash -ne $originalHash) { throw 'Installed DLL restoration failed.' }
    $temporaryMapFile=Join-Path $run 'hot-install-directory.txt'
    if(Test-Path -LiteralPath $temporaryMapFile){
        $ownedMap=[IO.Path]::GetFullPath([IO.File]::ReadAllText($temporaryMapFile).Trim())
        $mapsPrefix=[IO.Path]::GetFullPath((Join-Path $mods 'recharge.maps\maps')).TrimEnd('\')+'\'
        $mapId=Split-Path -Leaf $ownedMap
        if(!$ownedMap.StartsWith($mapsPrefix,[StringComparison]::OrdinalIgnoreCase) -or $mapId -notmatch '^tas-studio-probe-[a-f0-9]{32}$'){throw 'Invalid temporary map cleanup path.'}
        if(Test-Path -LiteralPath $ownedMap){
            if([IO.File]::ReadAllText((Join-Path $ownedMap 'tas-studio-test-owner.txt')) -ne $mapId){throw 'Temporary map ownership marker changed.'}
            Remove-Item -LiteralPath $ownedMap -Recurse
        }
    }
    # Check after process exit too: OnApplicationQuit can save after the probe's
    # last in-game hash. Restore only this controlled run's Savedata* changes.
    $restored=0;$removed=0
    foreach($file in SavedFiles){
        $full=[IO.Path]::GetFullPath($file.FullName)
        if(!$full.StartsWith($savePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Save inspection escaped its root.'}
        $relative=$full.Substring($savePrefix.Length)
        if(!$saveBefore.ContainsKey($relative)){Remove-Item -LiteralPath $full;$removed++}
    }
    foreach($relative in $saveBefore.Keys){
        $destination=[IO.Path]::GetFullPath((Join-Path $saveRoot $relative))
        if(!$destination.StartsWith($savePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Save restoration escaped its root.'}
        if(!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $saveBefore[$relative]){
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force|Out-Null
            Copy-Item -LiteralPath (Join-Path $saveBackup $relative) -Destination $destination -Force;$restored++
        }
        if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $saveBefore[$relative]){throw 'Saved progress restoration failed.'}
    }
    @{Preserved=$true;OriginalFiles=$saveBefore.Count;RestoredFiles=$restored;RemovedTestFiles=$removed}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'saved-progress-isolation.json')
    if (Test-Path -LiteralPath $probe) { Move-Item -LiteralPath $probe -Destination (Join-Path $run 'probe') }
    if ($referencePath -and (Get-FileHash -LiteralPath $referencePath -Algorithm SHA256).Hash -ne $referenceHash) { throw 'The original reference TAS file changed during validation.' }
    $smokeJob=Join-Path $run 'search-smoke-jobs.txt'
    if(Test-Path -LiteralPath $smokeJob) {
      foreach($line in [IO.File]::ReadAllLines($smokeJob) | Where-Object {![string]::IsNullOrWhiteSpace($_)} | Select-Object -Unique) {
        $owned=[IO.Path]::GetFullPath($line.Trim())
        $searchRoot=[IO.Path]::GetFullPath((Join-Path $mods 'Tas.Studio\data\search')).TrimEnd('\')+'\'
        if(!$owned.StartsWith($searchRoot,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $owned) -notmatch '^[a-f0-9]{32}$') { throw 'Invalid owned smoke-search path.' }
        $source=Get-Content -LiteralPath (Join-Path $owned 'original.tas.json') -Raw | ConvertFrom-Json
        if($source.Name -notlike 'engine-probe-*') { throw 'Refusing to move an unowned search.' }
        $archive=Join-Path $run 'search-jobs'; New-Item -ItemType Directory -Path $archive -Force | Out-Null
        Move-Item -LiteralPath $owned -Destination (Join-Path $archive (Split-Path -Leaf $owned))
      }
    }
    if (Test-Path -LiteralPath $movies) {
        New-Item -ItemType Directory -Path (Join-Path $run 'movies') -Force | Out-Null
        Get-ChildItem -LiteralPath $movies -Filter 'engine-probe-*.tas.json' -File | Where-Object { !$existingMovies.ContainsKey($_.FullName) } | ForEach-Object {
            $movie = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
            if ($movie.Name -like 'engine-probe-*') { Move-Item -LiteralPath $_.FullName -Destination (Join-Path $run 'movies') }
        }
    }
}
if (!(Select-String -LiteralPath (Join-Path $run 'result.log') -SimpleMatch 'PASS captures complete; original saved progress unchanged' -Quiet)) { throw "Native capture failed. Inspect $run" }
$summary = @()
foreach ($fps in $FrameRates) {
    $directory = Join-Path $run "fps-$fps"
    & $engine restore-check $directory *> (Join-Path $directory 'host.log')
    $code = $LASTEXITCODE
    $comparison = [PSCustomObject]@{Passed=$false;Compared=0;Frame=-1;Pu=-1;Field='Host setup/execution failed'}
    $restore = [PSCustomObject]@{Passed=$false}
    if (Test-Path -LiteralPath (Join-Path $directory 'comparison.json')) { $comparison = Get-Content -LiteralPath (Join-Path $directory 'comparison.json') -Raw | ConvertFrom-Json }
    if (Test-Path -LiteralPath (Join-Path $directory 'restore-summary.json')) { $restore = Get-Content -LiteralPath (Join-Path $directory 'restore-summary.json') -Raw | ConvertFrom-Json }
    $workersPassed = $false
    if ($code -eq 0) {
        & $engine worker-check $directory *> (Join-Path $directory 'workers.log')
        $workersPassed = $LASTEXITCODE -eq 0
    }
    $referenceMatch = $true
    if ($referencePath) {
        $referenceMatch = $false
        if (Test-Path -LiteralPath (Join-Path $directory 'reference-comparison.json')) { $referenceMatch = (Get-Content -LiteralPath (Join-Path $directory 'reference-comparison.json') -Raw | ConvertFrom-Json).Passed }
    }
    $summary += [PSCustomObject]@{ Fps=$fps; ExitCode=$code; NativeMatch=$comparison.Passed; Compared=$comparison.Compared; Frame=$comparison.Frame; Pu=$comparison.Pu; Field=$comparison.Field; RestoreMatch=$restore.Passed; WorkerMatch=$workersPassed; ReferenceMatch=$referenceMatch }
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding UTF8
$summary | Format-Table -AutoSize
Write-Host "Installed DLL restored. Captures and comparison reports: $run"
if ($summary | Where-Object { $_.ExitCode -ne 0 -or !$_.NativeMatch -or !$_.RestoreMatch -or !$_.WorkerMatch -or !$_.ReferenceMatch }) { exit 1 }
exit 0
