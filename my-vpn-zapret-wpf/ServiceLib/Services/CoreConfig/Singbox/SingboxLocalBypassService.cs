namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private static readonly string[] StaticLocalDomainSuffixes =
    [
        "local",
        "lan",
        "localdomain",
        "localhost",
        "home.arpa"
    ];

    private static readonly string[] StaticLocalDomainRegexes =
    [
        @"^[^.]+$"
    ];

    private static readonly string[] StaticLocalIpCidrs =
    [
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

    private static readonly string[] StaticLocalMulticastCidrs =
    [
        "224.0.0.0/4",
        "ff00::/8"
    ];

    private List<string> BuildTunRouteExcludeAddresses()
    {
        return StaticLocalIpCidrs
            .Concat(StaticLocalMulticastCidrs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<Rule4Sbox> BuildTunLocalBypassRouteRules()
    {
        var rules = new List<Rule4Sbox>
        {
            new()
            {
                outbound = Global.DirectTag,
                ip_cidr = StaticLocalIpCidrs.ToList()
            },
            new()
            {
                outbound = Global.DirectTag,
                ip_is_private = true
            }
        };

        var domainRule = new Rule4Sbox
        {
            outbound = Global.DirectTag,
            domain_suffix = BuildLocalDomainSuffixes(),
            domain_regex = StaticLocalDomainRegexes.ToList()
        };

        ApplyCustomLocalBypassDomains(domainRule);

        if ((domainRule.domain?.Count ?? 0) > 0
            || (domainRule.domain_suffix?.Count ?? 0) > 0
            || (domainRule.domain_keyword?.Count ?? 0) > 0
            || (domainRule.domain_regex?.Count ?? 0) > 0
            || (domainRule.geosite?.Count ?? 0) > 0)
        {
            rules.Add(domainRule);
        }

        return rules;
    }

    private Rule4Sbox BuildTunLocalBypassDnsRule()
    {
        var rule = new Rule4Sbox
        {
            server = Global.SingboxLocalDNSTag,
            strategy = Utils.DomainStrategy4Sbox(context.SimpleDnsItem.Strategy4Freedom),
            domain_suffix = BuildLocalDomainSuffixes(),
            domain_regex = StaticLocalDomainRegexes.ToList()
        };

        ApplyCustomLocalBypassDomains(rule);

        return rule;
    }

    private List<string> BuildLocalDomainSuffixes()
    {
        var result = new HashSet<string>(StaticLocalDomainSuffixes, StringComparer.OrdinalIgnoreCase);

        try
        {
            var globalSuffix = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            AddDomainSuffix(result, globalSuffix);

            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                AddDomainSuffix(result, networkInterface.GetIPProperties().DnsSuffix);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        foreach (var domain in ExtractCustomLocalBypassDomainSuffixes())
        {
            AddDomainSuffix(result, domain);
        }

        return result.ToList();
    }

    private IEnumerable<string> ExtractCustomLocalBypassDomainSuffixes()
    {
        foreach (var item in SplitCustomLocalBypassDomains())
        {
            if (item.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
            {
                yield return item[7..];
            }
        }
    }

    private void ApplyCustomLocalBypassDomains(Rule4Sbox rule)
    {
        foreach (var item in SplitCustomLocalBypassDomains())
        {
            ParseV2Domain(item, rule);
        }
    }

    private IEnumerable<string> SplitCustomLocalBypassDomains()
    {
        return (_config.TunModeItem.LocalBypassDomains ?? string.Empty)
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.IsNotEmpty());
    }

    private static void AddDomainSuffix(HashSet<string> result, string? domain)
    {
        if (domain.IsNullOrEmpty())
        {
            return;
        }

        var normalized = domain.Trim().TrimStart('.').TrimEnd('.');
        if (normalized.IsNullOrEmpty() || !normalized.Contains('.'))
        {
            return;
        }

        result.Add(normalized);
    }
}
