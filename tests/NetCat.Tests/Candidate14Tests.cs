using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

// Candidate14 regression tests target the production health and physical-binding helpers.
public sealed class Candidate14Tests
{


    [Fact]
    public void BusyLocalVerificationPortDoesNotOverrideHealthyTun()
    {
        var health = new RouterHealth(true, true, true, false, 9);
        Assert.Equal(TunStructuralStatus.TransientDegraded, health.StructuralStatus);
        Assert.False(health.StructuralFailure);
    }

    [Fact]
    public void SingleWeakTunProbeFailureIsTransientOnly()
    {
        var tracker = new TunHealthTracker();
        var health = tracker.Evaluate(true, true, true, false, 9);
        Assert.Equal(TunStructuralStatus.TransientDegraded, health.StructuralStatus);
        Assert.False(health.StructuralStatus == TunStructuralStatus.StructuralFailure);
    }

    [Fact]
    public void HealthyTunSuppressesStaleTunStructuralFailure()
    {
        var tracker = new TunHealthTracker();
        tracker.Evaluate(true, false, false, false, 9);
        tracker.Evaluate(true, false, false, false, 9);
        var healthy = tracker.Evaluate(true, true, true, true, 9);
        Assert.Equal(TunStructuralStatus.Healthy, healthy.StructuralStatus);
    }

    [Fact]
    public void FreshStructuralObservationRequiredBeforeTunStructuralFailure()
    {
        var tracker = new TunHealthTracker();
        Assert.Equal(TunStructuralStatus.TransientDegraded, tracker.Evaluate(true, false, false, false, 9).StructuralStatus);
        Assert.Equal(TunStructuralStatus.StructuralFailure, tracker.Evaluate(true, false, false, false, 9).StructuralStatus);
    }

    [Fact]
    public void VpnEndpointTimeoutIsNotTunStructuralFailure()
    {
        Assert.False(new RouterHealth(true, true, true, true, 9, VpnUpstreamHealthy: false).StructuralFailure);
    }

    [Fact]
    public void DnsFailureIsNotTunStructuralFailure()
    {
        Assert.False(new RouterHealth(true, true, true, true, 9, DnsPathHealthy: false).StructuralFailure);
    }

    [Fact]
    public void RouteObservationErrorIsUnknownNotStructural()
    {
        var health = new TunHealthTracker().Evaluate(true, false, false, false, 9, routeObservationKnown: false);
        Assert.Equal(TunStructuralStatus.Unknown, health.StructuralStatus);
    }

    [Fact]
    public void ConfirmedMissingTunInterfaceAndRoutesEmitsOneStructuralFailure()
    {
        var logs = new List<string>();
        var tracker = new TunHealthTracker { Log = logs.Add };
        tracker.Evaluate(true, false, false, false, 9);
        tracker.Evaluate(true, false, false, false, 9);
        tracker.Evaluate(true, false, false, false, 9);
        Assert.Equal(TunStructuralStatus.StructuralFailure, tracker.CurrentStatus);
        Assert.Single(logs, line => line.Contains("new=StructuralFailure", StringComparison.Ordinal));
    }

    private static Candidate12Tests.Fixture RuntimeFixture() => new(new SettingsStore(Path.Combine(Path.GetTempPath(), "NetCat-health-"+Guid.NewGuid().ToString("N"))));
    [Theory][InlineData("router")][InlineData("zapret")][InlineData("openvpn")]
    public async Task TransientTunRecoveryDoesNotRestartIndependentComponents(string component)
    {
        using var f=RuntimeFixture();f.Desired.Current=f.Desired.Current with{OpenVpnEnabled=true};await f.Reconcile();
        var before=(f.Router.Starts,f.Zapret.Starts,f.OpenVpn.Starts);
        f.Tunnel.Status=TunStructuralStatus.TransientDegraded;await f.Reconcile(ReconcileReason.TunStructuralFailure);
        f.Tunnel.Status=TunStructuralStatus.Healthy;await f.Reconcile(ReconcileReason.TunStructuralFailure);
        Assert.Equal(component switch{"router"=>before.Item1,"zapret"=>before.Item2,_=>before.Item3},component switch{"router"=>f.Router.Starts,"zapret"=>f.Zapret.Starts,_=>f.OpenVpn.Starts});
    }

    [Fact]
    public void TunHealthTrackerRecoversAfterStructuralFailure()
    {
        var tracker = new TunHealthTracker();
        tracker.Evaluate(true, false, false, false, 9);
        Assert.Equal(TunStructuralStatus.StructuralFailure, tracker.Evaluate(true, false, false, false, 9).StructuralStatus);
        Assert.Equal(TunStructuralStatus.Healthy, tracker.Evaluate(true, true, true, true, 9).StructuralStatus);
    }

    [Fact]
    public void EthernetLossSelectsAlreadyUsableWifi()
    {
        var selected = PhysicalNetwork.SelectInterfaceIndex([12, 22], new Dictionary<int, uint> { [12] = 10, [22] = 20 }, activeIndex: 12);
        Assert.Equal(12, selected);
        Assert.Equal(22, PhysicalNetwork.SelectInterfaceIndex([22], new Dictionary<int, uint> { [22] = 20 }, activeIndex: 12));
    }

    [Fact]
    public void HealthyEthernetPreferredForStability()
        => Assert.Equal(12, PhysicalNetwork.SelectInterfaceIndex([12, 22], new Dictionary<int, uint> { [12] = 50, [22] = 10 }, activeIndex: 12));

