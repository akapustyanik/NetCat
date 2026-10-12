using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

public sealed class SubscriptionAliasRegressionTests
{
    private const string Link = "vless://11111111-1111-1111-1111-111111111111@vpn.example:443?security=tls&type=ws&path=%2Fsocket#Remote";
    private static AppSettings Aliases()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 3; i++)
        {
            var sub = new Subscription { Url = "https://subscription.example/list" };
            settings.Subscriptions.Add(sub);
            var profile = ProfileImporter.Parse(Link).Profiles[0];
            profile.SubscriptionId = sub.Id;
            profile.SubscriptionItemId = ProfileIdentity.Key(profile);
            settings.Profiles.Add(profile);
        }
        return settings;
    }

    [Fact]
    public void RefreshOfAnAliasCannotAddAnotherCopyOfTheSameProfile()
    {
        var settings = Aliases();
        var result = SubscriptionMerge.Apply(settings, settings.Subscriptions[2].Id, ProfileImporter.Parse(Link));
        Assert.Single(result.Settings.Subscriptions);
        Assert.Single(result.Settings.Profiles);
        Assert.Equal(0, result.Added);
        Assert.Equal(3, settings.Profiles.Count);
    }

    [Fact]
    public async Task CleanupPreservesSelectedProfileAndDeletionRecordsAcrossTwoRestarts()
    {
        var settings = Aliases();
        var selected = settings.Profiles[2];
        selected.Name = "My custom name";
        settings.MainProfileId = selected.Id;
        settings.Subscriptions[0].ExcludedProfileKeys.Add(new string('A', 64));
        settings.Subscriptions[2].ExcludedProfileKeys.Add(new string('B', 64));
        settings.Subscriptions[1].UpdateHours = 0;
        var folder = Path.Combine(Path.GetTempPath(), "NetCat-Alias-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cleanup = SubscriptionMerge.NormalizeSources(settings);
            Assert.Equal((2, 2), cleanup);
            Assert.Equal(selected.Id, Assert.Single(settings.Profiles).Id);
            Assert.Equal("My custom name", settings.Profiles[0].Name);
            var sub = Assert.Single(settings.Subscriptions);
            Assert.Equal(0, sub.UpdateHours);
            Assert.Equal(2, sub.ExcludedProfileKeys.Count);
            await new SettingsStore(folder).SaveAsync(settings);
            for (var i = 0; i < 2; i++)
            {
                settings = new SettingsStore(folder).Load();
                Assert.Equal((0, 0), SubscriptionMerge.NormalizeSources(settings));
                var result = SubscriptionMerge.Apply(settings, sub.Id, ProfileImporter.Parse(Link));
                Assert.Equal(selected.Id, Assert.Single(result.Settings.Profiles).Id);
                Assert.Equal(0, result.Added);
                Assert.Equal(2, Assert.Single(result.Settings.Subscriptions).ExcludedProfileKeys.Count);
                await new SettingsStore(folder).SaveAsync(result.Settings);
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void OtherSourcesAndDifferentConnectionParametersAreNotDeleted()
    {
        var settings = Aliases();
        settings.Subscriptions[2].Url = "https://other-subscription.example/list";
        var changed = settings.Profiles[1];
        var json = System.Text.Json.Nodes.JsonNode.Parse(changed.OutboundJson)!;
        json["tls"]!["alpn"] = new System.Text.Json.Nodes.JsonArray("h2");
        changed.OutboundJson = json.ToJsonString();
        Assert.Equal((1, 0), SubscriptionMerge.NormalizeSources(settings));
        Assert.Equal(3, settings.Profiles.Count);
        var targets = SubscriptionMerge.DeletionTargets(settings, settings.Profiles[0]);
        Assert.Equal(2, targets.Count);
        Assert.DoesNotContain(targets, p => p.Id == settings.Profiles[2].Id);
    }

    [Fact]
    public void CorruptProfileDoesNotPreventCleanupOrOpeningTheApplication()
    {
        var settings = Aliases();
        settings.Profiles[2].OutboundJson = "invalid JSON";
        settings.Profiles[2].SubscriptionItemId = "";
        Assert.Equal((2, 1), SubscriptionMerge.NormalizeSources(settings));
        Assert.Contains(settings.Profiles, p => p.OutboundJson == "invalid JSON");
    }

    [Fact]
    public void PendingRestoreSelectionAndUserPreferencesArePreserved()
    {
        var settings = Aliases();
        settings.MainProfileId = settings.Profiles[0].Id;
        var pending = settings.Profiles[1].Id;
        settings.Profiles[2].Candidate = false;
        settings.Subscriptions[0].AllowInsecureTransport = true;
        Assert.Equal((2, 0), SubscriptionMerge.NormalizeSources(settings, [pending]));
        Assert.Equal(3, settings.Profiles.Count);
        Assert.False(Assert.Single(settings.Subscriptions).AllowInsecureTransport);
        Assert.Contains(settings.Profiles, p => !p.Candidate);
    }

    [Fact]
    public void OutboundTagAndPropertyOrderDoNotCreateDuplicateConnections()
    {
        var settings = Aliases();
        var json = System.Text.Json.Nodes.JsonNode.Parse(settings.Profiles[1].OutboundJson)!.AsObject();
        var reversed = new System.Text.Json.Nodes.JsonObject();
        foreach (var item in json.Reverse()) reversed[item.Key] = item.Value?.DeepClone();
        reversed["tag"] = "Different name";
        settings.Profiles[1].OutboundJson = reversed.ToJsonString();
        Assert.Equal((2, 2), SubscriptionMerge.NormalizeSources(settings));
        Assert.Single(settings.Profiles);
    }

    [Fact]
    public void ProviderRefreshReplacesStaleTlsCopiesButPreservesSelectedIdAndUnrelatedEntries()
    {
        var settings = Aliases();
        var selected = settings.Profiles[2];
        selected.Name = "My custom name";
        settings.MainProfileId = selected.Id;
        var json = System.Text.Json.Nodes.JsonNode.Parse(selected.OutboundJson)!;
        json["tls"]!["alpn"] = new System.Text.Json.Nodes.JsonArray("h2");
        selected.OutboundJson = json.ToJsonString();
        var unrelated = ProfileImporter.Parse(Link.Replace(":443", ":8443")).Profiles[0];
        unrelated.SubscriptionId = settings.Subscriptions[0].Id;
        unrelated.SubscriptionItemId = ProfileIdentity.Key(unrelated);
        settings.Profiles.Add(unrelated);
        var partial = new ImportResult(ProfileImporter.Parse(Link).Profiles, ["Malformed unrelated item"]);
        var result = SubscriptionMerge.Apply(settings, settings.Subscriptions[2].Id, partial);
        Assert.Equal(2, result.Settings.Profiles.Count);
        var refreshed = Assert.Single(result.Settings.Profiles, p => p.Port == 443);
        Assert.Equal(selected.Id, refreshed.Id);
        Assert.Equal("My custom name", refreshed.Name);
        Assert.Equal(ProfileImporter.Parse(Link).Profiles[0].OutboundJson, refreshed.OutboundJson);
        Assert.Contains(result.Settings.Profiles, p => p.Id == unrelated.Id);
        Assert.Equal(2, result.Removed);
    }

    [Fact]
    public void RemovingSubscriptionRemovesItsAliasesButKeepsOtherSourcesAndLocalProfiles()
    {
        var settings = Aliases();
        var other = new Subscription { Url = "https://other-subscription.example/list" };
        settings.Subscriptions.Add(other);
        var keep = ProfileImporter.Parse(Link).Profiles[0]; keep.SubscriptionId = other.Id;
        var local = ProfileImporter.Parse(Link).Profiles[0];
        settings.Profiles.AddRange([keep, local]); settings.MainProfileId = keep.Id;
        Assert.Equal(3, SubscriptionMerge.ProfilesForSubscription(settings, settings.Subscriptions[2].Id).Count);
        SubscriptionMerge.RemoveSubscription(settings, settings.Subscriptions[2].Id);
        Assert.Equal(other.Id, Assert.Single(settings.Subscriptions).Id);
        Assert.Equal(new[] { keep.Id, local.Id }, settings.Profiles.Select(p => p.Id));
        Assert.Equal(keep.Id, settings.MainProfileId);
    }
}

public sealed partial class Candidate32UiAuditTests
{
    [Fact]
    public Task SubscriptionRemovalSurvivesRestartAndStaleRefreshCannotRestoreIt() => Sta.Run(async () =>
    {
        var sub = new Subscription { Url = "https://subscription.example/list" };
        var document = ProfileImporter.Parse("vless://11111111-1111-1111-1111-111111111111@vpn.example:443?security=tls#Remote");
        var settings = SubscriptionMerge.Apply(new AppSettings { Subscriptions = [sub] }, sub.Id, document).Settings;
        var store = new SettingsStore(root); await store.SaveAsync(settings);
        using var vm = new NetCat.UI.MainViewModel(store, settings);
        var revision = vm.SettingsRevision;
        await vm.DeleteSubscriptionAsync(sub.Id);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Empty(vm.State.Profiles); Assert.Empty(vm.State.Subscriptions);
        var loaded = new SettingsStore(root).Load(); Assert.Empty(loaded.Profiles); Assert.Empty(loaded.Subscriptions);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.UpdateSubscriptionAsync(sub.Id, document.Profiles, revision, default));
        vm.AssertCommittedInvariant();
    });

    [Fact]
    public Task DeletingAnAliasAlsoRemovesItsExistingCopiesAndPersistsExclusion() => Sta.Run(async () =>
    {
        var settings = new AppSettings();
        for (var i = 0; i < 3; i++)
        {
            var sub = new Subscription { Url = "https://subscription.example/list" };
            settings.Subscriptions.Add(sub);
            var profile = ProfileImporter.Parse("vless://11111111-1111-1111-1111-111111111111@vpn.example:443?security=tls#Remote").Profiles[0];
            profile.SubscriptionId = sub.Id;
            profile.SubscriptionItemId = ProfileIdentity.Key(profile);
            settings.Profiles.Add(profile);
        }
        var store = new SettingsStore(root);
        await store.SaveAsync(settings);
        using var vm = new NetCat.UI.MainViewModel(store, settings);
        await vm.DeleteProfileAsync(settings.Profiles[0].Id);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Empty(vm.State.Profiles);
        Assert.All(new SettingsStore(root).Load().Subscriptions, sub => Assert.Single(sub.ExcludedProfileKeys));
        vm.AssertCommittedInvariant();
    });

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(1000)]
    public Task ComboBoxWheelMovesAPartOfTheListInsteadOfJumpingToTheEnd(int count) => Sta.Run(async () =>
    {
        var combo = new System.Windows.Controls.ComboBox
        {
            ItemsSource = Enumerable.Range(0, count).Select(i => $"Profile {i}").ToArray(),
            SelectedIndex = 0, Width = 350
        };
        var window = new System.Windows.Window { Content = combo, Width = 400, Height = 140 };
        try
        {
            window.Show(); combo.IsDropDownOpen = true; window.UpdateLayout();
            await Task.Delay(100);
            var container = Assert.IsType<System.Windows.Controls.ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(0));
            System.Windows.DependencyObject? parent = container;
            while (parent is not System.Windows.Controls.ScrollViewer)
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent!);
            var viewer = (System.Windows.Controls.ScrollViewer)parent;
            Assert.True(viewer.ScrollableHeight > 0);
            container.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
            await Task.Delay(350); viewer.UpdateLayout();
            Assert.InRange(viewer.VerticalOffset, 1, Math.Min(120, viewer.ScrollableHeight - .1));
            Assert.Equal(0, combo.SelectedIndex);
            if (count == 1000) Assert.Null(combo.ItemContainerGenerator.ContainerFromIndex(999));
            var beforePrecise = viewer.VerticalOffset;
            for (var i = 0; i < 24; i++)
                container.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -1)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
            await Task.Delay(150); viewer.UpdateLayout();
            var expected = (System.Windows.SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight * .85 : System.Windows.SystemParameters.WheelScrollLines * 16) / 5d;
            Assert.InRange(viewer.VerticalOffset - beforePrecise, expected - .2, expected + .2);
        }
        finally { combo.IsDropDownOpen = false; window.Close(); }
    });
}
