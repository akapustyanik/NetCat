param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "",
    [string]$ZipPath = "",
    [string]$SourcePublishDir = "",
    [bool]$RunSmokeTest = $true,
    [bool]$VerifySelfUpdateSmoke = $false,
    [bool]$SignBinaries = $true,
    [bool]$RequireCodeSigning = $false,
    [string]$CodeSigningPfxPath = $env:NETCAT_CODESIGN_PFX,
    [string]$CodeSigningPassword = $env:NETCAT_CODESIGN_PASSWORD,
    [string]$CodeSigningThumbprint = $env:NETCAT_CODESIGN_THUMBPRINT,
    [string]$CodeSigningSubject = $env:NETCAT_CODESIGN_SUBJECT,
    [string]$CodeSigningStoreLocation = $env:NETCAT_CODESIGN_STORE_LOCATION,
    [string]$CodeSigningStoreName = $env:NETCAT_CODESIGN_STORE_NAME,
    [string]$TimestampUrl = $env:NETCAT_CODESIGN_TIMESTAMP_URL,
    [string]$SignToolPath = $env:NETCAT_SIGNTOOL_PATH
)

$ErrorActionPreference = "Stop"

$repoRoot = if (Test-Path (Join-Path $PSScriptRoot "my-vpn-zapret-wpf")) {
    $PSScriptRoot
} else {
    Split-Path -Parent $PSScriptRoot
}

function Get-NetCatVersion {
    param([string]$PropsPath)

    $propertyMap = Get-ResolvedPropsMap -PropsPath $PropsPath
    $versionNode = $propertyMap["Version"]
    if ([string]::IsNullOrWhiteSpace($versionNode)) {
        throw "Failed to read Version from $PropsPath"
    }

    return $versionNode.Trim()
}

function Get-ResolvedPropsMap {
    param([string]$PropsPath)

    [xml]$props = Get-Content $PropsPath
    $propertyMap = @{}

    foreach ($propertyGroup in @($props.Project.PropertyGroup)) {
        foreach ($node in @($propertyGroup.ChildNodes)) {
            if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) {
                continue
            }

            $propertyMap[$node.Name] = $node.InnerText.Trim()
        }
    }

    $tokenPattern = '\$\(([A-Za-z0-9_.-]+)\)'
    for ($iteration = 0; $iteration -lt 8; $iteration++) {
        $changed = $false
        foreach ($key in @($propertyMap.Keys)) {
            $value = [string]$propertyMap[$key]
            $resolvedValue = [System.Text.RegularExpressions.Regex]::Replace($value, $tokenPattern, {
                    param($match)

                    $tokenKey = $match.Groups[1].Value
                    if ($propertyMap.ContainsKey($tokenKey)) {
                        return [string]$propertyMap[$tokenKey]
                    }

                    return $match.Value
                })

            if ($resolvedValue -ne $value) {
                $propertyMap[$key] = $resolvedValue
                $changed = $true
            }
        }

        if (-not $changed) {
            break
        }
    }

    return $propertyMap
}

