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

# All Eidos products in this repository derive from one hand-maintained version source.
# Cross-repository equality is enforced by the workspace release contract.
$eidosVersion = Read-ProductVersion "eng/Eidos.Version.props" "EidosVersionPrefix" "EidosVersionSuffix"
Assert-SemVer "Eidos" $eidosVersion

$derivedProps = @(
    "eng/Eidosc.Version.props",
    "eng/Std.Version.props",
    "eng/Eidosup.Version.props",
    "eng/EidosBindgen.Version.props"
)
foreach ($relativePath in $derivedProps)
{
    [string]$text = Get-Content -LiteralPath (Join-Path $RepositoryRoot $relativePath) -Raw
    if ($text.IndexOf('Eidos.Version.props', [StringComparison]::Ordinal) -lt 0)
    {
        throw "$relativePath must import eng/Eidos.Version.props instead of declaring an independent version."
    }
}

$eidoscVersion = $eidosVersion
$stdVersion = $eidosVersion
$eidosupVersion = $eidosVersion
$bindgenVersion = $eidosVersion

$languageSource = Get-Content -Raw (Join-Path $RepositoryRoot "src/Eidosc/ProjectSystem/EidosLanguageVersions.cs")
$languageMatch = [regex]::Match($languageSource, 'Current\s*=\s*"([^"]+)"')
if (-not $languageMatch.Success) { throw "Unable to read Eidos language version constant." }
$languageVersion = $languageMatch.Groups[1].Value

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

$releaseNotes = Join-Path $RepositoryRoot "changelogs/$eidosVersion.md"
if (-not (Test-Path -LiteralPath $releaseNotes)) { throw "Missing Eidos release notes: $releaseNotes" }

Write-Host "Version consistency verified:"
Write-Host "  Eidos language/compiler/Std $eidosVersion"
Write-Host "  Eidosup                    $eidosupVersion"
Write-Host "  Eidos Bindgen              $bindgenVersion"
