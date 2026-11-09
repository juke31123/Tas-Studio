param(
    [Parameter(Mandatory=$true)][string]$UpstreamRoot,
    [Parameter(Mandatory=$true)][string]$ManagedDir,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& (Join-Path $root 'prepare-engine-host.ps1') -UpstreamRoot $UpstreamRoot -ManagedDir $ManagedDir
$reference = Join-Path $root 'Engine.Host\obj\reference'
$staging = Join-Path $root ('artifacts\portable-staging\' + [Guid]::NewGuid().ToString('N'))
$hostOutput = Join-Path $staging 'Host'
$prepOutput = Join-Path $staging 'Prepare'
New-Item -ItemType Directory -Force -Path $staging | Out-Null
# Both executables carry their own runtime. End users do not need the SDK,
# Git, a checkout of the reference project, or a .NET runtime installation.
& dotnet publish (Join-Path $root 'Engine.Host\TasStudio.EngineHost.csproj') -c Release -r win-x64 --self-contained true --nologo `
    '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:PublishReadyToRun=false' "-p:EngineRoot=$reference" "-p:ManagedDir=$ManagedDir" -o $hostOutput
if ($LASTEXITCODE -ne 0) { throw 'Portable engine-host publish failed.' }
& dotnet publish (Join-Path $reference 'enginesim\Prep\IGTAP.EngineSim.Prep.csproj') -c Release -r win-x64 --self-contained true --nologo `
    '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:PublishReadyToRun=false' -o $prepOutput
if ($LASTEXITCODE -ne 0) { throw 'Portable local-game preparation publish failed.' }

foreach ($name in @('cuts.txt', 'hooks.txt', 'static-allow.txt')) {
    Copy-Item -LiteralPath (Join-Path $reference ('enginesim\' + $name)) -Destination (Join-Path $staging $name)
}
$notices = Join-Path $staging 'Notices'
New-Item -ItemType Directory -Force -Path $notices | Out-Null
Copy-Item -LiteralPath (Join-Path $reference 'enginesim\Box2D\LICENSE-Box2D.txt') -Destination $notices
Copy-Item -LiteralPath (Join-Path $reference 'enginesim\Box2D\LICENSE-Box2D.NetStandard.txt') -Destination $notices
Copy-Item -LiteralPath (Join-Path $root 'Solver.Cli\Newtonsoft-LICENSE.txt') -Destination $notices
Copy-Item -LiteralPath (Join-Path $root 'Engine.Host\Mono.Cecil-LICENSE.txt') -Destination (Join-Path $notices 'Mono.Cecil-LICENSE.txt')
$hostRuntime = Get-Content -LiteralPath (Join-Path $hostOutput 'TasStudio.EngineHost.runtimeconfig.json') -Raw | ConvertFrom-Json
$prepRuntime = Get-Content -LiteralPath (Join-Path $prepOutput 'IGTAP.EngineSim.Prep.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = ($hostRuntime.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
$prepVersion = ($prepRuntime.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
if (!$runtimeVersion -or $runtimeVersion -ne $prepVersion) { throw 'The portable tools must use the same self-contained runtime version.' }
$hostAssets = Get-Content -LiteralPath (Join-Path $root 'Engine.Host\obj\project.assets.json') -Raw | ConvertFrom-Json
$runtimePackage = $null
foreach ($folder in $hostAssets.packageFolders.PSObject.Properties.Name) {
    $candidate = Join-Path $folder ('microsoft.netcore.app.runtime.win-x64\' + $runtimeVersion)
    if (Test-Path -LiteralPath (Join-Path $candidate 'THIRD-PARTY-NOTICES.TXT')) { $runtimePackage = $candidate; break }
}
if (!$runtimePackage) { throw 'The .NET runtime license and third-party notices are required for the portable package.' }
Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination (Join-Path $notices 'NET-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $notices 'NET-THIRD-PARTY-NOTICES.txt')
@'
Tas Studio external simulator

Wave search, the assembly preparation tool, Unity compatibility layer and
Box2D integration adapt ChandlerFerry/IGTAPTasMod at commit
134d96c6011dd729e75f86f95d6fc16382538aff.
Source: https://github.com/ChandlerFerry/IGTAPTasMod
Tas Studio frame integration: https://github.com/juke31123/Tas-Studio

The Box2D, Mono.Cecil and Newtonsoft.Json notices are in this directory.
The bundled .NET runtime notices are NET-LICENSE.txt and
NET-THIRD-PARTY-NOTICES.txt in this directory.
The game assembly is deliberately excluded. On first search, the preparation
tool reads the installed game and generates a private local simulator copy.
No installed game file is changed. Do not redistribute the generated copy.
'@ | Set-Content -LiteralPath (Join-Path $notices 'SOURCES.txt') -Encoding UTF8

$files = @()
foreach ($file in (Get-ChildItem -LiteralPath $staging -File -Recurse | Sort-Object FullName)) {
    # The host was compiled against the developer's local game. Ship its tools
    # and runtime only; the end user's game is prepared on that user's PC.
    if ($file.Name -ieq 'Assembly-CSharp.dll' -or $file.Extension -ieq '.pdb' -or $file.Name -ieq 'engine-host.manifest.json') { continue }
    $relative = $file.FullName.Substring($staging.Length + 1).Replace('\', '/')
    $destination = Join-Path $OutputDirectory $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    $files += [ordered]@{ Path = $relative; Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash }
}
$manifest = [ordered]@{
    Version = 1; FrameAdapter = 1; ReferenceCommit = '134d96c6011dd729e75f86f95d6fc16382538aff'
    GameSha256 = (Get-FileHash -LiteralPath (Join-Path $ManagedDir 'Assembly-CSharp.dll') -Algorithm SHA256).Hash
    Files = $files
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'package.json') -Encoding UTF8
Write-Output "Built portable Bruteforce tools in $OutputDirectory. Generated game code is excluded."
