using NetCat.Core;

namespace NetCat.Network;

// Rebuilt from live profiles, never a process-global accumulating set.
// Remembered prefixes intercept traffic; only the verified generation may forward it.
public sealed record OpenVpnOwnership(string[] Active, string[] Blocked)
{
    public string[] Known => Active.Concat(Blocked).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
    public static OpenVpnOwnership Build(AppSettings settings, OpenVpnLink? link)
    {
        var profiles = settings.Profiles.Where(p => p.IsOpenVpn).ToArray();
        var id = link?.ProfileId ?? settings.OpenVpnProfileId;
        var profile = profiles.FirstOrDefault(p => p.Id == id);
        // Unscoped links are only used by isolated native/probe callers. Production
        // OpenVpnService always supplies identity and generation.
        var active = link == null || link.ProfileId.HasValue && profile == null ? [] : link.LearnedRoutes
            .Where(r => OpenVpnRoutes.ValidatePushedRoute(r, profile?.AllowPublicPushedRoutes ?? !link.ProfileId.HasValue)).Distinct().Order().ToArray();
        var known = profiles.SelectMany(p => link != null && p.Id == id ? active :
            p.LearnedRoutes.Where(r => OpenVpnRoutes.ValidatePushedRoute(r, p.AllowPublicPushedRoutes)))
            .Concat(active).Distinct().ToArray();
        return new(active, known.Except(active).Order().ToArray());
    }
}
