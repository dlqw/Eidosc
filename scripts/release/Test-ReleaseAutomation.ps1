[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot "../.."))
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("eidos-release-test-" + [Guid]::NewGuid().ToString("N"))
[xml]$eidoscVersionProps = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "eng/Eidosc.Version.props")
$eidoscVersionPrefix = [string]$eidoscVersionProps.Project.PropertyGroup.EidoscVersionPrefix
$eidoscVersionSuffix = [string]$eidoscVersionProps.Project.PropertyGroup.EidoscVersionSuffix
$eidoscVersion = if ($eidoscVersionSuffix) { "$eidoscVersionPrefix-$eidoscVersionSuffix" } else { $eidoscVersionPrefix }
[xml]$eidosupVersionProps = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "eng/Eidosup.Version.props")
$eidosupVersionPrefix = [string]$eidosupVersionProps.Project.PropertyGroup.EidosupVersionPrefix
$eidosupVersionSuffix = [string]$eidosupVersionProps.Project.PropertyGroup.EidosupVersionSuffix
$eidosupVersion = if ($eidosupVersionSuffix) { "$eidosupVersionPrefix-$eidosupVersionSuffix" } else { $eidosupVersionPrefix }
$commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$')
{
    throw "Release automation self-test requires a Git commit."
}
$rids = @("linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64")
$nativeRunnerLabels = @("windows-latest", "windows-11-arm", "ubuntu-latest", "ubuntu-24.04-arm", "macos-15-intel", "macos-15")

foreach ($workflowPath in @(".github/workflows/release-eidosc.yml", ".github/workflows/release-eidosup.yml"))
{
    $workflow = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot $workflowPath)
    if (-not $workflow.Contains("published-install:", [StringComparison]::Ordinal) -or
        -not $workflow.Contains("29_precompiled_stdlib.eidos", [StringComparison]::Ordinal) -or
        @($nativeRunnerLabels | Where-Object { -not $workflow.Contains($_, [StringComparison]::Ordinal) }).Count -ne 0)
    {
        throw "Release workflow '$workflowPath' does not contain the six-RID candidate and post-publication install gates."
    }
}

foreach ($scriptPath in @(
    "scripts/release/Invoke-EidosupCleanInstall.ps1",
    "scripts/release/Invoke-EidosupPublishedInstall.ps1"))
{
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repositoryRoot $scriptPath),
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0)
    {
        throw "Release verification script '$scriptPath' has PowerShell parse errors: $($parseErrors -join '; ')"
    }
}

function Add-ZipText(
    [IO.Compression.ZipArchive]$Archive,
    [string]$Name,
    [string]$Content)
{
    $entry = $Archive.CreateEntry($Name, [IO.Compression.CompressionLevel]::NoCompression)
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($Content) } finally { $writer.Dispose() }
}

Push-Location -LiteralPath $repositoryRoot
try
{
    $eidoscRoot = Join-Path $temporaryRoot "eidosc"
    $eidosupRoot = Join-Path $temporaryRoot "eidosup"
    New-Item -ItemType Directory -Path $eidoscRoot, $eidosupRoot | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    foreach ($rid in $rids)
    {
        $archivePath = Join-Path $eidoscRoot "eidosc-v$eidoscVersion-$rid.zip"
        $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
        try
        {
            $binaryName = if ($rid.StartsWith("win-", [StringComparison]::Ordinal)) { "eidosc.exe" } else { "eidosc" }
            $bindgenName = if ($rid.StartsWith("win-", [StringComparison]::Ordinal)) {
                "tools/eidos-bindgen/eidos-bindgen.exe"
            } else {
                "tools/eidos-bindgen/eidos-bindgen"
            }
            Add-ZipText $archive $binaryName "fixture-$rid"
            Add-ZipText $archive "compatibility.json" (Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "eng/compatibility.json"))
            Add-ZipText $archive "stdlib/eidos.toml" "[package]`nname = `"EidosStd`"`nversion = `"0.1.0-alpha.1`"`n"
            Add-ZipText $archive "stdlib/Std/Core.eidos" "module Std::Core"
            Add-ZipText $archive "runtime/eidos_runtime.h" "host runtime"
            Add-ZipText $archive "docs/index.md" "# docs"
            Add-ZipText $archive "docs/index.json" "{`"schema`":1,`"eidoscVersion`":`"$eidoscVersion`",`"topics`":{`"index`":`"index.md`"}}"
            Add-ZipText $archive $bindgenName "bindgen"
            foreach ($targetRid in $rids | Where-Object { $_ -cne $rid })
            {
                Add-ZipText $archive "targets/$targetRid/runtime/eidos_runtime.h" "runtime-$targetRid"
            }
        }
        finally
        {
            $archive.Dispose()
        }

        $extension = if ($rid.StartsWith("win-", [StringComparison]::Ordinal)) { ".exe" } else { "" }
        [IO.File]::WriteAllText(
            (Join-Path $eidosupRoot "eidosup-v$eidosupVersion-$rid$extension"),
            "fixture-$rid",
            [Text.UTF8Encoding]::new($false))
    }

    $packagePath = Join-Path $eidoscRoot "Eidosc.Cli.$eidoscVersion.nupkg"
    $package = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Create)
    try
    {
        $entry = $package.CreateEntry("Eidosc.Cli.nuspec", [IO.Compression.CompressionLevel]::NoCompression)
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write("<package />") } finally { $writer.Dispose() }
    }
    finally
    {
        $package.Dispose()
    }

    & (Join-Path $scriptRoot "New-ToolchainManifests.ps1") -Version $eidoscVersion -CommitSha $commit -AssetDirectory $eidoscRoot
    & (Join-Path $scriptRoot "New-ReleaseArtifacts.ps1") -Product eidosc -Version $eidoscVersion -CommitSha $commit -AssetDirectory $eidoscRoot
    & (Join-Path $scriptRoot "Test-ReleaseAssets.ps1") -Product eidosc -Version $eidoscVersion -CommitSha $commit -AssetDirectory $eidoscRoot
    & (Join-Path $scriptRoot "New-ReleaseArtifacts.ps1") -Product eidosup -Version $eidosupVersion -CommitSha $commit -AssetDirectory $eidosupRoot
    & (Join-Path $scriptRoot "Test-ReleaseAssets.ps1") -Product eidosup -Version $eidosupVersion -CommitSha $commit -AssetDirectory $eidosupRoot

    Add-Content -LiteralPath (Join-Path $eidosupRoot "eidosup-v$eidosupVersion-linux-x64") -Value "tampered"
    $rejectedTamper = $false
    try
    {
        & (Join-Path $scriptRoot "Test-ReleaseAssets.ps1") -Product eidosup -Version $eidosupVersion -CommitSha $commit -AssetDirectory $eidosupRoot
    }
    catch
    {
        $rejectedTamper = $_.Exception.Message.Contains("SHA-256 mismatch", [StringComparison]::Ordinal)
    }

    if (-not $rejectedTamper)
    {
        throw "Release verification did not reject a tampered asset."
    }

    Write-Host "Release automation self-test passed."
}
finally
{
    if (Test-Path -LiteralPath $temporaryRoot)
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }

    Pop-Location
}
