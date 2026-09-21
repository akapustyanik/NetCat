using NetCat.Core;
namespace NetCat.Engine;
public static class SubscriptionMerge
{
    public static AppSettings Prepare(AppSettings current, Guid subscriptionId, IEnumerable<Profile> profiles)
    {
        var next=JsonSettings.Clone(current); var seen=new HashSet<string>();
        foreach(var profile in profiles)
        {
            var incoming=JsonSettings.Clone(profile); var key=ProfileIdentity.Key(incoming);
            if(!seen.Add(key)) continue;
            var saved=next.Profiles.FirstOrDefault(p=>p.SubscriptionId==subscriptionId && (ProfileIdentity.Key(p)==key || current.ProfileFormatVersion<1 && ProfileIdentity.MatchesLegacyGrpc(p,incoming)));
            incoming.SubscriptionId=subscriptionId; incoming.SubscriptionItemId=key;
            if(saved==null) next.Profiles.Add(incoming);
            else { incoming.Id=saved.Id; incoming.Name=saved.Name; incoming.Candidate=saved.Candidate; next.Profiles[next.Profiles.IndexOf(saved)]=incoming; }
        }
        next.Subscriptions.Single(s=>s.Id==subscriptionId).UpdatedAt=DateTimeOffset.Now;
        return next;
    }
}