function Get-NetCatVersioningMetadata {
    param([string]$PropsPath)

    $propertyMap = Get-ResolvedPropsMap -PropsPath $PropsPath

    $versionFamily = $propertyMap["NetCatVersionFamily"]
    $embeddedImplementation = $propertyMap["NetCatTelegramWsProxyImplementation"]
    $embeddedSchemaVersion = $propertyMap["NetCatTelegramWsProxySchemaVersion"]
    $embeddedVersionFamily = $propertyMap["NetCatTelegramWsProxyVersionFamily"]

    if ([string]::IsNullOrWhiteSpace($versionFamily)) {
        $versionFamily = Get-NetCatVersion -PropsPath $PropsPath
    }
    if ([string]::IsNullOrWhiteSpace($embeddedImplementation)) {
        $embeddedImplementation = "tg-ws-proxy-main"
    }
    if ([string]::IsNullOrWhiteSpace($embeddedSchemaVersion)) {
        $embeddedSchemaVersion = "1"
    }
    if ([string]::IsNullOrWhiteSpace($embeddedVersionFamily)) {
        $embeddedVersionFamily = "netcat-v$versionFamily+schema.$embeddedSchemaVersion"
    }

    return @{
        VersionFamily = $versionFamily.Trim()
        EmbeddedImplementation = $embeddedImplementation.Trim()
        EmbeddedSchemaVersion = [int]$embeddedSchemaVersion
        EmbeddedVersionFamily = $embeddedVersionFamily.Trim()
    }
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

function Resolve-PublishOutputDirectory {
    param(
        [string]$RepoRoot,
        [string]$Configuration,
        [string]$TargetFramework,
        [string]$Runtime,
        [string]$ExplicitSourcePublishDir
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitSourcePublishDir)) {
        return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $ExplicitSourcePublishDir))
    }

    $candidateDirs = @(
        (Join-Path $RepoRoot "my-vpn-zapret-wpf\v2rayN\bin\$Configuration\$TargetFramework\$Runtime\publish"),
        (Join-Path $RepoRoot "my-vpn-zapret-wpf\v2rayN\bin\$Configuration\$TargetFramework\$Runtime"),
        (Join-Path $RepoRoot "my-vpn-zapret-wpf\v2rayN\bin\$Configuration\$TargetFramework")
    )

    foreach ($candidate in $candidateDirs) {
        if (Test-Path (Join-Path $candidate "NetCat.exe")) {
            return $candidate
        }
    }

    return $candidateDirs[0]
}

function Test-CompleteBinLayout {
    param([string]$PackageRoot)

    $requiredPaths = @(
        "bin\xray\xray.exe",
        "bin\sing_box\sing-box.exe",
        "bin\geoip.dat",
        "bin\geosite.dat"
    )

    foreach ($relativePath in $requiredPaths) {
        if (-not (Test-Path (Join-Path $PackageRoot $relativePath))) {
            return $false
        }
    }

    return $true
}

