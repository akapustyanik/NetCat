# Dot-source only. Never executes a scenario on import. Guest collectors are VM-only.
$script:ReleaseHash = 'C14B5FAC884BB570489E06E2BA694AD9BEEFFA1ECFF281BB9A5D8F7DCBF68BAE'
function Assert-AcceptanceGuest {
    if ($env:COMPUTERNAME -ne 'WIN-FMC6ISGUK3D') { throw 'Disposable VM required; host execution forbidden.' }
}
function Stop-AcceptanceInstance {
    Assert-AcceptanceGuest
    $app='C:\NetCat-C25-acceptance\work\NetCat.exe'
    $runtime=Join-Path $env:LOCALAPPDATA 'NetCat\runtime'
    $before=Get-OwnedProcesses $app $runtime
    if (-not $before.Complete) { throw 'Unknown process ownership; cleanup refused.' }
    if (@($before.Owned).Count -eq 0) { return }
    $exit=Start-Process -FilePath $app -ArgumentList '--exit' -WindowStyle Hidden -PassThru
    if (-not $exit.WaitForExit(15000)) { throw 'Exit CLI timeout; no force-kill fallback.' }
    Start-Sleep -Seconds 3
    $after=Get-OwnedProcesses $app $runtime $before.Parents
    if (@($after.Owned).Count -or -not $after.Complete) { throw 'Owned process remains; cleanup incomplete.' }
}
function Get-LogOffset($Path) {
    if (Test-Path -LiteralPath $Path) { return (Get-Item -LiteralPath $Path).Length }
    return 0L
}
function Read-AppendedLog($Path, [long]$Offset) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $stream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite,Delete')
    try {
        if ($stream.Length -lt $Offset) { throw 'Diagnostic log rotated/truncated; evidence window invalid.' }
        [void]$stream.Seek($Offset, 'Begin')
        $reader = [IO.StreamReader]::new($stream)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}
