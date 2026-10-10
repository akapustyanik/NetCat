using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NetCat.Core;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed class AppearancePreferenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-Appearance-" + Guid.NewGuid().ToString("N"));
    private static readonly AppearanceValues Custom = new("#283040", "#E080B0", -12, true);

    [Fact]
    public async Task AppearanceSurvivesRestartAndLegacyScaleRemainsIndependent()
    {
        var store = new SettingsStore(root);
        await store.SaveAsync(new AppSettings { TestIntervalSeconds = 15 });
        store.InterfaceScalePreference.Flush(.8);
        await store.AppearancePreference.SaveAsync(Custom);
        var reopened = new SettingsStore(root).Load();
        Assert.Equal(Custom, AppearanceValues.From(reopened));
        Assert.Equal(.8, reopened.InterfaceScale);
        Assert.Equal(15, reopened.TestIntervalSeconds);
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "appearance.json")));
        Assert.Equal(4, file.RootElement.EnumerateObject().Count());
        Assert.False(file.RootElement.TryGetProperty("Profiles", out _));
    }

    [Fact]
    public async Task LatestFlushWinsOverRapidQueuedChanges()
    {
        var preference = new AppearancePreference(root);
        var pending = Enumerable.Range(0, 100).Select(i => preference.SaveAsync(Custom with { PanelBrightness = i % 41 - 20 })).ToArray();
        preference.Flush(Custom);
        await Task.WhenAll(pending);
        Assert.Equal(Custom, new AppearancePreference(root).Load(AppearanceValues.From(new AppSettings())));
        Assert.Empty(Directory.GetFiles(root, "*.new"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"BaseColor\":\"#123456\",\"AccentColor\":\"bad\",\"PanelBrightness\":0}")]
    [InlineData("{\"BaseColor\":\"#123456\",\"AccentColor\":\"#ABCDEF\",\"PanelBrightness\":21}")]
    public void DamagedPreferencePreservesExistingAppearance(string text)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "appearance.json"), text);
        Assert.Equal(Custom, new AppearancePreference(root).Load(Custom));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

public sealed partial class Candidate32UiAuditTests
{
    [Fact]
    public Task AllAppearanceControlsAutosaveWithoutCommittingNetworkDrafts() => Sta.Run(async () =>
    {
        var store = new SettingsStore(root);
        await store.SaveAsync(new AppSettings());
        using var vm = new MainViewModel(store, store.Load());
        var window = new MainWindow(vm);
        try
        {
            ((TabControl)window.FindName("Pages")).SelectedIndex = 5;
            window.Show(); window.UpdateLayout();
            var network = JsonSerializer.Serialize(vm.DesiredState, JsonSettings.Options);
            vm.State.TestTimeoutSeconds = 17;
            vm.State.BaseColor = "#283040";
            vm.State.AccentColor = "#E080B0";
            ((Slider)window.FindName("PanelBrightnessSlider")).Value = -12;
            ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("HighContrastSwitch")).IsChecked = true;
            await vm.PendingAppearanceSave;
            var expected = new AppearanceValues("#283040", "#E080B0", -12, true);
            Assert.Equal(expected, AppearanceValues.From(new SettingsStore(root).Load()));
            Assert.Equal(8, new SettingsStore(root).Load().TestTimeoutSeconds);
            Assert.Equal(17, vm.State.TestTimeoutSeconds);
            Assert.Equal(network, JsonSerializer.Serialize(vm.DesiredState, JsonSettings.Options));
            await vm.UpdateSettingsAsync(settings => settings.TestIntervalSeconds = 2);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(expected, AppearanceValues.From(vm.State));
            vm.AssertCommittedInvariant();
            using var reopened = new MainViewModel(new SettingsStore(root), new SettingsStore(root).Load());
            Assert.Equal(expected, AppearanceValues.From(reopened.State));

            void Click(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            vm.State.TestTimeoutSeconds = 18;
            Click("Light_Click"); await vm.PendingAppearanceSave;
            Assert.Equal("#F4F6F8", store.Load().BaseColor);
            Click("Dark_Click"); await vm.PendingAppearanceSave;
            Assert.Equal("#151A22", store.Load().BaseColor);
            Click("ResetAppearance_Click"); await vm.PendingAppearanceSave; await vm.PendingInterfaceScaleSave;
            Assert.Equal(AppearanceValues.From(new AppSettings()), AppearanceValues.From(store.Load()));
            Assert.Equal(8, store.Load().TestTimeoutSeconds);
        }
        finally { window.Close(); Theme.Apply(new AppSettings()); }
    });

    [Fact]
    public Task ImmediateExitFlushesFinalAppearanceWithoutSaveButton() => Sta.Run(async () =>
    {
        var store = new SettingsStore(root);
        await store.SaveAsync(new AppSettings());
        var vm = new MainViewModel(store, store.Load());
        for (var i = 0; i < 40; i++) vm.State.PanelBrightness = i % 2 == 0 ? -20 : 20;
        vm.State.BaseColor = "#203040";
        vm.State.HighContrastText = true;
        var expected = AppearanceValues.From(vm.State);
        vm.FlushAppearancePreference(); // Also used by OnSessionEnding.
        vm.Dispose();
        await vm.PendingAppearanceSave;
        Assert.Equal(expected, AppearanceValues.From(new SettingsStore(root).Load()));
    });
}
