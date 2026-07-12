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

$eidoscVersion = Read-ProductVersion "eng/Eidosc.Version.props" "EidoscVersionPrefix" "EidoscVersionSuffix"
$eidosupVersion = Read-ProductVersion "eng/Eidosup.Version.props" "EidosupVersionPrefix" "EidosupVersionSuffix"
$bindgenVersion = Read-ProductVersion "eng/EidosBindgen.Version.props" "EidosBindgenVersionPrefix" "EidosBindgenVersionSuffix"
[xml]$stdProps = Get-Content -Raw (Join-Path $RepositoryRoot "eng/Std.Version.props")
$stdVersion = [string]$stdProps.Project.PropertyGroup.EidosStdVersion

Assert-SemVer "Eidosc" $eidoscVersion
Assert-SemVer "Eidosup" $eidosupVersion
Assert-SemVer "Eidos Bindgen" $bindgenVersion
Assert-SemVer "Std" $stdVersion

$compatibility = Get-Content -Raw (Join-Path $RepositoryRoot "eng/compatibility.json") | ConvertFrom-Json
if ($compatibility.version -ne $eidoscVersion) { throw "compatibility.json Eidosc version mismatch." }
if ($compatibility.stdlib -ne $stdVersion) { throw "compatibility.json Std version mismatch." }

$languageSource = Get-Content -Raw (Join-Path $RepositoryRoot "src/Eidosc/ProjectSystem/EidosLanguageVersions.cs")
$languageMatch = [regex]::Match($languageSource, 'Current\s*=\s*"([^"]+)"')
if (-not $languageMatch.Success) { throw "Unable to read Eidos language version constant." }
$languageVersion = $languageMatch.Groups[1].Value
Assert-SemVer "Eidos language" $languageVersion
if ($compatibility.language.default -ne $languageVersion) { throw "compatibility.json language version mismatch." }

$stdManifest = Get-Content -Raw (Join-Path $RepositoryRoot "src/Eidosc/Stdlib/Precompiled/eidos.toml")
$stdManifestMatch = [regex]::Match($stdManifest, '(?ms)^\[package\].*?^version\s*=\s*"([^"]+)"')
if (-not $stdManifestMatch.Success -or $stdManifestMatch.Groups[1].Value -ne $stdVersion)
{
    throw "Std manifest version does not match eng/Std.Version.props."
}

$releaseNotes = Join-Path $RepositoryRoot "changelogs/$eidoscVersion.md"
if (-not (Test-Path -LiteralPath $releaseNotes)) { throw "Missing Eidosc release notes: $releaseNotes" }
$componentReleaseNotes = @(
    "changelogs/eidosup/$eidosupVersion.md",
    "changelogs/eidos-bindgen/$bindgenVersion.md",
    "changelogs/stdlib/$stdVersion.md"
)
foreach ($relativePath in $componentReleaseNotes)
{
    if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $relativePath)))
    {
        throw "Missing component release notes: $relativePath"
    }
}

Write-Host "Version consistency verified:"
Write-Host "  Eidos language $languageVersion"
Write-Host "  Eidosc         $eidoscVersion"
Write-Host "  Std            $stdVersion"
Write-Host "  Eidosup        $eidosupVersion"
Write-Host "  Eidos Bindgen  $bindgenVersion"
