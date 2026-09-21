param([Parameter(Mandatory=$true)][string]$Folder,[Parameter(Mandatory=$true)][string]$Archive,[switch]$TestBuild)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $Archive) { throw 'Archive already exists; do not overwrite a release.' }
$taskRoot=(Resolve-Path -LiteralPath $Folder).Path
$taskManifest=Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'metadata/release-manifest.json') | ConvertFrom-Json
if(-not $TestBuild -and (Get-AuthenticodeSignature -LiteralPath (Join-Path $taskRoot 'NetCat.exe')).Status -ne 'Valid') { throw 'Production release requires trusted Authenticode signing before generating the manifest.' }
foreach($taskComponent in $taskManifest.Components) { foreach($taskFile in $taskComponent.Files) { if((Get-FileHash -LiteralPath (Join-Path $taskRoot $taskFile.Path) -Algorithm SHA256).Hash -ne $taskFile.Sha256) { throw "Manifest mismatch: $($taskFile.Path)" } } }
if(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'metadata') -Filter 'update*' -ErrorAction SilentlyContinue) { throw 'Use a clean build folder, not a used installation.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$taskArchive=[IO.Path]::GetFullPath($Archive)
$taskZip=[IO.Compression.ZipFile]::Open($taskArchive,[IO.Compression.ZipArchiveMode]::Create)
try {
 foreach($taskFile in Get-ChildItem -LiteralPath $taskRoot -File -Recurse) {
  $taskRelative=$taskFile.FullName.Substring($taskRoot.Length+1).Replace('\','/')
  [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip,$taskFile.FullName,$taskRelative,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
 }
} finally { $taskZip.Dispose() }
[IO.File]::WriteAllText($taskArchive+'.sha256',(Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($taskArchive)+[Environment]::NewLine)