function Resolve-BundledBinSourceDirectory {
    param([string]$RepoRoot)

    $searchRoots = @(
        (Join-Path $RepoRoot "artifacts"),
        (Join-Path $RepoRoot "pre-releases")
    ) | Where-Object { Test-Path $_ }

    $candidateRoots = foreach ($searchRoot in $searchRoots) {
        Get-ChildItem -Path $searchRoot -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object { Test-CompleteBinLayout -PackageRoot $_.FullName }
    }

    return $candidateRoots |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Ensure-BundledBinLayout {
    param(
        [string]$RepoRoot,
        [string]$OutputDir
    )

    if (Test-CompleteBinLayout -PackageRoot $OutputDir) {
        return
    }

    $binSourceDir = Resolve-BundledBinSourceDirectory -RepoRoot $RepoRoot
    if ([string]::IsNullOrWhiteSpace($binSourceDir)) {
        throw "Failed to find a complete bundled bin source directory under artifacts/ or pre-releases/."
    }

    $sourceBinPath = Join-Path $binSourceDir "bin"
    $targetBinPath = Join-Path $OutputDir "bin"
    if (Test-Path $targetBinPath) {
        Remove-Item $targetBinPath -Recurse -Force
    }

    Copy-Item $sourceBinPath $targetBinPath -Recurse
}

function Copy-UpdaterBundle {
    param(
        [string]$RepoRoot,
        [string]$Configuration,
        [string]$Runtime,
        [string]$OutputDir
    )

    $amazToolDir = Join-Path $RepoRoot "my-vpn-zapret-wpf\AmazTool\bin\$Configuration\net8.0\$Runtime"
    if (-not (Test-Path $amazToolDir)) {
        throw "AmazTool build output was not found: $amazToolDir"
    }

    $updaterDir = Join-Path $OutputDir "updater"
    New-Item -ItemType Directory -Path $updaterDir -Force | Out-Null

    Get-ChildItem -Path $amazToolDir -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\guiLogs\\' } |
        ForEach-Object {
            $relativePath = [System.IO.Path]::GetRelativePath($amazToolDir, $_.FullName)
            $targetPath = Join-Path $updaterDir $relativePath
            $targetParent = Split-Path -Parent $targetPath
            if (-not [string]::IsNullOrWhiteSpace($targetParent)) {
                New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
            }

            Copy-Item $_.FullName $targetPath -Force
        }
}

function Resolve-SignToolPath {
    param([string]$ExplicitPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates += $ExplicitPath
    }

    $candidates += @(
        "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe",
        "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\signtool.exe"
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    return $null
}

function Resolve-CodeSigningStoreLocation {
    param([string]$StoreLocation)

    if ([string]::IsNullOrWhiteSpace($StoreLocation)) {
        return "CurrentUser"
    }

    if ($StoreLocation -in @("CurrentUser", "LocalMachine")) {
        return $StoreLocation
    }

    throw "Unsupported code-signing store location: $StoreLocation"
}

function Resolve-CodeSigningStoreName {
    param([string]$StoreName)

    if ([string]::IsNullOrWhiteSpace($StoreName)) {
        return "My"
    }

    return $StoreName
}

function Resolve-CodeSigningCertificate {
    param(
        [string]$Thumbprint,
        [string]$Subject,
        [string]$StoreLocation,
        [string]$StoreName
    )

    if ([string]::IsNullOrWhiteSpace($Thumbprint) -and [string]::IsNullOrWhiteSpace($Subject)) {
        return $null
    }

    $resolvedStoreLocation = Resolve-CodeSigningStoreLocation -StoreLocation $StoreLocation
    $resolvedStoreName = Resolve-CodeSigningStoreName -StoreName $StoreName
    $certStorePath = "Cert:\$resolvedStoreLocation\$resolvedStoreName"
    if (-not (Test-Path $certStorePath)) {
        throw "Certificate store was not found: $certStorePath"
    }

    $certificates = Get-ChildItem -Path $certStorePath
    if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
        $normalizedThumbprint = ($Thumbprint -replace '\s+', '').ToUpperInvariant()
        return $certificates | Where-Object { $_.Thumbprint -eq $normalizedThumbprint } | Select-Object -First 1
    }

    return $certificates | Where-Object {
        $_.Subject -like "*$Subject*" -or $_.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -like "*$Subject*"
    } | Select-Object -First 1
}

function Sign-Executable {
    param(
        [string]$TargetPath,
        [string]$ResolvedSignToolPath,
        [string]$ResolvedCodeSigningPfxPath,
        [string]$ResolvedCodeSigningPassword,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$ResolvedStoreCertificate,
        [string]$ResolvedStoreLocation,
        [string]$ResolvedStoreName,
        [string]$ResolvedTimestampUrl
    )

    if (-not (Test-Path $TargetPath)) {
        throw "File to sign was not found: $TargetPath"
    }

    $arguments = @(
        "sign",
        "/fd", "SHA256"
    )

    if (-not [string]::IsNullOrWhiteSpace($ResolvedTimestampUrl)) {
        $arguments += @("/tr", $ResolvedTimestampUrl, "/td", "SHA256")
    }

    if (-not [string]::IsNullOrWhiteSpace($ResolvedCodeSigningPfxPath)) {
        $arguments += @("/f", $ResolvedCodeSigningPfxPath)
        if (-not [string]::IsNullOrWhiteSpace($ResolvedCodeSigningPassword)) {
            $arguments += @("/p", $ResolvedCodeSigningPassword)
        }
    }
    elseif ($null -ne $ResolvedStoreCertificate) {
        $arguments += @("/sha1", $ResolvedStoreCertificate.Thumbprint, "/s", (Resolve-CodeSigningStoreName -StoreName $ResolvedStoreName))
        if ((Resolve-CodeSigningStoreLocation -StoreLocation $ResolvedStoreLocation) -eq "LocalMachine") {
            $arguments += "/sm"
        }
    }
    else {
        throw "No code-signing certificate source was configured."
    }

    $arguments += $TargetPath

    & $ResolvedSignToolPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for $TargetPath with exit code $LASTEXITCODE"
    }
}

