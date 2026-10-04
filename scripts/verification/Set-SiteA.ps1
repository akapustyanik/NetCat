param([string]$TargetsPath = (Join-Path $env:LOCALAPPDATA 'NetCat-Test/acceptance-targets.json'))
. (Join-Path $PSScriptRoot 'Get-PrivateAcceptanceTargets.ps1')
$targets = Get-PrivateAcceptanceTargets -Path $TargetsPath
$ErrorActionPreference = 'Stop'
$cred = Import-Clixml "$env:LOCALAPPDATA\NetCat-Test\vm_win11_credential.xml"
$s = New-PSSession -VMName VM_win11 -Credential $cred
try {
    Invoke-Command -Session $s -ArgumentList $targets -ScriptBlock {
        param($targets)
        $targets.SiteADomain | Set-Content -LiteralPath 'C:\NetCat-C26-acceptance\site-a.txt' -Encoding ASCII
        Write-Host "Site-A target stored locally; value not logged."
    }
} finally {
    Remove-PSSession $s
}
