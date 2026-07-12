param(
    [string[]] $TrxPath = @(),
    [string] $OutputDirectory,
    [int] $Top = 50
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../..")).ProviderPath
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "tmp/test-audit"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function Format-Duration {
    param([TimeSpan] $Duration)
    if ($Duration.TotalMinutes -ge 1) {
        return "{0:n2}m" -f $Duration.TotalMinutes
    }

    return "{0:n2}s" -f $Duration.TotalSeconds
}

function Get-SlowCause {
    param(
        [string] $ClassName,
        [string] $TestName
    )

    if ($ClassName -like "*.RuntimeConcurrencyNativeSmokeTests" -or $TestName -match "NativeSmoke|ToolchainAvailable") {
        return "native toolchain/link/execute"
    }

    if ($ClassName -like "*.LlvmPipelineIntegrationTests") {
        return "LLVM pipeline/native smoke"
    }

    if ($ClassName -like "*.CoreFixtureRegressionTests" -or $ClassName -like "*.ListComprehensionRegressionTests") {
        return "fixture sweep pipeline"
    }

    if ($ClassName -like "*.ProofTypeCheckingTests" -or $ClassName -like "*.TypeInferencePipelineTests") {
        return "proof/type matrix"
    }

    if ($ClassName -like "*.IdeSemanticSnapshotTests") {
        return "IDE snapshot surface"
    }

    if ($ClassName -like "*.FunctionResolutionRegressionTests" -or $ClassName -like "*.ModuleExportResolutionTests" -or $ClassName -like "*.AbilityInheritanceResolutionTests") {
        return "semantic regression matrix"
    }

    if ($ClassName -like "*.AbilityCallConventionTests" -or $ClassName -like "*.HirBuilderDiagnosticsTests") {
        return "HIR/MIR pipeline slice"
    }

    return "CPU-bound compiler pipeline"
}

function Convert-Trx {
    param([string] $Path)

    [xml] $trx = Get-Content -LiteralPath $Path
    $classByTestId = @{}

    foreach ($unitTest in $trx.SelectNodes("//*[local-name()='UnitTest']")) {
        $testId = $unitTest.id
        $testMethod = $unitTest.SelectSingleNode("*[local-name()='TestMethod']")
        if ($testId -and $testMethod) {
            $classByTestId[$testId] = $testMethod.className
        }
    }

    foreach ($result in $trx.SelectNodes("//*[local-name()='UnitTestResult']")) {
        $duration = [TimeSpan]::Zero
        if ($result.duration) {
            $duration = [TimeSpan]::Parse($result.duration, [Globalization.CultureInfo]::InvariantCulture)
        }

        [pscustomobject]@{
            Trx      = Split-Path -Leaf $Path
            Name     = $result.testName
            Class    = $classByTestId[$result.testId]
            Outcome  = $result.outcome
            Cause    = Get-SlowCause -ClassName $classByTestId[$result.testId] -TestName $result.testName
            Duration = $duration
            Seconds  = $duration.TotalSeconds
        }
    }
}

function Get-FixtureReferences {
    $sourceRoot = Join-Path $repoRoot "src/Eidosc.Tests"
    $repoRootUri = [Uri]($repoRoot.TrimEnd("\") + "\")
    $patterns = @(
        'Fx\("([^"]+\.eidos)"\)',
        'Paths\.Fixture\("([^"]+\.eidos)"\)',
        'Success\("([^"]+\.eidos)"',
        'Error\("([^"]+\.eidos)"'
    )

    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -Filter "*.cs") {
        $relativeFile = [Uri]::UnescapeDataString($repoRootUri.MakeRelativeUri([Uri]$file.FullName).ToString())
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($pattern in $patterns) {
            foreach ($match in [regex]::Matches($text, $pattern)) {
                [pscustomobject]@{
                    Fixture = $match.Groups[1].Value
                    Test    = $relativeFile
                }
            }
        }
    }
}

function Get-FixtureDuplicateReason {
    param(
        [string] $Fixture,
        [string[]] $Tests
    )

    if ($Tests.Count -le 1) {
        return "multiple assertions within one test file"
    }

    $joined = $Tests -join "; "
    if ($joined -match "Fixtures/EidosFixtureInventory\.cs" -and $joined -match "Unit/Hir") {
        return "borrow-phase inventory coverage plus HIR-specific diagnostics"
    }

    if ($joined -match "Fixtures/EidosFixtureInventory\.cs" -and $joined -match "Unit/Borrow") {
        return "borrow-phase inventory coverage plus borrow unit diagnostics"
    }

    if ($joined -match "Fixtures/EidosFixtureInventory\.cs" -and $joined -match "LlvmPipelineIntegrationTests") {
        return "borrow-phase inventory coverage plus LLVM/MIR output assertions"
    }

    if ($joined -match "LlvmPipelineIntegrationTests\.Network\.cs" -and $joined -match "LlvmPipelineIntegrationTests\.RuntimeSmokeAndGeneric\.cs") {
        return "network IR/native variants with libcurl-gated runtime coverage"
    }

    if ($joined -match "LlvmPipelineIntegrationTests\.StdlibImports\.cs" -and $joined -match "LlvmPipelineIntegrationTests") {
        return "stdlib import behavior plus LLVM/MIR/backend-specific assertions"
    }

    if ($joined -match "LlvmPipelineIntegrationTests") {
        return "multiple LLVM/MIR/native assertions over one backend fixture"
    }

    return "different assertion surfaces or compiler phases"
}

function Get-CategoryReferences {
    $sourceRoot = Join-Path $repoRoot "src/Eidosc.Tests"
    $repoRootUri = [Uri]($repoRoot.TrimEnd("\") + "\")
    $pattern = '\[Trait\(TestCategories\.Category,\s*TestCategories\.([A-Za-z0-9_]+)\)\]'

    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -Filter "*.cs") {
        $relativeFile = [Uri]::UnescapeDataString($repoRootUri.MakeRelativeUri([Uri]$file.FullName).ToString())
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($match in [regex]::Matches($text, $pattern)) {
            [pscustomobject]@{
                Category = $match.Groups[1].Value
                File     = $relativeFile
            }
        }
    }
}

$resolvedTrxPaths = @()
foreach ($entry in $TrxPath) {
    $resolvedTrxPaths += $entry -split "," |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim() }
}

$allResults = @()
foreach ($path in $resolvedTrxPaths) {
    if (Test-Path -LiteralPath $path) {
        $allResults += Convert-Trx -Path (Resolve-Path $path)
    }
}

$fixtureReferences = Get-FixtureReferences
$categoryReferences = Get-CategoryReferences
$fixtureMap = $fixtureReferences |
    Group-Object Fixture |
    Sort-Object -Property @{ Expression = "Count"; Descending = $true }, @{ Expression = "Name"; Ascending = $true } |
    ForEach-Object {
        $tests = $_.Group.Test | Sort-Object -Unique
        [pscustomobject]@{
            Fixture    = $_.Name
            References = $_.Count
            Reason     = Get-FixtureDuplicateReason -Fixture $_.Name -Tests $tests
            Tests      = $tests -join "; "
        }
    }

$reportPath = Join-Path $OutputDirectory "test-suite-profile.md"
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("# Eidosc Test Suite Profile")
$lines.Add("")
$lines.Add("Generated: $(Get-Date -Format o)")
$lines.Add("")

if ($allResults.Count -gt 0) {
    $lines.Add("## Top Slow Test Instances")
    $lines.Add("")
    $lines.Add("| Seconds | Outcome | Cause | Class | Test | TRX |")
    $lines.Add("|---:|---|---|---|---|---|")
    foreach ($result in $allResults | Sort-Object Seconds -Descending | Select-Object -First $Top) {
        $lines.Add("| $([Math]::Round($result.Seconds, 3)) | $($result.Outcome) | $($result.Cause) | $($result.Class) | $($result.Name) | $($result.Trx) |")
    }

    $lines.Add("")
    $lines.Add("## Top Slow Test Classes")
    $lines.Add("")
    $lines.Add("| Seconds | Count | Class |")
    $lines.Add("|---:|---:|---|")
    foreach ($group in $allResults | Group-Object Class | Sort-Object { ($_.Group | Measure-Object Seconds -Sum).Sum } -Descending | Select-Object -First 30) {
        $seconds = ($group.Group | Measure-Object Seconds -Sum).Sum
        $lines.Add("| $([Math]::Round($seconds, 3)) | $($group.Count) | $($group.Name) |")
    }

    $totalDuration = [TimeSpan]::FromSeconds(($allResults | Measure-Object Seconds -Sum).Sum)
    $lines.Add("")
    $lines.Add("Total test result duration sum: $(Format-Duration $totalDuration)")
    $lines.Add("")
}
else {
    $lines.Add("No TRX files were provided. Only fixture mapping was generated.")
    $lines.Add("")
}

$lines.Add("## Fixture References")
$lines.Add("")
$lines.Add("| References | Fixture | Reason | Tests |")
$lines.Add("|---:|---|---|---|")
foreach ($item in $fixtureMap) {
    $lines.Add("| $($item.References) | $($item.Fixture) | $($item.Reason) | $($item.Tests) |")
}

$lines.Add("")
$lines.Add("## Category References")
$lines.Add("")
$lines.Add("| Category | References | Files |")
$lines.Add("|---|---:|---|")
foreach ($group in $categoryReferences | Group-Object Category | Sort-Object Name) {
    $files = ($group.Group.File | Sort-Object -Unique) -join "; "
    $lines.Add("| $($group.Name) | $($group.Count) | $files |")
}

$lines | Set-Content -LiteralPath $reportPath -Encoding UTF8
$fixtureMap | Export-Csv -LiteralPath (Join-Path $OutputDirectory "fixture-references.csv") -NoTypeInformation -Encoding UTF8
$fixtureMap |
    Where-Object { [int]$_.References -gt 1 } |
    Export-Csv -LiteralPath (Join-Path $OutputDirectory "fixture-duplicate-reasons.csv") -NoTypeInformation -Encoding UTF8
$categoryReferences | Export-Csv -LiteralPath (Join-Path $OutputDirectory "category-references.csv") -NoTypeInformation -Encoding UTF8

Write-Host "Wrote $reportPath"
Write-Host "Wrote $(Join-Path $OutputDirectory "fixture-references.csv")"
Write-Host "Wrote $(Join-Path $OutputDirectory "fixture-duplicate-reasons.csv")"
Write-Host "Wrote $(Join-Path $OutputDirectory "category-references.csv")"
