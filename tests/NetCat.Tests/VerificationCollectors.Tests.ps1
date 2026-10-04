param([Parameter(Mandatory)][string]$Repository)
$ErrorActionPreference='Stop'
. (Join-Path $Repository 'scripts\verification\Measurements.ps1')
# Pure process-table fixture: the real CIM/network/termination APIs are never called.
$exe='C:\Synthetic\NetCat.exe';$runtime='C:\SyntheticRuntime'
$created=[datetime]'2026-09-26T00:00:00Z'
$script:table=@([pscustomobject]@{Name='NetCat.exe';ExecutablePath=$exe;ProcessId=10;ParentProcessId=1;CreationDate=$created;CommandLine=''})
$specs=@(
    @('sing-box.exe','sing-box\sing-box.exe','router.json'),
    @('sing-box.exe','sing-box\sing-box.exe','openvpn-gateway\gateway.json'),
    @('xray.exe','xray\xray.exe','xray.json'),
    @('openvpn.exe','openvpn\openvpn.exe','openvpn\openvpn.conf'),
    @('winws.exe','zapret\bin\winws.exe','zapret\zapret-hosts.txt')
)
$number=11
foreach($spec in $specs){
    $script:table += [pscustomobject]@{Name=$spec[0];ExecutablePath=(Join-Path 'C:\Synthetic\modules' $spec[1]);ProcessId=$number;ParentProcessId=10;CreationDate=$created.AddSeconds(1);CommandLine=('engine --config "'+(Join-Path $runtime $spec[2])+'"')};$number++
}
$script:table += [pscustomobject]@{Name='sing-box.exe';ExecutablePath='C:\Foreign\sing-box.exe';ProcessId=99;ParentProcessId=9;CreationDate=$created;CommandLine='engine --config "C:\SyntheticRuntime\router.json"'}
function Get-CimInstance {param($ClassName) return $script:table}
$first=Get-OwnedProcesses $exe $runtime
if(@($first.Owned).Count -ne 6 -or @($first.Foreign).Count -ne 1 -or -not $first.Complete){throw 'Exact process ownership failed.'}
$second=Get-OwnedProcesses $exe $runtime $first.Parents
if(@($second.Owned).Count -ne 6){throw 'Parent identity deduplication failed.'}
if(@($first.Owned | Where-Object Role -eq 'OpenVpnSidecar').Count -ne 1){throw 'Sidecar misclassified.'}
$script:table[1].CommandLine='engine --config "C:\ForeignRuntime\router.json"'
$third=Get-OwnedProcesses $exe $runtime
if($third.Complete -or @($third.Unknown).Count -ne 1){throw 'Foreign runtime config accepted.'}
if(Test-AuthoritativeTun "TUN_OBSERVE runtimeLifecycle=Running finalStatus=Healthy`nTUN_OBSERVE runtimeLifecycle=Failed finalStatus=Weak"){throw 'Stale TUN accepted.'}
$counts=Get-LifecycleCounts "OpenVPN: startup`nACTION StartOpenVpn result=deferred`nACTION StartOpenVpn result=ok`nZAPRET_PROCESS state=started pid=20`nZAPRET_PROCESS state=started pid=20"
if($counts.OpenVpn -ne 1 -or $counts.Zapret -ne 1){throw 'Lifecycle markers counted incorrectly.'}
$temporary=Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString()+'.txt')
try {
    [IO.File]::WriteAllText($temporary,'old text')
    $offset=Get-LogOffset $temporary
    [IO.File]::AppendAllText($temporary,'current window')
    if((Read-AppendedLog $temporary $offset) -ne 'current window'){throw 'Byte offset did not isolate current run.'}
} finally {Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue}
$testRuntime=Join-Path ([IO.Path]::GetTempPath()) ('netcat-verification-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRuntime | Out-Null
try {
    # router.json and owned-routes.json are engine configs, not OS route journals.
    '{"route":{"rules":[]}}' | Set-Content -LiteralPath (Join-Path $testRuntime 'router.json')
    '{"version":3,"rules":[]}' | Set-Content -LiteralPath (Join-Path $testRuntime 'owned-routes.json')
    '{"Schema":3,"InterfaceIndex":123,"Gateway":"10.250.250.1","Prefixes":["10.250.251.0/24"],"Metric":7777,"HelperDefault":true}' | Set-Content -LiteralPath (Join-Path $testRuntime 'openvpn-route.json')
    $keys=@(Get-OwnedRouteKeys $testRuntime)
    if($keys.Count -ne 2 -or '123|10.250.251.0/24|10.250.250.1|7777' -notin $keys -or '123|0.0.0.0/0|10.250.250.1|9999' -notin $keys){throw 'Exact route journal ownership failed.'}
    '{"Schema":4,"InterfaceIndex":123,"Gateway":"10.250.250.1","Prefixes":["10.250.251.0/24"],"DnsPrefixes":["10.250.252.53/32"],"DestinationPrefixes":["10.250.253.10/32"],"Metric":7777,"HelperDefault":false}' | Set-Content -LiteralPath (Join-Path $testRuntime 'openvpn-route.json')
    $keys=@(Get-OwnedRouteKeys $testRuntime)
    if($keys.Count -ne 3 -or '123|10.250.252.53/32|10.250.250.1|7777' -notin $keys -or '123|10.250.253.10/32|10.250.250.1|7777' -notin $keys -or @($keys | Where-Object { $_ -like '*|0.0.0.0/0|*' }).Count -ne 0){throw 'Destination/DNS journal ownership failed.'}
} finally {
    # Fixed temporary children only; never recursive removal of a computed tree.
    foreach($name in @('router.json','owned-routes.json','openvpn-route.json')){Remove-Item -LiteralPath (Join-Path $testRuntime $name) -ErrorAction SilentlyContinue}
    Remove-Item -LiteralPath $testRuntime -ErrorAction SilentlyContinue
}
'CollectorsConfirmedPass'
