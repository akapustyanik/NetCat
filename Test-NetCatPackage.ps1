param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$ZipPath = ""
)

$ErrorActionPreference = "Stop"

Write-Host "Validating NetCat release package layout..." -ForegroundColor Cyan

# 1. Check OutputDir
if (-not (Test-Path $OutputDir)) {
    throw "Output directory does not exist: $OutputDir"
}

$exePath = Join-Path $OutputDir "NetCat.exe"
if (-not (Test-Path $exePath)) {
    throw "NetCat.exe not found in output directory: $OutputDir"
}

$manifestPath = Join-Path $OutputDir "release-manifest.json"
if (-not (Test-Path $manifestPath)) {
    throw "release-manifest.json not found in output directory: $OutputDir"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.app -ne "NetCat") {
    throw "Invalid app in manifest: $($manifest.app)"
}
if ([string]::IsNullOrWhiteSpace($manifest.version)) {
    throw "Manifest is missing version."
}

Write-Host "  [OK] NetCat.exe and valid release-manifest.json present (v$($manifest.version))" -ForegroundColor Green

# 2. Check ZipPath
if (-not [string]::IsNullOrWhiteSpace($ZipPath)) {
    if (-not (Test-Path $ZipPath)) {
        throw "ZIP archive does not exist: $ZipPath"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $hasExe = ($zip.Entries | Where-Object { $_.FullName -eq "NetCat.exe" } | Measure-Object).Count -gt 0
        if (-not $hasExe) {
            throw "ZIP archive does not contain NetCat.exe"
        }

        $hasManifest = ($zip.Entries | Where-Object { $_.FullName -eq "release-manifest.json" } | Measure-Object).Count -gt 0
        if (-not $hasManifest) {
            throw "ZIP archive does not contain release-manifest.json"
        }
    }
    finally {
        $zip.Dispose()
    }

    Write-Host "  [OK] ZIP archive contains required entries" -ForegroundColor Green

    # 3. Check SHA256 file
    $sha256Path = "$ZipPath.sha256"
    if (Test-Path $sha256Path) {
        $expectedHash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $shaFileContent = (Get-Content $sha256Path -Raw).Trim()
        if (-not $shaFileContent.StartsWith($expectedHash)) {
            throw "SHA256 in $sha256Path does not match computed hash: $expectedHash"
        }
        Write-Host "  [OK] SHA-256 checksum verified" -ForegroundColor Green
    }
}

Write-Host "Package validation passed successfully!" -ForegroundColor Green
