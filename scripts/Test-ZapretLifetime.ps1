param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$taskRoot=(Resolve-Path -LiteralPath $Root).Path
$taskRun=Join-Path $taskRoot ('artifacts/zapret-live-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRun | Out-Null
$taskReport=Join-Path $taskRoot 'artifacts/zapret-live-report.json'
$taskResults=@(); $taskOrphans=@()
function Test-Alive([int]$ProcessId) { return $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) }
function Wait-Check([scriptblock]$Check) { $taskLimit=[DateTime]::UtcNow.AddSeconds(15); while(-not (& $Check)) { if([DateTime]::UtcNow -gt $taskLimit) {throw 'Lifecycle verification timed out.'}; Start-Sleep -Milliseconds 30 } }
try {
    if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator token is required for the real WinDivert test.' }
    $taskWinws=Join-Path $taskRoot 'bin/zapret/bin/winws.exe'
    $taskProbe=Join-Path $taskRoot 'tests/NetCat.LifetimeProbe/bin/Debug/net8.0-windows/NetCat.LifetimeProbe.exe'
    $taskArgsFile=Join-Path $taskRun 'filter.json'
    @('--wf-raw=outbound and ip and ip.DstAddr == 192.0.2.127 and tcp.DstPort == 65533','--filter-tcp=65533','--dpi-desync=fake') | ConvertTo-Json | Set-Content -LiteralPath $taskArgsFile -Encoding UTF8
    foreach($taskMode in @('stop','dispose','kill','exit','stop','kill')) {
        $taskFile=Join-Path $taskRun ([guid]::NewGuid().ToString('N')+'.json')
        $taskStart=[Diagnostics.ProcessStartInfo]::new($taskProbe)
        $taskStart.UseShellExecute=$false; $taskStart.CreateNoWindow=$true; $taskStart.WindowStyle='Hidden'
        $taskStart.Arguments='winws-owner "'+$taskFile+'" "'+$taskWinws+'" "'+$taskArgsFile+'"'
        $taskStart.RedirectStandardError=$true
        $taskOwner=[Diagnostics.Process]::Start($taskStart); $taskError=$taskOwner.StandardError.ReadToEndAsync()
        try {
            Wait-Check { (Test-Path -LiteralPath $taskFile) -or $taskOwner.HasExited }
            if($taskOwner.HasExited) {throw $taskError.Result}
            $taskChildId=[int]((Get-Content -Raw -LiteralPath $taskFile | ConvertFrom-Json)[0])
            if(-not (Test-Alive $taskChildId)) {throw 'Winws did not remain running.'}
            if($taskMode -eq 'kill') {Stop-Process -Id $taskOwner.Id -Force}
            else { [IO.File]::WriteAllText($taskFile+'.command',$taskMode); Wait-Check {Test-Path -LiteralPath ($taskFile+'.done')} }
            Wait-Check {-not (Test-Alive $taskChildId)}
            $taskResults+=@{Mode=$taskMode;Owner=$taskOwner.Id;Winws=$taskChildId;Stopped=$true}
        } finally { if(-not $taskOwner.HasExited) {Stop-Process -Id $taskOwner.Id -Force}; $taskOwner.Dispose() }
    }
    # Only legacy NetCat-owned orphans: exact workspace path, NetCat runtime hostlist,
    # and a missing/reused parent. Never terminate all winws processes by name.
    foreach($taskChild in @(Get-CimInstance Win32_Process -Filter "Name='winws.exe'")) {
        if(-not $taskChild.ExecutablePath -or -not $taskChild.ExecutablePath.StartsWith($taskRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $taskChild.CommandLine -notmatch '(?i)NetCat[\\/]runtime[\\/]zapret[\\/]zapret-hosts') {continue}
        $taskParent=Get-CimInstance Win32_Process -Filter ("ProcessId="+$taskChild.ParentProcessId)
        if($taskParent -and $taskParent.CreationDate -lt $taskChild.CreationDate) {continue}
        $taskExact=Get-Process -Id $taskChild.ProcessId -ErrorAction SilentlyContinue
        if($taskExact -and $taskExact.StartTime -eq $taskChild.CreationDate) {Stop-Process -Id $taskChild.ProcessId -Force; $taskOrphans+=$taskChild.ProcessId}
    }
    @{Success=$true;Checks=$taskResults;RemovedLegacyOrphans=$taskOrphans;Filter='192.0.2.127 TCP 65533'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskReport -Encoding UTF8
} catch { @{Success=$false;Checks=$taskResults;Error=$_.Exception.Message} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskReport -Encoding UTF8; exit 1 }
