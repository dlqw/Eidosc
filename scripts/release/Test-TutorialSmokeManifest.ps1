[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ManifestPath,

    [Parameter(Mandatory)]
    [string]$CompatibilityPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$manifest = Get-Content -Raw -LiteralPath (Resolve-Path -LiteralPath $ManifestPath)
$compatibility = Get-Content -Raw -LiteralPath (Resolve-Path -LiteralPath $CompatibilityPath) | ConvertFrom-Json
$expectedVersion = [string]$compatibility.language.default
if ([string]::IsNullOrWhiteSpace($expectedVersion))
{
    throw "Compiler compatibility metadata does not declare language.default."
}

$languageSection = [regex]::Match(
    $manifest,
    '(?ms)^\[language\]\s*\r?\n(?<body>.*?)(?=^\[|\z)')
if (-not $languageSection.Success)
{
    throw "Tutorial smoke manifest '$ManifestPath' does not contain a [language] section."
}

$versionMatch = [regex]::Match(
    $languageSection.Groups['body'].Value,
    '(?m)^\s*version\s*=\s*"(?<version>[^"]+)"\s*(?:#.*)?$')
if (-not $versionMatch.Success)
{
    throw "Tutorial smoke manifest '$ManifestPath' does not declare language.version."
}

$actualVersion = $versionMatch.Groups['version'].Value
if ($actualVersion -cne $expectedVersion)
{
    throw "Tutorial smoke manifest language version '$actualVersion' does not match compiler default '$expectedVersion'."
}

Write-Host "Tutorial smoke manifest language version verified: $actualVersion"
