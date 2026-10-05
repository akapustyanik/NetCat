param([string]$Root = (Split-Path $PSScriptRoot -Parent), [switch]$SourceOnly)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($Root)
$taskCache=Join-Path $taskRoot 'artifacts/downloads/telegram-headless'
$taskRuntime=Join-Path $taskRoot 'bin/tg-runtime'
$taskModule=Join-Path $taskRoot 'bin/tg-ws-proxy'
New-Item -ItemType Directory -Force $taskCache,$taskRuntime,$taskModule | Out-Null
$taskTag='v1.10.4'
$taskHeaders = @{ 'User-Agent' = 'NetCat-Build'; Accept = 'application/vnd.github+json' }
if ($env:GITHUB_TOKEN) { $taskHeaders['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
$taskTree=Invoke-RestMethod "https://api.github.com/repos/Flowseal/tg-ws-proxy/git/trees/${taskTag}?recursive=1" -Headers $taskHeaders
foreach($taskEntry in ($taskTree.tree | Where-Object { $_.type -eq 'blob' -and ($_.path -match '^proxy/[a-zA-Z0-9_/]+\.py$' -or $_.path -eq 'LICENSE') })) {
    $taskDest=Join-Path $taskModule $taskEntry.path
    New-Item -ItemType Directory -Force (Split-Path $taskDest -Parent) | Out-Null
    & curl.exe --fail --location --silent --show-error --max-time 90 --retry 2 --output $taskDest "https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/$($taskTree.sha)/$($taskEntry.path)"
    if ($LASTEXITCODE -ne 0) { throw 'Official Telegram source download failed.' }
    $taskBytes=[IO.File]::ReadAllBytes($taskDest)
    $taskBlob=[Text.Encoding]::UTF8.GetBytes("blob $($taskBytes.Length)`0") + $taskBytes
    $taskHasher=[Security.Cryptography.SHA1]::Create()
    $taskDigest=[BitConverter]::ToString($taskHasher.ComputeHash($taskBlob)).Replace('-','').ToLowerInvariant()
    $taskHasher.Dispose()
    if($taskDigest -ne $taskEntry.sha) { throw 'Source blob hash mismatch' }
}
@{Key='tg-ws-proxy';Repository='Flowseal/tg-ws-proxy';Version=$taskTag;Asset='headless source';Url="https://github.com/Flowseal/tg-ws-proxy/tree/$taskTag";Sha256='';SourceTree=$taskTree.sha} | ConvertTo-Json | Set-Content (Join-Path $taskModule 'netcat-source.json') -Encoding utf8
if ($SourceOnly) { Write-Host "Telegram headless source $taskTag verified against the official Git tree."; return }
$taskPython=Join-Path $taskCache 'python-3.13.15-embed-amd64.zip'
if(-not (Test-Path -LiteralPath $taskPython)) { Invoke-WebRequest 'https://www.python.org/ftp/python/3.13.15/python-3.13.15-embed-amd64.zip' -OutFile $taskPython }
Expand-Archive -LiteralPath $taskPython -DestinationPath $taskRuntime -Force
$taskSignature=Get-AuthenticodeSignature (Join-Path $taskRuntime 'python.exe')
if($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'Python Software Foundation') { throw 'Python signature verification failed' }
Copy-Item -LiteralPath (Join-Path $taskRuntime 'python.exe') -Destination (Join-Path $taskRuntime 'NetCat.Telegram.exe') -Force
@('python313.zip','.','Lib/site-packages','../tg-ws-proxy') | Set-Content (Join-Path $taskRuntime 'python313._pth') -Encoding ascii
$taskWheels=Join-Path $taskCache 'wheels'
New-Item -ItemType Directory -Force $taskWheels | Out-Null
# v1.11.0 imports HTTP/2 unconditionally, including when --no-h2 is used.
# Bundle its headless dependencies with NetCat; the module updater must never
# install arbitrary packages into an already reviewed interpreter at runtime.
python -m pip download --index-url https://pypi.org/simple --only-binary=:all: --platform win_amd64 --python-version 313 --dest $taskWheels 'cryptography==46.0.5' certifi
if($LASTEXITCODE -ne 0) { throw 'Python wheel download failed' }
$taskPackages=Join-Path $taskRuntime 'Lib/site-packages'
New-Item -ItemType Directory -Force $taskPackages | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskRecords=@()
foreach($taskWheel in (Get-ChildItem $taskWheels -Filter '*.whl')) {
    $taskParts=$taskWheel.Name -split '-'
    $taskMeta=Invoke-RestMethod "https://pypi.org/pypi/$($taskParts[0])/$($taskParts[1])/json"
    $taskExpected=($taskMeta.urls | Where-Object filename -EQ $taskWheel.Name).digests.sha256
    $taskSha=(Get-FileHash -LiteralPath $taskWheel.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if($taskSha -ne $taskExpected) { throw 'PyPI wheel hash mismatch' }
    $zipTemp = Join-Path $taskCache ($taskWheel.Name + '.zip')
    Copy-Item -LiteralPath $taskWheel.FullName -Destination $zipTemp -Force
    Expand-Archive -LiteralPath $zipTemp -DestinationPath $taskPackages -Force
    Remove-Item -LiteralPath $zipTemp -Force
    $taskRecords+=@{file=$taskWheel.Name;sha256=$taskSha}
}
@{python='3.13.15';pythonSha256=(Get-FileHash $taskPython).Hash.ToLowerInvariant();sourceTree=$taskTree.sha;wheels=$taskRecords} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $taskRuntime 'runtime.lock.json') -Encoding utf8
python -B (Join-Path $PSScriptRoot 'Add-TelegramHttp2Runtime.py') --runtime $taskRuntime --cache (Join-Path $taskCache 'http2-wheels')
if($LASTEXITCODE -ne 0) { throw 'Reviewed Telegram HTTP/2 dependency installation failed' }
& (Join-Path $taskRuntime 'NetCat.Telegram.exe') -I -B -c "import proxy.tg_ws_proxy; import cryptography, httpx, h2; print('Headless Telegram imports OK')"
if($LASTEXITCODE -ne 0) { throw 'Headless import check failed' }
