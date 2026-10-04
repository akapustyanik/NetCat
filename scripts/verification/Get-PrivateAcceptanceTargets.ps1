function Get-PrivateAcceptanceTargets {
    param([Parameter(Mandatory=$true)][string]$Path)
    $canonical = [IO.Path]::GetFullPath($Path)
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\') + '\'
    if ($canonical.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Acceptance targets must be stored outside the repository.'
    }
    try {
        $targets = Get-Content -LiteralPath $canonical -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        foreach ($name in @('SiteAIp','DnsServer','CorporateGateway')) {
            $address = $null
            if (![Net.IPAddress]::TryParse([string]$targets.$name, [ref]$address) -or
                $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'Invalid target' }
        }
        if ([Uri]::CheckHostName([string]$targets.SiteADomain) -ne [UriHostNameType]::Dns) { throw 'Invalid domain' }
        $parts = ([string]$targets.CorporatePrefix).Split('/')
        $prefixAddress = $null
        $prefixLength = 0
        if ($parts.Length -ne 2 -or ![Net.IPAddress]::TryParse($parts[0], [ref]$prefixAddress) -or
            $prefixAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
            ![int]::TryParse($parts[1], [ref]$prefixLength) -or $prefixLength -lt 1 -or $prefixLength -gt 32) { throw 'Invalid prefix' }
        return $targets
    } catch { throw 'Private acceptance targets are missing or invalid; values were not logged.' }
}

function New-PrivateDnsQuery {
    param([Parameter(Mandatory=$true)][string]$Domain)
    $domain = ([Globalization.IdnMapping]::new()).GetAscii($Domain.TrimEnd('.'))
    $bytes = [Collections.Generic.List[byte]]::new()
    $bytes.AddRange([byte[]]@(0x12,0x34,0x01,0x00,0x00,0x01,0,0,0,0,0,0))
    foreach ($label in $domain.Split('.')) {
        $encoded = [Text.Encoding]::ASCII.GetBytes($label)
        if ($encoded.Length -lt 1 -or $encoded.Length -gt 63) { throw 'Invalid DNS label' }
        $bytes.Add([byte]$encoded.Length)
        $bytes.AddRange($encoded)
    }
    $bytes.AddRange([byte[]]@(0,0,1,0,1))
    if ($bytes.Count -gt 271) { throw 'Invalid DNS name length' }
    return ,$bytes.ToArray()
}