function Try-SignPackageExecutables {
    param(
        [string]$PackageRoot,
        [bool]$EnableSigning,
        [bool]$RequireSigning,
        [string]$ResolvedCodeSigningPfxPath,
        [string]$ResolvedCodeSigningPassword,
        [string]$ResolvedCodeSigningThumbprint,
        [string]$ResolvedCodeSigningSubject,
        [string]$ResolvedCodeSigningStoreLocation,
        [string]$ResolvedCodeSigningStoreName,
        [string]$ResolvedTimestampUrl,
        [string]$ExplicitSignToolPath
    )

    if (-not $EnableSigning) {
        Write-Host "Code signing disabled."
        return
    }

    $storeCertificate = $null
    if ([string]::IsNullOrWhiteSpace($ResolvedCodeSigningPfxPath)) {
        $storeCertificate = Resolve-CodeSigningCertificate `
            -Thumbprint $ResolvedCodeSigningThumbprint `
            -Subject $ResolvedCodeSigningSubject `
            -StoreLocation $ResolvedCodeSigningStoreLocation `
            -StoreName $ResolvedCodeSigningStoreName
    }

    if ([string]::IsNullOrWhiteSpace($ResolvedCodeSigningPfxPath) -and $null -eq $storeCertificate) {
        if ($RequireSigning) {
            throw "Code signing is required, but no signing certificate source is configured."
        }
        Write-Warning "Skipping code signing because no signing certificate source is configured."
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($ResolvedCodeSigningPfxPath) -and -not (Test-Path $ResolvedCodeSigningPfxPath)) {
        throw "Code-signing certificate was not found: $ResolvedCodeSigningPfxPath"
    }

    $resolvedSignToolPath = Resolve-SignToolPath -ExplicitPath $ExplicitSignToolPath
    if ([string]::IsNullOrWhiteSpace($resolvedSignToolPath)) {
        throw "signtool.exe was not found. Set NETCAT_SIGNTOOL_PATH or install Windows SDK signing tools."
    }

    foreach ($relativePath in @("NetCat.exe", "updater\AmazTool.exe")) {
        $targetPath = Join-Path $PackageRoot $relativePath
        Sign-Executable `
            -TargetPath $targetPath `
            -ResolvedSignToolPath $resolvedSignToolPath `
            -ResolvedCodeSigningPfxPath $ResolvedCodeSigningPfxPath `
            -ResolvedCodeSigningPassword $ResolvedCodeSigningPassword `
            -ResolvedStoreCertificate $storeCertificate `
            -ResolvedStoreLocation $ResolvedCodeSigningStoreLocation `
            -ResolvedStoreName $ResolvedCodeSigningStoreName `
            -ResolvedTimestampUrl $ResolvedTimestampUrl
    }
}

