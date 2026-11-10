param(
    [string]$GameDirectory = "C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer",
    [string]$HarmonyPath = (Join-Path $PSScriptRoot '..\.packages\lib.harmony.2.3.3\lib\net472\0Harmony.dll'),
    [string]$TasPath,
    [string]$ReferenceTasPath,
    [int]$ReferenceEndFrame = -1,
    [switch]$SearchSmoke,
    [switch]$SearchCheckpoints,
    [switch]$CheckpointBudgets,
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
if($SearchSmoke -and ($null -eq $referencePath -or $SearchSmokeStartFrame -lt 0 -or $SearchSmokeEndFrame -le $SearchSmokeStartFrame -or $ReferenceEndFrame -le $SearchSmokeEndFrame)) { throw 'SearchSmoke requires a captured reference containing the entire selected start/end range.' }
if($SearchCheckpoints -and !$SearchSmoke){throw 'SearchCheckpoints requires SearchSmoke.'}
if($CheckpointBudgets -and !$SearchCheckpoints){throw 'CheckpointBudgets requires SearchCheckpoints.'}
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
if ($referencePath) {
    Copy-Item -LiteralPath $referencePath -Destination (Join-Path $run 'reference.tas.json')
    Set-Content -LiteralPath (Join-Path $run 'reference-end.txt') -Value $ReferenceEndFrame
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
    $timeoutSeconds = if ($SearchSmoke -or $VerifyCompactMovie) { 480 } else { 240 }
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
