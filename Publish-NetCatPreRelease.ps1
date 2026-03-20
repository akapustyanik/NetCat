param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Label = "local",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = if (Test-Path (Join-Path $PSScriptRoot "my-vpn-zapret-wpf")) {
    $PSScriptRoot
} else {
    Split-Path -Parent $PSScriptRoot
}

function Get-NetCatVersion {
    param([string]$PropsPath)

    [xml]$props = Get-Content $PropsPath
    $versionNode = $props.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionNode)) {
        throw "Failed to read Version from $PropsPath"
    }

    return $versionNode.Trim()
}

function Normalize-Label {
    param([string]$InputLabel)

    $normalized = [Regex]::Replace($InputLabel, '[^a-zA-Z0-9._-]+', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return "local"
    }

    return $normalized
}

Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = Get-NetCatVersion -PropsPath (Join-Path $repoRoot "my-vpn-zapret-wpf\Directory.Build.props")
    }

    $normalizedLabel = Normalize-Label -InputLabel $Label
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $baseName = "NetCat-v$Version-$normalizedLabel-$stamp"
    $preReleaseRoot = Join-Path $repoRoot "pre-releases"
    $outputDir = Join-Path $preReleaseRoot $baseName
    $zipPath = "$outputDir.zip"

    New-Item -ItemType Directory -Path $preReleaseRoot -Force | Out-Null

    & (Join-Path $repoRoot "Publish-NetCatRelease.ps1") `
        -Configuration $Configuration `
        -Runtime $Runtime `
        -OutputDir $outputDir `
        -ZipPath $zipPath

    Write-Host "Pre-release folder: $outputDir"
    Write-Host "Pre-release zip: $zipPath"
}
finally {
    Pop-Location
}
