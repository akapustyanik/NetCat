namespace NetCat.Core;

// Stable for the lifetime of the main router; all adapter-specific values live
// behind these loopback endpoints and never enter its configuration.
public sealed record OpenVpnGateway(int SocksPort, int DnsPort, string RulesPath, string Username, string Password)
{
    // Production gateway uses mutually authenticated AEAD-2022 across the local
    // relay. A process stealing a private backend port cannot impersonate it.
    public string? TransportKey { get; init; }
    public string? OwnedRouteKey { get; init; }
    public string? ExplicitRouteKey { get; init; }
    public string DomainsPath => RulesPath + ".domains.json";
    public int? GuardedDirectPort { get; init; }
    public string? GuardedDirectUsername { get; init; }
    public string? GuardedDirectPassword { get; init; }
    public int? GuardedDirectDnsPort { get; init; }
    public int? GuardedVpnDnsPort { get; init; }
    public int? DirectDnsReturnPort { get; init; }
    public int? VpnDnsReturnPort { get; init; }
    public int? GuardedVpnPort { get; init; }
    public int? VpnReturnPort { get; init; }
    public string? GuardedVpnUsername { get; init; }
    public string? GuardedVpnPassword { get; init; }
}
