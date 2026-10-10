#requires -Version 5.1
#requires -RunAsAdministrator
param(
    [Parameter(Mandatory=$true)][string]$InstallPath,
    [Parameter(Mandatory=$true)][string]$CleanupExecutable,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
# This removes only a closed installation's modules. No process is killed and
# user profiles, Windows network settings and other installations are preserved.
$installation = (Resolve-Path -LiteralPath $InstallPath).Path.TrimEnd('\')
$modules = Join-Path $installation 'modules'
if (-not [IO.Path]::IsPathRooted($installation) -or $installation -eq [IO.Path]::GetPathRoot($installation).TrimEnd('\')) {throw 'A drive root cannot be an installation.'}
$actualModules = (Resolve-Path -LiteralPath $modules).Path
if ($actualModules -ne $modules) {throw 'Unexpected module path.'}
$parent = [IO.DirectoryInfo]::new($actualModules)
while ($null -ne $parent) {
    if ($parent.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {throw 'Reparse paths are refused.'}
    $parent = $parent.Parent
}
# Check each directory before descending; never follow links in the deletion tree.
function Get-VerifiedModuleEntries([string]$Root) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if ((Get-Item -LiteralPath $directory -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {throw 'A module entry is a reparse point.'}
        foreach ($entry in @(Get-ChildItem -LiteralPath $directory -Force)) {
            if ($entry.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {throw 'A module entry is a reparse point.'}
            $entry
            if ($entry.PSIsContainer) {$pending.Push($entry.FullName)}
        }
    }
}
$entries = @(Get-VerifiedModuleEntries $actualModules)
$inventory = Get-Content -Raw -LiteralPath (Join-Path $modules 'modules.lock.json') | ConvertFrom-Json
foreach ($required in @('sing-box','xray','zapret')) {
    if (@($inventory | Where-Object {$_.key -eq $required}).Count -ne 1) {throw 'Not a recognized NetCat module folder.'}
}
if (Get-Process -Name NetCat -ErrorAction SilentlyContinue) {throw 'Exit every NetCat instance through its tray menu first. Nothing was changed.'}
if (Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($actualModules+'\',[StringComparison]::OrdinalIgnoreCase)}) {throw 'A process is still executing from this module folder. Nothing was changed.'}
$cleanup = (Resolve-Path -LiteralPath $CleanupExecutable).Path
$version = $null
if (-not [version]::TryParse([Diagnostics.FileVersionInfo]::GetVersionInfo($cleanup).FileVersion,[ref]$version) -or $version -lt [version]'1.0.3.0') {throw 'Use a NetCat build that supports --release-driver (1.0.3 preview or newer).'}
$signature = Get-AuthenticodeSignature -LiteralPath $cleanup
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne '167454D556EADE28134733A1BB2599F2D4BF8978') {throw 'The cleanup executable must have a valid trusted NetCat Publisher signature.'}
if (-not $Apply) {
    [pscustomobject]@{ReadOnly=$true;Modules=$actualModules;Files=@($entries | Where-Object {-not $_.PSIsContainer}).Count;ApplyRequired=$true} | ConvertTo-Json
    return
}
$process = Start-Process -FilePath $cleanup -ArgumentList ('--release-driver "'+$actualModules+'"') -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(15000)) {throw 'Cleanup has not completed. No module files were removed.'}
    if ($process.ExitCode -ne 0) {throw 'The driver is still in use or could not be safely released. No module files were removed.'}
} finally {$process.Dispose()}
if (Get-Process -Name NetCat -ErrorAction SilentlyContinue) {throw 'NetCat restarted. No module files were removed.'}
# Recheck the final absolute target immediately before the destructive operation.
if ((Resolve-Path -LiteralPath $actualModules).Path -ne $modules -or (Get-Item -LiteralPath $actualModules -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {throw 'Module path changed.'}
$null = @(Get-VerifiedModuleEntries $actualModules)
Remove-Item -LiteralPath $actualModules -Recurse -Force
[pscustomobject]@{Completed=$true;ModulesRemoved=(-not (Test-Path -LiteralPath $actualModules));RebootRequired=$false} | ConvertTo-Json
