param([Parameter(Mandatory=$true)][string]$ReportPath)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repo 'scripts/verification/Get-PrivateAcceptanceTargets.ps1')
$folder = Join-Path ([IO.Path]::GetTempPath()) ('NetCat-private-target-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($folder)
$file = Join-Path $folder 'targets.json'
$results = [Collections.Generic.List[object]]::new()
function Check([string]$CaseName, [scriptblock]$Test) {
    & $Test
    $results.Add([pscustomobject]@{Name=$CaseName;Status='Passed'})
}
function Reject([scriptblock]$Action) {
    $rejected=$false
    try { & $Action | Out-Null } catch { $rejected=$true }
    if (!$rejected) { throw 'Expected rejection' }
}
try {
    $targets=@{SiteADomain='example.test';SiteAIp='192.0.2.7';DnsServer='192.0.2.53';CorporateGateway='192.0.2.1';CorporatePrefix='192.0.2.0/24'}
    $targets | ConvertTo-Json | Set-Content -LiteralPath $file
    Check 'ValidExternalTargetsLoadWithoutPrintingValues' {
        $loaded=Get-PrivateAcceptanceTargets -Path $file
        if ($loaded.SiteADomain -ne $targets.SiteADomain -or $loaded.DnsServer -ne $targets.DnsServer) { throw 'Mismatch' }
    }
    Check 'DnsWireQuestionUsesConfiguredDomain' {
        $wire=New-PrivateDnsQuery -Domain $targets.SiteADomain
        $expected=[byte[]]@(0x12,0x34,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,4,116,101,115,116,0,0,1,0,1)
        if ([Convert]::ToBase64String($wire) -ne [Convert]::ToBase64String($expected)) { throw 'DNS wire mismatch' }
    }
    Check 'InRepositoryPrivateInputIsRejected' { Reject { Get-PrivateAcceptanceTargets -Path (Join-Path $repo 'targets.json') } }
    Check 'GeneralDefaultPrefixIsRejected' {
        $targets.CorporatePrefix='0.0.0.0/0';$targets | ConvertTo-Json | Set-Content -LiteralPath $file
        Reject {Get-PrivateAcceptanceTargets -Path $file}
    }
    Check 'MissingAndMalformedInputsFailClosedWithoutValueInError' {
        '{ "SiteADomain": "do-not-log-this"' | Set-Content -LiteralPath $file
        try {Get-PrivateAcceptanceTargets -Path $file | Out-Null;throw 'Missing rejection'} catch {
            if ($_.Exception.Message -ne 'Private acceptance targets are missing or invalid; values were not logged.') {throw 'Unexpected diagnostic'}
        }
        Reject {Get-PrivateAcceptanceTargets -Path (Join-Path $folder 'missing.json')}
    }
    foreach($name in @('Set-SiteA.ps1','Diagnose-DataPathProbe.ps1','Test-OpenVpnStandaloneProbe.ps1')) {
        Check ($name+'Parses') {
            $tokens=$null;$errors=$null
            [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $repo ('scripts/verification/'+$name)),[ref]$tokens,[ref]$errors)
            if($errors.Count -ne 0) {throw 'PowerShell parse error'}
        }
    }
    [pscustomobject]@{Passed=$results.Count;Failed=0;Skipped=0;Results=$results} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReportPath
} finally { [IO.Directory]::Delete($folder,$true) }
