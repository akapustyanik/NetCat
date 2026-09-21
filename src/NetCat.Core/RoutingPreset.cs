namespace NetCat.Core;

public static class RoutingPreset
{
    // Append, never replace/reorder an existing user decision (including a disabled rule).
    public static IReadOnlyList<RoutingRule> MissingStandardRules(IEnumerable<RoutingRule> existing)
    {
        var rules = existing.ToArray();
        RoutingRule[] preset = [
            new() { Kind = RuleKind.GeoSite, Value = "category-ads-all", Target = RouteTarget.Block },
            new() { Kind = RuleKind.GeoSite, Value = "category-ru", Target = RouteTarget.Direct },
            new() { Kind = RuleKind.Domain, Value = "ru", Target = RouteTarget.Direct },
            new() { Kind = RuleKind.Domain, Value = "xn--p1ai", Target = RouteTarget.Direct }
        ];
        return preset.Where(p => !rules.Any(r => r.Kind == p.Kind && r.Value.Equals(p.Value, StringComparison.OrdinalIgnoreCase)
            && r.Network.Length == 0 && r.Port.Length == 0)).ToArray();
    }
    public static void SelectTelegramDefault(AppSettings settings, bool vpn)
    {
        settings.TelegramVpnDefault = vpn;
        settings.TelegramSocks = false;
    }
}
