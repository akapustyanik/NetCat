using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;
public sealed class DeletedSubscriptionProfileTests
{
    private const string Link = "vless://11111111-1111-1111-1111-111111111111@vpn.example:443?security=tls&type=ws&path=%2Fsocket#Remote";
    private static (AppSettings Settings, Subscription Sub, ImportResult Document) Fixture(bool manifest = false)
    {
        var sub = new Subscription { Url = "https://subscription.example/fixture" };
        var document = ProfileImporter.Parse(Link);
        if (manifest) document.Profiles[0].SubscriptionItemId = "manifest:fixture";
        return (SubscriptionMerge.Apply(new AppSettings { Subscriptions = [sub] }, sub.Id, document).Settings, sub, document);
    }
    private static void Delete(AppSettings s, Profile p)
    { SubscriptionMerge.ExcludeProfile(s, p); s.Profiles.Remove(p); SettingsValidation.RepairSelections(s); }
    [Fact] public void SubscriptionRefreshWouldReinsertDeletionWithoutAnExclusion()
    {
        var (s, sub, document) = Fixture(); s.Profiles.Clear();
        Assert.Equal(1, SubscriptionMerge.Apply(s, sub.Id, document).Added);
    }
    [Fact] public void DeletedGenericProfileStaysExcludedAcrossRenamingAndRepeatedRefresh()
    {
        var (s, sub, document) = Fixture(); Delete(s, Assert.Single(s.Profiles));
        document.Profiles[0].Name = "Renamed by provider";
        for (var i = 0; i < 3; i++)
        {
            var result = SubscriptionMerge.Apply(s, sub.Id, document);
            Assert.Empty(result.Settings.Profiles); Assert.Equal(1, result.Excluded); Assert.Equal(0, result.Added);
            Assert.Equal(0, result.Rejected); s = result.Settings;
        }
    }
    [Fact] public void ManifestExclusionUsesStableIdAndDoesNotSuppressAnotherManifestEntry()
    {
        var (s, sub, document) = Fixture(manifest:true); Delete(s, Assert.Single(s.Profiles));
        document.Profiles[0].Port = 8443;
        Assert.Empty(SubscriptionMerge.Apply(s, sub.Id, document).Settings.Profiles);
        document.Profiles[0].SubscriptionItemId = "manifest:another";
        Assert.Single(SubscriptionMerge.Apply(s, sub.Id, document).Settings.Profiles);
    }
    [Fact] public async Task ExclusionSurvivesEncryptedSettingsRestartAndExplicitResetRestoresProfile()
    {
        var (s, sub, document) = Fixture(); Delete(s, Assert.Single(s.Profiles));
        var root = Path.Combine(Path.GetTempPath(), "NetCat-Deleted-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new SettingsStore(root).SaveAsync(s);
            var loaded = new SettingsStore(root).Load();
            Assert.Empty(SubscriptionMerge.Apply(loaded, sub.Id, document).Settings.Profiles);
            Assert.DoesNotContain("vpn.example", Assert.Single(loaded.Subscriptions[0].ExcludedProfileKeys));
            loaded.Subscriptions[0].ExcludedProfileKeys.Clear();
            Assert.Single(SubscriptionMerge.Apply(loaded, sub.Id, document).Settings.Profiles);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public void DuplicateSourceAliasesShareExclusionButOtherSourceDoesNot()
    {
        var (s, sub, document) = Fixture();
        var alias = new Subscription { Url = sub.Url };
        var different = new Subscription { Url = "https://other-subscription.example/fixture" };
        s.Subscriptions.AddRange([alias, different]);
        Delete(s, Assert.Single(s.Profiles));
        Assert.Empty(SubscriptionMerge.Apply(s, alias.Id, document).Settings.Profiles);
        Assert.Single(SubscriptionMerge.Apply(s, different.Id, document).Settings.Profiles);
    }
    [Fact] public void PartialResponseCannotRemoveOtherProfilesWhileExcludedEntryIsIgnored()
    {
        var (s, sub, document) = Fixture(); Delete(s, Assert.Single(s.Profiles));
        var other = ProfileImporter.Parse(Link.Replace(":443", ":8443")).Profiles[0];
        other.SubscriptionId = sub.Id; s.Profiles.Add(other);
        var partial = new ImportResult(document.Profiles, ["Malformed second entry"]);
        var result = SubscriptionMerge.Apply(s, sub.Id, partial);
        Assert.Equal(other.Id, Assert.Single(result.Settings.Profiles).Id);
        Assert.Equal(1, result.Excluded); Assert.Equal(1, result.Rejected); Assert.Equal(0, result.Removed);
    }
    [Fact] public void LocalDeletionDoesNotCreateSubscriptionExclusions()
    {
        var (s, _, _) = Fixture(); var local = new Profile(); s.Profiles.Add(local);
        Delete(s, local); Assert.Empty(s.Subscriptions[0].ExcludedProfileKeys);
    }
}
public sealed partial class Candidate32UiAuditTests
{
    [Fact] public Task DeletedProfileCannotBeResurrectedByStaleOrNextRefresh() => Sta.Run(async () =>
    {
        var sub = new Subscription { Url = "https://subscription.example/fixture" };
        var document = ProfileImporter.Parse("vless://11111111-1111-1111-1111-111111111111@vpn.example:443?security=tls#Remote");
        var initial = SubscriptionMerge.Apply(new AppSettings { Subscriptions = [sub] }, sub.Id, document).Settings;
        var store = new SettingsStore(root); await store.SaveAsync(initial);
        using var vm = new NetCat.UI.MainViewModel(store, initial);
        var staleRevision = vm.SettingsRevision;
        await vm.DeleteProfileAsync(initial.Profiles[0].Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.UpdateSubscriptionAsync(sub.Id, document.Profiles, staleRevision, default));
        await vm.UpdateSubscriptionAsync(sub.Id, document.Profiles, vm.SettingsRevision, default);
        Assert.Empty(vm.State.Profiles); Assert.Empty(new SettingsStore(root).Load().Profiles);
        Assert.Single(new SettingsStore(root).Load().Subscriptions[0].ExcludedProfileKeys);
        vm.AssertCommittedInvariant();
    });
}
