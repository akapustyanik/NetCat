param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "",
    [string]$ZipPath = "",
    [string]$SourcePublishDir = "",
    [string]$CodeSigningPfxPath = $env:NETCAT_CODESIGN_PFX,
    [string]$CodeSigningPassword = $env:NETCAT_CODESIGN_PASSWORD,
    [string]$CodeSigningThumbprint = $env:NETCAT_CODESIGN_THUMBPRINT,
    [string]$CodeSigningSubject = $env:NETCAT_CODESIGN_SUBJECT,
    [string]$CodeSigningStoreLocation = $env:NETCAT_CODESIGN_STORE_LOCATION,
    [string]$CodeSigningStoreName = $env:NETCAT_CODESIGN_STORE_NAME,
    [string]$TimestampUrl = $env:NETCAT_CODESIGN_TIMESTAMP_URL,
    [string]$SignToolPath = $env:NETCAT_SIGNTOOL_PATH,
    [switch]$Fast,
    [switch]$SkipSigning,
    [switch]$AllowUnsigned,
    [switch]$SkipSmoke,
    [switch]$SkipSelfUpdateSmoke
)

$ErrorActionPreference = "Stop"

$publishScript = Join-Path $PSScriptRoot "Publish-NetCatRelease.ps1"
if (-not (Test-Path $publishScript)) {
    throw "Publish script not found: $publishScript"
}

$runSmokeTest = -not $SkipSmoke
$verifySelfUpdateSmoke = -not $Fast -and -not $SkipSelfUpdateSmoke
$signBinaries = -not $SkipSigning
$requireCodeSigning = $signBinaries -and -not $AllowUnsigned

Write-Host "Local NetCat release"
Write-Host "  Configuration: $Configuration"
Write-Host "  Runtime: $Runtime"
Write-Host "  Smoke test: $runSmokeTest"
Write-Host "  Self-update smoke: $verifySelfUpdateSmoke"
Write-Host "  Sign binaries: $signBinaries"
Write-Host "  Require code signing: $requireCodeSigning"

& $publishScript `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -OutputDir $OutputDir `
    -ZipPath $ZipPath `
    -SourcePublishDir $SourcePublishDir `
    -RunSmokeTest:$runSmokeTest `
    -VerifySelfUpdateSmoke:$verifySelfUpdateSmoke `
    -SignBinaries:$signBinaries `
    -RequireCodeSigning:$requireCodeSigning `
    -CodeSigningPfxPath $CodeSigningPfxPath `
    -CodeSigningPassword $CodeSigningPassword `
    -CodeSigningThumbprint $CodeSigningThumbprint `
    -CodeSigningSubject $CodeSigningSubject `
    -CodeSigningStoreLocation $CodeSigningStoreLocation `
    -CodeSigningStoreName $CodeSigningStoreName `
    -TimestampUrl $TimestampUrl `
    -SignToolPath $SignToolPath
