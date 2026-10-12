using NetCat.Core;
namespace NetCat.Engine;
public sealed class SubscriptionSecurityApprovalRequiredException(int count) : IOException($"Подписка отключает проверку TLS-сертификатов в {count} профилях. Обновление не применено; требуется явное подтверждение в настройках подписки.");
public sealed record SubscriptionMergeResult(AppSettings Settings, int Added, int Updated, int Removed, int Retained, int Rejected, int Excluded = 0)
{
    public string Summary(int received) => $"Получено: {received}; добавлено: {Added}; обновлено: {Updated}; удалено: {Removed}; сохранено выбранных: {Retained}; отклонено: {Rejected}; исключено пользователем: {Excluded}.";
}
public static class SubscriptionMerge
{
    private static string Source(Subscription sub) => Uri.TryCreate(sub.Url, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : sub.Url;
    private static string ItemIdentity(Profile profile)
    {
        if (!string.IsNullOrEmpty(profile.SubscriptionItemId)) return profile.SubscriptionItemId;
        try { return ProfileIdentity.Key(profile); }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        { return "invalid:" + profile.Id; }
    }

    private static bool MatchesStoredProfile(Profile saved, Profile incoming, string stable, string key, bool legacy)
    {
        if (saved.SubscriptionItemId == stable) return true;
        if (saved.SubscriptionItemId.StartsWith("manifest:", StringComparison.Ordinal)) return false;
        try { return ProfileIdentity.Key(saved) == key || legacy && ProfileIdentity.MatchesLegacyGrpc(saved, incoming); }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        { return false; } // An invalid stored entry must not block other valid subscription items.
    }

    public static (int Subscriptions, int Profiles) NormalizeSources(AppSettings settings, IEnumerable<Guid>? preserveIds = null)
    {
        var preserved = (preserveIds ?? []).ToHashSet();
        if (settings.MainProfileId is { } main) preserved.Add(main);
        if (settings.OpenVpnProfileId is { } openVpn) preserved.Add(openVpn);
        int subscriptions = 0, profiles = 0;
        foreach (var group in settings.Subscriptions.Where(s => !string.IsNullOrWhiteSpace(s.Url)).GroupBy(Source, StringComparer.Ordinal).ToArray())
        {
            var canonical = group.First();
            var ids = group.Select(s => s.Id).ToHashSet();
            var exclusions = group.SelectMany(s => s.ExcludedProfileKeys).Distinct(StringComparer.Ordinal).ToList();
            // Never discard deletion records to fit the settings limit.
            if (exclusions.Count > 10000) continue;
            canonical.ExcludedProfileKeys = exclusions;
            canonical.UpdateHours = group.Min(s => s.UpdateHours);
            canonical.AllowInsecureTransport = group.All(s => s.AllowInsecureTransport);
            if (ids.Count > 1) canonical.UpdatedAt = null; // Confirm differing legacy copies with the provider on the next refresh.
            foreach (var profile in settings.Profiles.Where(p => p.SubscriptionId.HasValue && ids.Contains(p.SubscriptionId.Value)))
            {
                profile.SubscriptionId = canonical.Id;
                profile.SubscriptionSource = canonical.Name;
            }
            subscriptions += settings.Subscriptions.RemoveAll(s => ids.Contains(s.Id) && s.Id != canonical.Id);
            foreach (var copies in settings.Profiles.Where(p => p.SubscriptionId == canonical.Id).GroupBy(ItemIdentity).ToArray())
            {
                var remaining = copies.OrderByDescending(p => preserved.Contains(p.Id)).ToList();
                while (remaining.Count > 0)
                {
                    var keep = remaining[0]; remaining.RemoveAt(0);
                    foreach (var duplicate in remaining.Where(p => !preserved.Contains(p.Id) && EquivalentConnection(keep, p)).ToArray())
                    {
                        settings.Profiles.Remove(duplicate); remaining.Remove(duplicate); profiles++;
                    }
                }
            }
        }
        return (subscriptions, profiles);
    }

    private static bool EquivalentConnection(Profile first, Profile second)
    {
        if (!string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase) || first.Port != second.Port ||
            !string.Equals(first.Protocol, second.Protocol, StringComparison.OrdinalIgnoreCase) || first.Core != second.Core ||
            first.Username != second.Username || first.Password != second.Password ||
            first.OpenVpnConfig != second.OpenVpnConfig || first.OpenVpnLegacyProviderRequired != second.OpenVpnLegacyProviderRequired ||
            first.Candidate != second.Candidate || first.AllowPublicPushedRoutes != second.AllowPublicPushedRoutes ||
            !first.LearnedRoutes.SequenceEqual(second.LearnedRoutes)) return false;
        // The outbound tag labels a connection; it does not change its transport.
        try
        {
            var a = System.Text.Json.Nodes.JsonNode.Parse(first.OutboundJson);
            var b = System.Text.Json.Nodes.JsonNode.Parse(second.OutboundJson);
            if (a is System.Text.Json.Nodes.JsonObject ao) ao.Remove("tag");
            if (b is System.Text.Json.Nodes.JsonObject bo) bo.Remove("tag");
            return System.Text.Json.Nodes.JsonNode.DeepEquals(a, b);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public static IReadOnlyList<Profile> DeletionTargets(AppSettings settings, Profile profile)
    {
        var sub = settings.Subscriptions.FirstOrDefault(s => s.Id == profile.SubscriptionId);
        if (sub == null) return [profile];
        var ids = settings.Subscriptions.Where(s => Source(s) == Source(sub)).Select(s => s.Id).ToHashSet();
        var identity = ItemIdentity(profile);
        return settings.Profiles.Where(p => p.Id == profile.Id || p.SubscriptionId.HasValue && ids.Contains(p.SubscriptionId.Value) && ItemIdentity(p) == identity).ToArray();
    }

    public static IReadOnlyList<Profile> ProfilesForSubscription(AppSettings settings, Guid subscriptionId)
    {
        var sub = settings.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
        if (sub == null) return [];
        var ids = settings.Subscriptions.Where(s => Source(s) == Source(sub)).Select(s => s.Id).ToHashSet();
        return settings.Profiles.Where(p => p.SubscriptionId.HasValue && ids.Contains(p.SubscriptionId.Value)).ToArray();
    }

    public static void RemoveSubscription(AppSettings settings, Guid subscriptionId)
    {
        var sub = settings.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
        if (sub == null) return;
        var profiles = ProfilesForSubscription(settings, subscriptionId).Select(p => p.Id).ToHashSet();
        settings.Profiles.RemoveAll(p => profiles.Contains(p.Id));
        settings.Subscriptions.RemoveAll(s => Source(s) == Source(sub));
        SettingsValidation.RepairSelections(settings);
    }

    private static string ExclusionKey(string identity) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
    public static void ExcludeProfile(AppSettings settings, Profile profile)
    {
        var subscription = settings.Subscriptions.FirstOrDefault(s => s.Id == profile.SubscriptionId);
        if (subscription == null) return;
        var stable = ItemIdentity(profile);
        var key = ExclusionKey(stable);
        // Legacy settings can contain multiple subscription IDs for one URL.
        // Remember the same deletion in those aliases; otherwise a second
        // scheduled refresh can resurrect exactly the entry just removed.
        foreach (var alias in settings.Subscriptions.Where(s => s.Id == subscription.Id || Source(s) == Source(subscription)))
            if (!alias.ExcludedProfileKeys.Contains(key, StringComparer.Ordinal)) alias.ExcludedProfileKeys.Add(key);
    }
    public static AppSettings Prepare(AppSettings current, Guid subscriptionId, IEnumerable<Profile> profiles, bool approveSecurityChanges = false) =>
        Apply(current, subscriptionId, new ImportResult(profiles.ToList(), []), approveSecurityChanges).Settings;

    public static SubscriptionMergeResult Apply(AppSettings current, Guid subscriptionId, ImportResult document, bool approveSecurityChanges = false)
    {
        if (document.Profiles.Count == 0 && !document.CompleteSnapshot) throw new InvalidDataException("Корректных профилей нет; прежняя подписка сохранена.");
        var next = JsonSettings.Clone(current);
        var requested = next.Subscriptions.Single(s => s.Id == subscriptionId);
        var cleanup = NormalizeSources(next);
        var sub = next.Subscriptions.First(s => s.Id == requested.Id || Source(s) == Source(requested));
        subscriptionId = sub.Id;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new HashSet<Guid>(); int added = 0, updated = 0, removed = cleanup.Profiles, retained = 0, excluded = 0, rejected = document.Errors.Count;
        var exclusions = sub.ExcludedProfileKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var profile in document.Profiles)
        {
            var incoming = JsonSettings.Clone(profile); incoming.Name = ProfileDisplayNames.Normalize(incoming.Name); var key = ProfileIdentity.Key(incoming);
            var stable = incoming.SubscriptionItemId.StartsWith("manifest:", StringComparison.Ordinal) ? incoming.SubscriptionItemId : key;
            if (!seen.Add(stable)) { rejected++; continue; }
            if (exclusions.Contains(ExclusionKey(stable))) { excluded++; continue; }
            var matches = next.Profiles.Where(p => p.SubscriptionId == subscriptionId &&
                MatchesStoredProfile(p, incoming, stable, key, current.ProfileFormatVersion < 1)).ToArray();
            var saved = matches.OrderByDescending(p => p.Id == current.MainProfileId || p.Id == current.OpenVpnProfileId).FirstOrDefault();
            if (ProfileSecurity.CertificateValidationDisabled(incoming) && (saved == null || !ProfileSecurity.CertificateValidationDisabled(saved)) && !approveSecurityChanges)
                throw new SubscriptionSecurityApprovalRequiredException(1);
            incoming.SubscriptionId = subscriptionId; incoming.SubscriptionItemId = stable; incoming.SubscriptionSource = sub.Name; incoming.SubscriptionRemoved = false;
            if (saved == null) { next.Profiles.Add(incoming); added++; }
            else
            {
                incoming.Id = saved.Id; incoming.Name = ProfileDisplayNames.Refresh(saved.Name, incoming.Name); incoming.Candidate = saved.Candidate;
                incoming.LearnedRoutes = saved.LearnedRoutes.ToList(); incoming.AllowPublicPushedRoutes = saved.AllowPublicPushedRoutes;
                if (incoming.Name != saved.Name || incoming.OutboundJson != saved.OutboundJson || incoming.Host != saved.Host || incoming.Port != saved.Port || saved.SubscriptionRemoved) updated++;
                next.Profiles[next.Profiles.IndexOf(saved)] = incoming;
                // A successfully parsed entry confirms the current connection
                // for this stable item even in a partial subscription response.
                // Remove only its stale copies, never unrelated missing entries.
                foreach (var duplicate in matches.Where(p => p.Id != saved.Id && p.Id != current.MainProfileId && p.Id != current.OpenVpnProfileId))
                { next.Profiles.Remove(duplicate); removed++; }
            }
            kept.Add(incoming.Id);
        }
        // Partial/malformed responses are never authority to remove working entries.
        if (document.CompleteSnapshot && rejected == 0)
        {
            foreach (var old in next.Profiles.Where(p => p.SubscriptionId == subscriptionId && !kept.Contains(p.Id)).ToArray())
            {
                if (old.Id == current.MainProfileId || old.Id == current.OpenVpnProfileId) { old.SubscriptionRemoved = true; retained++; }
                else { next.Profiles.Remove(old); removed++; }
            }
        }
        var outcome = new SubscriptionMergeResult(next, added, updated, removed, retained, rejected, excluded);
        sub.LastRefreshSummary = outcome.Summary(document.ReceivedCount > 0 ? document.ReceivedCount : document.Profiles.Count + document.Errors.Count);
        sub.UpdatedAt = DateTimeOffset.UtcNow;
        return outcome;
    }
    public static AppSettings Import(AppSettings current, Subscription subscription, IEnumerable<Profile> profiles)
    {
        var next = JsonSettings.Clone(current);
        var existing = next.Subscriptions.FirstOrDefault(s => Uri.TryCreate(s.Url, UriKind.Absolute, out var old) && old.AbsoluteUri == new Uri(subscription.Url).AbsoluteUri);
        if (existing == null) { existing = JsonSettings.Clone(subscription); next.Subscriptions.Add(existing); }
        return Prepare(next, existing.Id, profiles);
    }
}
