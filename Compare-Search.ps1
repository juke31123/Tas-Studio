param(
    [Parameter(Mandatory=$true)][string]$JobPath,
    [string]$HostPath=(Join-Path $PSScriptRoot 'Engine.Host\bin\Release\net10.0\TasStudio.EngineHost.exe'),
    [ValidateRange(1,86400)][int]$Seconds=5,
    [ValidateRange(1,32)][int[]]$Workers=@(1,2,4),
    [ValidateRange(1,10)][int]$Repeats=1,
    [ValidateRange(1,4096)][int]$BeamWidth=128,
    [switch]$WithoutSeed
)
$ErrorActionPreference='Stop'
$source=Split-Path -Parent (Resolve-Path -LiteralPath $JobPath).Path
$hostExe=(Resolve-Path -LiteralPath $HostPath).Path
$output=Join-Path $PSScriptRoot ('artifacts\search-matrix-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $output | Out-Null
$rows=@()
foreach($repeat in 1..$Repeats){
  foreach($count in $Workers){
    foreach($steal in @($false,$true)){
      $job=[IO.File]::ReadAllText((Join-Path $source 'job.json'))|ConvertFrom-Json
      $job.Options.Mode='beam';$job.Options.Seconds=$Seconds;$job.Options.Workers=$count
      $job.Options.BeamWidth=$BeamWidth
      $job.Options|Add-Member -NotePropertyName ExperimentalWorkStealing -NotePropertyValue $steal -Force
      if($WithoutSeed){$job.SeedInputs=@()}
      $folder=Join-Path $output ($repeat.ToString()+'-'+$count+'-'+$steal)
      New-Item -ItemType Directory -Path (Join-Path $folder 'engine') | Out-Null
      foreach($name in @('native.trace.json','world.json','start.json','animation.json')){
        Copy-Item -LiteralPath (Join-Path $source ('engine\'+$name)) -Destination (Join-Path $folder ('engine\'+$name))
      }
      $copy=Join-Path $folder 'job.json'
      [IO.File]::WriteAllText($copy,($job|ConvertTo-Json -Depth 50),(New-Object Text.UTF8Encoding($false)))
      $watch=[Diagnostics.Stopwatch]::StartNew()
      & $hostExe solve $copy *> (Join-Path $folder 'host.log')
      $code=$LASTEXITCODE;$watch.Stop()
      $result=[IO.File]::ReadAllText((Join-Path $folder 'result.json'))|ConvertFrom-Json
      $row=[pscustomobject]@{Repeat=$repeat;Workers=$count;Stealing=$steal;Seeded=!$WithoutSeed;Fps=$job.Fps;BeamWidth=$BeamWidth;
        Budget=$Seconds;ExitCode=$code;Status=$result.Status;Frames=$result.BestFrames;Depth=$result.Depth;
        Evaluated=$result.Evaluated;SimulatedFrames=$result.SimulatedFrames;ReplayFrames=$result.ReplayFrames;
        CacheBytes=$result.CacheBytes;StolenTasks=$result.StolenTasks;WorkerTasks=$result.WorkerTasks;
        SearchSeconds=$result.Seconds;WallSeconds=$watch.Elapsed.TotalSeconds}
      $rows+=$row;$row|ConvertTo-Json -Compress
      $rows|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding UTF8
      if($code -ne 0){throw "Search failed; inspect $folder"}
    }
  }
}
Write-Output "Offline candidates require native verification. Reports: $output"
