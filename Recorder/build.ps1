param(
    [Parameter(Mandatory=$true)][string]$ManagedDir,
    [string]$HarmonyPath,
    [string]$HarmonyLicensePath
)
$ErrorActionPreference='Stop'
$repository=Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $repository '..\.tools\dotnet-home'
$env:APPDATA=Join-Path $repository '..\.tools\appdata'
if(!$HarmonyPath) {
    foreach($candidate in @((Join-Path $repository '.packages\lib.harmony.2.3.3\lib\net472\0Harmony.dll'),(Join-Path $repository '..\.packages\lib.harmony.2.3.3\lib\net472\0Harmony.dll'))) {
        if(Test-Path -LiteralPath $candidate){$HarmonyPath=(Resolve-Path -LiteralPath $candidate).Path;break}
    }
}
if(!$HarmonyPath -or !(Test-Path -LiteralPath $HarmonyPath)){throw 'Pass -HarmonyPath for the pinned Harmony 2.3.3 net472 DLL, or build Tas Studio first.'}
if(!$HarmonyLicensePath){$HarmonyLicensePath=Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $HarmonyPath))) 'LICENSE'}
if(!(Test-Path -LiteralPath $HarmonyLicensePath)){throw 'The Harmony license is required for the shareable package.'}
& dotnet build (Join-Path $PSScriptRoot 'TasStudio.Recorder.csproj') -c Release --nologo "-p:ManagedDir=$ManagedDir" "-p:HarmonyPath=$HarmonyPath"
if($LASTEXITCODE -ne 0){throw 'Recorder build failed.'}
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mod.json') -Raw | ConvertFrom-Json
$output=Join-Path $repository 'artifacts\Tas.Studio.Recorder'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$files=@('TasStudioRecorder.dll','0Harmony.dll','mod.json','README.md','Harmony-LICENSE.txt')
foreach($name in $files | Where-Object {$_ -ne 'Harmony-LICENSE.txt'}) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('bin\Release\netstandard2.1\'+$name)) -Destination (Join-Path $output $name) -Force
}
Copy-Item -LiteralPath $HarmonyLicensePath -Destination (Join-Path $output 'Harmony-LICENSE.txt') -Force
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=Join-Path $repository ('artifacts\Tas-Studio-Validation-Recorder-'+$manifest.version+'.zip')
$temporary=$zip+'.tmp'
try {
    $stream=[IO.File]::Open($temporary,[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $archive=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
    try {foreach($name in $files){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $output $name),('Tas.Studio.Recorder/'+$name),[IO.Compression.CompressionLevel]::Optimal)|Out-Null}}
    finally {$archive.Dispose();$stream.Dispose()}
    if(Test-Path -LiteralPath $zip){[IO.File]::Replace($temporary,$zip,[NullString]::Value)}else{[IO.File]::Move($temporary,$zip)}
} finally {if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Set-Content -LiteralPath ($zip+'.sha256') -Value ($hash+'  '+(Split-Path -Leaf $zip)) -Encoding ASCII
Write-Host "Shareable recorder: $zip"
Write-Host ('Compressed size: '+(Get-Item -LiteralPath $zip).Length+' bytes; only the five listed mod files are included.')
