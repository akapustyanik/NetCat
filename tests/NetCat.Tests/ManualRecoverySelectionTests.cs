using System.Reflection;
using System.Windows;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed class ManualRecoverySelectionTests
{
    [Theory]
    [InlineData(ComponentId.MainRouter, true)]
    [InlineData(ComponentId.Tun, true)]
    [InlineData(ComponentId.OpenVpnRoutes, false)]
    [InlineData(ComponentId.Zapret, false)]
    public async Task ManualSelectionCanRecoverMainFailureButPreservesOtherPreflight(ComponentId failed, bool switches)
    {
        var root = RoutingTests.TestArtifacts("manual-recovery-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(root);
        var first = ProfileImporter.ParseLink("socks://127.0.0.1:19998");
        var next = ProfileImporter.ParseLink("socks://127.0.0.1:19999");
        var probes = 0;
        using var router = new RouterService(RoutingTests.ModuleRoot, root)
        {
            PreflightOverride = (_, _, _) => { Interlocked.Increment(ref probes); return Task.FromResult(new DelayResult(false, 0, "unavailable")); }
        };
        store.SaveDesiredState(new DesiredRuntimeState { MainVpnEnabled = true, SelectedVpnProfileId = first.Id, TunEnabled = false });
        using var vm = new MainViewModel(store, new AppSettings { Profiles = [first, next], MainProfileId = first.Id, Tun = false }, router);
        typeof(RuntimeCoordinator).GetProperty(nameof(RuntimeCoordinator.PhysicalNetworkProvider))!.SetValue(vm.RuntimeCoordinator,
            new Candidate9Tests.FakePhysicalProvider { Current = null });
        typeof(RuntimeCoordinator).GetProperty(nameof(RuntimeCoordinator.CurrentConvergenceState))!.SetValue(vm.RuntimeCoordinator,
            new RuntimeConvergenceState(true, false, [failed], "test failure", ConvergencePhase.Degraded));
        vm.UserSelectedVpnProfile(next.Id);
        await vm.PendingSelection.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(switches ? next.Id : first.Id, vm.GetCurrentDesiredState().SelectedVpnProfileId);
        Assert.Equal(switches ? next.Id : first.Id, vm.ConfigRepository.CurrentSettings.MainProfileId);
        Assert.True(vm.GetCurrentDesiredState().MainVpnEnabled);
        Assert.Equal(switches ? 0 : 1, probes);
        Assert.False(router.Running);
    }
}

public sealed partial class Candidate32UiAuditTests
{
    [Fact]
    public Task PowerButtonCancelsDesiredVpnWhenCoreHasFailed() => Sta.Run(() =>
    {
        var store = new SettingsStore(root);
        store.SaveDesiredState(new DesiredRuntimeState { MainVpnEnabled = true });
        using var vm = new MainViewModel(store, new AppSettings());
        var window = new MainWindow(vm);
        try
        {
            Assert.False(vm.Router.VpnRequested);
            Assert.Equal("Отключить VPN", vm.VpnButton);
            typeof(MainWindow).GetMethod("Vpn_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            Assert.False(vm.DesiredState.MainVpnEnabled);
            Assert.Equal("Подключить VPN", vm.VpnButton);
            // The core may still be winding down; the label must agree with
            // the next click rather than reverting to the old runtime state.
            typeof(RouterService).GetProperty(nameof(RouterService.VpnRequested))!.SetValue(vm.Router, true);
            Assert.Equal("Подключить VPN", vm.VpnButton);
            typeof(RouterService).GetProperty(nameof(RouterService.VpnRequested))!.SetValue(vm.Router, false);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });
}
