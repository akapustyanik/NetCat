param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "",
    [string]$ZipPath = "",
    [bool]$SelfContained = $true,
    [bool]$RunSmokeTest = $true
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$version = "0.5.0"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\publish\NetCat"
}
if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = Join-Path $repoRoot "artifacts\NetCat-v$version.zip"
}

$artifactsDir = Split-Path -Parent $ZipPath
if (-not (Test-Path $artifactsDir)) {
    New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " Building NetCat v$version Release ($Runtime)" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Publish WPF App
Write-Host "Publishing NetCat.UI ($Configuration, $Runtime, SelfContained: $SelfContained)..."
$projectPath = Join-Path $repoRoot "src\NetCat.UI\NetCat.UI.csproj"

$publishArgs = @(
    "publish",
    $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" }),
    "-o", $OutputDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# 2. Bundle bin/, assets/, and data/ layouts
Write-Host "Bundling assets, runtime data and modules..."
$sourceBinDir = Join-Path $repoRoot "bin"
$targetBinDir = Join-Path $OutputDir "bin"
if (-not (Test-Path $targetBinDir)) {
    New-Item -ItemType Directory -Path $targetBinDir -Force | Out-Null
}
if (Test-Path $sourceBinDir) {
    Copy-Item "$sourceBinDir\*" $targetBinDir -Recurse -Force
}

$sourceDataDir = Join-Path $repoRoot "data"
$targetDataDir = Join-Path $OutputDir "data"
if (-not (Test-Path $targetDataDir)) {
    New-Item -ItemType Directory -Path $targetDataDir -Force | Out-Null
}
if (Test-Path $sourceDataDir) {
    Copy-Item "$sourceDataDir\*" $targetDataDir -Recurse -Force
}

$sourceAssetsDir = Join-Path $repoRoot "assets"
$targetAssetsDir = Join-Path $OutputDir "assets"
if (-not (Test-Path $targetAssetsDir)) {
    New-Item -ItemType Directory -Path $targetAssetsDir -Force | Out-Null
}
if (Test-Path $sourceAssetsDir) {
    Copy-Item "$sourceAssetsDir\*" $targetAssetsDir -Recurse -Force
}

# 3. Create release-manifest.json
$manifestObj = @{
    app = "NetCat"
    version = $version
    runtime = $Runtime
    version_family = $version
    embedded_proxy_implementation = "tg-ws-proxy-main"
    embedded_proxy_schema = 1
    embedded_proxy_version_family = "netcat-v$version+schema.1"
    built_at = (Get-Date).ToUniversalTime().ToString("o")
}
$manifestJson = $manifestObj | ConvertTo-Json -Depth 4
$manifestPath = Join-Path $OutputDir "release-manifest.json"
Set-Content -Path $manifestPath -Value $manifestJson -Encoding UTF8

# 4. Create ZIP Archive
Write-Host "Creating release archive: $ZipPath ..."
if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($OutputDir, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# 5. Generate SHA-256 Checksum
Write-Host "Generating SHA-256 checksum..."
$hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$zipFileName = Split-Path -Leaf $ZipPath
$sha256FilePath = "$ZipPath.sha256"
$sha256Content = "$hash  $zipFileName"
Set-Content -Path $sha256FilePath -Value $sha256Content -Encoding ASCII

Write-Host "Package created:" -ForegroundColor Green
Write-Host "  Archive: $ZipPath"
Write-Host "  SHA-256: $hash"
Write-Host "  Checksum file: $sha256FilePath"

# 6. Run Smoke Tests if requested
if ($RunSmokeTest) {
    $smokeScript = Join-Path $repoRoot "Test-NetCatPackage.ps1"
    if (Test-Path $smokeScript) {
        Write-Host "Running package validation smoke test..." -ForegroundColor Yellow
        & $smokeScript -OutputDir $OutputDir -ZipPath $ZipPath
    }
}

Write-Host "NetCat v$version release published successfully!" -ForegroundColor Green
