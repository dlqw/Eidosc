[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("eidosc", "eidosup")]
    [string]$Product,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9a-fA-F]{40}$")]
    [string]$CommitSha,

    [Parameter(Mandatory = $true)]
    [string]$AssetDirectory,

    [Parameter(Mandatory = $true)]
    [string]$NotesFile,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    [string]$Repository
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$tag = "$Product-v$Version"
$assetRoot = (Resolve-Path -LiteralPath $AssetDirectory).Path
$notes = (Resolve-Path -LiteralPath $NotesFile).Path
$testAssets = Join-Path $PSScriptRoot "Test-ReleaseAssets.ps1"
$recoveryRoot = $null

function Get-ReleaseByTag
{
    $json = (& gh api "repos/$Repository/releases?per_page=100" | Out-String)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Failed to list GitHub releases for $Repository."
    }

    $matches = @(@($json | ConvertFrom-Json) | Where-Object { $_.tag_name -ceq $tag })
    if ($matches.Count -gt 1)
    {
        throw "Multiple GitHub releases use tag '$tag'."
    }

    if ($matches.Count -eq 1) { return $matches[0] }
    return $null
}

try
{
    & $testAssets `
        -Product $Product `
        -Version $Version `
        -CommitSha $CommitSha `
        -AssetDirectory $assetRoot

    $release = Get-ReleaseByTag
    $verificationRoot = $assetRoot
    if ($null -eq $release)
    {
        $assets = @(Get-ChildItem -LiteralPath $assetRoot -File | Sort-Object Name | ForEach-Object FullName)
        $arguments = @(
            "release", "create", $tag
        ) + $assets + @(
            "--repo", $Repository,
            "--target", $CommitSha,
            "--title", $tag,
            "--notes-file", $notes,
            "--draft"
        )
        & gh @arguments
        if ($LASTEXITCODE -ne 0)
        {
            throw "GitHub draft release creation failed."
        }

        $release = Get-ReleaseByTag
    }
    else
    {
        if (-not $release.draft)
        {
            throw "Release '$tag' already exists and is not a draft."
        }
        if ([string]$release.target_commitish -cne $CommitSha)
        {
            throw "Existing draft '$tag' targets '$($release.target_commitish)', not '$CommitSha'."
        }

        $recoveryRoot = Join-Path ([IO.Path]::GetTempPath()) "$Product-$Version-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $recoveryRoot | Out-Null
        & gh release download $tag --repo $Repository --dir $recoveryRoot
        if ($LASTEXITCODE -ne 0)
        {
            throw "Existing draft release assets could not be downloaded."
        }

        & $testAssets `
            -Product $Product `
            -Version $Version `
            -CommitSha $CommitSha `
            -AssetDirectory $recoveryRoot
        $verificationRoot = $recoveryRoot
    }

    if ($null -eq $release -or -not $release.draft)
    {
        throw "Draft release verification failed."
    }
    if ([string]$release.target_commitish -cne $CommitSha)
    {
        throw "Draft release target mismatch."
    }

    $verifiedAssets = @(Get-ChildItem -LiteralPath $verificationRoot -File | Sort-Object Name)
    if (@($release.assets).Count -ne $verifiedAssets.Count)
    {
        throw "Draft release asset count mismatch."
    }
    foreach ($asset in $verifiedAssets)
    {
        $remote = @($release.assets | Where-Object { $_.name -ceq $asset.Name })
        if ($remote.Count -ne 1 -or $remote[0].size -ne $asset.Length)
        {
            throw "Draft release asset mismatch: $($asset.Name)"
        }
    }

    $isPrerelease = $Version.Contains('-').ToString().ToLowerInvariant()
    & gh release edit $tag --repo $Repository --draft=false "--prerelease=$isPrerelease"
    if ($LASTEXITCODE -ne 0)
    {
        throw "GitHub release publication failed."
    }

    $published = Get-ReleaseByTag
    if ($null -eq $published -or $published.draft -or $published.prerelease -ne $Version.Contains('-'))
    {
        throw "Published release verification failed."
    }
}
finally
{
    if ($null -ne $recoveryRoot -and (Test-Path -LiteralPath $recoveryRoot))
    {
        Remove-Item -LiteralPath $recoveryRoot -Recurse -Force
    }
}
