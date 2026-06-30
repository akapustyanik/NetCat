# Requires Administrator privileges to modify LocalMachine certificate stores.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "This script must be run as an Administrator to trust the developer certificate."
    Write-Host "Attempting to elevate..."
    Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}

$PfxPath = Join-Path $PSScriptRoot "artifacts\NetCat-dev-codesign.pfx"
$Password = "netcat-dev"

if (-not (Test-Path $PfxPath)) {
    Write-Error "PFX certificate file not found at $PfxPath. Please run New-NetCatDevCodeSigningCert.ps1 first."
    exit 1
}

try {
    Write-Host "Loading self-signed certificate from $PfxPath..."
    $securePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $securePassword, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet)

    # Import into Root (Trusted Root Certification Authorities)
    Write-Host "Importing into Trusted Root Certification Authorities store (LocalMachine\Root)..."
    $storeRoot = New-Object System.Security.Cryptography.X509Certificates.X509Store([System.Security.Cryptography.X509Certificates.StoreName]::Root, [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $storeRoot.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $storeRoot.Add($cert)
    $storeRoot.Close()

    # Import into TrustedPublisher
    Write-Host "Importing into Trusted Publishers store (LocalMachine\TrustedPublisher)..."
    $storePub = New-Object System.Security.Cryptography.X509Certificates.X509Store([System.Security.Cryptography.X509Certificates.StoreName]::TrustedPublisher, [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $storePub.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $storePub.Add($cert)
    $storePub.Close()

    Write-Host "Success! The developer certificate is now fully trusted on this machine."
    Write-Host "NetCat updates signed with this certificate will run without SmartScreen or execution blocks."
}
catch {
    Write-Error "Failed to install certificate: $_"
    exit 1
}
