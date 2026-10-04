using NetCat.Core;
namespace NetCat.Engine;
public sealed class SubscriptionSecurityApprovalRequiredException(int count) : IOException($"Подписка отключает проверку TLS-сертификатов в {count} профилях. Обновление не применено; требуется явное подтверждение в настройках подписки.");
public sealed record SubscriptionMergeResult(AppSettings Settings, int Added, int Updated, int Removed, int Retained, int Rejected)
{
    public string Summary(int received) => $"Получено: {received}; добавлено: {Added}; обновлено: {Updated}; удалено: {Removed}; сохранено выбранных: {Retained}; отклонено: {Rejected}.";
}
public static class SubscriptionMerge
{
    public static AppSettings Prepare(AppSettings current, Guid subscriptionId, IEnumerable<Profile> profiles, bool approveSecurityChanges = false) =>
        Apply(current, subscriptionId, new ImportResult(profiles.ToList(), []), approveSecurityChanges).Settings;

    public static SubscriptionMergeResult Apply(AppSettings current, Guid subscriptionId, ImportResult document, bool approveSecurityChanges = false)
    {
        if (document.Profiles.Count == 0 && !document.CompleteSnapshot) throw new InvalidDataException("Корректных профилей нет; прежняя подписка сохранена.");
        var next = JsonSettings.Clone(current);
        var sub = next.Subscriptions.Single(s => s.Id == subscriptionId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new HashSet<Guid>(); int added = 0, updated = 0, removed = 0, retained = 0, rejected = document.Errors.Count;
        foreach (var profile in document.Profiles)
        {
            var incoming = JsonSettings.Clone(profile); incoming.Name = ProfileDisplayNames.Normalize(incoming.Name); var key = ProfileIdentity.Key(incoming);
            var stable = incoming.SubscriptionItemId.StartsWith("manifest:", StringComparison.Ordinal) ? incoming.SubscriptionItemId : key;
            if (!seen.Add(stable)) { rejected++; continue; }
            var saved = next.Profiles.FirstOrDefault(p => p.SubscriptionId == subscriptionId &&
                (p.SubscriptionItemId == stable || !p.SubscriptionItemId.StartsWith("manifest:", StringComparison.Ordinal) && (ProfileIdentity.Key(p) == key || current.ProfileFormatVersion < 1 && ProfileIdentity.MatchesLegacyGrpc(p, incoming))));
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
        var outcome = new SubscriptionMergeResult(next, added, updated, removed, retained, rejected);
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
