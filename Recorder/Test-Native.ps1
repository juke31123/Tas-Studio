param(
    [string]$GameDirectory="C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer",
    [string]$SaveRoot=(Join-Path $env:USERPROFILE 'AppData\LocalLow\Pepper tango games\IGTAPfullGame'),
    [string]$HarmonyPath=(Join-Path $PSScriptRoot '..\..\.packages\lib.harmony.2.3.3\lib\net472\0Harmony.dll')
)
$ErrorActionPreference='Stop'
if(Get-Process IGTAPfullGame -ErrorAction SilentlyContinue){throw 'Close IGTAP before the controlled recorder test.'}
$repo=Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $repo '..\.tools\dotnet-home'
$env:APPDATA=Join-Path $repo '..\.tools\appdata'
$managed=Join-Path $GameDirectory 'IGTAPfullGame_Data\Managed'
$mods=[IO.Path]::GetFullPath((Join-Path $GameDirectory 'Recharge\Mods'))
$target=Join-Path $mods 'Tas.Studio.Recorder'
$probe=Join-Path $mods 'Tas.Studio.Recorder.Test'
if((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $probe)){throw 'Do not overwrite an existing recorder/probe installation.'}
$run=Join-Path $repo ('Engine.Host\artifacts\recorder-native\'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $run | Out-Null
& dotnet build (Join-Path $PSScriptRoot 'TasStudio.Recorder.csproj') -c Release --nologo "-p:ManagedDir=$managed" "-p:HarmonyPath=$HarmonyPath" *> (Join-Path $run 'build.log')
if($LASTEXITCODE -ne 0){throw "Recorder build failed: $run"}
& dotnet build (Join-Path $PSScriptRoot 'Tests.Native\Probe.csproj') -c Release --nologo "-p:ManagedDir=$managed" "-p:HarmonyPath=$HarmonyPath" *> (Join-Path $run 'probe-build.log')
if($LASTEXITCODE -ne 0){throw "Probe build failed: $run"}
$studioManifest=Join-Path $mods 'Tas.Studio\mod.json'
$manifestHash=if(Test-Path -LiteralPath $studioManifest){(Get-FileHash -LiteralPath $studioManifest).Hash}else{$null}
$oldEnvironment=$env:TAS_RECORDER_TEST
$process=$null
$saveRootPath=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $SaveRoot).Path).TrimEnd('\')
$saveBackup=Join-Path $run 'original-saves'
New-Item -ItemType Directory -Path $saveBackup | Out-Null
function Save-Hashes([string]$directory){
    $rows=@(Get-ChildItem -LiteralPath $directory -Directory -Filter 'Savedata*' | ForEach-Object {Get-ChildItem -LiteralPath $_.FullName -Recurse -File} | Sort-Object FullName | ForEach-Object {$_.FullName.Substring($directory.Length)+':'+(Get-FileHash -LiteralPath $_.FullName).Hash})
    return ($rows -join "`n")
}
$saveHashBefore=Save-Hashes $saveRootPath
foreach($directory in Get-ChildItem -LiteralPath $saveRootPath -Directory -Filter 'Savedata*'){Copy-Item -LiteralPath $directory.FullName -Destination $saveBackup -Recurse}
# Normal gameplay is allowed to save. The test harness, not the mod, restores
# the user's specific IGTAP save folders and existing IGTAP PlayerPrefs values.
$preferences=@()
$company=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Pepper tango games')
if($company){
 foreach($name in $company.GetSubKeyNames() | Where-Object {$_ -like 'IGTAP*'}){
  $key=$company.OpenSubKey($name);$values=@{}
  foreach($valueName in $key.GetValueNames()){$values[$valueName]=@{Value=$key.GetValue($valueName,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames);Kind=$key.GetValueKind($valueName)}}
  $preferences+=@{Path='Software\Pepper tango games\'+$name;Values=$values};$key.Dispose()
 }
 $company.Dispose()
}
$preferences | Export-Clixml -LiteralPath (Join-Path $run 'preferences-before.clixml')
try {
    # Move only the specifically named manifest. Keep all installed binaries,
    # recordings and saves in place; restore this exact file in finally.
    if($manifestHash){Move-Item -LiteralPath $studioManifest -Destination (Join-Path $run 'studio-mod.json')}
    New-Item -ItemType Directory -Path $target,$probe | Out-Null
    foreach($name in @('TasStudioRecorder.dll','0Harmony.dll','mod.json','README.md')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('bin\Release\netstandard2.1\'+$name)) -Destination $target}
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Tests.Native\mod.json') -Destination $probe
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Tests.Native\bin\Release\netstandard2.1\TasRecorderProbe.dll') -Destination $probe
    $env:TAS_RECORDER_TEST=$run
    $arguments=@('-logFile',('"'+(Join-Path $run 'unity.log')+'"'),'-screen-fullscreen','0','-screen-width','1280','-screen-height','720')
    $process=Start-Process -FilePath (Join-Path $GameDirectory 'IGTAPfullGame.exe') -ArgumentList $arguments -WorkingDirectory $GameDirectory -WindowStyle Hidden -PassThru
    if(!$process.WaitForExit(240000)){Stop-Process -Id $process.Id;throw 'Recorder native test timed out.'}
} finally {
    if($process -and !$process.HasExited){Stop-Process -Id $process.Id}
    $env:TAS_RECORDER_TEST=$oldEnvironment
    try {
    $after=Join-Path $run 'saves-after-test';New-Item -ItemType Directory -Path $after | Out-Null
    foreach($directory in Get-ChildItem -LiteralPath $saveRootPath -Directory -Filter 'Savedata*'){
        $absolute=[IO.Path]::GetFullPath($directory.FullName)
        if(!$absolute.StartsWith($saveRootPath+'\',[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $absolute) -notmatch '^Savedata[^\\/:]*$'){throw 'Invalid named save-folder restore target.'}
        Move-Item -LiteralPath $absolute -Destination (Join-Path $after $directory.Name)
    }
    foreach($directory in Get-ChildItem -LiteralPath $saveBackup -Directory){Copy-Item -LiteralPath $directory.FullName -Destination $saveRootPath -Recurse}
    foreach($pref in $preferences){
        $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($pref['Path'],$true)
        if($key){
          try {
            foreach($name in $key.GetValueNames()){if(!$pref['Values'].ContainsKey($name)){$key.DeleteValue($name)}}
            foreach($name in $pref['Values'].Keys){
              $v=$pref['Values'][$name];$value=$v['Value'];$kind=$v['Kind']
              # GetValue can return an unsigned DWORD as Int64. SetValue expects
              # the same 32 bits represented by an Int32, including monitor -1.
              if($kind -eq [Microsoft.Win32.RegistryValueKind]::DWord){$value=[BitConverter]::ToInt32([BitConverter]::GetBytes([long]$value),0)}
              if($kind -eq [Microsoft.Win32.RegistryValueKind]::Binary){$value=[byte[]]$value}
              $key.SetValue($name,$value,$kind)
            }
          } finally {$key.Dispose()}
        }
    }
    if((Save-Hashes $saveRootPath) -ne $saveHashBefore){throw 'Original save-file restoration differs.'}
    } finally {
    foreach($path in @($target,$probe)) {
        $absolute=[IO.Path]::GetFullPath($path)
        if(!$absolute.StartsWith($mods+'\',[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $absolute) -notin @('Tas.Studio.Recorder','Tas.Studio.Recorder.Test')){throw 'Invalid owned test-mod path.'}
        if(Test-Path -LiteralPath $absolute){Move-Item -LiteralPath $absolute -Destination (Join-Path $run (Split-Path -Leaf $absolute))}
    }
    if($manifestHash){Move-Item -LiteralPath (Join-Path $run 'studio-mod.json') -Destination $studioManifest;if((Get-FileHash -LiteralPath $studioManifest).Hash -ne $manifestHash){throw 'Installed manifest was not restored exactly.'}}
    }
}
if(!(Test-Path -LiteralPath (Join-Path $run 'summary.json'))){throw "Native recorder test failed. Inspect $run"}
Get-Content -LiteralPath (Join-Path $run 'summary.json')
Write-Host "Controlled recorder test complete; full Tas Studio restored. Reports: $run"
