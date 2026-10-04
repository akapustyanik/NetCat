using System.Net;
using System.Net.NetworkInformation;
using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate27AddressTests
{
    internal sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>Ticks;
        public Task Advance(TimeSpan delay,CancellationToken ct){ct.ThrowIfCancellationRequested();Ticks+=delay.Ticks;return Task.CompletedTask;}
    }
    private static readonly OpenVpnLink Link=new("synthetic",4242,"192.0.2.2","192.0.2.1","192.0.2.53",[]);
    [Fact] public async Task PreferredImmediatelyDoesNotDelayStartup()
    {
        int delays=0;await OpenVpnAddressReadiness.WaitAsync(Link,()=>true,default,TimeSpan.FromSeconds(10),_=>OpenVpnAddressState.Preferred,
            delay:(_,_)=>{delays++;throw new Exception("Unexpected delay");});Assert.Equal(0,delays);
    }
    [Fact] public async Task TentativeThenPreferredUsesActualState()
    {
        var clock=new Clock();int reads=0,waits=0;
        await OpenVpnAddressReadiness.WaitAsync(Link,()=>true,default,TimeSpan.FromSeconds(10),_=>++reads==4?OpenVpnAddressState.Preferred:OpenVpnAddressState.Tentative,clock,
            (d,ct)=>{waits++;Assert.Equal(TimeSpan.FromMilliseconds(250),d);return clock.Advance(d,ct);});
        Assert.Equal(4,reads);Assert.Equal(3,waits);Assert.Equal(TimeSpan.FromMilliseconds(750).Ticks,clock.Ticks);
    }
    [Theory][InlineData(OpenVpnAddressState.Duplicate)][InlineData(OpenVpnAddressState.Invalid)][InlineData(OpenVpnAddressState.Deprecated)]
    public async Task UnusableStateFailsImmediately(OpenVpnAddressState state)
    {
        int waits=0;await Assert.ThrowsAsync<IOException>(()=>OpenVpnAddressReadiness.WaitAsync(Link,()=>true,default,TimeSpan.FromSeconds(10),_=>state,
            delay:(_,_)=>{waits++;return Task.CompletedTask;}));Assert.Equal(0,waits);
    }
    [Fact] public async Task AddressReadinessTimeoutFailsClosed()
    {
        var clock=new Clock();int reads=0;await Assert.ThrowsAsync<TimeoutException>(()=>OpenVpnAddressReadiness.WaitAsync(Link,()=>true,default,TimeSpan.FromSeconds(10),_=>{reads++;return OpenVpnAddressState.Tentative;},clock,clock.Advance));
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks,clock.Ticks);Assert.Equal(41,reads);
    }
    [Fact] public async Task CancellationDuringDadWaitReturnsPromptly()
    {
        using var ct=new CancellationTokenSource();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait=OpenVpnAddressReadiness.WaitAsync(Link,()=>true,ct.Token,TimeSpan.FromSeconds(10),_=>OpenVpnAddressState.Tentative,
            delay:async(_,token)=>{entered.SetResult();await Task.Delay(Timeout.Infinite,token);});
        await entered.Task;ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>wait.WaitAsync(TimeSpan.FromSeconds(1)));
    }
    [Fact] public async Task DadWaitDoesNotBusySpin()
    {
        using var ct=new CancellationTokenSource();int reads=0;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait=OpenVpnAddressReadiness.WaitAsync(Link,()=>true,ct.Token,TimeSpan.FromSeconds(10),_=>{reads++;return OpenVpnAddressState.Missing;},
            delay:async(d,token)=>{Assert.Equal(TimeSpan.FromMilliseconds(250),d);entered.SetResult();await Task.Delay(Timeout.Infinite,token);});
        await entered.Task;Assert.Equal(1,reads);ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>wait);
    }
    [Fact] public async Task AdapterDisappearanceRemainsBoundedStartupRace()
    {
        var states=new Queue<OpenVpnAddressState>([OpenVpnAddressState.Tentative,OpenVpnAddressState.Missing,OpenVpnAddressState.Missing,OpenVpnAddressState.Preferred]);var clock=new Clock();
        await OpenVpnAddressReadiness.WaitAsync(Link,()=>true,default,TimeSpan.FromSeconds(10),_=>states.Dequeue(),clock,clock.Advance);Assert.Empty(states);
    }
    [Fact] public void ReadinessMatchesBothInterfaceAndExactIpv4()
    {
        var address=IPAddress.Parse(Link.Address);
        OpenVpnAddressObservation[] rows=[new(4243,address,DuplicateAddressDetectionState.Preferred),new(4242,IPAddress.Parse("192.0.2.3"),DuplicateAddressDetectionState.Preferred),new(4242,address,DuplicateAddressDetectionState.Tentative)];
        Assert.Equal(OpenVpnAddressState.Tentative,OpenVpnAddressReadiness.Select(4242,address,rows));Assert.Equal(OpenVpnAddressState.Missing,OpenVpnAddressReadiness.Select(4244,address,rows));
    }
    [Fact] public async Task StaleStateCaptureCannotReturnPreferred()
    {
        bool current=true;await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>OpenVpnAddressReadiness.WaitAsync(Link,()=>current,default,TimeSpan.FromSeconds(10),_=>{current=false;return OpenVpnAddressState.Preferred;}));
    }
    [Fact] public void ManagedCaptureSeesRealLoopbackAddress()
    {
        var nic=NetworkInterface.GetAllNetworkInterfaces().First(n=>n.NetworkInterfaceType==NetworkInterfaceType.Loopback);
        var link=Link with{Index=nic.GetIPProperties().GetIPv4Properties()!.Index,Address="127.0.0.1"};
        Assert.Equal(OpenVpnAddressState.Preferred,OpenVpnAddressReadiness.Capture(link));
        Assert.Equal(OpenVpnAddressState.Missing,OpenVpnAddressReadiness.Capture(link with{Address="192.0.2.2"}));
    }
}

