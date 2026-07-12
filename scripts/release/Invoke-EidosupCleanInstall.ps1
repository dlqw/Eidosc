[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$CommitSha,

    [Parameter(Mandatory)]
    [string]$CandidateDirectory,

    [Parameter(Mandatory)]
    [string]$EidosupPath,

    [Parameter(Mandatory)]
    [string]$TutorialExample,

    [string]$Repository = "dlqw/Eidosc"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $PSCommandPath
$candidateRoot = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$bootstrap = (Resolve-Path -LiteralPath $EidosupPath).Path
$example = (Resolve-Path -LiteralPath $TutorialExample).Path
& (Join-Path $scriptRoot "Test-ReleaseAssets.ps1") `
    -Product eidosc `
    -Version $Version `
    -CommitSha $CommitSha `
    -AssetDirectory $candidateRoot

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
    throw "Unsupported release verification platform."
}

$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($architecture -notin @("x64", "arm64"))
{
    throw "Unsupported release verification architecture: $architecture"
}
$rid = "$platform-$architecture"

$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$portProbe.Start()
$port = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
$portProbe.Stop()
$serverBase = "http://127.0.0.1:$port/"
$metadata = Get-Content -Raw -LiteralPath (Join-Path $candidateRoot "eidosc-release.json") | ConvertFrom-Json
$assetMap = @{}
$releaseAssets = @()
foreach ($asset in @($metadata.assets) + @([pscustomobject]@{
    name = "SHA256SUMS"
    size = (Get-Item -LiteralPath (Join-Path $candidateRoot "SHA256SUMS")).Length
}))
{
    $assetPath = Join-Path $candidateRoot $asset.name
    $route = "/assets/$([Uri]::EscapeDataString($asset.name))"
    $assetMap[$route] = $assetPath
    $releaseAssets += [ordered]@{
        name = $asset.name
        browser_download_url = "$($serverBase.TrimEnd('/'))$route"
        size = $asset.size
    }
}

$releasePayload = [ordered]@{
    tag_name = "eidosc-v$Version"
    name = "eidosc-v$Version release candidate"
    draft = $false
    prerelease = $Version.Contains('-', [StringComparison]::Ordinal)
    published_at = "2026-01-01T00:00:00Z"
    assets = $releaseAssets
} | ConvertTo-Json -Depth 5 -Compress
$releaseBytes = [Text.Encoding]::UTF8.GetBytes($releasePayload)
$apiRoute = "/repos/$Repository/releases/tags/eidosc-v$Version"

$serverJob = Start-Job -ArgumentList $port, $apiRoute, $releaseBytes, $assetMap -ScriptBlock {
    param($Port, $ApiRoute, $ReleaseBytes, $AssetMap)

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    $listener.Start()

    function Send-Response([IO.Stream]$Stream, [int]$Status, [string]$ContentType, [long]$Length, [string]$Method)
    {
        $reason = if ($Status -eq 200) { "OK" } else { "Not Found" }
        $header = "HTTP/1.1 $Status $reason`r`nContent-Type: $ContentType`r`nContent-Length: $Length`r`nConnection: close`r`n`r`n"
        $headerBytes = [Text.Encoding]::ASCII.GetBytes($header)
        $Stream.Write($headerBytes, 0, $headerBytes.Length)
        return $Method -cne "HEAD"
    }

    while ($true)
    {
        if (-not $listener.Pending())
        {
            Start-Sleep -Milliseconds 50
            continue
        }

        $client = $listener.AcceptTcpClient()
        try
        {
            $stream = $client.GetStream()
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 4096, $true)
            try
            {
                $requestLine = $reader.ReadLine()
                if ([string]::IsNullOrWhiteSpace($requestLine)) { continue }
                while (-not [string]::IsNullOrEmpty($reader.ReadLine())) { }
                $parts = $requestLine.Split(' ')
                $method = $parts[0]
                $route = ([Uri]::new("http://127.0.0.1$($parts[1])")).AbsolutePath

                if ($route -ceq $ApiRoute)
                {
                    if (Send-Response $stream 200 "application/json" $ReleaseBytes.Length $method)
                    {
                        $stream.Write($ReleaseBytes, 0, $ReleaseBytes.Length)
                    }
                }
                elseif ($AssetMap.ContainsKey($route))
                {
                    $file = [IO.File]::OpenRead($AssetMap[$route])
                    try
                    {
                        if (Send-Response $stream 200 "application/octet-stream" $file.Length $method)
                        {
                            $file.CopyTo($stream)
                        }
                    }
                    finally
                    {
                        $file.Dispose()
                    }
                }
                else
                {
                    [void](Send-Response $stream 404 "text/plain" 0 $method)
                }

                $stream.Flush()
            }
            finally
            {
                $reader.Dispose()
            }
        }
        finally
        {
            $client.Dispose()
        }
    }
}