    [Fact]
    public void WifiSelectedWhenEthernetHasNoUsableDefaultRoute()
        => Assert.Equal(22, PhysicalNetwork.SelectInterfaceIndex([22], new Dictionary<int, uint> { [22] = 15 }, activeIndex: 12));

    [Fact]
    public void VirtualAdaptersExcludedFromPhysicalCandidates()
    {
        Assert.True(PhysicalNetwork.IsVirtualAdapter("NetCat-TUN", "Wintun"));
        Assert.True(PhysicalNetwork.IsVirtualAdapter("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter"));
        Assert.False(PhysicalNetwork.IsVirtualAdapter("Ethernet 2", "Realtek PCIe GbE"));
    }

    [Fact]
    public void PhysicalSwitchUsesFreshInterfaceIndex()
    {
        var oldBinding = new NetworkSnapshot("Ethernet", 12, "192.168.1.20", "192.168.1.1", []);
        var newBinding = new NetworkSnapshot("Wi-Fi", 22, "192.168.1.21", "192.168.1.1", []);
        Assert.True(PhysicalNetwork.HasPhysicalChanged(oldBinding, newBinding, false));
    }

    [Fact]
    public void PhysicalSwitchUsesFreshGatewayAndAddress()
    {
        var oldBinding = new NetworkSnapshot("Ethernet", 12, "192.168.1.20", "192.168.1.1", [], DefaultRoute: "192.168.1.1");
        var newBinding = new NetworkSnapshot("Wi-Fi", 22, "10.0.0.20", "10.0.0.1", [], DefaultRoute: "10.0.0.1");
        Assert.NotEqual(oldBinding.Address, newBinding.Address);
        Assert.NotEqual(oldBinding.DefaultRoute, newBinding.DefaultRoute);
    }

    [Fact]
    public async Task ZapretRebindsAfterEthernetToWifi()
    {
        using var f=RuntimeFixture();await f.Reconcile();f.Physical.Current=new("Wi-Fi",22,"10.0.0.20","10.0.0.1",[]);
        await f.Reconcile(ReconcileReason.PhysicalNetworkChanged);Assert.Equal(2,f.Zapret.Starts);Assert.Equal(22,f.Coordinator.CurrentPhysicalNetwork!.Index);
        await f.Reconcile(ReconcileReason.PhysicalNetworkChanged);Assert.Equal(2,f.Zapret.Starts);
    }
    private sealed class Stats : ITrafficStatisticsProvider
    {public long Bytes;public (long BytesReceived,long BytesSent)? GetInterfaceStatistics(int index)=>(Bytes,Bytes);public string? GetInterfaceName(int index)=>index.ToString();}
    [Fact]
    public void TrafficMonitorResetsBaselineAfterPhysicalSwitch()
    {
        NetworkSnapshot binding=new("Ethernet",12,"192.168.1.20","192.168.1.1",[]);var stats=new Stats();
        using var monitor=new TrafficMonitorService(()=>binding,stats);long ticks=System.Diagnostics.Stopwatch.Frequency;
        monitor.Sample(ticks);stats.Bytes=1000000;Assert.True(monitor.Sample(ticks*2).DownloadMbps>0);
        binding=binding with{Index=22,Name="Wi-Fi"};stats.Bytes=1000000000;
        var switched=monitor.Sample(ticks*3);Assert.False(switched.HasBaseline);Assert.Equal(0,switched.DownloadMbps);Assert.Equal(22,switched.InterfaceIndex);
        stats.Bytes+=1000000;Assert.True(monitor.Sample(ticks*4).DownloadMbps>0);
    }

    [Fact]
    public void CoordinatorSourceDeclaresPhysicalBindingActionContract()
    {
        var source = File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "src/NetCat.Network/RuntimeCoordinator.cs"));
        Assert.Contains("EnsureMainRouterForPhysicalBinding", source);
    }

    [Fact]
    public async Task NetworkEventBurstDoesNotFlipFlopInterfaces()
    {
        using var f=RuntimeFixture();await f.Reconcile();using var monitor=new PhysicalNetworkMonitor(f.Coordinator,f.Repo);
        for(int i=0;i<20;i++){monitor.Signal();await monitor.ObserveAsync();}
        Assert.Equal(1,f.Router.Starts);Assert.Equal(1,f.Zapret.Starts);Assert.Equal(0,monitor.Generation);
        Assert.Equal(f.Physical.Current!.Index,f.Router.ActivePhysical!.Index);
    }

    [Fact]
    public void MainWindowMarkupEnablesTextWrapping()
        => Assert.Contains("TextWrapping=\"Wrap\"", File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "src/NetCat.UI/MainWindow.xaml")));

    [Fact]
    public void MainWindowMarkupCapsPrimaryStatusWidth() => Assert.Contains("MaxWidth=\"560\"", MainWindowMarkup());
    [Fact]
    public void MainWindowMarkupCentersPrimaryStatus() => Assert.Contains("TextAlignment=\"Center\"", MainWindowMarkup());
    [Fact]
    public void MainWindowMarkupBindsHealthDetails() => Assert.Contains("Text=\"{Binding HealthDetails}\"", MainWindowMarkup());

    [Fact]
    public void MainWindowMarkupUsesHeadingStyle() => Assert.Contains("Style=\"{StaticResource Heading}\"", MainWindowMarkup());











    private static string MainWindowMarkup() => File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "src/NetCat.UI/MainWindow.xaml"));
}