[Collection("Candidate18 native resources")]
public sealed class Candidate27StartupTests
{
    private sealed class Dad
    {
        public OpenVpnAddressState State=OpenVpnAddressState.Tentative;
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Probes;
        public Task Delay(TimeSpan _,CancellationToken ct){Entered.TrySetResult();return Release.Task.WaitAsync(ct);}
        public void Preferred(){State=OpenVpnAddressState.Preferred;Release.TrySetResult();}
        public Candidate18ReconnectTests.Fixture Fixture()=>new((_,_)=>{Interlocked.Increment(ref Probes);return Task.FromResult(true);},_=>State,Delay);
    }
    [Fact] public async Task TentativeThenPreferredAllowsDatapathProbe()
    {
        var dad=new Dad();await using var f=dad.Fixture();var start=f.Start();await dad.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0,dad.Probes);dad.Preferred();await start;Assert.Equal(1,dad.Probes);Assert.NotNull(f.Service.Link);
    }
    [Fact] public async Task TentativeDoesNotCallDatapathProbeEarly()
    {
        var dad=new Dad();await using var f=dad.Fixture();var start=f.Start();await dad.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0,dad.Probes);Assert.Null(f.Service.Link);Assert.Empty(f.Routes);Assert.False(File.Exists(Path.Combine(f.Root,"openvpn-route.json")));
        Assert.Throws<IOException>(()=>f.Service.DestinationLeases.Capture());dad.Preferred();await start;
    }
    [Fact] public async Task DuplicateAddressFailsWithoutPublishingReady()
    {
        int probes=0;await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>{probes++;return Task.FromResult(true);},_=>OpenVpnAddressState.Duplicate);
        await Assert.ThrowsAsync<IOException>(()=>f.Start());Assert.Equal(0,probes);Assert.Null(f.Service.Link);Assert.Empty(f.Routes);Assert.True(f.Service.DataPathRecoveryRequired);
    }
    [Fact] public async Task AddressReadinessFailureCleansOwnedRoutes()
    {
        var clock=new Candidate27AddressTests.Clock();await using var f=new Candidate18ReconnectTests.Fixture(addressState:_=>OpenVpnAddressState.Missing,addressDelay:clock.Advance,readinessTime:clock);
        await Assert.ThrowsAsync<TimeoutException>(()=>f.Start());Assert.Empty(f.Routes);Assert.False(File.Exists(Path.Combine(f.Root,"openvpn-route.json")));Assert.Throws<IOException>(()=>f.Service.DestinationLeases.Capture());
    }
    [Fact] public async Task DesiredOffDuringDadWaitCannotPublishReady()
    {
        var dad=new Dad();await using var f=dad.Fixture();var start=f.Start();await dad.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Service.StopAsync();dad.Preferred();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>start);Assert.Null(f.Service.Link);Assert.Empty(f.Routes);Assert.Equal(0,dad.Probes);Assert.False(f.Service.DataPathRecoveryRequired);
    }
    [Fact] public async Task ProfileSwitchDuringDadWaitCannotPublishReady()
    {
        var dad=new Dad();await using var f=dad.Fixture();var start=f.Start();await dad.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Service.StopAsync();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>start);
        var next=new Profile{Protocol="openvpn",OpenVpnConfig="client\ndev tun\n",OpenVpnLegacyProviderRequired=true};
        f.PrepareNextProcessTranscript();dad.Preferred();var link=await f.Service.StartAsync(next,"",default);
        Assert.Equal(next.Id,link.ProfileId);Assert.Equal(next.Id,f.Service.DestinationLeases.Capture().Link.ProfileId);Assert.Equal(1,dad.Probes);
    }
    [Fact] public async Task PhysicalGenerationChangeDuringDadWaitCannotPublishReady()
    {
        var dad=new Dad();await using var f=dad.Fixture();int ready=0;f.Service.Log+=s=>{if(s.StartsWith("OPENVPN_GENERATION_READY"))ready++;};
        var start=f.Start();await dad.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));dad.State=OpenVpnAddressState.Preferred;
        f.Service.PhysicalNetworkChanged();await start.WaitAsync(TimeSpan.FromSeconds(5));dad.Release.TrySetResult();
        Assert.Equal(1,dad.Probes);Assert.Equal(1,ready);Assert.False(f.Service.DataPathRecoveryRequired);Assert.NotNull(f.Service.Link);
    }
    [Fact] public async Task ReconnectDuringDadWaitCannotPublishOldGeneration()
    {
        var dad=new Dad();await using var f=dad.Fixture();var start=f.Start();await dad.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Append("SIGUSR1[soft,ping-restart] received, process restarting");await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.RuntimePhase==OpenVpnRuntimePhase.Reconnecting);
        dad.Preferred();f.Push("10.2.0.0");await start.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(1,dad.Probes);Assert.Contains("10.2.0.0/16",f.Service.Link!.LearnedRoutes);Assert.False(f.Service.DataPathRecoveryRequired);
    }
    [Fact] public async Task OldPreferredCompletionCannotPublishNewGeneration()
    {
        var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);bool preferred=false;int probes=0;
        await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>{probes++;return Task.FromResult(true);},_=>preferred?OpenVpnAddressState.Preferred:OpenVpnAddressState.Tentative,(_,_)=>{entered.TrySetResult();return blocked.Task;});
        var start=f.Start();await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));preferred=true;f.Service.PhysicalNetworkChanged();await start.WaitAsync(TimeSpan.FromSeconds(5));
        var link=f.Service.Link;blocked.SetResult();Assert.Equal(1,probes);Assert.Same(link,f.Service.Link);Assert.False(f.Service.DataPathRecoveryRequired);
    }
    [Fact] public async Task DatapathProbeStillRequiredAfterPreferred()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var proof=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>{entered.TrySetResult();return proof.Task;});var start=f.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.NotEmpty(f.Routes);Assert.Null(f.Service.Link);Assert.Throws<IOException>(()=>f.Service.DestinationLeases.Capture());
        proof.SetResult(true);await start;Assert.NotNull(f.Service.Link);
    }
    [Fact] public async Task DatapathFailureAfterPreferredStillEscalatesRecovery()
    {
        int probes=0;await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>{probes++;return Task.FromResult(false);});
        await Assert.ThrowsAsync<TimeoutException>(()=>f.Start());Assert.Equal(3,probes);Assert.True(f.Service.DataPathRecoveryRequired);Assert.Empty(f.Routes);Assert.Null(f.Service.Link);
    }
    [Fact] public async Task DadWaitDoesNotInstallOpenVpnDefaultRoute()
    {
        var dad=new Dad();await using var f=dad.Fixture();var start=f.Start();await dad.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.Commands);dad.Preferred();await start;Assert.DoesNotContain(f.Commands,s=>s.Contains("0.0.0.0/0")||s.Contains("9999"));
    }
}
