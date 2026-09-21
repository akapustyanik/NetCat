param(
    [string]$Version = '1.0.0-beta',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    Write-Host "=== Starting NetCat Release Build: $Version ===" -ForegroundColor Cyan

    if (-not (Test-Path -LiteralPath 'bin/modules.lock.json')) { throw 'modules.lock.json not found. Run scripts/Fetch-OfficialModules.ps1' }
    if (-not (Test-Path -LiteralPath 'bin/tg-runtime/NetCat.Telegram.exe')) { throw 'Telegram runtime not found. Run scripts/Fetch-TelegramHeadless.ps1' }
    if (-not (Test-Path -LiteralPath 'bin/geoip/geoip.dat') -or -not (Test-Path -LiteralPath 'bin/geosite/geosite.dat')) { throw 'Geodata not found. Run scripts/Fetch-Geodata.ps1' }

    if (-not $SkipTests) {
        Write-Host "Running dotnet clean..." -ForegroundColor Yellow
        dotnet clean NetCat.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw 'dotnet clean failed.' }

        Write-Host "Running dotnet restore..." -ForegroundColor Yellow
        dotnet restore NetCat.sln
        if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

        Write-Host "Running full test suite..." -ForegroundColor Yellow
        dotnet test NetCat.sln -c Release --no-restore --logger "trx;LogFileName=release-$Version.trx"
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }

    $taskDestination = Join-Path $taskRoot "artifacts/NetCat-$Version"
    $taskOutput = Join-Path $taskRoot ("artifacts/.build-" + [guid]::NewGuid().ToString('N'))

    Write-Host "Publishing NetCat.UI to $taskOutput..." -ForegroundColor Yellow
    dotnet publish src/NetCat.UI/NetCat.UI.csproj -c Release -r win-x64 --self-contained true -o $taskOutput -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    Write-Host "Copying modules and assets..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force (Join-Path $taskOutput 'modules') | Out-Null
    Copy-Item -Path (Join-Path $taskRoot 'bin/*') -Destination (Join-Path $taskOutput 'modules') -Recurse -Force

    Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $taskOutput 'README.md') -Force
    if (Test-Path -LiteralPath (Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $taskOutput 'THIRD_PARTY_NOTICES.md') -Force
    }

    New-Item -ItemType Directory -Force (Join-Path $taskOutput 'docs') | Out-Null
    Copy-Item -Path (Join-Path $taskRoot 'docs/*.md') -Destination (Join-Path $taskOutput 'docs') -Force

    Write-Host "Writing package manifest..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'Write-PackageManifest.ps1') -Folder $taskOutput -Version $Version

    Write-Host "Running smoke test on release binary..." -ForegroundColor Yellow
    $exePath = Join-Path $taskOutput 'NetCat.exe'
    $prevCompat = $env:__COMPAT_LAYER
    try {
        $env:__COMPAT_LAYER = 'RunAsInvoker'
        $smokeProc = Start-Process -FilePath $exePath -ArgumentList '--smoke' -PassThru -Wait
        if ($smokeProc.ExitCode -ne 0) {
            throw "Smoke test failed with ExitCode $($smokeProc.ExitCode)"
        }
        Write-Host "Smoke test passed with ExitCode 0." -ForegroundColor Green
    } finally {
        $env:__COMPAT_LAYER = $prevCompat
    }

    if (Test-Path -LiteralPath $taskDestination) {
        Remove-Item -LiteralPath $taskDestination -Recurse -Force
    }
    Move-Item -LiteralPath $taskOutput -Destination $taskDestination
    $taskOutput = $taskDestination

    $zipName = "artifacts/NetCat-v$Version-win-x64.zip"
    $zipPath = Join-Path $taskRoot $zipName
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    if (Test-Path -LiteralPath ($zipPath + '.sha256')) {
        Remove-Item -LiteralPath ($zipPath + '.sha256') -Force
    }

    Write-Host "Packaging portable zip to $zipPath..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'Pack-Portable.ps1') -Folder $taskOutput -Archive $zipPath -TestBuild

    Write-Host "Verifying generated zip..." -ForegroundColor Yellow
    $shaContent = Get-Content -Raw -LiteralPath ($zipPath + '.sha256')
    Write-Host "SHA256: $shaContent" -ForegroundColor Green

    # Test extraction
    $testExtractDir = Join-Path $taskRoot ("artifacts/.extract-test-" + [guid]::NewGuid().ToString('N'))
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $testExtractDir)
        if (-not (Test-Path -LiteralPath (Join-Path $testExtractDir 'NetCat.exe'))) {
            throw "Zip verification failed: NetCat.exe missing in extracted zip."
        }
        Write-Host "Zip extraction test passed." -ForegroundColor Green
    } finally {
        if (Test-Path -LiteralPath $testExtractDir) {
            Remove-Item -LiteralPath $testExtractDir -Recurse -Force
        }
    }

    Write-Host "=== Build Completed Successfully! ===" -ForegroundColor Green
    Write-Host "Release folder: $taskOutput"
    Write-Host "Archive: $zipPath"
} finally {
    Pop-Location
}