function Get-TunObservation([string]$Text) {
    # Export only the one observation, stripped of free text/endpoints.
    $line = @($Text -split "`n" | Where-Object { $_ -match '\bTUN_OBSERVE\b' } | Select-Object -Last 1)
    $fields = if ($line.Count) { [regex]::Matches($line[0], '\b(?:runtimeLifecycle|finalStatus|localDataPath|interfacePresent|routesPresent)=[A-Za-z]+') | ForEach-Object Value } else { @() }
    return 'TUN_OBSERVE ' + ($fields -join ' ')
}
function Test-AuthoritativeTun([string]$Text) {
    $line = Get-TunObservation $Text
    return ($line -match '\bruntimeLifecycle=Running\b' -and $line -match '\bfinalStatus=Healthy\b')
}
function Test-OpenVpnReady([string]$Text, $Inventory) {
    $process=@($Inventory.Owned | Where-Object Role -eq 'OpenVpn')
    if($process.Count -ne 1){return $false}
    $last=@($Text -split "`n" | Where-Object {$_ -match 'OPENVPN_GENERATION_READY|OPENVPN_RECONNECT|ACTION StopOpenVpn result=ok|ACTION StartOpenVpn result=failed'} | Select-Object -Last 1)
    return $last.Count -eq 1 -and $last[0] -match ('OPENVPN_GENERATION_READY pid=' + $process[0].Pid + '\b')
}
function Get-LifecycleCounts([string]$Text) {
    return [ordered]@{
        MainSingBox = [regex]::Matches($Text, 'MAIN_ROUTER lifecycle=[^\r\n]* -> Starting\b').Count
        OpenVpn = [regex]::Matches($Text, 'ACTION StartOpenVpn result=(?:ok|failed)\b').Count
        OpenVpnSidecar = [regex]::Matches($Text, 'OPENVPN_SIDECAR state=started pid=\d+').Count
        Zapret = @([regex]::Matches($Text, 'ZAPRET_PROCESS state=started pid=(\d+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique).Count
        RetryEventCount = [regex]::Matches($Text, '\bRETRY in=\d').Count
        ReconcileCount = [regex]::Matches($Text, '\bRECONCILE gen=\d+ trigger=\w+ begin\b').Count
        OpenVpnSessionReconnectCount = [regex]::Matches($Text, 'OPENVPN_RECONNECT state=long-reconnect\b').Count
    }
}
function Get-SiteAStatus([string]$Domain) {
    $result = [ordered]@{ DnsSucceeded=$false; TcpSucceeded=$false; TlsCertificateValid=$false; HttpStatus=0; CorporatePathObserved=$false }
    $tcp = $null; $tls = $null
    try {
        if (-not $Domain -or $Domain -eq 'Site-A' -or $Domain -eq 'conflict.corp.test') { throw 'Secret Site-A fixture required.' }
        # No certificate override; hostname and chain validation use platform defaults.
        $lookup = [Net.Dns]::GetHostAddressesAsync($Domain)
        if (-not $lookup.Wait(5000)) { throw 'DNS timeout' }
        $result.DnsSucceeded = $lookup.Result.Count -gt 0
        $tcp = [Net.Sockets.TcpClient]::new()
        if (-not $tcp.ConnectAsync($Domain,443).Wait(5000)) { throw 'TCP timeout' }
        $result.TcpSucceeded = $true
        try {
            $corporate=@(Get-NetAdapter -IncludeHidden | Where-Object Name -eq 'NetCat-OpenVPN')
            $effective=@(Find-NetRoute -RemoteIPAddress $tcp.Client.RemoteEndPoint.Address.IPAddressToString -ErrorAction Stop)
            $result.CorporatePathObserved=$corporate.Count -eq 1 -and @($effective | Where-Object InterfaceIndex -eq $corporate[0].ifIndex).Count -gt 0
        } catch { } # Unknown route attribution stays false; never infer from HTTP 200.
        $tcp.ReceiveTimeout = 5000; $tcp.SendTimeout = 5000
        $tls = [Net.Security.SslStream]::new($tcp.GetStream(),$false)
        if (-not $tls.AuthenticateAsClientAsync($Domain).Wait(5000)) { throw 'TLS timeout' }
        $result.TlsCertificateValid = $tls.IsAuthenticated
        $request = [Text.Encoding]::ASCII.GetBytes("HEAD / HTTP/1.1`r`nHost: $Domain`r`nConnection: close`r`n`r`n")
        $tls.Write($request,0,$request.Length)
        # Read the status line only. No response body is retained or serialized.
        $line = [Collections.Generic.List[byte]]::new()
        while ($line.Count -lt 256) { $b=$tls.ReadByte(); if ($b -lt 0 -or $b -eq 10) { break }; $line.Add([byte]$b) }
        $status = [Text.Encoding]::ASCII.GetString($line.ToArray())
        if ($status -match '^HTTP/\d(?:\.\d)?\s+(\d{3})\b') { $result.HttpStatus = [int]$Matches[1] }
    } catch { } finally { if ($tls) {$tls.Dispose()}; if ($tcp) {$tcp.Dispose()} }
    return $result
}
function Get-ProcessIdentity($Process) {
    return ([string]$Process.ProcessId + '@' + $Process.CreationDate.ToUniversalTime().ToString('o'))
}
function Get-OwnedProcesses([string]$Exe, [string]$Runtime, $KnownParents = @()) {
    $all = @(Get-CimInstance Win32_Process)
    $roots = @($all | Where-Object { $_.ExecutablePath -eq $Exe })
    if ($roots.Count -gt 1) { throw 'Duplicate exact-path NetCat processes.' }
    $parents = @(@($KnownParents) + @($roots | ForEach-Object { [pscustomobject]@{ Pid=[int]$_.ProcessId; CreationTime=$_.CreationDate.ToUniversalTime().ToString('o'); Identity=(Get-ProcessIdentity $_) } }) | Sort-Object Identity -Unique)
    $owned = @(); $foreign = @(); $unknown = @()
    foreach ($p in $all | Where-Object Name -in @('NetCat.exe','sing-box.exe','xray.exe','openvpn.exe','winws.exe')) {
        $role = $null; $config = $null
        $root = $p.ExecutablePath -eq $Exe
        $parent = @($parents | Where-Object { $_.Pid -eq $p.ParentProcessId -and [datetime]$_.CreationTime -le $p.CreationDate })
        $liveParent=@($all | Where-Object ProcessId -eq $p.ParentProcessId)
        if($liveParent.Count -eq 1){$parent=@($parent | Where-Object Identity -eq (Get-ProcessIdentity $liveParent[0]))}
        $inModules = $p.ExecutablePath -and $p.ExecutablePath.StartsWith(((Split-Path $Exe) + '\modules\'), [StringComparison]::OrdinalIgnoreCase)
        $relative=switch($p.Name){'sing-box.exe'{'sing-box\sing-box.exe'};'xray.exe'{'xray\xray.exe'};'openvpn.exe'{'openvpn\openvpn.exe'};'winws.exe'{'zapret\bin\winws.exe'};default{''}}
        $exactChildPath=$relative -and $p.ExecutablePath -eq (Join-Path (Join-Path (Split-Path $Exe) 'modules') $relative)
        if ($root) { $role='NetCat' }
        elseif ($parent.Count -eq 1 -and $exactChildPath) {
            $candidates = @(switch ($p.Name) {
                'sing-box.exe' { [pscustomobject]@{Role='MainSingBox';File='router.json'};[pscustomobject]@{Role='OpenVpnSidecar';File='openvpn-gateway\gateway.json'} }
                'xray.exe' { [pscustomobject]@{Role='Xray';File='xray.json'} }
                'openvpn.exe' { [pscustomobject]@{Role='OpenVpn';File='openvpn\openvpn.conf'} }
                'winws.exe' { [pscustomobject]@{Role='Zapret';File='zapret\zapret-hosts.txt'} }
            })
            foreach ($candidate in $candidates) {
                # Exact runtime config path, including a sidecar generation subfolder.
                $matches = [regex]::Matches([string]$p.CommandLine, '(?i)(?:"(?<path>[A-Z]:\\[^"]+\.(?:json|conf|txt))"|(?<path>[A-Z]:\\[^\s"]+\.(?:json|conf|txt)))')
                foreach($match in $matches){
                    $path = $match.Groups['path'].Value
                    if ($path -and [IO.Path]::GetFullPath($path) -eq (Join-Path $Runtime $candidate.File)) { $role=$candidate.Role; $config=$path; break }
                }
            }
        }
        $record = [pscustomobject]@{ Role=$role; Pid=[int]$p.ProcessId; ExecutablePath=$p.ExecutablePath; CreationTime=$p.CreationDate.ToUniversalTime().ToString('o'); Identity=(Get-ProcessIdentity $p); ParentPid=[int]$p.ParentProcessId; ExpectedRuntimePath=$config; ParentIdentity=if($parent.Count -eq 1){$parent[0].Identity}else{$null} }
        if ($role) { $owned += $record }
        else { $foreign += $record; if (-not $p.ExecutablePath -or $inModules -or ($parent.Count -gt 0)) { $unknown += $record } }
    }
    return [pscustomobject]@{ Owned=$owned; Foreign=$foreign; Unknown=$unknown; Parents=$parents; Complete=($unknown.Count -eq 0) }
}
function Stop-ExactOwnedProcess($Target, [string]$Exe, [string]$Runtime, $Ledger) {
    Assert-AcceptanceGuest
    $current = Get-OwnedProcesses $Exe $Runtime
    $same = @($current.Owned | Where-Object { $_.Identity -eq $Target.Identity -and $_.ExecutablePath -eq $Target.ExecutablePath -and $_.ParentIdentity -eq $Target.ParentIdentity -and $_.ExpectedRuntimePath -eq $Target.ExpectedRuntimePath })
    if ($same.Count -ne 1 -or $Target.Role -eq 'NetCat') { throw 'Kill target no longer has exact ownership.' }
    # Open a handle and compare StartTime; Kill uses that handle, not a later reused PID.
    $handle = [Diagnostics.Process]::GetProcessById($Target.Pid)
    try {
        [void]$handle.Handle
        # CIM datetime has microsecond precision; Process.StartTime has 100ns ticks.
        if ([math]::Abs(($handle.StartTime.ToUniversalTime() - ([datetime]$Target.CreationTime).ToUniversalTime()).Ticks) -gt 100) { throw 'PID reused.' }
        $Ledger.Add([pscustomobject]@{ Identity=$Target.Identity; Pid=$Target.Pid; Role=$Target.Role; Time=[datetime]::UtcNow.ToString('o') })
        $handle.Kill(); [void]$handle.WaitForExit(5000)
    } finally { $handle.Dispose() }
}
function Get-OwnedRouteKeys([string]$Runtime) {
    # These keys remain local to the guest; only counts enter shareable evidence.
    $keys = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $Runtime -Filter 'openvpn-route.json' -Recurse -ErrorAction SilentlyContinue)) {
        $j = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($j.Schema -in @(3,4) -and $j.InterfaceIndex -and $j.Gateway) {
            $prefixes = @($j.Prefixes)
            if ($j.Schema -eq 4) {
                if ($j.HelperDefault) { throw 'Schema 4 may not own a default route.' }
                $prefixes += @($j.DnsPrefixes) + @($j.DestinationPrefixes)
            }
            foreach ($prefix in @($prefixes | Sort-Object -Unique)) { $keys += "$($j.InterfaceIndex)|$prefix|$($j.Gateway)|$($j.Metric)" }
            if ($j.Schema -eq 3 -and $j.HelperDefault) { $keys += "$($j.InterfaceIndex)|0.0.0.0/0|$($j.Gateway)|9999" }
        } else { throw 'Unrecognized owned route journal; cannot claim cleanup.' }
    }
    return @($keys | Sort-Object -Unique)
}
function Count-OwnedRoutes($Keys) {
    $set = @{}; foreach ($key in @($Keys)) { $set[$key]=$true }
    return @(Get-NetRoute -ErrorAction Stop | Where-Object { $set.ContainsKey("$($_.InterfaceIndex)|$($_.DestinationPrefix)|$($_.NextHop)|$($_.RouteMetric)") }).Count
}
function Write-AcceptanceEvidence($Stage, $Inputs, $Start, $End, $RunId, $Runner, $Output) {
    $requestPath = $Output + '.request.json'
    [ordered]@{Stage=$Stage;Inputs=$Inputs;StartTime=$Start.ToString('o');EndTime=$End.ToString('o');RunId=$RunId} | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $requestPath -Encoding UTF8
    $fresh=$Output+'.'+[guid]::NewGuid().ToString('N')+'.new'
    & $Runner --evaluate $requestPath $fresh | Out-Null
    if (-not (Test-Path -LiteralPath $fresh)) { throw 'Evidence evaluator did not produce fresh output.' }
    & $Runner --validate-evidence $fresh | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Evidence failed independent recalculation.' }
    Move-Item -LiteralPath $fresh -Destination $Output -Force
    Remove-Item -LiteralPath $requestPath
}
