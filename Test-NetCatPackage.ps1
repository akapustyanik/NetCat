param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$ZipPath = "",
    [bool]$VerifySelfUpdate = $false
)

$ErrorActionPreference = "Stop"

function Assert-PathExists {
    param(
        [string]$Path,
        [string]$Message
    )

    if (-not (Test-Path $Path)) {
        throw $Message
    }
}

function Test-ZipContainsEntry {
    param(
        [string]$ArchivePath,
        [string]$ExpectedEntry
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        return $zip.Entries.FullName -contains $ExpectedEntry
    }
    finally {
        $zip.Dispose()
    }
}

function Get-StaleUpdaterDirs {
    param([string]$PackageRoot)

    $tempRoot = Join-Path $PackageRoot "userdata\guiTemps"
    if (-not (Test-Path $tempRoot)) {
        return @()
    }

    return Get-ChildItem -Path $tempRoot -Directory -Filter "updater-*" -ErrorAction SilentlyContinue
}

function Invoke-SelfUpdateSmoke {
    param(
        [string]$SourceDir,
        [string]$ArchivePath
    )

    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("NetCat-smoke-" + [guid]::NewGuid().ToString("N"))
    $workDir = Join-Path $smokeRoot "NetCat"
    New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
    Copy-Item $SourceDir $workDir -Recurse

    try {
        $updaterPath = Join-Path $workDir "updater\AmazTool.exe"
        Assert-PathExists $updaterPath "Smoke test updater is missing: $updaterPath"

        & $updaterPath upgrade $workDir $ArchivePath | Out-Null
        Start-Sleep -Seconds 8

        $appPath = Join-Path $workDir "NetCat.exe"
        Assert-PathExists $appPath "Self-update smoke test did not leave NetCat.exe in place."

        $backupPath = "$appPath.tmp"
        if (Test-Path $backupPath) {
            throw "Self-update smoke test left rollback file behind: $backupPath"
        }

        $staleUpdaterDirs = @(Get-StaleUpdaterDirs -PackageRoot $workDir)
        if ($staleUpdaterDirs.Count -gt 0) {
            throw "Self-update smoke test left staged updater directories behind: $($staleUpdaterDirs.Name -join ', ')"
        }
    }
    finally {
        $smokeAppPath = Join-Path $workDir "NetCat.exe"
        Get-Process NetCat -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                if ($_.Path -and [System.StringComparer]::OrdinalIgnoreCase.Equals($_.Path, $smokeAppPath)) {
                    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
            }
        }

        Remove-Item $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
Assert-PathExists $OutputDir "Package folder not found: $OutputDir"

$requiredPaths = @(
    "NetCat.exe",
    "updater\AmazTool.exe",
    "userdata\guiNConfig.json",
    "release-manifest.json",
    "bin\xray\xray.exe",
    "bin\sing_box\sing-box.exe",
    "bin\geoip.dat",
    "bin\geosite.dat"
)

foreach ($relativePath in $requiredPaths) {
    Assert-PathExists (Join-Path $OutputDir $relativePath) "Package is missing required path: $relativePath"
}

$staleUpdaterDirs = @(Get-StaleUpdaterDirs -PackageRoot $OutputDir)
if ($staleUpdaterDirs.Count -gt 0) {
    throw "Package already contains staged updater directories: $($staleUpdaterDirs.Name -join ', ')"
}

$zapretDir = Join-Path $OutputDir "zapret"
if (Test-Path $zapretDir) {
    $staleHiddenLaunchers = Get-ChildItem -Path $zapretDir -File -Filter "zapret-hidden-*.bat" -ErrorAction SilentlyContinue
    if ($staleHiddenLaunchers.Count -gt 0) {
        throw "Package already contains stale zapret launcher files: $($staleHiddenLaunchers.Name -join ', ')"
    }
}

if (-not [string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = [System.IO.Path]::GetFullPath($ZipPath)
    Assert-PathExists $ZipPath "Package zip not found: $ZipPath"

    foreach ($entry in @("NetCat/NetCat.exe", "NetCat/updater/AmazTool.exe", "NetCat/release-manifest.json")) {
        if (-not (Test-ZipContainsEntry -ArchivePath $ZipPath -ExpectedEntry $entry)) {
            throw "Package zip is missing required entry: $entry"
        }
    }
}

if ($VerifySelfUpdate) {
    if ([string]::IsNullOrWhiteSpace($ZipPath)) {
        throw "VerifySelfUpdate requires -ZipPath."
    }

    Invoke-SelfUpdateSmoke -SourceDir $OutputDir -ArchivePath $ZipPath
}

Write-Host "Smoke test passed: $OutputDir"
