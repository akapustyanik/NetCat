$ErrorActionPreference = "Stop"

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = ".\artifacts\NetCat-release",
    [string]$ZipPath = ".\artifacts\NetCat-release.zip"
)

$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-cli"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

    if (Test-Path $OutputDir) {
        Remove-Item $OutputDir -Recurse -Force
    }

    if (Test-Path $ZipPath) {
        Remove-Item $ZipPath -Force
    }

    dotnet publish .\my-vpn-zapret-wpf\v2rayN\v2rayN.csproj -c $Configuration -r $Runtime --self-contained false -o $OutputDir

    $amazToolBuildDir = Join-Path $repoRoot "my-vpn-zapret-wpf\AmazTool\bin\$Configuration\net8.0\$Runtime"
    $amazToolFiles = @(
        "AmazTool.exe",
        "AmazTool.dll",
        "AmazTool.deps.json",
        "AmazTool.runtimeconfig.json"
    )

    foreach ($file in $amazToolFiles) {
        $source = Join-Path $amazToolBuildDir $file
        if (Test-Path $source) {
            Copy-Item $source $OutputDir -Force
        }
    }

    Get-ChildItem -Path $amazToolBuildDir -Directory -Filter "zh-*" -ErrorAction SilentlyContinue | ForEach-Object {
        $targetDir = Join-Path $OutputDir $_.Name
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        Copy-Item (Join-Path $_.FullName "AmazTool.resources.dll") $targetDir -Force
    }

    Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $ZipPath -Force

    Write-Host "Published folder: $OutputDir"
    Write-Host "Release zip: $ZipPath"
}
finally {
    Pop-Location
}
