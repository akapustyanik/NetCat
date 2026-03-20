namespace ServiceLib.Models;

public class QuickRuleConfig
{
    public List<string> DirectProcesses { get; set; } = new();
    public List<string> DirectDomains { get; set; } = new();
    public List<string> ProxyDomains { get; set; } = new();
    public List<string> BlockDomains { get; set; } = new();
    public bool UseProxyDomainsPreset { get; set; }
    public bool ProxyOnlyMode { get; set; }
    public bool BypassPrivate { get; set; } = true;
    public string? RoutingId { get; set; }
}
