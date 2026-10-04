namespace NetCat.Network;

using System.Text.Json;
using NetCat.Core;

public static class EffectiveRuntimeConfigBuilder
{
    public static EffectiveRuntimeConfig Build(
        AppSettings persisted,
        DesiredRuntimeState desired,
        NetworkSnapshot? physical,
        IReadOnlyList<string>? openVpnLearnedRoutes = null)
    {
        var target = JsonSettings.Clone(persisted);

        // Authoritative user intent overrides: desired state is canonical
        target.MainProfileId = desired.SelectedVpnProfileId ?? persisted.MainProfileId;
        target.OpenVpnProfileId = desired.SelectedOpenVpnProfileId ?? persisted.OpenVpnProfileId;
        target.Tun = desired.TunEnabled;

        // Ensure selected OpenVPN profile reflects latest learned routes
        if (target.OpenVpnProfileId.HasValue && openVpnLearnedRoutes != null)
        {
            var ovpnProf = target.Profiles.FirstOrDefault(p => p.Id == target.OpenVpnProfileId.Value);
            if (ovpnProf != null)
            {
                ovpnProf.LearnedRoutes = openVpnLearnedRoutes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        var ovpnProfileObj = target.Profiles.FirstOrDefault(p => p.Id == target.OpenVpnProfileId);
        var learnedRoutes = ovpnProfileObj != null
            ? string.Join(",", ovpnProfileObj.LearnedRoutes.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
            : "";

        string rules = JsonSerializer.Serialize(new { target.Rules, target.Mode, target.YouTube, target.Discord,
            target.TelegramSocks, target.TelegramSocksHost, target.TelegramSocksPort, target.TelegramVpnDefault,
            target.LocalDomains, target.SocksPort,
            Profile = target.Profiles.FirstOrDefault(p => p.Id == target.MainProfileId) is {} p
                ? new { p.Id, p.Host, p.Port, p.Protocol, p.Core, p.OutboundJson } : null }, JsonSettings.Options);
        string dns = target.DirectDns;
        string physFingerprint = physical != null ? JsonSerializer.Serialize(physical, JsonSettings.Options) : "";
        string zapretStrategy = target.ZapretStrategy;

        return new EffectiveRuntimeConfig(
            VpnEnabled: desired.MainVpnEnabled,
            TunEnabled: desired.TunEnabled,
            VpnProfileId: target.MainProfileId,
            ZapretEnabled: desired.ZapretEnabled,
            ZapretStrategy: zapretStrategy,
            Scenario: target.Scenario,
            OpenVpnEnabled: desired.OpenVpnEnabled,
            OpenVpnProfileId: target.OpenVpnProfileId,
            LearnedOpenVpnRoutesFingerprint: learnedRoutes + "|dns=" + target.OpenVpnDns + "|domains=" + target.OpenVpnDomains + "|owners=" + JsonSerializer.Serialize(target.Profiles.Where(p => p.IsOpenVpn).OrderBy(p => p.Id).Select(p => new { p.Id, p.AllowPublicPushedRoutes, p.LearnedRoutes })),
            RoutingRulesFingerprint: rules,
            DnsPolicyFingerprint: dns,
            PhysicalBindingFingerprint: physFingerprint,
            TargetSettings: target
        );
    }
}
