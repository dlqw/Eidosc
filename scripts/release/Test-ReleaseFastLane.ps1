[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$script = Join-Path $PSScriptRoot "Get-ReleaseTestMode.ps1"
$same = & $script -Component eidosup -BaselineRef HEAD -CandidateRef HEAD
if ($same.mode -cne "fast" -or $same.baselineRoot -cne $same.candidateRoot)
{
    throw "An identical Eidosup source set must select the fast lane."
}

$changed = & $script -Component eidosup -BaselineRef origin/main -CandidateRef HEAD
if ($changed.mode -cne "full" -or $changed.reason -cne "artifact-source-set-changed")
{
    throw "A changed Eidosup source set must select the full lane."
}

$missing = & $script -Component eidosup -BaselineRef refs/tags/does-not-exist -CandidateRef HEAD
if ($missing.mode -cne "full" -or $missing.reason -cne "baseline-unverifiable")
{
    throw "An unverifiable baseline must fail closed to the full lane."
}

Write-Host "Release fast-lane proof tests passed."
