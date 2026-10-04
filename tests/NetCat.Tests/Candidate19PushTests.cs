using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate19PushTests
{
    private sealed class Probe
    {
        public readonly OpenVpnGenerationMonitor Monitor = new(false);
        public readonly List<OpenVpnGeneration> Ready = [];
        public readonly List<Exception> Errors = [];
        public int Invalidations;
        public Probe() { Monitor.Ready += Ready.Add; Monitor.Failed += Errors.Add; Monitor.Invalidated += _ => Invalidations++; }
        public void Push(string value) => Monitor.Observe("PUSH: Received control message: 'PUSH_REPLY," + value + "'");
        public void Complete() => Monitor.Observe("Initialization Sequence Completed");
        public void Restart() => Monitor.Observe("SIGUSR1[soft,ping-restart] received, process restarting");
    }
    [Theory]
    [InlineData("route 10.20.0.0 255.255.0.0 net_gateway")]
    [InlineData("route 10.20.0.0 255.255.0.0 remote_host")]
    [InlineData("route remote_host")]
    [InlineData("route 10.20.0.0 255.255.0.0 192.168.1.1")]
    [InlineData("route 10.20.0.0 255.255.0.0 vpn_gateway 12")]
    [InlineData("route malformed 255.255.0.0")]
    public void UnsupportedRouteSemanticsPreventGenerationPublication(string route)
    { var p = new Probe(); p.Push(route); p.Complete(); Assert.Empty(p.Ready); Assert.NotEmpty(p.Errors); }
    [Fact] public void NetGatewayBypassIsNeverConvertedToCorporateOwnership()
    { var p = new Probe(); p.Push("route 10.20.0.0 255.255.0.0 net_gateway"); p.Complete(); Assert.DoesNotContain(p.Ready, g => g.Routes.Contains("10.20.0.0/16")); Assert.NotEmpty(p.Errors); }
    [Fact] public void VpnGatewayRouteRetainsTunnelSemantics()
    { var p = new Probe(); p.Push("route 10.20.0.0 255.255.0.0 vpn_gateway"); p.Complete(); Assert.Equal(["10.20.0.0/16"], Assert.Single(p.Ready).Routes); }
    [Fact] public void PushedHostRouteWithoutNetmaskBecomes32()
    { var p = new Probe(); p.Push("route 10.20.30.40"); p.Complete(); Assert.Equal(["10.20.30.40/32"], Assert.Single(p.Ready).Routes); }
    [Fact] public void MultiFragmentPushReplyPreservesAllRoutes()
    { var p = new Probe(); p.Push("route 10.1.0.0 255.255.0.0,push-continuation 2"); p.Push("route 10.2.0.0 255.255.0.0,push-continuation 1"); p.Complete(); Assert.Equal(["10.1.0.0/16", "10.2.0.0/16"], Assert.Single(p.Ready).Routes); }
    [Fact] public void MultiFragmentPushReplyPreservesDnsFromEarlierFragment()
    { var p = new Probe(); p.Push("dhcp-option DNS 10.1.0.53,push-continuation 2"); p.Push("route 10.2.0.0 255.255.0.0,push-continuation 1"); p.Complete(); Assert.Equal("10.1.0.53", Assert.Single(p.Ready).Dns); }
    [Fact] public void MultiFragmentPushReplyPreservesGateway()
    { var p = new Probe(); p.Push("route-gateway 10.1.0.1,push-continuation 2"); p.Push("route 10.2.0.0 255.255.0.0,push-continuation 1"); p.Complete(); Assert.Equal("10.1.0.1", Assert.Single(p.Ready).Gateway); }
    [Fact] public void IncompletePushContinuationDoesNotPublishGeneration()
    { var p = new Probe(); p.Push("route 10.1.0.0 255.255.0.0,push-continuation 2"); p.Complete(); Assert.Empty(p.Ready); }
    [Theory] [InlineData("3")] [InlineData("bad")] [InlineData("1")]
    public void MalformedContinuationFailsClosed(string value)
    { var p = new Probe(); p.Push("route 10.1.0.0 255.255.0.0,push-continuation " + value); p.Complete(); Assert.Empty(p.Ready); Assert.NotEmpty(p.Errors); }
    [Fact] public void ModernOpenVpn26DnsPushIsRecognized()
    { var p = new Probe(); p.Push("dns server 0 address 10.1.0.53"); p.Complete(); Assert.Equal("10.1.0.53", Assert.Single(p.Ready).Dns); }
    [Fact] public void ModernDnsServerIdOrderingIsDeterministic()
    { var p = new Probe(); p.Push("dns server 2 address 10.2.0.53,dns server 0 address 10.1.0.53"); p.Complete(); Assert.Equal("10.1.0.53", Assert.Single(p.Ready).Dns); }
    [Theory] [InlineData("dns server 0 address ::1")] [InlineData("dns server 0 address 10.1.0.53,dns server 0 transport DoT")]
    public void UnsupportedDnsIsExplicit(string option)
    { var p = new Probe(); p.Push(option); p.Complete(); Assert.Empty(p.Ready); Assert.NotEmpty(p.Errors); }
    [Fact] public void LegacyDhcpDnsStillWorks()
    { var p = new Probe(); p.Push("dhcp-option DNS 10.1.0.53"); p.Complete(); Assert.Equal("10.1.0.53", Assert.Single(p.Ready).Dns); }
    [Fact] public void DuplicateRestartLinesInvalidateGenerationOnce()
    { var p = new Probe(); p.Push("route 10.1.0.0 255.255.0.0"); p.Complete(); p.Restart(); p.Monitor.Observe("Connection reset, restarting"); Assert.Equal(1, p.Invalidations); }
    [Fact] public void MonitorCallbacksRunOutsideInternalLock()
    {
        var monitor = new OpenVpnGenerationMonitor(false); bool acquired = false;
        monitor.Ready += _ => acquired = Task.Run(() => monitor.Revision).Wait(TimeSpan.FromSeconds(1));
        monitor.Observe("PUSH: 'PUSH_REPLY,route 10.1.0.0 255.255.0.0'"); monitor.Observe("Initialization Sequence Completed");
        Assert.True(acquired);
    }
}
