param(
    [Parameter(Mandatory=$true)][string]$JobPath,
    [string]$HostPath
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($HostPath)) { $HostPath = Join-Path $PSScriptRoot 'bin\Release\net10.0\TasStudio.EngineHost.exe' }
$source = (Resolve-Path -LiteralPath $JobPath).Path
$sourceDirectory = Split-Path -Parent $source
$originalHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$worker = (Resolve-Path -LiteralPath $HostPath).Path
$run = Join-Path $PSScriptRoot ('artifacts\protocol\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
function Trial([string]$name) {
    $directory = Join-Path $run $name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $directory 'job.json')
    Copy-Item -LiteralPath (Join-Path $sourceDirectory 'engine') -Destination (Join-Path $directory 'engine') -Recurse
    return $directory
}
function Run-Rejected([string]$directory, [string]$status, [string]$message) {
    $process = Start-Process -FilePath $worker -ArgumentList @('solve', ('"' + (Join-Path $directory 'job.json') + '"')) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $directory 'host.log') -RedirectStandardError (Join-Path $directory 'host-error.log')
    $code = $process.ExitCode
    $result = Get-Content -LiteralPath (Join-Path $directory 'result.json') -Raw | ConvertFrom-Json
    if (($code -eq 0 -and $status -ne 'Cancelled') -or $result.Status -ne $status -or @($result.Inputs).Count -ne 0 -or !$result.Finished -or $result.Message -notlike $message) {
        throw "Host failed the expected rejection for $(Split-Path -Leaf $directory): $($result.Status) / $($result.Message)"
    }
    Write-Host "PASS: $(Split-Path -Leaf $directory)"
}
# Identity checks run before loading any exported game state.
$directory = Trial 'tampered-world'
[IO.File]::AppendAllText((Join-Path $directory 'engine\world.json'), ' ')
Run-Rejected $directory 'Error' '*Engine capture changed: world.json*'

$directory = Trial 'wrong-prefix-fps'
$tracePath = Join-Path $directory 'engine\native.trace.json'
$trace = Get-Content -LiteralPath $tracePath -Raw | ConvertFrom-Json
$jobPath = Join-Path $directory 'job.json'
$job = Get-Content -LiteralPath $jobPath -Raw | ConvertFrom-Json
$trace.Fps = if ($job.Fps -eq 280) { 144 } else { 280 }
$trace | ConvertTo-Json -Depth 100 -Compress | Set-Content -LiteralPath $tracePath -Encoding UTF8
$hex = (Get-FileHash -LiteralPath $tracePath -Algorithm SHA256).Hash
$bytes = New-Object byte[] ($hex.Length / 2)
for ($i=0; $i -lt $bytes.Length; $i++) { $bytes[$i]=[Convert]::ToByte($hex.Substring($i*2,2),16) }
$job.Engine.TraceHash=[Convert]::ToBase64String($bytes)
$job | ConvertTo-Json -Depth 100 -Compress | Set-Content -LiteralPath $jobPath -Encoding UTF8
Run-Rejected $directory 'Error' '*same-FPS native witness*'

$directory = Trial 'missing-mover-witness'
$tracePath = Join-Path $directory 'engine\native.trace.json'
$trace = Get-Content -LiteralPath $tracePath -Raw | ConvertFrom-Json
$jobPath = Join-Path $directory 'job.json'
$job = Get-Content -LiteralPath $jobPath -Raw | ConvertFrom-Json
$trace.CaptureMovers = $false
$trace | ConvertTo-Json -Depth 100 -Compress | Set-Content -LiteralPath $tracePath -Encoding UTF8
$hex = (Get-FileHash -LiteralPath $tracePath -Algorithm SHA256).Hash
$bytes = New-Object byte[] ($hex.Length / 2)
for ($i=0; $i -lt $bytes.Length; $i++) { $bytes[$i]=[Convert]::ToByte($hex.Substring($i*2,2),16) }
$job.Engine.TraceHash=[Convert]::ToBase64String($bytes)
$job | ConvertTo-Json -Depth 100 -Compress | Set-Content -LiteralPath $jobPath -Encoding UTF8
Run-Rejected $directory 'Error' '*same-FPS native witness*'

$directory = Trial 'missing-spring-witness'
$tracePath = Join-Path $directory 'engine\native.trace.json'
$trace = Get-Content -LiteralPath $tracePath -Raw | ConvertFrom-Json
$jobPath = Join-Path $directory 'job.json'
$job = Get-Content -LiteralPath $jobPath -Raw | ConvertFrom-Json
$trace.CaptureSprings = $false
$trace | ConvertTo-Json -Depth 100 -Compress | Set-Content -LiteralPath $tracePath -Encoding UTF8
$hex = (Get-FileHash -LiteralPath $tracePath -Algorithm SHA256).Hash
$bytes = New-Object byte[] ($hex.Length / 2)
for ($i=0; $i -lt $bytes.Length; $i++) { $bytes[$i]=[Convert]::ToByte($hex.Substring($i*2,2),16) }
$job.Engine.TraceHash=[Convert]::ToBase64String($bytes)
$job | ConvertTo-Json -Depth 100 -Compress | Set-Content -LiteralPath $jobPath -Encoding UTF8
Run-Rejected $directory 'Error' '*same-FPS native witness*'

$directory = Trial 'cancel-before-prefix'
Set-Content -LiteralPath (Join-Path $directory 'cancel') -Value 'cancel'
Run-Rejected $directory 'Cancelled' '*cancel*'
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $originalHash) { throw 'The original job changed.' }
@{Passed=$true;Checks=5;SourceJob=$source} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding UTF8
Write-Host "Search-host protocol checks passed. Reports: $run"
