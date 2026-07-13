[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$EidosupPath,

    [Parameter(Mandatory)]
    [string]$TutorialExample,

    [Parameter(Mandatory)]
    [string]$ExpectedRid,

    [string]$Repository = "dlqw/Eidosc"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$bootstrap = (Resolve-Path -LiteralPath $EidosupPath).Path
$example = (Resolve-Path -LiteralPath $TutorialExample).Path
$platform = if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows))
{
    "win"
}
elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Linux))
{
    "linux"
}
elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX))
{
    "osx"
}
else
{
    throw "Unsupported published-install verification platform."
}

$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$rid = "$platform-$architecture"
if ($rid -cne $ExpectedRid)
{
    throw "Published-install runner RID '$rid' does not match expected RID '$ExpectedRid'."
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("eidosup-published-install-" + [Guid]::NewGuid().ToString("N"))
$installRoot = Join-Path $temporaryRoot "install"
$downloadRoot = Join-Path $temporaryRoot "downloads"
$previousGitHubToken = [Environment]::GetEnvironmentVariable("GITHUB_TOKEN")
$previousGhToken = [Environment]::GetEnvironmentVariable("GH_TOKEN")
$previousTestServer = [Environment]::GetEnvironmentVariable("EIDOSUP_TEST_RELEASE_SERVER")

try
{
    Remove-Item Env:GITHUB_TOKEN, Env:GH_TOKEN, Env:EIDOSUP_TEST_RELEASE_SERVER -ErrorAction SilentlyContinue
    $installed = $false
    for ($attempt = 1; $attempt -le 6; $attempt++)
    {
        & $bootstrap setup `
            --repo $Repository `
            --version $Version `
            --install-root $installRoot `
            --download-root $downloadRoot `
            --skip-clang `
            --skip-env
        if ($LASTEXITCODE -eq 0)
        {
            $installed = $true
            break
        }

        if ($attempt -lt 6)
        {
            Start-Sleep -Seconds (10 * $attempt)
        }
    }

    if (-not $installed)
    {
        throw "Anonymous Eidosup setup did not converge for published eidosc-v$Version."
    }

    $managerName = if ($platform -ceq "win") { "eidosup.exe" } else { "eidosup" }
    $compilerName = if ($platform -ceq "win") { "eidosc.exe" } else { "eidosc" }
    $manager = Join-Path $installRoot "bin/$managerName"
    $shim = Join-Path $installRoot "bin/$compilerName"
    if (-not (Test-Path -LiteralPath $manager -PathType Leaf) -or
        -not (Test-Path -LiteralPath $shim -PathType Leaf))
    {
        throw "Published installation did not create stable Eidosup and Eidosc commands."
    }

    $statePath = Join-Path $installRoot "state/toolchains.json"
    $state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
    $toolchain = @($state.toolchains | Where-Object {
        [string]$_.version -ceq $Version -and [string]$_.rid -ceq $rid
    })
    if ($state.schema -ne 3 -or $toolchain.Count -ne 1 -or
        $null -eq $state.default -or
        [string]$state.default.toolchainId -cne [string]$toolchain[0].id)
    {
        throw "Published installation state does not contain one active schema-3 $Version@$rid toolchain."
    }

    $compilerVersion = (& $shim --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or
        -not $compilerVersion.StartsWith($Version, [StringComparison]::Ordinal))
    {
        throw "Published Eidosc version output '$compilerVersion' does not match '$Version'."
    }

    $stdlib = (& $shim info --stdlib | Out-String)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($stdlib))
    {
        throw "Published Eidosc could not load the external Std component."
    }

    $components = (& $manager component list --installed --toolchain $Version --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    $componentsExitCode = $LASTEXITCODE
    $targets = (& $manager target list --installed --toolchain $Version --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    $targetsExitCode = $LASTEXITCODE
    if ($componentsExitCode -ne 0 -or $targetsExitCode -ne 0 -or
        $components.schemaVersion -ne 1 -or
        "eidosc-core" -cnotin @($components.components.id) -or
        "eidos-std" -cnotin @($components.components.id) -or
        "eidos-runtime@$rid" -cnotin @($components.components.id) -or
        $targets.schemaVersion -ne 1 -or
        $rid -cnotin @($targets.targets.name))
    {
        throw "Published component and target contracts do not report the installed host toolchain."
    }

    $nativeName = if ($platform -ceq "win") { "published-native-smoke.exe" } else { "published-native-smoke" }
    $nativeOutput = Join-Path $temporaryRoot $nativeName
    & $shim build $example -o $nativeOutput --no-color --no-cache
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $nativeOutput -PathType Leaf))
    {
        throw "Published Eidosc did not produce a native executable on $rid."
    }

    Write-Host "Anonymous published-install verification passed for eidosc-v$Version on $rid."
}
finally
{
    foreach ($variable in @(
        @{ Name = "GITHUB_TOKEN"; Value = $previousGitHubToken },
        @{ Name = "GH_TOKEN"; Value = $previousGhToken },
        @{ Name = "EIDOSUP_TEST_RELEASE_SERVER"; Value = $previousTestServer }))
    {
        if ($null -eq $variable.Value)
        {
            [Environment]::SetEnvironmentVariable($variable.Name, $null)
        }
        else
        {
            [Environment]::SetEnvironmentVariable($variable.Name, [string]$variable.Value)
        }
    }

    if (Test-Path -LiteralPath $temporaryRoot)
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
