namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigV2rayService
{
    private static readonly string[] V2rayStaticLocalDomainSuffixes =
    [
        "local",
        "lan",
        "localdomain",
        "localhost",
        "home.arpa"
    ];

    private static readonly string[] V2rayStaticLocalIpCidrs =
    [
        "geoip:private",
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "127.0.0.0/8",
        "169.254.0.0/16",
        "100.64.0.0/10",
        "26.0.0.0/8",
        "::1/128",
        "fc00::/7",
        "fe80::/10"
    ];

    private static readonly string[] V2rayStaticLocalMulticastCidrs =
    [
        "224.0.0.0/4",
        "ff00::/8"
    ];

    private void InsertV2rayLocalBypassRoutingRules()
    {
        if (!context.IsTunEnabled || _coreConfig.routing?.rules == null)
        {
            return;
        }

        var rules = new List<RulesItem4Ray>
        {
            new()
            {
                type = "field",
                outboundTag = Global.DirectTag,
                ip = V2rayStaticLocalIpCidrs
                    .Concat(V2rayStaticLocalMulticastCidrs)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            }
        };

        var domains = BuildV2rayLocalBypassDomains();
        if (domains.Count > 0)
        {
            rules.Add(new()
            {
                type = "field",
                outboundTag = Global.DirectTag,
                domain = domains
            });
        }

        var insertIndex = _coreConfig.routing.rules.FindLastIndex(rule =>
            rule.inboundTag?.Contains("api") == true
            || string.Equals(rule.outboundTag, Global.BlockTag, StringComparison.OrdinalIgnoreCase));

        _coreConfig.routing.rules.InsertRange(Math.Max(insertIndex + 1, 0), rules);
    }

    private List<string> BuildV2rayLocalBypassDomains(IEnumerable<string>? extraDomains = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var suffix in V2rayStaticLocalDomainSuffixes)
        {
            AddV2rayDomainMatcher(result, suffix);
        }
        result.Add("regexp:^[^.]+$");

        try
        {
            AddV2rayDomainMatcher(result, IPGlobalProperties.GetIPGlobalProperties().DomainName);

            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                AddV2rayDomainMatcher(result, networkInterface.GetIPProperties().DnsSuffix);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        foreach (var domain in SplitV2rayCustomLocalBypassDomains())
        {
            AddV2rayDomainMatcher(result, domain);
        }

        foreach (var domain in extraDomains ?? [])
        {
            AddV2rayDomainMatcher(result, domain);
        }

        return result.ToList();
    }

    private IEnumerable<string> SplitV2rayCustomLocalBypassDomains()
    {
        return (_config.TunModeItem.LocalBypassDomains ?? string.Empty)
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.IsNotEmpty());
    }

    private static void AddV2rayDomainMatcher(HashSet<string> result, string? domain)
    {
        if (domain.IsNullOrEmpty())
        {
            return;
        }

        var normalized = domain.Trim().TrimEnd('.');
        if (normalized.IsNullOrEmpty())
        {
            return;
        }

        if (normalized.StartsWith("domain:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("full:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(normalized);
            return;
        }

        result.Add($"domain:{normalized.TrimStart('.')}");
    }
}
