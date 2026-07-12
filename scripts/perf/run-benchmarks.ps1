param(
    [string]$Filter = "*",
    [switch]$SkipBenchmarks,
    [switch]$SkipProfileBatch,
    [string]$GateThresholds = "",
    [int]$ProfileIterations = 1,
    [int]$ProfileWarmup = 0,
    [switch]$IncludeNativeProfile
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outputRoot = Join-Path $repoRoot "tmp/perf/$timestamp"
$bdnArtifacts = Join-Path $outputRoot "bdn"
$profileOutput = Join-Path $outputRoot "profile-batch.md"
$profileJsonOutput = Join-Path $outputRoot "profile-batch.json.out"
$summaryOutput = Join-Path $outputRoot "summary.md"
$manifestPath = Join-Path $outputRoot "profile-batch.json"
$snakeGuiSource = Join-Path $repoRoot "../projects/snake-gui/src/Main.eidos"
$snakeGuiProject = Join-Path $repoRoot "../projects/snake-gui/eidos.toml"
$snakeSource = Join-Path $repoRoot "../projects/snake/src/Main.eidos"
$snakeProject = Join-Path $repoRoot "../projects/snake/eidos.toml"
$stdlibPreludeSource = Join-Path $repoRoot "src/Eidosc/Stdlib/Precompiled/Std/Prelude.eidos"

if (Test-Path $snakeGuiSource) {
    $profileCaseName = "snake-gui-types"
    $profileSource = $snakeGuiSource
    $profileProject = $snakeGuiProject
} else {
    $profileCaseName = "stdlib-prelude-types"
    $profileSource = $stdlibPreludeSource
    $profileProject = ""
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$gateStatus = "not run"

dotnet build-server shutdown | Out-Null

if (-not $SkipBenchmarks) {
    dotnet run `
        --project (Join-Path $repoRoot "src/Eidosc.Benchmarks/Eidosc.Benchmarks.csproj") `
        -c Release `
        -- `
        --filter $Filter `
        --artifacts $bdnArtifacts
}

if (-not $SkipProfileBatch) {
    $cases = @()
    $cases += [ordered]@{
        name = $profileCaseName
        source = $profileSource
        project = $profileProject
        stopAtPhase = "Types"
    }
    if ($IncludeNativeProfile -and (Test-Path $snakeSource)) {
        $cases += [ordered]@{
            name = "snake-native"
            source = $snakeSource
            project = $snakeProject
            target = "Native"
            optimizationLevel = 0
        }
    }
    if ($IncludeNativeProfile -and (Test-Path $snakeGuiSource)) {
        $cases += [ordered]@{
            name = "snake-gui-native"
            source = $snakeGuiSource
            project = $snakeGuiProject
            target = "Native"
            optimizationLevel = 0
        }
    }

    $manifest = [ordered]@{
        name = "eidosc-local-perf"
        cases = $cases
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8

    dotnet run `
        --project (Join-Path $repoRoot "src/Eidosc.Cli/Eidosc.Cli.csproj") `
        -c Release `
        -- `
        profile-batch $manifestPath `
        --output $profileOutput `
        --iterations $ProfileIterations `
        --warmup $ProfileWarmup `
        --no-color

    dotnet run `
        --project (Join-Path $repoRoot "src/Eidosc.Cli/Eidosc.Cli.csproj") `
        -c Release `
        -- `
        profile-batch $manifestPath `
        --format Json `
        --output $profileJsonOutput `
        --iterations $ProfileIterations `
        --warmup $ProfileWarmup `
        --no-color

    if (-not [string]::IsNullOrWhiteSpace($GateThresholds)) {
        powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "check-profile-gate.ps1") `
            -ProfileJson $profileJsonOutput `
            -ThresholdsJson $GateThresholds
        if ($LASTEXITCODE -ne 0) {
            throw "Performance gate failed with exit code $LASTEXITCODE."
        }

        $gateStatus = "passed"
    }
}

$summary = @"
# Eidosc Perf Run

- Time: $timestamp
- Filter: $Filter
- Benchmarks skipped: $SkipBenchmarks
- Profile-batch skipped: $SkipProfileBatch
- Profile iterations: $ProfileIterations
- Profile warmup: $ProfileWarmup
- Include native profile: $IncludeNativeProfile
- Gate: $gateStatus

## Artifacts

- BenchmarkDotNet: $bdnArtifacts
- Profile markdown: $profileOutput
- Profile JSON: $profileJsonOutput
- Profile manifest: $manifestPath
- Thresholds: $GateThresholds
"@
$summary | Set-Content -Path $summaryOutput -Encoding UTF8

Write-Host "Perf summary: $summaryOutput"
Write-Host "Perf output: $outputRoot"