Push-Location $repoRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-cli"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $projectPath = Join-Path $repoRoot "my-vpn-zapret-wpf\v2rayN\v2rayN.csproj"
    $targetFramework = Get-TargetFramework -ProjectPath $projectPath
    $stagingDir = Join-Path $repoRoot ".publish-staging"
    $zipStagingDir = Join-Path $repoRoot ".zip-staging"
    $testInstallRoot = Join-Path $repoRoot ".test-installs"
    $archiveRootName = "NetCat"
    $publishSourceDir = Resolve-PublishOutputDirectory `
        -RepoRoot $repoRoot `
        -Configuration $Configuration `
        -TargetFramework $targetFramework `
        -Runtime $Runtime `
        -ExplicitSourcePublishDir $SourcePublishDir

    $propsPath = Join-Path $repoRoot "my-vpn-zapret-wpf\Directory.Build.props"
    $version = Get-NetCatVersion -PropsPath $propsPath
    $versioningMetadata = Get-NetCatVersioningMetadata -PropsPath $propsPath
    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $OutputDir = ".\artifacts\NetCat-releaseV$version"
    }
    if ([string]::IsNullOrWhiteSpace($ZipPath)) {
        $ZipPath = ".\artifacts\NetCat-v$version.zip"
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
    if (Test-Path $zipStagingDir) {
        Remove-Item $zipStagingDir -Recurse -Force
    }
    if (Test-Path $testInstallRoot) {
        Remove-Item $testInstallRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ([string]::IsNullOrWhiteSpace($SourcePublishDir)) {
        if (Test-Path $publishSourceDir) {
            Remove-Item $publishSourceDir -Recurse -Force
        }
        dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained false
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE"
        }
        $publishSourceDir = Resolve-PublishOutputDirectory `
            -RepoRoot $repoRoot `
            -Configuration $Configuration `
            -TargetFramework $targetFramework `
            -Runtime $Runtime `
            -ExplicitSourcePublishDir $SourcePublishDir
        if (-not (Test-Path $publishSourceDir)) {
            throw "Publish output was not created: $publishSourceDir"
        }
    }
    elseif (-not (Test-Path $publishSourceDir)) {
        throw "Source publish directory was not found: $publishSourceDir"
    }

    Copy-Item $publishSourceDir $stagingDir -Recurse
    Copy-Item $stagingDir $OutputDir -Recurse
    Ensure-BundledBinLayout -RepoRoot $repoRoot -OutputDir $OutputDir
    Copy-UpdaterBundle -RepoRoot $repoRoot -Configuration $Configuration -Runtime $Runtime -OutputDir $OutputDir
    Try-SignPackageExecutables `
        -PackageRoot $OutputDir `
        -EnableSigning $SignBinaries `
        -RequireSigning $RequireCodeSigning `
        -ResolvedCodeSigningPfxPath $CodeSigningPfxPath `
        -ResolvedCodeSigningPassword $CodeSigningPassword `
        -ResolvedCodeSigningThumbprint $CodeSigningThumbprint `
        -ResolvedCodeSigningSubject $CodeSigningSubject `
        -ResolvedCodeSigningStoreLocation $CodeSigningStoreLocation `
        -ResolvedCodeSigningStoreName $CodeSigningStoreName `
        -ResolvedTimestampUrl $TimestampUrl `
        -ExplicitSignToolPath $SignToolPath

    $userDataDir = Join-Path $OutputDir "userdata"
    $defaultConfigPath = Join-Path $repoRoot "my-vpn-zapret\resources\v2rayn\guiConfigs\guiNConfig.json"

    [System.IO.Directory]::CreateDirectory($userDataDir) | Out-Null
    Get-ChildItem -Path $OutputDir -File -Filter "AmazTool*" -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item (Join-Path $OutputDir "guiNConfig.json") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $OutputDir "updater\guiLogs") -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem -Path (Join-Path $OutputDir "zapret") -File -Filter "zapret-hidden-*.bat" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    if ((Test-Path $defaultConfigPath) -and (-not (Test-Path (Join-Path $userDataDir "guiNConfig.json")))) {
        Copy-Item $defaultConfigPath (Join-Path $userDataDir "guiNConfig.json") -Force
    }

    $manifest = @{
        app = "NetCat"
        version = $version
        version_family = $versioningMetadata.VersionFamily
        runtime = $Runtime
        configuration = $Configuration
        embedded_proxy_implementation = $versioningMetadata.EmbeddedImplementation
        embedded_proxy_schema = $versioningMetadata.EmbeddedSchemaVersion
        embedded_proxy_version_family = $versioningMetadata.EmbeddedVersionFamily
        built_at = (Get-Date).ToString("o")
        package_root = $archiveRootName
    } | ConvertTo-Json
    Set-Content -Path (Join-Path $OutputDir "release-manifest.json") -Value $manifest -Encoding UTF8

    $archiveRootPath = Join-Path $zipStagingDir $archiveRootName
    New-Item -ItemType Directory -Path $zipStagingDir -Force | Out-Null
    Copy-Item $OutputDir $archiveRootPath -Recurse

    Compress-Archive -Path $archiveRootPath -DestinationPath $ZipPath -Force
    Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $zipStagingDir -Recurse -Force -ErrorAction SilentlyContinue

    $hash = Get-FileHash -Path $ZipPath -Algorithm SHA256
    Set-Content -Path "$ZipPath.sha256" -Value "$($hash.Hash.ToLowerInvariant()) *$([System.IO.Path]::GetFileName($ZipPath))" -Encoding ascii

    if ($RunSmokeTest) {
        & (Join-Path $repoRoot "Test-NetCatPackage.ps1") `
            -OutputDir $OutputDir `
            -ZipPath $ZipPath `
            -VerifySelfUpdate $VerifySelfUpdateSmoke `
            -RequireCodeSigning $RequireCodeSigning `
            -TestInstallRoot $testInstallRoot
    }

    Write-Host "Published folder: $OutputDir"
    Write-Host "Release zip: $ZipPath"
    Write-Host "SHA256 file: $ZipPath.sha256"
}
finally {
    Pop-Location
}
