$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskCache=Join-Path $taskRoot 'artifacts/downloads/telegram-headless'
$taskRuntime=Join-Path $taskRoot 'bin/tg-runtime'
$taskModule=Join-Path $taskRoot 'bin/tg-ws-proxy'
New-Item -ItemType Directory -Force $taskCache,$taskRuntime,$taskModule | Out-Null
$taskTag='v1.10.2'
$taskTree=Invoke-RestMethod "https://api.github.com/repos/Flowseal/tg-ws-proxy/git/trees/${taskTag}?recursive=1"
foreach($taskEntry in ($taskTree.tree | Where-Object { $_.type -eq 'blob' -and ($_.path -match '^proxy/[a-zA-Z0-9_/]+\.py$' -or $_.path -eq 'LICENSE') })) {
    $taskDest=Join-Path $taskModule $taskEntry.path
    New-Item -ItemType Directory -Force (Split-Path $taskDest -Parent) | Out-Null
    Invoke-WebRequest "https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/$($taskTree.sha)/$($taskEntry.path)" -OutFile $taskDest
    $taskBytes=[IO.File]::ReadAllBytes($taskDest)
    $taskBlob=[Text.Encoding]::UTF8.GetBytes("blob $($taskBytes.Length)`0") + $taskBytes
    $taskHasher=[Security.Cryptography.SHA1]::Create()
    $taskDigest=[BitConverter]::ToString($taskHasher.ComputeHash($taskBlob)).Replace('-','').ToLowerInvariant()
    $taskHasher.Dispose()
    if($taskDigest -ne $taskEntry.sha) { throw 'Source blob hash mismatch' }
}
@{Key='tg-ws-proxy';Repository='Flowseal/tg-ws-proxy';Version=$taskTag;Asset='headless source';Url="https://github.com/Flowseal/tg-ws-proxy/tree/$taskTag";Sha256='';SourceTree=$taskTree.sha} | ConvertTo-Json | Set-Content (Join-Path $taskModule 'netcat-source.json') -Encoding utf8
$taskPython=Join-Path $taskCache 'python-3.13.15-embed-amd64.zip'
if(-not (Test-Path -LiteralPath $taskPython)) { Invoke-WebRequest 'https://www.python.org/ftp/python/3.13.15/python-3.13.15-embed-amd64.zip' -OutFile $taskPython }
Expand-Archive -LiteralPath $taskPython -DestinationPath $taskRuntime -Force
$taskSignature=Get-AuthenticodeSignature (Join-Path $taskRuntime 'python.exe')
if($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'Python Software Foundation') { throw 'Python signature verification failed' }
Copy-Item -LiteralPath (Join-Path $taskRuntime 'python.exe') -Destination (Join-Path $taskRuntime 'NetCat.Telegram.exe') -Force
@('python313.zip','.','Lib/site-packages','../tg-ws-proxy') | Set-Content (Join-Path $taskRuntime 'python313._pth') -Encoding ascii
$taskWheels=Join-Path $taskCache 'wheels'
New-Item -ItemType Directory -Force $taskWheels | Out-Null
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
    [IO.Compression.ZipFile]::ExtractToDirectory($taskWheel.FullName,$taskPackages,$true)
    $taskRecords+=@{file=$taskWheel.Name;sha256=$taskSha}
}
@{python='3.13.15';pythonSha256=(Get-FileHash $taskPython).Hash.ToLowerInvariant();sourceTree=$taskTree.sha;wheels=$taskRecords} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $taskRuntime 'runtime.lock.json') -Encoding utf8
& (Join-Path $taskRuntime 'NetCat.Telegram.exe') -c 'import proxy.tg_ws_proxy; import cryptography; print("Headless Telegram imports OK")'
if($LASTEXITCODE -ne 0) { throw 'Headless import check failed' }
