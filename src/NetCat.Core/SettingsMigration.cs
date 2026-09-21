using System.Text.Json.Nodes;

namespace NetCat.Core;
public static class SettingsMigration
{
    public static void Apply(AppSettings settings)
    {
        settings.Mode = settings.Mode is RoutingMode.Global or RoutingMode.SelectiveDirect ? RoutingMode.Global : RoutingMode.Rules;
        settings.Fallback = settings.Mode == RoutingMode.Global ? RouteTarget.Vpn : RouteTarget.Direct;
        foreach (var profile in settings.Profiles) NormalizeCore(profile);
    }
    public static void NormalizeCore(Profile profile)
    {
        if (profile.IsOpenVpn) return;
        if (profile.Protocol.Equals("trojan", StringComparison.OrdinalIgnoreCase)) { profile.Core = "Xray"; return; }
        try
        {
            var config = JsonNode.Parse(profile.OutboundJson);
            if (config?["tls"]?["reality"] != null || config?["protocol"] != null) profile.Core = "Xray";
        }
        catch (System.Text.Json.JsonException) { /* The editor/engine reports invalid profile JSON. */ }
    }
}
