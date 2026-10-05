using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate26OpenVpnTests
{
    [Fact] public async Task PostReconnectControlReadyWithoutDatapathDoesNotBecomeHealthy()
    {
        bool healthy=true; var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f=new Candidate18ReconnectTests.Fixture(async (_,ct)=>{ if(healthy)return true; entered.TrySetResult(); await Task.Delay(Timeout.Infinite,ct); return false; });
        await f.Start(); healthy=false; await f.Reconnect(); f.Push(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(f.Service.Link); Assert.False(f.Service.IsRunning);
    }
    [Fact] public async Task PostReconnectHealthyDatapathBecomesReady()
    {
        int probes=0;
        await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>{Interlocked.Increment(ref probes);return Task.FromResult(true);});
        await f.Start(); var pid=f.Service.ProcessId; await f.Reconnect(); f.Push();
        await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.IsRunning);
        Assert.Equal(2,probes); Assert.Equal(pid,f.Service.ProcessId);
    }
    [Fact] public async Task PostReconnectDatapathTimeoutEscalatesToFullRestart()
    {
        bool healthy=true;
        await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>Task.FromResult(healthy));
        await f.Start(); var oldPid=f.Service.ProcessId; healthy=false; await f.Reconnect(); f.Push();
        await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.DataPathRecoveryRequired);
        Assert.Null(f.Service.Link); Assert.False(f.Service.Reconnecting);
        healthy=true; f.PrepareNextProcessTranscript(); await f.Service.EnsureRunningAsync(f.Profile,"",CancellationToken.None);
        Assert.NotEqual(oldPid,f.Service.ProcessId); Assert.True(f.Service.IsRunning);
    }
    [Fact] public async Task StalePostReconnectProbeCannotPublishReady()
    {
        int calls=0;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>Interlocked.Increment(ref calls)==1?Task.FromResult(true):Block());
        Task<bool> Block(){entered.TrySetResult();return release.Task;}
        await f.Start(); await f.Reconnect(); f.Push(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Reconnect(); release.SetResult(true); await Task.Delay(200);
        Assert.Null(f.Service.Link); Assert.False(f.Service.DataPathRecoveryRequired);
    }
    [Fact] public async Task DesiredOffDuringReconnectPreventsRestart()
    {
        bool healthy=true;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f=new Candidate18ReconnectTests.Fixture(async (_,ct)=>{if(healthy)return true;entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return false;});
        await f.Start(); healthy=false;await f.Reconnect();f.Push();await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Service.StopAsync(); Assert.Equal(0,f.Service.ProcessId); Assert.Null(f.Service.Link);
        lock(f.Routes) Assert.Empty(f.Routes);
    }
    [Fact] public async Task ProfileSwitchDuringReconnectDoesNotResurrectOldGeneration()
    {
        using var f=new OpenVpnBehaviorFixture();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OpenVpn.Attempt=async ct=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);};
        var pass=f.Pass();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var next=new Profile{Protocol="openvpn"};f.Settings.Profiles.Add(next);
        f.Desired.Current=f.Desired.Current with{SelectedOpenVpnProfileId=next.Id};f.Coordinator.CancelOpenVpnOperation();
        await pass;f.OpenVpn.Attempt=null;await f.Pass(); Assert.Equal(next.Id,f.OpenVpn.ActiveProfileId);
    }
    [Fact] public async Task PostReconnectFullRestartUsesBoundedRetry()
    {
        using var f=new OpenVpnBehaviorFixture();f.Fail("OpenVPN datapath probe timed out.");
        await f.Pass();await f.Storm(1);f.Clock.Advance(TimeSpan.FromSeconds(5));await f.Pass();Assert.Equal(2,f.OpenVpn.Starts);
    }
    [Fact] public async Task RepeatedDatapathFailureDoesNotRestartStorm()
    {
        using var f=new OpenVpnBehaviorFixture();f.Fail("OpenVPN datapath probe timed out.");
        for(int i=0;i<4;i++){await f.Pass();f.Clock.Advance(TimeSpan.FromHours(1));}
        await f.Storm(4);Assert.Equal(OpenVpnRetryState.WaitingForRelevantChange,f.Coordinator.OpenVpnRetryController.State);
    }
    [Fact] public async Task EstablishedSessionDatapathFailureIsChargedBeforeFullRestart()
    {
        using var f=new OpenVpnBehaviorFixture();await f.Pass();
        f.OpenVpn.IsRunning=false;f.OpenVpn.Link=null;f.OpenVpn.DataPathRecoveryRequired=true;
        await f.Pass();await f.Storm(1);
        Assert.Equal(OpenVpnRetryState.RetryScheduled,f.Coordinator.OpenVpnRetryController.State);
        f.Clock.Advance(TimeSpan.FromSeconds(5));await f.Pass();
        Assert.Equal(2,f.OpenVpn.Starts);Assert.True(f.OpenVpn.IsRunning);
        // The independent carrier starts once and survives link recovery.
        Assert.Equal(1,f.Main.Starts);Assert.Null(f.Main.ActiveProfileId);Assert.Equal(0,f.Main.Stops);
    }
    [Fact] public async Task PhysicalGenerationChangeRequiresNewDatapathProof()
    {
        bool healthy=true;int probes=0;
        await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>{Interlocked.Increment(ref probes);return Task.FromResult(healthy);});
        await f.Start();healthy=false; f.Service.PhysicalNetworkChanged();
        await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.DataPathRecoveryRequired);
        Assert.False(f.Service.IsRunning);Assert.True(probes>=4);
    }
    [Fact] public async Task PhysicalChangeDiscardsOldProbeCompletionBeforeNewProof()
    {
        int calls=0;
        var oldEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResult=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResult=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>
        {
            var call=Interlocked.Increment(ref calls);
            if(call==1)return Task.FromResult(true);
            if(call==2){oldEntered.TrySetResult();return oldResult.Task;}
            newEntered.TrySetResult();return newResult.Task;
        });
        await f.Start();await f.Reconnect();f.Push();await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Service.PhysicalNetworkChanged();oldResult.TrySetResult(true);
        await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Null(f.Service.Link);
        newResult.TrySetResult(true);await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.IsRunning);
        Assert.False(f.Service.DataPathRecoveryRequired);
    }
    [Fact] public void DnsProbeRejectsWrongTransactionAndMalformedReplies()
    {
        byte[] query=[1,2,1,0,0,1,0,0,0,0,0,0,0,0,2,0,1];
        var reply=query.ToArray();reply[2]=0x81;reply[3]=5;
        Assert.True(OpenVpnDataPathProbe.IsResponse(query,reply));reply[0]=3;Assert.False(OpenVpnDataPathProbe.IsResponse(query,reply));
        Assert.False(OpenVpnDataPathProbe.IsResponse(query,[1,2,128]));
    }
    [Fact] public async Task DatapathProbeRequiresTcpDnsRoundTripOnBoundInterface()
    {
        var loopback=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.NetworkInterfaceType==System.Net.NetworkInformation.NetworkInterfaceType.Loopback);
        var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
        try
        {
            using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var link=new OpenVpnLink(loopback.Name,loopback.GetIPProperties().GetIPv4Properties().Index,"127.0.0.1","127.0.0.1","127.0.0.1",[]){DnsPort=((System.Net.IPEndPoint)listener.LocalEndpoint).Port};
            var probe=OpenVpnDataPathProbe.CheckAsync(link,ct.Token);
            using var peer=await listener.AcceptTcpClientAsync(ct.Token);var stream=peer.GetStream();
            var frame=new byte[19];await stream.ReadExactlyAsync(frame,ct.Token);
            Assert.Equal(17,frame[1]);Assert.Equal(new byte[]{0,0,2,0,1},frame[14..]);
            Assert.False(probe.IsCompleted);frame[4]=0x81;frame[5]=5;
            await stream.WriteAsync(frame,ct.Token);Assert.True(await probe);
        }
        finally{listener.Stop();}
    }
}
