param([Parameter(Mandatory=$true)][string]$Candidate,[Parameter(Mandatory=$true)][string]$Destination,[string]$ReleasesRoot)
$ErrorActionPreference='Stop'
$taskCandidate=(Resolve-Path -LiteralPath $Candidate).Path
$taskManifest=Get-Content -Raw -LiteralPath (Join-Path $taskCandidate 'metadata/release-manifest.json') | ConvertFrom-Json
function Get-ReleaseInventory([string]$Root) {
 $taskResolved=(Resolve-Path -LiteralPath $Root).Path
 @(Get-ChildItem -LiteralPath $taskResolved -File -Recurse | ForEach-Object {
  $taskFileStream=[IO.File]::OpenRead($_.FullName)
  $taskSha=[Security.Cryptography.SHA256]::Create()
  try { ($_.FullName.Substring($taskResolved.Length+1).Replace('\','/').ToLowerInvariant())+' '+[BitConverter]::ToString($taskSha.ComputeHash($taskFileStream)).Replace('-','') }
  finally { $taskFileStream.Dispose(); $taskSha.Dispose() }
 } | Sort-Object) -join "`n"
}
$taskExisting=@()
if(Test-Path -LiteralPath $Destination) { $taskExisting+=(Resolve-Path -LiteralPath $Destination).Path }
if($ReleasesRoot -and (Test-Path -LiteralPath $ReleasesRoot)) {
 foreach($taskDirectory in Get-ChildItem -LiteralPath $ReleasesRoot -Directory) {
  if($taskDirectory.FullName -eq $taskCandidate -or $taskDirectory.Name.StartsWith('.build-')) { continue }
  $taskPriorManifest=Join-Path $taskDirectory.FullName 'metadata/release-manifest.json'
  if(Test-Path -LiteralPath $taskPriorManifest) {
   $taskPrior=Get-Content -Raw -LiteralPath $taskPriorManifest | ConvertFrom-Json
   if($taskPrior.Version -eq $taskManifest.Version) { $taskExisting+=$taskDirectory.FullName }
  }
 }
}
if($taskExisting.Count -gt 0) {
 $taskInventory=Get-ReleaseInventory $taskCandidate
 foreach($taskPrior in $taskExisting | Select-Object -Unique) {
  if((Get-ReleaseInventory $taskPrior) -ne $taskInventory) { throw "Release $($taskManifest.Version) or destination already exists with different SHA-256 content: $taskPrior. Increment the version; do not overwrite a release." }
 }
}
