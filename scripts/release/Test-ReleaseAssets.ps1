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

$assetRoot = (Resolve-Path -LiteralPath $AssetDirectory).Path
$metadataPath = Join-Path $assetRoot "$Product-release.json"
$checksumPath = Join-Path $assetRoot "SHA256SUMS"
if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $checksumPath -PathType Leaf))
{
    throw "Release metadata or SHA256SUMS is missing."
}

$metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
if ($metadata.schemaVersion -ne 1 -or
    $metadata.product -cne $Product -or
    $metadata.version -cne $Version -or
    $metadata.tag -cne "$Product-v$Version" -or
    $metadata.commit -cne $CommitSha.ToLowerInvariant())
{
    throw "Release metadata identity does not match the requested release."
}

$rids = @("linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64")
$expectedNames = if ($Product -eq "eidosc")
{
    @($rids | ForEach-Object { "eidosc-v$Version-$_.zip" }) + @("Eidosc.Cli.$Version.nupkg")
}
else
{
    @($rids | ForEach-Object {
        $extension = if ($_.StartsWith("win-", [StringComparison]::Ordinal)) { ".exe" } else { "" }
        "eidosup-v$Version-$_$extension"
    })
}

$allowedNames = @($expectedNames) + @("SHA256SUMS", "$Product-release.json")
$unexpectedFiles = Get-ChildItem -LiteralPath $assetRoot -File | Where-Object { $_.Name -notin $allowedNames }
$unexpectedDirectories = Get-ChildItem -LiteralPath $assetRoot -Directory
if ($unexpectedFiles -or $unexpectedDirectories)
{
    $unexpectedNames = @($unexpectedFiles.Name) + @($unexpectedDirectories.Name)
    throw "Unexpected release assets: $($unexpectedNames -join ', ')"
}

$metadataAssets = @($metadata.assets)
if ($metadataAssets.Count -ne $expectedNames.Count)
{
    throw "Release metadata contains $($metadataAssets.Count) assets; expected $($expectedNames.Count)."
}

$checksumLines = @(Get-Content -LiteralPath $checksumPath)
if ($checksumLines.Count -ne $expectedNames.Count)
{
    throw "SHA256SUMS contains $($checksumLines.Count) entries; expected $($expectedNames.Count)."
}

$checksums = @{}
foreach ($line in $checksumLines)
{
    if ($line -cnotmatch '^([0-9a-f]{64})  ([^/\\]+)$')
    {
        throw "Invalid SHA256SUMS entry: $line"
    }

    if ($checksums.ContainsKey($Matches[2]))
    {
        throw "Duplicate SHA256SUMS entry: $($Matches[2])"
    }

    $checksums[$Matches[2]] = $Matches[1]
}

$orderedNames = @($expectedNames | Sort-Object)
if (($checksumLines -join "`n") -cne (($orderedNames | ForEach-Object { "$($checksums[$_])  $_" }) -join "`n"))
{
    throw "SHA256SUMS entries are not in deterministic filename order."
}

foreach ($name in $expectedNames)
{
    $path = Join-Path $assetRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Required release asset is missing: $name"
    }

    $file = Get-Item -LiteralPath $path
    $digest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($checksums[$name] -cne $digest)
    {
        throw "SHA-256 mismatch for release asset: $name"
    }

    $record = @($metadataAssets | Where-Object { $_.name -ceq $name })
    if ($record.Count -ne 1 -or $record[0].sha256 -cne $digest -or $record[0].size -ne $file.Length)
    {
        throw "Release metadata does not match asset: $name"
    }
}

if ($Product -eq "eidosc")
{
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($rid in $rids)
    {
        $name = "eidosc-v$Version-$rid.zip"
        $path = Join-Path $assetRoot $name
        $archive = [IO.Compression.ZipFile]::OpenRead($path)
        try
        {
            if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt 100000)
            {
                throw "Archive '$name' has an invalid entry count."
            }

            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $filePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            [long]$totalLength = 0
            foreach ($entry in $archive.Entries)
            {
                $entryName = $entry.FullName
                $segments = $entryName.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
                if ([string]::IsNullOrWhiteSpace($entryName) -or
                    $entryName.Contains('\', [StringComparison]::Ordinal) -or
                    $entryName.Contains(':', [StringComparison]::Ordinal) -or
                    $entryName.StartsWith('/', [StringComparison]::Ordinal) -or
                    $segments -contains '..' -or
                    $segments -contains '.' -or
                    @($segments | Where-Object { $_.ToCharArray() | Where-Object { [char]::IsControl($_) } }).Count -gt 0 -or
                    -not $seen.Add($entryName))
                {
                    throw "Archive '$name' contains an unsafe or duplicate path: $entryName"
                }

                if ($entryName.TrimEnd('/') -ceq '.eidosup-install.json')
                {
                    throw "Archive '$name' contains the reserved installation manifest path."
                }

                $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
                if ($unixType -notin @(0, 0x4000, 0x8000) -or ($entry.ExternalAttributes -band 0x0400) -ne 0)
                {
                    throw "Archive '$name' contains an unsupported special file: $entryName"
                }

                $isDirectory = $entryName.EndsWith('/', [StringComparison]::Ordinal) -or [string]::IsNullOrEmpty($entry.Name)
                if (-not $isDirectory)
                {
                    [void]$filePaths.Add($entryName)
                    if ($entry.Length -lt 0 -or $entry.Length -gt 2GB)
                    {
                        throw "Archive '$name' contains an oversized file: $entryName"
                    }
                    $totalLength += $entry.Length
                    if ($totalLength -gt 4GB -or
                        ($entry.Length -gt 0 -and ($entry.CompressedLength -eq 0 -or $entry.Length / [double]$entry.CompressedLength -gt 1000)))
                    {
                        throw "Archive '$name' exceeds expansion limits."
                    }
                }
            }

            foreach ($entryName in $seen)
            {
                $parent = [IO.Path]::GetDirectoryName($entryName.Replace('/', [IO.Path]::DirectorySeparatorChar))
                while (-not [string]::IsNullOrEmpty($parent))
                {
                    $normalizedParent = $parent.Replace([IO.Path]::DirectorySeparatorChar, '/')
                    if ($filePaths.Contains($normalizedParent))
                    {
                        throw "Archive '$name' nests '$entryName' below file '$normalizedParent'."
                    }
                    $parent = [IO.Path]::GetDirectoryName($parent)
                }
            }

            $executable = if ($rid.StartsWith("win-", [StringComparison]::Ordinal)) { "eidosc.exe" } else { "eidosc" }
            if (@($archive.Entries | Where-Object { $_.FullName -ceq $executable }).Count -ne 1)
            {
                throw "Archive '$name' does not contain exactly one root '$executable' executable."
            }
        }
        finally
        {
            $archive.Dispose()
        }
    }

    $packagePath = Join-Path $assetRoot "Eidosc.Cli.$Version.nupkg"
    $package = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try
    {
        if (@($package.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) }).Count -ne 1)
        {
            throw "The .NET tool package does not contain exactly one NuSpec manifest."
        }
    }
    finally
    {
        $package.Dispose()
    }
}

Write-Host "Verified $Product-v$Version release assets, checksums, metadata, and archive structure."
