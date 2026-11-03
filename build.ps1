param(
    [Parameter(Mandatory = $true)][string]$ManagedDir,
    [string]$HarmonyPath,
    [string]$HarmonyLicensePath,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home'
$env:APPDATA = Join-Path $root '.tools\appdata'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (!$HarmonyPath) {
    $packageRoot = Join-Path $root '.packages\lib.harmony.2.3.3'
    $HarmonyPath = Join-Path $packageRoot 'lib\net472\0Harmony.dll'
    if (!(Test-Path -LiteralPath $HarmonyPath)) {
        if ($Offline) { throw 'Harmony is missing. Run once without -Offline, or provide -HarmonyPath.' }
        New-Item -ItemType Directory -Force (Join-Path $root '.packages') | Out-Null
        $archive = Join-Path $root '.packages\lib.harmony.2.3.3.nupkg'
        Invoke-WebRequest -UseBasicParsing 'https://api.nuget.org/v3-flatcontainer/lib.harmony/2.3.3/lib.harmony.2.3.3.nupkg' -OutFile $archive
        $expectedHash = '87B63DDB92F04FCB89C30B7EBAE473C948DD65E80569EB94AEF08B163BC3BF63'
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Harmony package checksum did not match the pinned version.' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $packageRoot)
    }
}
$project = Join-Path $root 'RechargeTas.csproj'
dotnet restore $project --configfile (Join-Path $root 'NuGet.Config') --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
dotnet build $project --no-restore -c Release "-p:ManagedDir=$ManagedDir" "-p:HarmonyPath=$HarmonyPath"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$manifest = Get-Content -LiteralPath (Join-Path $root 'mod.json') -Raw | ConvertFrom-Json
$output = Join-Path $root 'artifacts\Tas.Studio'
New-Item -ItemType Directory -Force $output | Out-Null
$packageFiles = @('TasStudio.dll', '0Harmony.dll', 'mod.json', 'INSTALL.md', 'Harmony-LICENSE.txt')
foreach ($name in @('TasStudio.dll', '0Harmony.dll', 'mod.json', 'INSTALL.md')) {
    Copy-Item -LiteralPath (Join-Path $root "bin\Release\netstandard2.1\$name") -Destination $output
}
$license = if ($HarmonyLicensePath) { $HarmonyLicensePath } else { Join-Path $root '.packages\lib.harmony.2.3.3\LICENSE' }
if (!(Test-Path -LiteralPath $license)) { throw 'Harmony license is required for the release package.' }
Copy-Item -LiteralPath $license -Destination (Join-Path $output 'Harmony-LICENSE.txt')
$docs = Join-Path $output 'docs'
New-Item -ItemType Directory -Force $docs | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Filter '*.md' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $docs
    $packageFiles += 'docs/' + $_.Name
}
# Package an explicit file list so stale binaries or local data cannot leak into
# a release. One Tas.Studio directory is the archive's only top-level folder.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $root ('artifacts\Tas-Studio-' + $manifest.version + '-clean.zip')
$zipStream = [IO.File]::Open($zip, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$zipArchive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    foreach ($name in $packageFiles) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, (Join-Path $output $name), ('Tas.Studio/' + $name), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zipArchive.Dispose(); $zipStream.Dispose() }
Write-Output "Built $output and $zip. Copy the whole folder to <Game>\Recharge\Mods\Tas.Studio with the game closed."
