param([string]$Version = '1.0.0',[switch]$CandidateConfirmed)
$ErrorActionPreference='Stop'
if(-not $CandidateConfirmed) { throw 'Validate the candidate manually before preparing release assets. This script never publishes.' }
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskProps=[xml](Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'Directory.Build.props'))
if($Version -ne [string]$taskProps.Project.PropertyGroup.Version) { throw 'Version must match Directory.Build.props.' }
$taskFolder=Join-Path $taskRoot "artifacts/NetCat-$Version"
$taskAssets=Join-Path $taskRoot "artifacts/release-$Version"
if((Test-Path -LiteralPath $taskFolder) -or (Test-Path -LiteralPath $taskAssets)) { throw 'Existing release assets are immutable; never overwrite them.' }
& (Join-Path $PSScriptRoot 'Build-Test.ps1') -BuildName "NetCat-$Version"
New-Item -ItemType Directory -Path $taskAssets | Out-Null
& (Join-Path $PSScriptRoot 'Pack-Portable.ps1') -Folder $taskFolder -Archive (Join-Path $taskAssets "NetCat-v$Version-win-x64.zip")
Write-Output "Signed assets prepared in $taskAssets. NOT PUBLISHED."
