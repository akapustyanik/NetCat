param([Parameter(Mandatory=$true)][string]$Folder,[string]$Version,[switch]$RequireSignature)
$ErrorActionPreference='Stop'
if(-not $Version) { $taskProps=[xml](Get-Content -Raw -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'Directory.Build.props')); $Version=[string]$taskProps.Project.PropertyGroup.Version }
$taskRoot=(Resolve-Path -LiteralPath $Folder).Path
if($RequireSignature -and (Get-AuthenticodeSignature -LiteralPath (Join-Path $taskRoot 'NetCat.exe')).Status -ne 'Valid') { throw 'Release requires a trusted Authenticode signature.' }
$taskLock=Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'modules/modules.lock.json') | ConvertFrom-Json
$taskVersions=@{netcat=$Version}
foreach($taskEntry in $taskLock) { $taskVersions[$taskEntry.key]=$taskEntry.version }
$taskComponents=@()
foreach($taskKey in @('netcat','sing-box','xray','zapret','tg-ws-proxy','openvpn','wintun','geoip','geosite')) {
 if($taskKey -ne 'netcat') {
  $taskSource=Join-Path $taskRoot "modules/$taskKey/netcat-source.json"
  if(Test-Path -LiteralPath $taskSource) { $taskVersions[$taskKey]=(Get-Content -Raw -LiteralPath $taskSource | ConvertFrom-Json).Version }
 }
 $taskFiles=@()
 if($taskKey -eq 'netcat') { $taskCandidates=@(Get-Item -LiteralPath (Join-Path $taskRoot 'NetCat.exe')) }
 else {
  $taskFolders=@($taskKey); if($taskKey -eq 'tg-ws-proxy') { $taskFolders+= 'tg-runtime' }
  $taskCandidates=@(foreach($taskSub in $taskFolders) { Get-ChildItem -LiteralPath (Join-Path $taskRoot "modules/$taskSub") -File -Recurse })
  if($taskKey -in @('sing-box','openvpn')) { $taskCandidates=@($taskCandidates | Where-Object Name -ne 'wintun.dll') }
  if($taskKey -eq 'wintun') { foreach($taskSecondary in @('sing-box','openvpn')) { $taskCandidates+=Get-Item -LiteralPath (Join-Path $taskRoot "modules/$taskSecondary/wintun.dll") } }
 }
 foreach($taskFile in $taskCandidates) { $taskRelative=$taskFile.FullName.Substring($taskRoot.Length+1).Replace('\','/'); $taskFiles+=@{Path=$taskRelative;Sha256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} }
 $taskComponents+=@{Key=$taskKey;Version=$taskVersions[$taskKey];Files=@($taskFiles)}
}
New-Item -ItemType Directory -Force (Join-Path $taskRoot 'metadata') | Out-Null
$taskEncoding=[Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $taskRoot 'metadata/release-manifest.json'),(@{Schema=1;Version=$Version;Components=$taskComponents}|ConvertTo-Json -Depth 9),$taskEncoding)
[IO.File]::WriteAllText((Join-Path $taskRoot 'metadata/installed.json'),($taskVersions|ConvertTo-Json),$taskEncoding)
