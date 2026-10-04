param([string]$TargetsPath = (Join-Path $env:LOCALAPPDATA 'NetCat-Test/acceptance-targets.json'))
. (Join-Path $PSScriptRoot 'Get-PrivateAcceptanceTargets.ps1')
$targets = Get-PrivateAcceptanceTargets -Path $TargetsPath
$targets | Add-Member -NotePropertyName DnsQuery -NotePropertyValue (New-PrivateDnsQuery -Domain $targets.SiteADomain)
$ErrorActionPreference = 'Stop'
$cred = Import-Clixml "$env:LOCALAPPDATA\NetCat-Test\vm_win11_credential.xml"
$s = New-PSSession -VMName VM_win11 -Credential $cred
try {
    $res = Invoke-Command -Session $s -ArgumentList $targets -ScriptBlock {
        param($targets)
        Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

        # Stop any existing NetCat
        Get-Process NetCat, sing-box, openvpn, xray, zapret -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

        $work = 'C:\NetCat-C26-acceptance\work'
        $openvpnExe = Join-Path $work 'modules\openvpn\openvpn.exe'
        $conf = 'C:\NetCat-C26-acceptance\saved-openvpn.conf'
        $ovpnLog = 'C:\NetCat-C26-acceptance\standalone-ovpn.log'

        # Start openvpn directly
        Write-Host "Starting OpenVPN standalone..."
        $proc = Start-Process -FilePath $openvpnExe -ArgumentList @(
            '--config', $conf,
            '--dev-node', 'NetCat-OpenVPN',
            '--route-noexec',
            '--verb', '3'
        ) -RedirectStandardOutput $ovpnLog -RedirectStandardError ($ovpnLog + '.err') -PassThru

        # Wait for Initialization Sequence Completed
        $deadline = [datetime]::UtcNow.AddSeconds(20)
        $initialized = $false
        do {
            Start-Sleep -Milliseconds 500
            if (Test-Path $ovpnLog) {
                $content = Get-Content $ovpnLog -Raw
                if ($content -match 'Initialization Sequence Completed') {
                    $initialized = $true
                    break
                }
            }
        } while ([datetime]::UtcNow -lt $deadline)

        Write-Host "OpenVPN Initialized: $initialized"

        # Find NetCat-OpenVPN interface
        $nic = Get-NetAdapter -IncludeHidden | Where-Object Name -eq 'NetCat-OpenVPN'
        $ip = (Get-NetIPAddress -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).IPAddress

        # Manually install the corporate route [private target] via OpenVPN gateway [private target]
        $routeInstalled = $false
        try {
            New-NetRoute -InterfaceIndex $nic.ifIndex -DestinationPrefix $targets.CorporatePrefix -NextHop $targets.CorporateGateway -RouteMetric 7777 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
            $routeInstalled = $true
        } catch {
            Write-Host "Route install failed; details kept private."
        }

        # Check BestRoute for [private target] and [private target]
        $bestDns = Find-NetRoute -RemoteIPAddress $targets.DnsServer -ErrorAction SilentlyContinue
        $bestSiteA = Find-NetRoute -RemoteIPAddress $targets.SiteAIp -ErrorAction SilentlyContinue

        # TEST 1: Ping / ICMP to gateway and DNS
        $pingGateway = Test-Connection -ComputerName $targets.CorporateGateway -Count 2 -Quiet
        $pingDns = Test-Connection -ComputerName $targets.DnsServer -Count 2 -Quiet
        $pingSiteA = Test-Connection -ComputerName $targets.SiteAIp -Count 2 -Quiet

        # TEST 2: TCP connection to DNS [private target]:53
        $tcpDnsResult = $null
        try {
            $tcp = [System.Net.Sockets.TcpClient]::new()
            $tcp.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($ip), 0))
            $netOrder = [uint32][System.Net.IPAddress]::HostToNetworkOrder([int]$nic.ifIndex)
            $tcp.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::IP, [System.Net.Sockets.SocketOptionName]31, [BitConverter]::GetBytes($netOrder))
            $async = $tcp.BeginConnect($targets.DnsServer, 53, $null, $null)
            if ($async.AsyncWaitHandle.WaitOne(3000, $true)) {
                $tcp.EndConnect($async)
                $tcpDnsResult = "Connected"
            } else {
                $tcpDnsResult = "TimedOut"
            }
            $tcp.Dispose()
        } catch {
            $tcpDnsResult = "Failed (details kept private)"
        }

        # TEST 3: UDP query to DNS [private target]:53
        $udpDnsResult = $null
        try {
            $udp = [System.Net.Sockets.UdpClient]::new()
            $udp.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($ip), 0))
            $netOrder = [uint32][System.Net.IPAddress]::HostToNetworkOrder([int]$nic.ifIndex)
            $udp.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::IP, [System.Net.Sockets.SocketOptionName]31, [BitConverter]::GetBytes($netOrder))
            $udp.Client.ReceiveTimeout = 3000
            $udp.Client.SendTimeout = 3000
            $q = [byte[]]$targets.DnsQuery
            $ep = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($targets.DnsServer), 53)
            [void]$udp.Send($q, $q.Length, $ep)
            $recvEp = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            $resp = $udp.Receive([ref]$recvEp)
            $udpDnsResult = "Success (received $($resp.Length) bytes)"
            $udp.Dispose()
        } catch {
            $udpDnsResult = "Failed (details kept private)"
        }

        # TEST 4: TCP connection to Site-A ([private target]:443)
        $tcpSiteAResult = $null
        try {
            $tcp = [System.Net.Sockets.TcpClient]::new()
            $tcp.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($ip), 0))
            $netOrder = [uint32][System.Net.IPAddress]::HostToNetworkOrder([int]$nic.ifIndex)
            $tcp.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::IP, [System.Net.Sockets.SocketOptionName]31, [BitConverter]::GetBytes($netOrder))
            $async = $tcp.BeginConnect($targets.SiteAIp, 443, $null, $null)
            if ($async.AsyncWaitHandle.WaitOne(3000, $true)) {
                $tcp.EndConnect($async)
                $tcpSiteAResult = "Connected"
            } else {
                $tcpSiteAResult = "TimedOut"
            }
            $tcp.Dispose()
        } catch {
            $tcpSiteAResult = "Failed (details kept private)"
        }

        # Cleanup
        try { Remove-NetRoute -InterfaceIndex $nic.ifIndex -DestinationPrefix $targets.CorporatePrefix -Confirm:$false -ErrorAction SilentlyContinue } catch {}
        $proc | Stop-Process -Force -ErrorAction SilentlyContinue

        [ordered]@{
            Initialized = $initialized
            NicIndex = $nic.ifIndex
            NicIpPresent = -not [string]::IsNullOrWhiteSpace($ip)
            RouteInstalled = $routeInstalled
            BestDnsInterface = $bestDns.InterfaceIndex
            BestSiteAInterface = $bestSiteA.InterfaceIndex
            PingGateway = $pingGateway
            PingDns = $pingDns
            PingSiteA = $pingSiteA
            TcpDns_Port53 = $tcpDnsResult
            UdpDns_Port53 = $udpDnsResult
            TcpSiteA_Port443 = $tcpSiteAResult
        }
    }

    $res | Format-List | Out-String | Write-Host

} finally {
    Remove-PSSession $s
}