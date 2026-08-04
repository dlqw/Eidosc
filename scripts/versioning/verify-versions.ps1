param(
    [string]$RepositoryRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepositoryRoot))
{
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
}

$semVerPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'

function Read-ProductVersion([string]$RelativePath, [string]$PrefixName, [string]$SuffixName)
{
    [xml]$document = Get-Content -Raw (Join-Path $RepositoryRoot $RelativePath)
    $group = $document.Project.PropertyGroup
    $prefix = [string]$group.$PrefixName
    $suffix = [string]$group.$SuffixName
    if ([string]::IsNullOrWhiteSpace($suffix)) { return $prefix }
    return "$prefix-$suffix"
}

function Assert-SemVer([string]$Name, [string]$Version)
{
    if ($Version -notmatch $semVerPattern)
    {
        throw "$Name version '$Version' is not strict SemVer 2.0.0."
    }
}

# Eidos 核心（语言 + Eidosc + Std）是单一版本域：本仓内三源必须同值。
# eidos-language.toml（语言仓）与 Eidosc/Std 权威源的跨仓一致性由根工作区
# scripts/verify-ecosystem-lock.ps1 校验。
$eidoscVersion = Read-ProductVersion "eng/Eidosc.Version.props" "EidoscVersionPrefix" "EidoscVersionSuffix"
[xml]$stdProps = Get-Content -Raw (Join-Path $RepositoryRoot "eng/Std.Version.props")
$stdVersion = [string]$stdProps.Project.PropertyGroup.EidosStdVersion

$languageSource = Get-Content -Raw (Join-Path $RepositoryRoot "src/Eidosc/ProjectSystem/EidosLanguageVersions.cs")
$languageMatch = [regex]::Match($languageSource, 'Current\s*=\s*"([^"]+)"')
if (-not $languageMatch.Success) { throw "Unable to read Eidos language version constant." }
$languageVersion = $languageMatch.Groups[1].Value

Assert-SemVer "Eidos core" $eidoscVersion
if ($stdVersion -ne $eidoscVersion) { throw "Std version '$stdVersion' does not match Eidos core version '$eidoscVersion'." }
if ($languageVersion -ne $eidoscVersion) { throw "Eidos language version '$languageVersion' does not match Eidos core version '$eidoscVersion'." }

$compatibility = Get-Content -Raw (Join-Path $RepositoryRoot "eng/compatibility.json") | ConvertFrom-Json
if ($compatibility.version -ne $eidoscVersion) { throw "compatibility.json Eidosc version mismatch." }
if ($compatibility.stdlib -ne $stdVersion) { throw "compatibility.json Std version mismatch." }
if ($compatibility.language.default -ne $languageVersion) { throw "compatibility.json language version mismatch." }

$stdManifest = Get-Content -Raw (Join-Path $RepositoryRoot "src/Eidosc/Stdlib/Precompiled/eidos.toml")
$stdManifestMatch = [regex]::Match($stdManifest, '(?ms)^\[package\].*?^version\s*=\s*"([^"]+)"')
if (-not $stdManifestMatch.Success -or $stdManifestMatch.Groups[1].Value -ne $stdVersion)
{
    throw "Std manifest version does not match eng/Std.Version.props."
}

# 独立版本域：Eidosup、Bindgen
$eidosupVersion = Read-ProductVersion "eng/Eidosup.Version.props" "EidosupVersionPrefix" "EidosupVersionSuffix"
$bindgenVersion = Read-ProductVersion "eng/EidosBindgen.Version.props" "EidosBindgenVersionPrefix" "EidosBindgenVersionSuffix"
Assert-SemVer "Eidosup" $eidosupVersion
Assert-SemVer "Eidos Bindgen" $bindgenVersion

$releaseNotes = Join-Path $RepositoryRoot "changelogs/$eidoscVersion.md"
if (-not (Test-Path -LiteralPath $releaseNotes)) { throw "Missing Eidosc release notes: $releaseNotes" }
$componentReleaseNotes = @(
    "changelogs/eidosup/$eidosupVersion.md",
    "changelogs/eidos-bindgen/$bindgenVersion.md"
)
foreach ($relativePath in $componentReleaseNotes)
{
    if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $relativePath)))
    {
        throw "Missing component release notes: $relativePath"
    }
}

Write-Host "Version consistency verified:"
Write-Host "  Eidos core (language + Eidosc + Std) $eidoscVersion"
Write-Host "  Eidosup        $eidosupVersion"
Write-Host "  Eidos Bindgen  $bindgenVersion"
