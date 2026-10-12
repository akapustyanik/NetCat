using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

public sealed class CodeRecheckRegressionTests
{
    [Theory]
    [InlineData("invalid JSON")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ACorruptStoredEntryDoesNotBlockRefreshOfValidProfiles(string json)
    {
        var sub = new Subscription { Url = "https://subscription.example/list" };
        var document = ProfileImporter.Parse("socks://vpn.example:1080#Working");
        var settings = SubscriptionMerge.Apply(new AppSettings { Subscriptions = [sub] }, sub.Id, document).Settings;
        var corrupt = JsonSettings.Clone(settings.Profiles[0]);
        corrupt.Id = Guid.NewGuid(); corrupt.SubscriptionItemId = ""; corrupt.OutboundJson = json;
        settings.Profiles.Add(corrupt);
        var result = SubscriptionMerge.Apply(settings, sub.Id, document);
        Assert.Single(result.Settings.Profiles, p => p.OutboundJson != json);
        Assert.Contains(result.Settings.Profiles, p => p.Id == corrupt.Id);
        Assert.Equal(0, result.Added);
        Assert.Equal(2, settings.Profiles.Count);
        var complete = SubscriptionMerge.Apply(settings, sub.Id, document with { CompleteSnapshot = true });
        Assert.Single(complete.Settings.Profiles);
        SettingsValidation.Validate(complete.Settings);
    }
}

public sealed partial class Candidate32UiAuditTests
{
    private static (AppSettings Settings, Subscription Subscription, ImportResult Document) RecheckSubscription()
    {
        var sub = new Subscription { Url = "https://subscription.example/list" };
        var document = ProfileImporter.Parse("socks://vpn.example:1080#Working");
        return (SubscriptionMerge.Apply(new AppSettings { Subscriptions = [sub] }, sub.Id, document).Settings, sub, document);
    }

    [Theory]
    [InlineData("invalid JSON")]
    [InlineData("[]")]
    [InlineData("null")]
    public Task ACorruptStoredSubscriptionProfileCanBeDeleted(string json) => Sta.Run(async () =>
    {
        var (settings, sub, _) = RecheckSubscription();
        var corrupt = JsonSettings.Clone(settings.Profiles[0]);
        corrupt.Id = Guid.NewGuid(); corrupt.SubscriptionItemId = ""; corrupt.OutboundJson = json;
        settings.Profiles.Add(corrupt);
        var store = new SettingsStore(root); await store.SaveAsync(settings);
        using var vm = new NetCat.UI.MainViewModel(store, settings);
        await vm.DeleteProfileAsync(corrupt.Id);
        var saved = new SettingsStore(root).Load();
        Assert.Single(saved.Profiles); Assert.DoesNotContain(saved.Profiles, p => p.Id == corrupt.Id);
        Assert.Single(saved.Subscriptions.Single(s => s.Id == sub.Id).ExcludedProfileKeys);
    });

    [Fact]
    public Task SubscriptionRefreshWorksAfterARepositoryCommit() => Sta.Run(async () =>
    {
        var (settings, sub, document) = RecheckSubscription();
        var store = new SettingsStore(root); await store.SaveAsync(settings);
        using var vm = new NetCat.UI.MainViewModel(store, settings);
        await vm.ConfigRepository.UpdateSettingsAsync(next => { next.BaseColor = "#223344"; return next; });
        await vm.RefreshSubscriptionAsync(sub.Id, default, approvedDocument: document);
        Assert.Equal("#223344", new SettingsStore(root).Load().BaseColor);
        Assert.Equal(vm.ConfigRepository.Snapshot.Revision, vm.SettingsRevision);
    });

    [Fact]
    public Task AStaleApprovedRefreshIsRejectedAfterARepositoryCommit() => Sta.Run(async () =>
    {
        var (settings, sub, document) = RecheckSubscription();
        var store = new SettingsStore(root); await store.SaveAsync(settings);
        using var vm = new NetCat.UI.MainViewModel(store, settings);
        var approvedRevision = vm.SettingsRevision;
        await vm.ConfigRepository.UpdateSettingsAsync(next => { next.BaseColor = "#223344"; return next; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            vm.RefreshSubscriptionAsync(sub.Id, default, approvedDocument: document, approvedRevision: approvedRevision));
        Assert.Equal("#223344", new SettingsStore(root).Load().BaseColor);
    });

    [Fact]
    public Task ARepositoryCommitDuringValidationInvalidatesTheExpectedRevision() => Sta.Run(async () =>
    {
        var (settings, _, _) = RecheckSubscription();
        var store = new SettingsStore(root); await store.SaveAsync(settings);
        using var vm = new NetCat.UI.MainViewModel(store, settings);
        var expectedRevision = vm.SettingsRevision;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.UpdateSettingsAsync(
            next => next.TestIntervalSeconds = 2, expectedRevision: expectedRevision,
            validateExtra: (_, _) => vm.ConfigRepository.UpdateSettingsAsync(
                next => { next.BaseColor = "#223344"; return next; })));
        var saved = new SettingsStore(root).Load();
        Assert.Equal(15, saved.TestIntervalSeconds);
        Assert.Equal("#223344", saved.BaseColor);
    });
}
