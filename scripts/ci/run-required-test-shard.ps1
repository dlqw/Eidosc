[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Project,

    [Parameter(Mandatory = $true)]
    [string]$Filter,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z0-9][a-z0-9-]*$')]
    [string]$ShardName,

    [ValidateRange(1, [int]::MaxValue)]
    [int]$MinimumTests = 1,

    [switch]$NoBuild,

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$projectPath = (Resolve-Path -LiteralPath $Project).Path
$resultsRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'eidosc-required-test-shards'
$runDirectory = Join-Path $resultsRoot ("{0}-{1}" -f $ShardName, [Guid]::NewGuid().ToString('N'))
$trxName = "$ShardName.trx"
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

$arguments = @(
    'test'
    $projectPath
    '--filter'
    $Filter
    '--logger'
    "trx;LogFileName=$trxName"
    '--results-directory'
    $runDirectory
    '--verbosity'
    'minimal'
    '/p:UseSharedCompilation=false'
    '/nr:false'
)
if ($NoBuild)
{
    $arguments += '--no-build'
}
if ($NoRestore)
{
    $arguments += '--no-restore'
}

& dotnet @arguments
$testExitCode = $LASTEXITCODE

$trxPath = Join-Path $runDirectory $trxName
if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf))
{
    throw "Required test shard '$ShardName' did not produce a TRX result at '$trxPath'."
}

[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$counters = $trx.TestRun.ResultSummary.Counters
if ($null -eq $counters)
{
    throw "Required test shard '$ShardName' produced a TRX file without result counters."
}

$total = [int]$counters.total
$executed = [int]$counters.executed
$passed = [int]$counters.passed
$failed = [int]$counters.failed
Write-Host "Required shard '$ShardName': total=$total executed=$executed passed=$passed failed=$failed"

if ($total -lt $MinimumTests)
{
    throw "Required test shard '$ShardName' selected $total tests; expected at least $MinimumTests. Filter: $Filter"
}
if ($testExitCode -ne 0)
{
    throw "Required test shard '$ShardName' failed with dotnet test exit code $testExitCode."
}
