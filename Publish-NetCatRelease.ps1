param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "",
    [string]$ZipPath = "",
    [string]$SourcePublishDir = ""
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

function Get-TargetFramework {
    param([string]$ProjectPath)

    [xml]$project = Get-Content $ProjectPath
    $frameworkNode = $project.Project.PropertyGroup.TargetFramework | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($frameworkNode)) {
        throw "Failed to read TargetFramework from $ProjectPath"
    }

    return $frameworkNode.Trim()
}

Push-Location $repoRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-cli"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $projectPath = Join-Path $repoRoot "my-vpn-zapret-wpf\v2rayN\v2rayN.csproj"
    $targetFramework = Get-TargetFramework -ProjectPath $projectPath
    $stagingDir = Join-Path $repoRoot ".publish-staging"
    $publishSourceDir = if ([string]::IsNullOrWhiteSpace($SourcePublishDir)) {
        Join-Path $repoRoot "my-vpn-zapret-wpf\v2rayN\bin\$Configuration\$targetFramework\$Runtime\publish"
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $repoRoot $SourcePublishDir))
    }

    $version = Get-NetCatVersion -PropsPath (Join-Path $repoRoot "my-vpn-zapret-wpf\Directory.Build.props")
    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $OutputDir = ".\artifacts\NetCat-releaseV$version"
    }
    if ([string]::IsNullOrWhiteSpace($ZipPath)) {
        $ZipPath = ".\artifacts\NetCat-releaseV$version.zip"
    }

    $OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
    $ZipPath = [System.IO.Path]::GetFullPath($ZipPath)

    if (Test-Path $OutputDir) {
        Remove-Item $OutputDir -Recurse -Force
    }

    if (Test-Path $ZipPath) {
        Remove-Item $ZipPath -Force
    }

    if (Test-Path $stagingDir) {
        Remove-Item $stagingDir -Recurse -Force
    }
    if ([string]::IsNullOrWhiteSpace($SourcePublishDir)) {
        if (Test-Path $publishSourceDir) {
            Remove-Item $publishSourceDir -Recurse -Force
        }
        dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained false
        if (-not (Test-Path $publishSourceDir)) {
            throw "Publish output was not created: $publishSourceDir"
        }
    }
    elseif (-not (Test-Path $publishSourceDir)) {
        throw "Source publish directory was not found: $publishSourceDir"
    }

    Copy-Item $publishSourceDir $stagingDir -Recurse
    Copy-Item $stagingDir $OutputDir -Recurse

    $userDataDir = Join-Path $OutputDir "userdata"
    $defaultConfigPath = Join-Path $repoRoot "my-vpn-zapret\resources\v2rayn\guiConfigs\guiNConfig.json"

    [System.IO.Directory]::CreateDirectory($userDataDir) | Out-Null
    Get-ChildItem -Path $OutputDir -File -Filter "AmazTool*" -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item (Join-Path $OutputDir "guiNConfig.json") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $OutputDir "updater\guiLogs") -Recurse -Force -ErrorAction SilentlyContinue
    if ((Test-Path $defaultConfigPath) -and (-not (Test-Path (Join-Path $userDataDir "guiNConfig.json")))) {
        Copy-Item $defaultConfigPath (Join-Path $userDataDir "guiNConfig.json") -Force
    }

    $manifest = @{
        app = "NetCat"
        version = $version
        runtime = $Runtime
        configuration = $Configuration
        built_at = (Get-Date).ToString("o")
        package_root = [System.IO.Path]::GetFileName($OutputDir)
    } | ConvertTo-Json
    Set-Content -Path (Join-Path $OutputDir "release-manifest.json") -Value $manifest -Encoding UTF8

    Compress-Archive -Path $OutputDir -DestinationPath $ZipPath -Force
    Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "Published folder: $OutputDir"
    Write-Host "Release zip: $ZipPath"
}
finally {
    Pop-Location
}
