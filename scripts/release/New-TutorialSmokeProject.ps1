[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourcePath,

    [Parameter(Mandatory)]
    [string]$DestinationDirectory,

    [Parameter(Mandatory)]
    [string]$CompatibilityPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$source = (Resolve-Path -LiteralPath $SourcePath).Path
$compatibility = Get-Content -Raw -LiteralPath (Resolve-Path -LiteralPath $CompatibilityPath) | ConvertFrom-Json
$languageVersion = [string]$compatibility.language.default
$stdlibVersion = [string]$compatibility.stdlib
if ([string]::IsNullOrWhiteSpace($languageVersion) -or [string]::IsNullOrWhiteSpace($stdlibVersion))
{
    throw "Compiler compatibility metadata must declare language.default and stdlib."
}

$projectRoot = [IO.Path]::GetFullPath($DestinationDirectory)
$sourceRoot = Join-Path $projectRoot "src"
New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
$example = Join-Path $sourceRoot "main.eidos"
Copy-Item -LiteralPath $source -Destination $example -Force

$manifest = @"
manifestSchema = 3
sourceRoots = ["src"]

[language]
version = "$languageVersion"

[package]
name = "dev.eidos.release-smoke"
version = "0.1.0"

[dependencies]
std = "$stdlibVersion"
"@
[IO.File]::WriteAllText(
    (Join-Path $projectRoot "eidos.toml"),
    $manifest,
    [Text.UTF8Encoding]::new($false))

Write-Output $example
