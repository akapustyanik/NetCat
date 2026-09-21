param([string]$ModuleRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'bin'))
$ErrorActionPreference='Stop'
$taskRepo='Loyalsoldier/v2ray-rules-dat'
$taskRelease=Invoke-RestMethod "https://api.github.com/repos/$taskRepo/releases/latest"
$taskRecords=@()
$taskLock=Join-Path $ModuleRoot 'modules.lock.json'
if(Test-Path -LiteralPath $taskLock) { $taskRecords=@(Get-Content -Raw -LiteralPath $taskLock | ConvertFrom-Json | Where-Object { $_.key -notin @('geoip','geosite') }) }
foreach($taskKey in @('geoip','geosite')) {
 $taskAsset=@($taskRelease.assets | Where-Object name -EQ "$taskKey.dat")
 if($taskAsset.Count -ne 1 -or $taskAsset[0].digest -notmatch '^sha256:[a-f0-9]{64}$') { throw "No verified asset for $taskKey" }
 $taskAsset=$taskAsset[0]
 $taskDestination=Join-Path $ModuleRoot $taskKey
 New-Item -ItemType Directory -Force $taskDestination | Out-Null
 $taskTemporary=Join-Path $taskDestination ($taskKey+'.download')
 Invoke-WebRequest -UseBasicParsing $taskAsset.browser_download_url -OutFile $taskTemporary
 $taskHash=(Get-FileHash -LiteralPath $taskTemporary -Algorithm SHA256).Hash.ToLowerInvariant()
 if($taskHash -ne $taskAsset.digest.Substring(7)) { throw "SHA-256 mismatch for $taskKey" }
 Move-Item -LiteralPath $taskTemporary -Destination (Join-Path $taskDestination "$taskKey.dat") -Force
 $taskSource=@{Key=$taskKey;Repository=$taskRepo;Version=$taskRelease.tag_name;Asset=$taskAsset.name;Url=$taskAsset.browser_download_url;Sha256=$taskHash}
 $taskSource | ConvertTo-Json | Set-Content (Join-Path $taskDestination 'netcat-source.json') -Encoding utf8
 Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/$taskRepo/master/LICENSE" -OutFile (Join-Path $taskDestination 'LICENSE')
 "Geodata source: https://github.com/$taskRepo`nRelease: $($taskRelease.tag_name)`nData sources and notices: https://github.com/$taskRepo#readme" | Set-Content (Join-Path $taskDestination 'SOURCE.txt') -Encoding utf8
 $taskRecords+=@{key=$taskKey;repo=$taskRepo;version=$taskRelease.tag_name;asset=$taskAsset.name;url=$taskAsset.browser_download_url;sha256=$taskHash;verified='GitHub release asset SHA-256'}
 Write-Host "$taskKey $($taskRelease.tag_name) verified"
}
$taskRecords | ConvertTo-Json -Depth 8 | Set-Content $taskLock -Encoding utf8
