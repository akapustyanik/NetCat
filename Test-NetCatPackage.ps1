param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$ZipPath = "",
    [bool]$VerifySelfUpdate = $false,
    [bool]$RequireCodeSigning = $false,
    [string]$TestInstallRoot = ""
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

function Get-ZipEntryContent {
    param(
        [string]$ArchivePath,
        [string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq $EntryName } | Select-Object -First 1
        if ($null -eq $entry) {
            return $null
        }

        $reader = New-Object System.IO.StreamReader($entry.Open())
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Assert-ManifestMetadata {
    param(
        [string]$ManifestJson,
        [string]$ExpectedVersion
    )

    if ([string]::IsNullOrWhiteSpace($ManifestJson)) {
        throw "release-manifest.json is empty."
    }

    $manifest = $ManifestJson | ConvertFrom-Json
    if ($manifest.app -ne "NetCat") {
        throw "release-manifest.json contains unexpected app: $($manifest.app)"
    }
    if ([string]::IsNullOrWhiteSpace($manifest.version)) {
        throw "release-manifest.json is missing version."
    }
    if ([string]::IsNullOrWhiteSpace($manifest.runtime)) {
        throw "release-manifest.json is missing runtime."
    }
    $normalizedExpectedVersion = ($ExpectedVersion -split '\+', 2)[0]
    if ($manifest.version -ne $normalizedExpectedVersion) {
        throw "release-manifest.json version '$($manifest.version)' does not match package version '$ExpectedVersion'."
    }
    if ([string]::IsNullOrWhiteSpace($manifest.version_family)) {
        throw "release-manifest.json is missing version_family."
    }
    if ($manifest.version_family -ne $normalizedExpectedVersion) {
        throw "release-manifest.json version_family '$($manifest.version_family)' does not match package version '$ExpectedVersion'."
    }
    if ([string]::IsNullOrWhiteSpace($manifest.embedded_proxy_implementation)) {
        throw "release-manifest.json is missing embedded_proxy_implementation."
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.embedded_proxy_schema)) {
        throw "release-manifest.json is missing embedded_proxy_schema."
    }
    if ([string]::IsNullOrWhiteSpace($manifest.embedded_proxy_version_family)) {
        throw "release-manifest.json is missing embedded_proxy_version_family."
    }
    $expectedEmbeddedVersionFamily = "netcat-v$normalizedExpectedVersion+schema.$($manifest.embedded_proxy_schema)"
    if ($manifest.embedded_proxy_version_family -ne $expectedEmbeddedVersionFamily) {
        throw "release-manifest.json embedded_proxy_version_family '$($manifest.embedded_proxy_version_family)' does not match expected '$expectedEmbeddedVersionFamily'."
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

function Assert-AuthenticodeSigned {
    param([string]$Path)

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Executable is not code-signed: $Path (status: $($signature.Status))"
    }
}

function New-TestInstallWorkspace {
    param(
        [string]$WorkRoot,
        [string]$Prefix
    )

    $baseRoot = if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
        Join-Path $PSScriptRoot ".test-installs"
    }
    else {
        [System.IO.Path]::GetFullPath($WorkRoot)
    }

    New-Item -ItemType Directory -Path $baseRoot -Force | Out-Null
    return Join-Path $baseRoot ("$Prefix-" + [guid]::NewGuid().ToString("N"))
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Wait-TcpListener {
    param(
        [string]$TcpHost,
        [int]$Port,
        [int]$TimeoutSeconds = 20
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $client = [System.Net.Sockets.TcpClient]::new()
        try {
            $connectTask = $client.ConnectAsync($TcpHost, $Port)
            if ($connectTask.Wait(500) -and $client.Connected) {
                return $true
            }
        }
        catch {
        }
        finally {
            $client.Dispose()
        }

        Start-Sleep -Milliseconds 300
    }

    return $false
}

function Stop-NetCatProcessSafe {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            $Process.WaitForExit(5000) | Out-Null
        }
    }
    catch {
    }
}