$serverReady = $false
for ($attempt = 0; $attempt -lt 50; $attempt++)
{
    if ($serverJob.State -eq [Management.Automation.JobState]::Failed)
    {
        throw "The local release candidate server failed to start."
    }

    $probe = [Net.Sockets.TcpClient]::new()
    try
    {
        $probe.Connect([Net.IPAddress]::Loopback, $port)
        $serverReady = $true
        break
    }
    catch [Net.Sockets.SocketException]
    {
        Start-Sleep -Milliseconds 100
    }
    finally
    {
        $probe.Dispose()
    }
}

if (-not $serverReady)
{
    throw "Timed out waiting for the local release candidate server."
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("eidosup-clean-install-" + [Guid]::NewGuid().ToString("N"))
$installRoot = Join-Path $temporaryRoot "install"
$downloadRoot = Join-Path $temporaryRoot "downloads"
$previousTestServer = $env:EIDOSUP_TEST_RELEASE_SERVER

try
{
    $env:EIDOSUP_TEST_RELEASE_SERVER = $serverBase
    & $bootstrap setup `
        --repo $Repository `
        --version $Version `
        --install-root $installRoot `
        --download-root $downloadRoot `
        --skip-clang `
        --skip-env
    if ($LASTEXITCODE -ne 0)
    {
        throw "Eidosup clean installation failed with exit code $LASTEXITCODE."
    }

    $binaryName = if ($platform -eq "win") { "eidosc.exe" } else { "eidosc" }
    $statePath = Join-Path $installRoot "state/toolchains.json"
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf))
    {
        throw "Installed toolchain state was not found at '$statePath'."
    }

    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.schema -ne 1)
    {
        throw "Installed toolchain state uses unexpected schema '$($state.schema)'."
    }

    $matches = @($state.toolchains | Where-Object {
        $_.version -ceq $Version -and $_.rid -ceq $rid
    })
    if ($matches.Count -ne 1)
    {
        throw "Expected exactly one registered $Version@$rid toolchain, found $($matches.Count)."
    }

    $toolchainId = [string]$matches[0].id
    if ($toolchainId -notmatch '^eidosc-[0-9A-Za-z.+-]+-(?:win|linux|osx)-(?:x64|arm64)-[0-9a-f]{64}$')
    {
        throw "Registered toolchain ID '$toolchainId' is invalid."
    }

    $eidosc = Join-Path $installRoot "toolchains/$toolchainId/$binaryName"
    if (-not (Test-Path -LiteralPath $eidosc -PathType Leaf))
    {
        throw "Installed eidosc binary was not found at '$eidosc'."
    }

    if ($null -eq $state.default -or [string]$state.default.toolchainId -cne $toolchainId)
    {
        throw "The installed toolchain was not activated as the global default."
    }

    $managerName = if ($platform -eq "win") { "eidosup.exe" } else { "eidosup" }
    $shim = Join-Path $installRoot "bin/$binaryName"
    $manager = Join-Path $installRoot "bin/$managerName"
    if (-not (Test-Path -LiteralPath $shim -PathType Leaf) -or
        -not (Test-Path -LiteralPath $manager -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $installRoot "bin/.eidosup-shims.json") -PathType Leaf))
    {
        throw "Stable Eidosup manager and eidosc shim were not installed in '$installRoot/bin'."
    }

    $list = (& $manager toolchain list --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or @($list.toolchains).Count -ne 1 -or
        [string]$list.toolchains[0].id -cne $toolchainId)
    {
        throw "Eidosup toolchain list did not report the clean-installed immutable toolchain."
    }

    $which = (& $manager which eidosc --toolchain $Version --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or [string]$which.toolchainId -cne $toolchainId -or
        -not [IO.Path]::GetFullPath([string]$which.commandPath).Equals(
            [IO.Path]::GetFullPath($eidosc),
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Eidosup which did not resolve the exact-version toolchain."
    }

    & $manager run $Version --install-root $installRoot -- eidosc --version *> $null
    if ($LASTEXITCODE -ne 0) { throw "Eidosup run failed for the exact-version toolchain." }

    $check = (& $manager check $Version --repo $Repository --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or [string]$check.toolchains[0].status -cne "current")
    {
        throw "Eidosup check did not report the candidate exact version as current."
    }

    & $manager update $Version `
        --repo $Repository `
        --install-root $installRoot `
        --download-root $downloadRoot *> $null
    if ($LASTEXITCODE -ne 0) { throw "Eidosup exact-version update convergence failed." }

    & $manager default none --install-root $installRoot *> $null
    if ($LASTEXITCODE -ne 0) { throw "Eidosup failed to clear the global default." }
    & $shim --version *> $null
    if ($LASTEXITCODE -ne 32)
    {
        throw "The stable shim returned '$LASTEXITCODE' instead of 32 after clearing the default."
    }

    & $manager default $Version --install-root $installRoot *> $null
    if ($LASTEXITCODE -ne 0) { throw "Eidosup failed to restore the exact-version default." }

    & $shim info
    if ($LASTEXITCODE -ne 0) { throw "Installed eidosc shim info command failed." }

    & $shim build $example --phase hir --no-color --no-cache
    if ($LASTEXITCODE -ne 0) { throw "Installed eidosc shim failed to compile the tutorial smoke example." }

    $missingInput = Join-Path $temporaryRoot "missing-input.eidos"
    & $eidosc build $missingInput --phase hir --no-color --no-cache *> $null
    $directFailure = $LASTEXITCODE
    & $shim build $missingInput --phase hir --no-color --no-cache *> $null
    $shimFailure = $LASTEXITCODE
    if ($directFailure -eq 0 -or $shimFailure -ne $directFailure)
    {
        throw "The eidosc shim did not preserve the compiler exit code (direct=$directFailure, shim=$shimFailure)."
    }

    function Measure-MedianStartup([string]$Command)
    {
        for ($warmup = 0; $warmup -lt 2; $warmup++) { & $Command --version *> $null }
        $samples = for ($iteration = 0; $iteration -lt 9; $iteration++)
        {
            $timer = [Diagnostics.Stopwatch]::StartNew()
            & $Command --version *> $null
            if ($LASTEXITCODE -ne 0) { throw "Startup benchmark command failed: $Command" }
            $timer.Stop()
            $timer.Elapsed.TotalMilliseconds
        }

        return ($samples | Sort-Object)[[Math]::Floor($samples.Count / 2)]
    }

    $directMedian = Measure-MedianStartup $eidosc
    $shimMedian = Measure-MedianStartup $shim
    $shimOverhead = [Math]::Max(0, $shimMedian - $directMedian)
    if ($shimOverhead -gt 200)
    {
        throw "Median eidosc shim startup overhead $([Math]::Round($shimOverhead, 1))ms exceeds the 200ms release baseline."
    }

    & $manager toolchain uninstall $Version --install-root $installRoot *> $null
    if ($LASTEXITCODE -ne 22)
    {
        throw "Eidosup returned '$LASTEXITCODE' instead of 22 when uninstalling the active default."
    }

    & $manager default none --install-root $installRoot *> $null
    if ($LASTEXITCODE -ne 0) { throw "Eidosup failed to clear the default before uninstall." }
    & $manager toolchain uninstall $Version --install-root $installRoot
    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath (Join-Path $installRoot "toolchains/$toolchainId")))
    {
        throw "Eidosup failed to transactionally uninstall the inactive exact-version toolchain."
    }

    $emptyList = (& $manager toolchain list --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or @($emptyList.toolchains).Count -ne 0)
    {
        throw "Eidosup toolchain list was not empty after verified uninstall."
    }

    Write-Host "Clean install verification passed for $rid using eidosc-v$Version (shim median overhead $([Math]::Round($shimOverhead, 1))ms)."
}
finally
{
    $env:EIDOSUP_TEST_RELEASE_SERVER = $previousTestServer
    Stop-Job -Job $serverJob -ErrorAction SilentlyContinue
    Remove-Job -Job $serverJob -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $temporaryRoot)
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
