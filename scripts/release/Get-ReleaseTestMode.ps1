[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Component,

    [string]$CandidateRef = "HEAD",
    [string]$BaselineRef = "",
    [string]$ConfigurationPath = "eng/release-source-sets.json",
    [switch]$Json
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-Git([string[]]$Arguments)
{
    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        throw "git $($Arguments -join ' ') failed: $($output | Out-String)"
    }
    return @($output)
}

function Resolve-Commit([string]$Reference)
{
    return ([string](Invoke-Git @("rev-parse", "--verify", "$Reference^{commit}") | Select-Object -First 1)).Trim()
}

function Get-SourceRoot([string]$Commit, [string[]]$Paths)
{
    $arguments = @("ls-tree", "-r", "--full-tree", $Commit, "--") + $Paths
    $entries = [Collections.Generic.List[string]]::new()
    foreach ($line in Invoke-Git $arguments)
    {
        $match = [regex]::Match([string]$line, '^[0-9]+\s+blob\s+([0-9a-f]+)\t(.+)$')
        if (-not $match.Success)
        {
            throw "Unexpected git ls-tree entry: $line"
        }
        $entries.Add("$($match.Groups[2].Value)`0$($match.Groups[1].Value)")
    }

    $payload = [string]::Join("`n", @($entries | Sort-Object -CaseSensitive))
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($bytes) }
    finally { $algorithm.Dispose() }
    return [BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()
}

$configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
if ($configuration.schema -ne 1) { throw "Unsupported release source-set schema '$($configuration.schema)'." }
$componentProperty = $configuration.components.PSObject.Properties[$Component]
$componentConfiguration = if ($null -eq $componentProperty) { $null } else { $componentProperty.Value }
if ($null -eq $componentConfiguration) { throw "Unknown release component '$Component'." }

$candidateCommit = Resolve-Commit $CandidateRef
if ([string]::IsNullOrWhiteSpace($BaselineRef))
{
    $pattern = "$($componentConfiguration.tagPrefix)*"
    $BaselineRef = [string](Invoke-Git @("tag", "--merged", $candidateCommit, "--list", $pattern, "--sort=-version:refname") | Select-Object -First 1)
}

$mode = "full"
$reason = "baseline-not-found"
$baselineCommit = $null
$baselineRoot = $null
$candidateRoot = Get-SourceRoot $candidateCommit @($componentConfiguration.sourcePaths)
if (-not [string]::IsNullOrWhiteSpace($BaselineRef))
{
    try
    {
        $baselineCommit = Resolve-Commit $BaselineRef
        $baselineRoot = Get-SourceRoot $baselineCommit @($componentConfiguration.sourcePaths)
        if ($baselineRoot -ceq $candidateRoot)
        {
            $mode = "fast"
            $reason = "artifact-source-set-unchanged"
        }
        else
        {
            $reason = "artifact-source-set-changed"
        }
    }
    catch
    {
        $reason = "baseline-unverifiable"
    }
}

$result = [ordered]@{
    schema = 1
    component = $Component
    mode = $mode
    reason = $reason
    baselineRef = if ([string]::IsNullOrWhiteSpace($BaselineRef)) { $null } else { $BaselineRef }
    baselineCommit = $baselineCommit
    candidateCommit = $candidateCommit
    sourceSet = @($componentConfiguration.sourcePaths)
    baselineRoot = $baselineRoot
    candidateRoot = $candidateRoot
}

if ($Json)
{
    $result | ConvertTo-Json -Depth 5
}
else
{
    [pscustomobject]$result
}
