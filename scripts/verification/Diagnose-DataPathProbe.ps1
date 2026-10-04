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

        # Check current routes
        $routes = Get-NetRoute -AddressFamily IPv4 | Where-Object { $_.DestinationPrefix -like '10.*' }

        # Probe [private target]:53 TCP
        $tcpDns = $null
        try {
            $client = [System.Net.Sockets.TcpClient]::new()
            $async = $client.BeginConnect($targets.DnsServer, 53, $null, $null)
            $ok = $async.AsyncWaitHandle.WaitOne(3000, $true)
            if ($ok) {
                $client.EndConnect($async)
                $tcpDns = "Connected"
            } else {
                $client.Close()
                $tcpDns = "TimedOut"
            }
        } catch {
            $tcpDns = "Failed (details kept private)"
        }

        # Probe [private target]:53 UDP (standard DNS query for [private domain])
        $udpDns = $null
        try {
            $udp = [System.Net.Sockets.UdpClient]::new()
            $udp.Client.ReceiveTimeout = 3000
            $udp.Client.SendTimeout = 3000
            # Header: ID=0x1234, Flags=0x0100 (standard query, recursion desired), QDCOUNT=1
            $q = [byte[]]$targets.DnsQuery
            $ep = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($targets.DnsServer), 53)
            [void]$udp.Send($q, $q.Length, $ep)
            $recvEp = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            $resp = $udp.Receive([ref]$recvEp)
            if ($resp.Length -gt 12) {
                $udpDns = "ResponseReceived (len=$($resp.Length))"
            } else {
                $udpDns = "ShortResponse (len=$($resp.Length))"
            }
        } catch {
            $udpDns = "Failed (details kept private)"
        }

        # Probe Site-A ([private target]:443) TCP
        $tcpSiteA = $null
        try {
            $client = [System.Net.Sockets.TcpClient]::new()
            $async = $client.BeginConnect($targets.SiteAIp, 443, $null, $null)
            $ok = $async.AsyncWaitHandle.WaitOne(3000, $true)
            if ($ok) {
                $client.EndConnect($async)
                $tcpSiteA = "Connected"
            } else {
                $client.Close()
                $tcpSiteA = "TimedOut"
            }
        } catch {
            $tcpSiteA = "Failed (details kept private)"
        }

        [ordered]@{
            Routes10 = @($routes).Count
            TcpDns_Port53 = $tcpDns
            UdpDns_Port53 = $udpDns
            TcpSiteA_Port443 = $tcpSiteA
        }
    }

    $res | Format-List | Out-String | Write-Host

} finally {
    Remove-PSSession $s
}