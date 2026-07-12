[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $PSCommandPath
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("eidos-release-test-" + [Guid]::NewGuid().ToString("N"))
$version = "0.4.0-alpha.2"
$commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
$rids = @("linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64")

try
{
    $eidoscRoot = Join-Path $temporaryRoot "eidosc"
    $eidosupRoot = Join-Path $temporaryRoot "eidosup"
    New-Item -ItemType Directory -Path $eidoscRoot, $eidosupRoot | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    foreach ($rid in $rids)
    {
        $archivePath = Join-Path $eidoscRoot "eidosc-v$version-$rid.zip"
        $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
        try
        {
            $binaryName = if ($rid.StartsWith("win-", [StringComparison]::Ordinal)) { "eidosc.exe" } else { "eidosc" }
            $entry = $archive.CreateEntry($binaryName, [IO.Compression.CompressionLevel]::NoCompression)
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write("fixture-$rid") } finally { $writer.Dispose() }
        }
        finally
        {
            $archive.Dispose()
        }

        $extension = if ($rid.StartsWith("win-", [StringComparison]::Ordinal)) { ".exe" } else { "" }
        [IO.File]::WriteAllText(
            (Join-Path $eidosupRoot "eidosup-v$version-$rid$extension"),
            "fixture-$rid",
            [Text.UTF8Encoding]::new($false))
    }

    $packagePath = Join-Path $eidoscRoot "Eidosc.Cli.$version.nupkg"
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

    & (Join-Path $scriptRoot "New-ReleaseArtifacts.ps1") -Product eidosc -Version $version -CommitSha $commit -AssetDirectory $eidoscRoot
    & (Join-Path $scriptRoot "Test-ReleaseAssets.ps1") -Product eidosc -Version $version -CommitSha $commit -AssetDirectory $eidoscRoot
    & (Join-Path $scriptRoot "New-ReleaseArtifacts.ps1") -Product eidosup -Version $version -CommitSha $commit -AssetDirectory $eidosupRoot
    & (Join-Path $scriptRoot "Test-ReleaseAssets.ps1") -Product eidosup -Version $version -CommitSha $commit -AssetDirectory $eidosupRoot

    Add-Content -LiteralPath (Join-Path $eidosupRoot "eidosup-v$version-linux-x64") -Value "tampered"
    $rejectedTamper = $false
    try
    {
        & (Join-Path $scriptRoot "Test-ReleaseAssets.ps1") -Product eidosup -Version $version -CommitSha $commit -AssetDirectory $eidosupRoot
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
}
