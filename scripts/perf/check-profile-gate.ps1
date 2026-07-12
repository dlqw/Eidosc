param(
    [Parameter(Mandatory = $true)]
    [string]$ProfileJson,

    [Parameter(Mandatory = $true)]
    [string]$ThresholdsJson
)

$ErrorActionPreference = "Stop"

function Format-Bytes([double]$bytes) {
    if ($bytes -ge 1MB) {
        return "{0:N2} MiB" -f ($bytes / 1MB)
    }

    if ($bytes -ge 1KB) {
        return "{0:N2} KiB" -f ($bytes / 1KB)
    }

    return "{0:N0} B" -f $bytes
}

function Add-Failure([System.Collections.Generic.List[string]]$failures, [string]$message) {
    $failures.Add($message) | Out-Null
}

if (-not (Test-Path -LiteralPath $ProfileJson)) {
    throw "profile json not found: $ProfileJson"
}

if (-not (Test-Path -LiteralPath $ThresholdsJson)) {
    throw "thresholds json not found: $ThresholdsJson"
}

$profile = Get-Content -Raw -LiteralPath $ProfileJson | ConvertFrom-Json
$thresholds = Get-Content -Raw -LiteralPath $ThresholdsJson | ConvertFrom-Json
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($caseThreshold in $thresholds.cases) {
    $case = @($profile.Cases | Where-Object { $_.Name -eq $caseThreshold.name }) | Select-Object -First 1
    if ($null -eq $case) {
        if ($caseThreshold.optional -eq $true) {
            continue
        }

        Add-Failure $failures "case '$($caseThreshold.name)' missing from profile output"
        continue
    }

    if (-not $case.Success) {
        Add-Failure $failures "case '$($case.Name)' failed: $($case.FailureReason)"
        continue
    }

    if ($null -ne $caseThreshold.maxTotalMs -and $case.AverageTotalTimeMs -gt $caseThreshold.maxTotalMs) {
        Add-Failure $failures ("case '{0}' total {1:N2} ms > {2:N2} ms" -f $case.Name, $case.AverageTotalTimeMs, $caseThreshold.maxTotalMs)
    }

    foreach ($phaseThreshold in @($caseThreshold.phases)) {
        $phase = @($case.Phases | Where-Object { $_.Phase -eq $phaseThreshold.name }) | Select-Object -First 1
        if ($null -eq $phase) {
            Add-Failure $failures "case '$($case.Name)' phase '$($phaseThreshold.name)' missing"
            continue
        }

        if ($null -ne $phaseThreshold.maxMs -and $phase.ElapsedMs -gt $phaseThreshold.maxMs) {
            Add-Failure $failures ("case '{0}' phase '{1}' {2:N2} ms > {3:N2} ms" -f $case.Name, $phase.Phase, $phase.ElapsedMs, $phaseThreshold.maxMs)
        }

        if ($null -ne $phaseThreshold.maxAllocatedBytes -and $phase.AllocatedBytes -gt $phaseThreshold.maxAllocatedBytes) {
            Add-Failure $failures ("case '{0}' phase '{1}' alloc {2} > {3}" -f $case.Name, $phase.Phase, (Format-Bytes $phase.AllocatedBytes), (Format-Bytes $phaseThreshold.maxAllocatedBytes))
        }
    }

    foreach ($subphaseThreshold in @($caseThreshold.subphases)) {
        $parts = $subphaseThreshold.name -split '\.', 2
        if ($parts.Count -ne 2) {
            Add-Failure $failures "case '$($case.Name)' invalid subphase threshold name '$($subphaseThreshold.name)'"
            continue
        }

        $subphase = @($case.Subphases | Where-Object { $_.Phase -eq $parts[0] -and $_.Name -eq $parts[1] }) | Select-Object -First 1
        if ($null -eq $subphase) {
            Add-Failure $failures "case '$($case.Name)' subphase '$($subphaseThreshold.name)' missing"
            continue
        }

        if ($null -ne $subphaseThreshold.maxMs -and $subphase.ElapsedMs -gt $subphaseThreshold.maxMs) {
            Add-Failure $failures ("case '{0}' subphase '{1}' {2:N2} ms > {3:N2} ms" -f $case.Name, $subphaseThreshold.name, $subphase.ElapsedMs, $subphaseThreshold.maxMs)
        }

        if ($null -ne $subphaseThreshold.maxAllocatedBytes -and $subphase.AllocatedBytes -gt $subphaseThreshold.maxAllocatedBytes) {
            Add-Failure $failures ("case '{0}' subphase '{1}' alloc {2} > {3}" -f $case.Name, $subphaseThreshold.name, (Format-Bytes $subphase.AllocatedBytes), (Format-Bytes $subphaseThreshold.maxAllocatedBytes))
        }
    }

    foreach ($counterThreshold in @($caseThreshold.counters)) {
        $counter = @($case.Counters | Where-Object { $_.Name -eq $counterThreshold.name }) | Select-Object -First 1
        if ($null -eq $counter) {
            Add-Failure $failures "case '$($case.Name)' counter '$($counterThreshold.name)' missing"
            continue
        }

        if ($null -ne $counterThreshold.min -and $counter.Value -lt $counterThreshold.min) {
            Add-Failure $failures ("case '{0}' counter '{1}' {2} < {3}" -f $case.Name, $counter.Name, $counter.Value, $counterThreshold.min)
        }

        if ($null -ne $counterThreshold.max -and $counter.Value -gt $counterThreshold.max) {
            Add-Failure $failures ("case '{0}' counter '{1}' {2} > {3}" -f $case.Name, $counter.Name, $counter.Value, $counterThreshold.max)
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "Performance gate failed:"
    foreach ($failure in $failures) {
        Write-Host " - $failure"
    }
    exit 1
}

Write-Host "Performance gate passed: $ProfileJson"
