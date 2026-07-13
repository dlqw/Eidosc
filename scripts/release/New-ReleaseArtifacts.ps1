[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("eidosc", "eidosup")]
    [string]$Product,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$CommitSha,

    [Parameter(Mandatory)]
    [string]$AssetDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$semVerPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
if ($Version -notmatch $semVerPattern)
{
    throw "Version '$Version' is not a valid SemVer value."
}

$versionWithoutBuild = $Version.Split('+', 2)[0]
$prereleaseSeparator = $versionWithoutBuild.IndexOf('-')
if ($prereleaseSeparator -ge 0)
{
    foreach ($identifier in $versionWithoutBuild.Substring($prereleaseSeparator + 1).Split('.'))
    {
        if ($identifier -match '^[0-9]+$' -and $identifier.Length -gt 1 -and $identifier[0] -eq '0')
        {
            throw "Version '$Version' contains a numeric prerelease identifier with a leading zero."
        }
    }
}

if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$')
{
    throw "CommitSha must contain exactly 40 hexadecimal characters."
}

$assetRoot = (Resolve-Path -LiteralPath $AssetDirectory).Path
$rids = @("linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64")
$expectedNames = if ($Product -eq "eidosc")
{
    @($rids | ForEach-Object { "eidosc-v$Version-$_.zip" }) +
    @($rids | ForEach-Object { "eidos-toolchain-v$Version-$_.json" }) +
    @("Eidosc.Cli.$Version.nupkg")
}
else
{
    @($rids | ForEach-Object {
        $extension = if ($_.StartsWith("win-", [StringComparison]::Ordinal)) { ".exe" } else { "" }
        "eidosup-v$Version-$_$extension"
    })
}

$payload = foreach ($name in $expectedNames)
{
    $path = Join-Path $assetRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Required release asset is missing: $name"
    }

    Get-Item -LiteralPath $path
}

$reservedNames = @("SHA256SUMS", "$Product-release.json")
$unexpected = Get-ChildItem -LiteralPath $assetRoot -File |
    Where-Object { $_.Name -notin $expectedNames -and $_.Name -notin $reservedNames }
$unexpectedDirectories = Get-ChildItem -LiteralPath $assetRoot -Directory
if ($unexpected -or $unexpectedDirectories)
{
    $unexpectedNames = @($unexpected.Name) + @($unexpectedDirectories.Name)
    throw "Unexpected release assets: $($unexpectedNames -join ', ')"
}

$assetRecords = @($payload | Sort-Object Name | ForEach-Object {
    $file = $_
    $rid = $rids | Where-Object { $file.Name.Contains("-$_", [StringComparison]::Ordinal) } | Select-Object -First 1
    $kind = if ($file.Extension -eq ".nupkg") { "dotnet-tool" } elseif ($file.Name.StartsWith("eidos-toolchain-v", [StringComparison]::Ordinal)) { "toolchain-manifest" } elseif ($Product -eq "eidosc") { "toolchain-bundle" } else { "bootstrap-binary" }
    [ordered]@{
        name = $file.Name
        kind = $kind
        rid = if ($rid) { $rid } else { $null }
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})

$checksumLines = @($assetRecords | ForEach-Object { "$($_.sha256)  $($_.name)" })
$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText(
    (Join-Path $assetRoot "SHA256SUMS"),
    (($checksumLines -join "`n") + "`n"),
    $utf8NoBom)

$metadata = [ordered]@{
    schemaVersion = 1
    product = $Product
    version = $Version
    tag = "$Product-v$Version"
    commit = $CommitSha.ToLowerInvariant()
    assets = $assetRecords
}
$json = ($metadata | ConvertTo-Json -Depth 6).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $assetRoot "$Product-release.json"), $json, $utf8NoBom)

Write-Host "Generated SHA256SUMS and $Product-release.json for $($assetRecords.Count) release assets."
