param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$moduleRoot = Join-Path $Root 'bin'
$cache = Join-Path $Root 'artifacts/downloads'
New-Item -ItemType Directory -Force $cache,$moduleRoot | Out-Null
$headers = @{ 'User-Agent' = 'NetCat-Build'; Accept = 'application/vnd.github+json' }
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
$records = @()
function Get-GitHubModule([string]$Key, [string]$Repo, [string]$Tag, [string]$Pattern, [string]$Binary) {
    $release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/tags/$Tag" -Headers $headers
    $assets = @($release.assets | Where-Object { $_.name -match $Pattern })
    if ($assets.Count -ne 1) { throw "Ambiguous asset for $Key" }
    $asset = $assets[0]
    if ($asset.digest -notmatch '^sha256:([a-f0-9]{64})$') { throw "No trusted release digest for $Key" }
    $expected = $Matches[1]
    $archive = Join-Path $cache $asset.name
    if (-not (Test-Path $archive)) { Invoke-WebRequest $asset.browser_download_url -OutFile $archive }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "SHA256 mismatch: $Key" }
    $dest = Join-Path $moduleRoot $Key
    New-Item -ItemType Directory -Force $dest | Out-Null
    if ($asset.name.EndsWith('.zip')) {
        $expanded = Join-Path $cache "$Key-expanded"
        Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
        $found = @(Get-ChildItem $expanded -Filter $Binary -Recurse)
        if ($found.Count -ne 1) { throw "Missing or ambiguous executable: $Key" }
        $contentRoot = if ($Key -eq 'zapret') { Split-Path (Split-Path $found[0].FullName -Parent) -Parent } else { Split-Path $found[0].FullName -Parent }
        Copy-Item -Path (Join-Path $contentRoot '*') -Destination $dest -Recurse -Force
    } else { Copy-Item -LiteralPath $archive -Destination (Join-Path $dest $Binary) -Force }
    $script:records += @{ key=$Key; repo=$Repo; version=$Tag; asset=$asset.name; url=$asset.browser_download_url; sha256=$expected; verified='GitHub release asset SHA-256' }
    Write-Host "$Key $Tag verified"
}
Get-GitHubModule 'sing-box' 'SagerNet/sing-box' 'v1.14.0' '^sing-box-1\.14\.0-windows-amd64\.zip$' 'sing-box.exe'
Get-GitHubModule 'xray' 'XTLS/Xray-core' 'v26.3.27' '^Xray-windows-64\.zip$' 'xray.exe'
Get-GitHubModule 'zapret' 'Flowseal/zapret-discord-youtube' '1.10.2' '\.zip$' 'winws.exe'
Get-GitHubModule 'tg-ws-proxy' 'Flowseal/tg-ws-proxy' 'v1.10.2' '^TgWsProxy_windows\.exe$' 'TgWsProxy_windows.exe'
$wintunZip = Join-Path $cache 'wintun-0.14.1.zip'
if (-not (Test-Path $wintunZip)) { Invoke-WebRequest 'https://www.wintun.net/builds/wintun-0.14.1.zip' -OutFile $wintunZip }
Expand-Archive -LiteralPath $wintunZip -DestinationPath (Join-Path $cache 'wintun') -Force
$wintunDll = Join-Path $cache 'wintun/wintun/bin/amd64/wintun.dll'
$signature = Get-AuthenticodeSignature -LiteralPath $wintunDll
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'WireGuard') { throw 'Wintun signature verification failed' }
Copy-Item -LiteralPath $wintunDll -Destination (Join-Path $moduleRoot 'sing-box/wintun.dll') -Force
New-Item -ItemType Directory -Force (Join-Path $moduleRoot 'wintun') | Out-Null
Copy-Item -LiteralPath $wintunDll -Destination (Join-Path $moduleRoot 'wintun/wintun.dll') -Force
$records += @{ key='wintun'; version='0.14.1'; url='https://www.wintun.net/builds/wintun-0.14.1.zip'; sha256=(Get-FileHash $wintunZip).Hash.ToLowerInvariant(); verified='Authenticode: WireGuard' }
$msi = Join-Path $cache 'OpenVPN-2.6.22-I001-amd64.msi'
if (-not (Test-Path $msi)) { Invoke-WebRequest 'https://swupdate.openvpn.org/community/releases/OpenVPN-2.6.22-I001-amd64.msi' -OutFile $msi }
$signature = Get-AuthenticodeSignature -LiteralPath $msi
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'OpenVPN') { throw 'OpenVPN MSI signature verification failed' }
$msiDir = Join-Path $cache 'openvpn-2.6.22-expanded'
New-Item -ItemType Directory -Force $msiDir | Out-Null
$process = Start-Process msiexec.exe -ArgumentList @('/a', ('"'+$msi+'"'), '/qn', ('TARGETDIR="'+$msiDir+'"')) -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "MSI administrative extraction failed: $($process.ExitCode)" }
$openvpn = Get-ChildItem $msiDir -Filter openvpn.exe -Recurse | Select-Object -First 1
if (-not $openvpn) { throw 'OpenVPN executable missing from MSI' }
$dest = Join-Path $moduleRoot 'openvpn'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item -Path (Join-Path $openvpn.DirectoryName '*') -Destination $dest -Recurse -Force
Copy-Item -LiteralPath $wintunDll -Destination (Join-Path $dest 'wintun.dll') -Force
$records += @{key='openvpn';version='2.6.22';url='https://swupdate.openvpn.org/community/releases/OpenVPN-2.6.22-I001-amd64.msi';sha256=(Get-FileHash $msi).Hash.ToLowerInvariant();verified='Authenticode: OpenVPN'}
$records | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $moduleRoot 'modules.lock.json') -Encoding utf8
Write-Host 'Official modules ready. Drivers were not installed or started.'

& (Join-Path $PSScriptRoot "Fetch-Geodata.ps1") -ModuleRoot $moduleRoot