function Invoke-SelfUpdateSmoke {
    param(
        [string]$SourceDir,
        [string]$ArchivePath,
        [string]$WorkRoot
    )

    $smokeRoot = New-TestInstallWorkspace -WorkRoot $WorkRoot -Prefix "self-update"
    $workDir = Join-Path $smokeRoot "NetCat"
    $archiveCopyPath = Join-Path $smokeRoot ([System.IO.Path]::GetFileName($ArchivePath))
    New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
    Copy-Item $SourceDir $workDir -Recurse
    Copy-Item $ArchivePath $archiveCopyPath -Force

    try {
        $updaterPath = Join-Path $workDir "updater\AmazTool.exe"
        Assert-PathExists $updaterPath "Smoke test updater is missing: $updaterPath"

        & $updaterPath upgrade $workDir $archiveCopyPath | Out-Null
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

        $updateCacheRoot = Join-Path $workDir "userdata\guiTemps\updates"
        if (Test-Path $updateCacheRoot) {
            $cachedPackages = @(Get-ChildItem -Path $updateCacheRoot -File -Filter "*.zip" -ErrorAction SilentlyContinue)
            if ($cachedPackages.Count -gt 1) {
                throw "Self-update smoke test left multiple cached update archives behind: $($cachedPackages.Name -join ', ')"
            }
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

function Invoke-TelegramLocalSocksSmoke {
    param(
        [string]$SourceDir,
        [string]$WorkRoot
    )

    $smokeRoot = New-TestInstallWorkspace -WorkRoot $WorkRoot -Prefix "telegram-socks"
    $workDir = Join-Path $smokeRoot "NetCat"
    $process = $null
    Copy-Item $SourceDir $workDir -Recurse

    try {
        $port = Get-FreeTcpPort
        $quickRulesPath = Join-Path $workDir "userdata\\guiConfigs\\quick-rules.json"
        $telegramConfigPath = Join-Path $workDir "userdata\\telegram-ws-proxy\\config.json"
        New-Item -ItemType Directory -Path (Split-Path -Parent $quickRulesPath) -Force | Out-Null
        New-Item -ItemType Directory -Path (Split-Path -Parent $telegramConfigPath) -Force | Out-Null

        $quickRules = @{
            DirectProcesses = @()
            DirectDomains = @()
            ProxyProcesses = @()
            ProxyDomains = @()
            BlockDomains = @()
            UseProxyDomainsPreset = $false
            ProxyOnlyMode = $false
            BypassPrivate = $true
            TelegramTrafficMode = "local-socks"
            RoutingId = $null
        } | ConvertTo-Json -Depth 4
        Set-Content -Path $quickRulesPath -Value $quickRules -Encoding UTF8

        $telegramConfig = @{
            SchemaVersion = 3
            Host = "127.0.0.1"
            Port = $port
            DcIps = @(
                "1:149.154.175.50",
                "2:149.154.167.220",
                "3:149.154.175.100",
                "4:149.154.167.91",
                "5:91.108.56.100"
            )
        } | ConvertTo-Json -Depth 4
        Set-Content -Path $telegramConfigPath -Value $telegramConfig -Encoding UTF8

        $appPath = Join-Path $workDir "NetCat.exe"
        $process = Start-Process -FilePath $appPath -WorkingDirectory $workDir -PassThru -WindowStyle Hidden

        if (-not (Wait-TcpListener -TcpHost "127.0.0.1" -Port $port -TimeoutSeconds 25)) {
            throw "Telegram SOCKS smoke test listener did not start on 127.0.0.1:$port"
        }

        Start-Sleep -Seconds 3
        if ($process.HasExited) {
            throw "Telegram SOCKS smoke test process exited prematurely with code $($process.ExitCode)"
        }

        $curlSucceeded = $false
        $curlErrors = @()
        foreach ($target in @("https://api.telegram.org", "https://149.154.167.220")) {
            $curlOutput = & curl.exe --socks5-hostname "127.0.0.1:$port" --max-time 45 --connect-timeout 25 -k -o NUL -D - $target 2>&1
            $curlText = ($curlOutput | Out-String)
            if ($LASTEXITCODE -eq 0 -and $curlText -match "HTTP/\d\.\d\s+(200|30[12378]|40[013])") {
                $curlSucceeded = $true
                break
            }

            $curlErrors += "target=$target exit=$LASTEXITCODE output=$curlText"
            Start-Sleep -Seconds 2
        }

        if (-not $curlSucceeded) {
            throw "Telegram SOCKS smoke test curl failed: $($curlErrors -join ' || ')"
        }

        $logDir = Join-Path $workDir "userdata\\guiLogs"
        if (-not (Test-Path $logDir)) {
            throw "Telegram SOCKS smoke test did not create guiLogs."
        }

        $latestLog = Get-ChildItem -Path $logDir -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($null -eq $latestLog) {
            throw "Telegram SOCKS smoke test did not create a log file."
        }

        $logTail = Get-Content $latestLog.FullName -Tail 200 -ErrorAction SilentlyContinue
        $logText = $logTail | Out-String
        if ($logText -match "IO_SharingViolation|App_DispatcherUnhandledException") {
            throw "Telegram SOCKS smoke test detected runtime error in log $($latestLog.Name): $logText"
        }
    }
    finally {
        Stop-NetCatProcessSafe -Process $process
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

$packageVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $OutputDir "NetCat.exe")).ProductVersion
if ([string]::IsNullOrWhiteSpace($packageVersion)) {
    throw "Failed to read ProductVersion from NetCat.exe"
}

$manifestPath = Join-Path $OutputDir "release-manifest.json"
Assert-ManifestMetadata -ManifestJson (Get-Content $manifestPath -Raw) -ExpectedVersion $packageVersion

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

    Assert-ManifestMetadata `
        -ManifestJson (Get-ZipEntryContent -ArchivePath $ZipPath -EntryName "NetCat/release-manifest.json") `
        -ExpectedVersion $packageVersion
}

if ($VerifySelfUpdate) {
    if ([string]::IsNullOrWhiteSpace($ZipPath)) {
        throw "VerifySelfUpdate requires -ZipPath."
    }

    Invoke-SelfUpdateSmoke -SourceDir $OutputDir -ArchivePath $ZipPath -WorkRoot $TestInstallRoot
}

Invoke-TelegramLocalSocksSmoke -SourceDir $OutputDir -WorkRoot $TestInstallRoot

if ($RequireCodeSigning) {
    foreach ($relativePath in @("NetCat.exe", "updater\\AmazTool.exe")) {
        Assert-AuthenticodeSigned -Path (Join-Path $OutputDir $relativePath)
    }
}

Write-Host "Smoke test passed: $OutputDir"
