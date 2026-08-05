[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$script = Join-Path $PSScriptRoot "Get-ReleaseTestMode.ps1"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$same = & $script -Component eidosup -BaselineRef HEAD -CandidateRef HEAD
if ($same.mode -cne "fast" -or $same.baselineRoot -cne $same.candidateRoot)
{
    throw "An identical Eidosup source set must select the fast lane."
}

& git -C $repositoryRoot rev-parse --verify "HEAD^`{commit`}" 2>$null | Out-Null
if ($LASTEXITCODE -eq 0)
{
    $changed = & $script -Component eidosup -BaselineRef HEAD^ -CandidateRef HEAD
    if ($changed.mode -cne "full" -or $changed.reason -cne "artifact-source-set-changed")
    {
        throw "A changed Eidosup source set must select the full lane."
    }
}
else
{
    Write-Host "Shallow checkout has no parent commit; changed-source proof is skipped and release selection remains fail-closed."
}

$missing = & $script -Component eidosup -BaselineRef refs/tags/does-not-exist -CandidateRef HEAD
if ($missing.mode -cne "full" -or $missing.reason -cne "baseline-unverifiable")
{
    throw "An unverifiable baseline must fail closed to the full lane."
}

Write-Host "Release fast-lane proof tests passed."
