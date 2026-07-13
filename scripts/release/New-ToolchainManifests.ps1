[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$CommitSha,

    [Parameter(Mandatory)]
    [string]$AssetDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')
{
    throw "Version '$Version' is not a valid SemVer value."
}
if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$')
{
    throw "CommitSha must contain exactly 40 hexadecimal characters."
}

$assetRoot = (Resolve-Path -LiteralPath $AssetDirectory).Path
$rids = @("linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64")
$compatibility = Get-Content -Raw -LiteralPath "eng/compatibility.json" | ConvertFrom-Json
if ($compatibility.version -cne $Version)
{
    throw "eng/compatibility.json version '$($compatibility.version)' does not match '$Version'."
}

[xml]$stdProps = Get-Content -Raw -LiteralPath "eng/Std.Version.props"
$stdVersion = [string]$stdProps.Project.PropertyGroup.EidosStdVersion
[xml]$bindgenProps = Get-Content -Raw -LiteralPath "eng/EidosBindgen.Version.props"
$bindgenPrefix = [string]$bindgenProps.Project.PropertyGroup.EidosBindgenVersionPrefix
$bindgenSuffix = [string]$bindgenProps.Project.PropertyGroup.EidosBindgenVersionSuffix
$bindgenVersion = if ($bindgenSuffix) { "$bindgenPrefix-$bindgenSuffix" } else { $bindgenPrefix }
$runtimeVersion = $stdVersion
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$publishedAtText = (& git show -s --format=%cI $CommitSha).Trim()
$publishedAt = [DateTimeOffset]::MinValue
if ($LASTEXITCODE -ne 0 -or -not [DateTimeOffset]::TryParse(
        $publishedAtText,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$publishedAt))
{
    throw "Unable to resolve a deterministic commit timestamp for '$CommitSha'."
}
$publishedAtUtc = $publishedAt.ToUniversalTime().ToString("O")

$targetMetadata = @{
    "linux-x64" = @{ triple = "x86_64-pc-linux-gnu"; linker = "clang" }
    "win-x64" = @{ triple = "x86_64-pc-windows-msvc"; linker = "clang" }
    "osx-x64" = @{ triple = "x86_64-apple-macosx10.15"; linker = "clang" }
    "linux-arm64" = @{ triple = "aarch64-unknown-linux-gnu"; linker = "clang" }
    "win-arm64" = @{ triple = "aarch64-pc-windows-msvc"; linker = "clang" }
    "osx-arm64" = @{ triple = "arm64-apple-macosx11"; linker = "clang" }
}

function Get-ComponentId([string]$relativePath, [string]$hostRid)
{
    if (-not $relativePath.Contains('/', [StringComparison]::Ordinal)) { return "eidosc-core" }
    if ($relativePath.StartsWith("stdlib/", [StringComparison]::Ordinal)) { return "eidos-std" }
    if ($relativePath.StartsWith("runtime/", [StringComparison]::Ordinal)) { return "eidos-runtime@$hostRid" }
    if ($relativePath.StartsWith("docs/", [StringComparison]::Ordinal)) { return "eidos-docs" }
    if ($relativePath.StartsWith("tools/eidos-bindgen/", [StringComparison]::Ordinal)) { return "eidos-bindgen" }
    if ($relativePath -match '^targets/([^/]+)/runtime/')
    {
        if ($Matches[1] -notin $rids -or $Matches[1] -ceq $hostRid)
        {
            throw "Invalid target runtime path '$relativePath' in host bundle '$hostRid'."
        }
        return "eidos-runtime@$($Matches[1])"
    }
    throw "No component owns bundle path '$relativePath'."
}

foreach ($hostRid in $rids)
{
    $bundleName = "eidosc-v$Version-$hostRid.zip"
    $bundlePath = Join-Path $assetRoot $bundleName
    if (-not (Test-Path -LiteralPath $bundlePath -PathType Leaf))
    {
        throw "Required host bundle is missing: $bundleName"
    }

    $temporary = Join-Path ([IO.Path]::GetTempPath()) "eidos-toolchain-manifest-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temporary | Out-Null
    try
    {
        Expand-Archive -LiteralPath $bundlePath -DestinationPath $temporary
        $owned = @{}
        $hostExecutable = if ($hostRid.StartsWith("win-", [StringComparison]::Ordinal)) { "eidosc.exe" } else { "eidosc" }
        $bindgenExecutable = if ($hostRid.StartsWith("win-", [StringComparison]::Ordinal)) {
            "tools/eidos-bindgen/eidos-bindgen.exe"
        } else {
            "tools/eidos-bindgen/eidos-bindgen"
        }
        foreach ($file in Get-ChildItem -LiteralPath $temporary -Recurse -File | Sort-Object FullName)
        {
            $relativePath = [IO.Path]::GetRelativePath($temporary, $file.FullName).Replace('\', '/')
            $componentId = Get-ComponentId $relativePath $hostRid
            if (-not $owned.ContainsKey($componentId)) { $owned[$componentId] = [Collections.Generic.List[object]]::new() }
            $owned[$componentId].Add([ordered]@{
                path = $relativePath
                size = $file.Length
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                executable = $relativePath -ceq $hostExecutable -or $relativePath -ceq $bindgenExecutable
            })
        }

        $requiredIds = @("eidosc-core", "eidos-std", "eidos-docs", "eidos-bindgen") + @($rids | ForEach-Object { "eidos-runtime@$_" })
        $missing = @($requiredIds | Where-Object { -not $owned.ContainsKey($_) -or $owned[$_].Count -eq 0 })
        if ($missing.Count -ne 0)
        {
            throw "Bundle '$bundleName' is missing component files for: $($missing -join ', ')."
        }

        $bundle = Get-Item -LiteralPath $bundlePath
        $artifact = [ordered]@{
            name = $bundleName
            size = $bundle.Length
            sha256 = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $components = [Collections.Generic.List[object]]::new()
        $components.Add([ordered]@{
            id = "eidosc-core"; name = "eidosc-core"; version = $Version; required = $true; target = $null
            dependencies = @(); conflicts = @(); artifact = $artifact; files = @($owned["eidosc-core"])
        })
        $components.Add([ordered]@{
            id = "eidos-std"; name = "eidos-std"; version = $stdVersion; required = $true; target = $null
            dependencies = @("eidosc-core"); conflicts = @(); artifact = $artifact; files = @($owned["eidos-std"])
        })
        foreach ($targetRid in $rids)
        {
            $componentId = "eidos-runtime@$targetRid"
            $components.Add([ordered]@{
                id = $componentId; name = "eidos-runtime"; version = $runtimeVersion; required = $false; target = $targetRid
                dependencies = @("eidosc-core"); conflicts = @(); artifact = $artifact; files = @($owned[$componentId])
            })
        }
        $components.Add([ordered]@{
            id = "eidos-docs"; name = "eidos-docs"; version = $Version; required = $false; target = $null
            dependencies = @("eidosc-core"); conflicts = @(); artifact = $artifact; files = @($owned["eidos-docs"])
        })
        $components.Add([ordered]@{
            id = "eidos-bindgen"; name = "eidos-bindgen"; version = $bindgenVersion; required = $false; target = $null
            dependencies = @("eidosc-core"); conflicts = @(); artifact = $artifact; files = @($owned["eidos-bindgen"])
        })

        $targets = @($rids | ForEach-Object {
            $targetRid = $_
            [ordered]@{
                name = $targetRid
                triple = $targetMetadata[$targetRid].triple
                component = "eidos-runtime@$targetRid"
                support = if ($targetRid -ceq $hostRid) { "host" } else { "crossCompile" }
                linker = [ordered]@{
                    command = $targetMetadata[$targetRid].linker
                    externalSdkRequired = $targetRid -cne $hostRid
                }
            }
        })
        $manifest = [ordered]@{
            schema = 1
            toolchain = "eidosc-$Version-$hostRid"
            channel = if ($Version.Contains('-')) { "preview" } else { "stable" }
            host = $hostRid
            eidosc = [ordered]@{ version = $Version; commit = $CommitSha.ToLowerInvariant() }
            language = [ordered]@{ version = [string]$compatibility.language.default }
            profiles = @(
                [ordered]@{ name = "minimal"; components = @("eidosc-core", "eidos-std") },
                [ordered]@{ name = "default"; components = @("eidosc-core", "eidos-std", "eidos-runtime@$hostRid") },
                [ordered]@{ name = "complete"; components = @("eidosc-core", "eidos-std", "eidos-runtime@$hostRid", "eidos-docs", "eidos-bindgen") }
            )
            components = @($components)
            targets = $targets
            requirements = [ordered]@{ llvm = [ordered]@{ supported = ">=18.0.0 <23.0.0" } }
            publishedAt = $publishedAtUtc
        }
        $output = Join-Path $assetRoot "eidos-toolchain-v$Version-$hostRid.json"
        [IO.File]::WriteAllText($output, (($manifest | ConvertTo-Json -Depth 12).Replace("`r`n", "`n") + "`n"), $utf8NoBom)
    }
    finally
    {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    }
}

Write-Host "Generated component manifests for $($rids.Count) Eidosc host bundles."
