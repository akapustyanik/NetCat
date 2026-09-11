param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipSmoke
)

$ErrorActionPreference = "Stop"

$publishScript = Join-Path $PSScriptRoot "Publish-NetCatRelease.ps1"
if (-not (Test-Path $publishScript)) {
    throw "Publish script not found: $publishScript"
}

Write-Host "Starting local NetCat release build..." -ForegroundColor Cyan
& $publishScript -Configuration $Configuration -Runtime $Runtime -RunSmokeTest:(-not $SkipSmoke)
