param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = ".\artifacts\NetCat-release",
    [string]$ZipPath = ".\artifacts\NetCat-release.zip"
)

$ErrorActionPreference = "Stop"

$repoRoot = if (Test-Path (Join-Path $PSScriptRoot "my-vpn-zapret-wpf")) {
    $PSScriptRoot
} else {
    Split-Path -Parent $PSScriptRoot
}

Push-Location $repoRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-cli"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
    $ZipPath = [System.IO.Path]::GetFullPath($ZipPath)

    if (Test-Path $OutputDir) {
        Remove-Item $OutputDir -Recurse -Force
    }

    if (Test-Path $ZipPath) {
        Remove-Item $ZipPath -Force
    }

    dotnet publish .\my-vpn-zapret-wpf\v2rayN\v2rayN.csproj -c $Configuration -r $Runtime --self-contained false -o $OutputDir
    if (-not (Test-Path $OutputDir)) {
        throw "Publish output was not created: $OutputDir"
    }

    Get-ChildItem -Path $OutputDir -File -Filter "AmazTool*" -ErrorAction SilentlyContinue | Remove-Item -Force

    Compress-Archive -Path $OutputDir -DestinationPath $ZipPath -Force

    Write-Host "Published folder: $OutputDir"
    Write-Host "Release zip: $ZipPath"
}
finally {
    Pop-Location
}
