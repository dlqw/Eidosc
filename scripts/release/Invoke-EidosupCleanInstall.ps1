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
    if ($state.schema -ne 3)
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
    $initialToolchainId = $toolchainId
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

    $initialManifestPath = Join-Path $installRoot "toolchains/$initialToolchainId/.eidosup-install.json"
    $initialManifestSha256 = (Get-FileHash -LiteralPath $initialManifestPath -Algorithm SHA256).Hash
    $components = (& $manager component list --installed --toolchain $Version --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    $expectedInitialComponents = @("eidosc-core", "eidos-std", "eidos-runtime@$rid")
    if ($LASTEXITCODE -ne 0 -or $components.schemaVersion -ne 1 -or
        [string]$components.profile -cne "default" -or
        @($components.components).Count -ne $expectedInitialComponents.Count -or
        @($expectedInitialComponents | Where-Object { $_ -cnotin @($components.components.id) }).Count -ne 0)
    {
        throw "The default profile did not install the expected core, Std, and host runtime components."
    }

    $stdlibOutput = (& $shim info --stdlib | Out-String)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($stdlibOutput))
    {
        throw "Installed eidosc did not load and report the external Std component."
    }

    $docsChange = (& $manager component add eidos-docs --toolchain $Version --install-root $installRoot --download-root $downloadRoot --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $docsChange.schemaVersion -ne 1 -or
        [string]$docsChange.action -cne "add" -or
        "eidos-docs" -cnotin @($docsChange.addedComponents) -or
        [string]$docsChange.install.disposition -cnotin @("installed", "replaced", "alreadyInstalled"))
    {
        throw "Eidosup failed to add the local documentation component with the composition-change-v1 contract."
    }
    $docsPath = (& $manager doc index --toolchain $Version --install-root $installRoot --path | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $docsPath -PathType Leaf))
    {
        throw "Eidosup doc did not resolve a version-matched offline document."
    }
    $docsResult = (& $manager doc index --toolchain $Version --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $docsResult.schemaVersion -ne 1 -or
        [string]$docsResult.topic -cne "index" -or
        [string]$docsResult.eidoscVersion -cne $Version -or
        -not [IO.Path]::GetFullPath([string]$docsResult.path).Equals(
            [IO.Path]::GetFullPath($docsPath),
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Eidosup doc JSON did not match the doc-v1 contract."
    }

    $profileChange = (& $manager set profile complete --toolchain $Version --install-root $installRoot --download-root $downloadRoot --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $profileChange.schemaVersion -ne 1 -or
        [string]$profileChange.action -cne "set-profile" -or
        [string]$profileChange.profile -cne "complete")
    {
        throw "Eidosup failed to activate the complete profile with the composition-change-v1 contract."
    }
    $complete = (& $manager component list --installed --toolchain $Version --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or
        "eidos-docs" -cnotin @($complete.components.id) -or
        "eidos-bindgen" -cnotin @($complete.components.id))
    {
        throw "The complete profile did not materialize docs and bindgen components."
    }

    $crossArchitecture = if ($architecture -ceq "x64") { "arm64" } else { "x64" }
    $crossRid = "$platform-$crossArchitecture"
    $crossTriple = switch ($crossRid)
    {
        "win-x64" { "x86_64-pc-windows-msvc" }
        "win-arm64" { "aarch64-pc-windows-msvc" }
        "linux-x64" { "x86_64-pc-linux-gnu" }
        "linux-arm64" { "aarch64-unknown-linux-gnu" }
        "osx-x64" { "x86_64-apple-macosx10.15" }
        "osx-arm64" { "arm64-apple-macosx11" }
        default { throw "The clean-install cross smoke has no triple for '$crossRid'." }
    }
    $targetChange = (& $manager target add $crossRid --toolchain $Version --install-root $installRoot --download-root $downloadRoot --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $targetChange.schemaVersion -ne 1 -or
        [string]$targetChange.action -cne "add" -or
        $crossRid -cnotin @($targetChange.targets))
    {
        throw "Eidosup failed to add cross target '$crossRid' with the composition-change-v1 contract."
    }
    $targets = (& $manager target list --installed --toolchain $Version --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    $hostTarget = @($targets.targets | Where-Object { [string]$_.name -ceq $rid })
    $crossTarget = @($targets.targets | Where-Object { [string]$_.name -ceq $crossRid })
    $validLinkerReadiness = @("ready", "notInstalled", "commandMissing", "externalSdkRequired")
    if ($LASTEXITCODE -ne 0 -or $targets.schemaVersion -ne 1 -or
        $rid -cnotin @($targets.targets.name) -or
        $crossRid -cnotin @($targets.targets.name) -or
        $hostTarget.Count -ne 1 -or [string]$hostTarget[0].support -cne "host" -or
        $hostTarget[0].compilerReady -cne $true -or
        $crossTarget.Count -ne 1 -or [string]$crossTarget[0].support -cne "crossCompile" -or
        $crossTarget[0].compilerReady -cne $true -or
        [string]$crossTarget[0].linkerReadiness -cnotin $validLinkerReadiness)
    {
        throw "Target list did not report both host and cross runtime components."
    }

    $crossIr = Join-Path $temporaryRoot "cross-$crossRid.ll"
    $crossObjectExtension = if ($platform -eq "win") { ".obj" } else { ".o" }
    $crossObject = Join-Path $temporaryRoot "cross-$crossRid$crossObjectExtension"
    & $shim build $example -t LlvmIr -o $crossIr --target-triple $crossTriple --no-color --no-cache
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $crossIr -PathType Leaf))
    {
        throw "Installed eidosc failed to generate LLVM IR for '$crossTriple'."
    }
    & clang -target $crossTriple -c $crossIr -o $crossObject
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $crossObject -PathType Leaf))
    {
        throw "LLVM failed to compile Eidosc cross-target IR to an object for '$crossTriple'."
    }

    if ((Get-FileHash -LiteralPath $initialManifestPath -Algorithm SHA256).Hash -cne $initialManifestSha256)
    {
        throw "Creating component variants mutated the original immutable toolchain."
    }

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
    $afterUpdate = (& $manager component list --installed --toolchain $Version --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    $afterUpdateTargets = (& $manager target list --installed --toolchain $Version --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    if ("eidos-docs" -cnotin @($afterUpdate.components.id) -or
        "eidos-bindgen" -cnotin @($afterUpdate.components.id) -or
        $crossRid -cnotin @($afterUpdateTargets.targets.name))
    {
        throw "Eidosup update did not preserve profile, explicit components, and explicit targets."
    }

    & $manager set profile minimal --toolchain $Version --install-root $installRoot --download-root $downloadRoot *> $null
    if ($LASTEXITCODE -ne 0) { throw "Eidosup failed to switch to the minimal profile." }
    & $manager component remove eidos-docs --toolchain $Version --install-root $installRoot --download-root $downloadRoot *> $null
    if ($LASTEXITCODE -ne 0) { throw "Eidosup failed to remove an explicit docs component." }
    & $manager target remove $crossRid --toolchain $Version --install-root $installRoot --download-root $downloadRoot *> $null
    if ($LASTEXITCODE -ne 0) { throw "Eidosup failed to remove an explicit cross target." }
    $minimal = (& $manager component list --installed --toolchain $Version --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    $minimalTargets = (& $manager target list --installed --toolchain $Version --install-root $installRoot --json | Out-String) | ConvertFrom-Json
    if (@($minimal.components).Count -ne 2 -or
        "eidosc-core" -cnotin @($minimal.components.id) -or
        "eidos-std" -cnotin @($minimal.components.id) -or
        @($minimalTargets.targets).Count -ne 0)
    {
        throw "Minimal profile cleanup did not leave exactly core and Std."
    }

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

    $nativeName = if ($platform -ceq "win") { "native-smoke.exe" } else { "native-smoke" }
    $nativeOutput = Join-Path $temporaryRoot $nativeName
    & $shim build $example -o $nativeOutput --no-color --no-cache
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $nativeOutput -PathType Leaf))
    {
        throw "Installed eidosc shim failed to produce a native tutorial smoke executable."
    }

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
    $shimOverheadBaselines = @{
        "linux-x64" = 250.0
        "linux-arm64" = 200.0
        "osx-arm64" = 300.0
        "win-x64" = 300.0
        "win-arm64" = 300.0
        "osx-x64" = 600.0
    }
    $shimOverheadBaseline = [double]$shimOverheadBaselines[$rid]
    Write-Host (
        "Eidosc startup medians on {0}: direct={1}ms, shim={2}ms, overhead={3}ms, baseline={4}ms." -f
        $rid,
        [Math]::Round($directMedian, 1),
        [Math]::Round($shimMedian, 1),
        [Math]::Round($shimOverhead, 1),
        [Math]::Round($shimOverheadBaseline, 1))
    if ($shimOverhead -gt $shimOverheadBaseline)
    {
        throw "Median eidosc shim startup overhead $([Math]::Round($shimOverhead, 1))ms exceeds the $([Math]::Round($shimOverheadBaseline, 1))ms release baseline for $rid."
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
