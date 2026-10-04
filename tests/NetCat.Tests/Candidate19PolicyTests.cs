using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
public sealed class Candidate19PolicyTests
{
    [Fact] public void ListenerOwnerRetriesWhenTcpTableGrows()
    {
        int calls=0;
        var owner=LocalListener.Owner(1234,(nint memory,ref int bytes)=>
        {
            calls++;if(memory==0){bytes=28;return 122;}if(calls==2){bytes=52;return 122;}
            System.Runtime.InteropServices.Marshal.WriteInt32(memory,1);
            System.Runtime.InteropServices.Marshal.WriteByte(memory,12,4);System.Runtime.InteropServices.Marshal.WriteByte(memory,13,210);
            System.Runtime.InteropServices.Marshal.WriteInt32(memory,24,19019);return 0;
        });
        Assert.Equal(19019,owner);Assert.Equal(3,calls);
    }
    [Fact] public void PushedRouteOverlapWithPhysicalLanIsDetected() => Assert.Throws<InvalidDataException>(()=>OpenVpnLanPolicy.Validate(["192.168.0.0/16"],["192.168.1.4/24"]));
    [Fact] public void UnrelatedLanRemainsReachable() => OpenVpnLanPolicy.Validate(["10.30.0.0/16"],["192.168.1.4/24"]);
    [Fact] public void PhysicalFailoverReevaluatesOverlap()
    {OpenVpnLanPolicy.Validate(["10.30.0.0/16"],["192.168.1.4/24"]);Assert.Throws<InvalidDataException>(()=>OpenVpnLanPolicy.Validate(["10.30.0.0/16"],["10.30.0.4/24"]));}
    [Fact] public void LegacyStoredProfileGetsExplicitMigrationDiagnostic()
    {var p=new Profile{Name="Corporate legacy",Protocol="openvpn",OpenVpnConfig="client\ndev tun\nroute 10.1.0.0 255.255.0.0"};var original=p.OpenVpnConfig;var message=OpenVpnConfiguration.MigrationDiagnostic(p);Assert.Contains(p.Name,message);Assert.Contains("route",message);Assert.Equal(original,p.OpenVpnConfig);}
    [Fact] public void UserProfileEditPreservesConcurrentLearnedRoutes()
    {var p=new Profile{Protocol="openvpn"};var edited=JsonSettings.Clone(p);edited.Name="renamed";var live=JsonSettings.Clone(p);live.LearnedRoutes=["10.30.0.0/16"];var merged=Assert.Single(ProfileSettingsMerge.Merge([p],[edited],[live]));Assert.Equal("renamed",merged.Name);Assert.Equal(live.LearnedRoutes,merged.LearnedRoutes);}
    [Fact] public void ConcurrentCredentialEditIsRejected()
    {var p=new Profile{Protocol="openvpn"};var edited=JsonSettings.Clone(p);edited.Password="first";var live=JsonSettings.Clone(p);live.Password="second";Assert.Throws<OperationCanceledException>(()=>ProfileSettingsMerge.Merge([p],[edited],[live]));}
    [Fact] public async Task CandidateBarrierCompletesBeforeReadyPublication()
    {
        using var monitor=new OpenVpnGenerationMonitor(false);using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();int ready=0;
        monitor.CandidatePrepared+=_=>{entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};monitor.Ready+=_=>Interlocked.Increment(ref ready);
        var push=Task.Run(()=>monitor.Observe("PUSH_REPLY,route 10.1.0.0 255.255.0.0"));Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try{monitor.Observe("Initialization Sequence Completed");Assert.Equal(0,ready);}finally{release.Set();}
        await push;Assert.Equal(1,ready);
    }
    [Fact] public void RejectedPushCanRecoverAfterRestart()
    {using var monitor=new OpenVpnGenerationMonitor(false);int ready=0;monitor.Ready+=_=>ready++;monitor.Observe("PUSH_REPLY,route invalid");monitor.Observe("SIGUSR1 received, restarting");monitor.Observe("PUSH_REPLY,route 10.1.0.0 255.255.0.0");monitor.Observe("Initialization Sequence Completed");Assert.Equal(1,ready);}
    [Fact] public async Task IncompleteContinuationTimesOutWithoutPublication()
    {
        using var monitor=new OpenVpnGenerationMonitor(false){FragmentTimeout=TimeSpan.FromMilliseconds(30)};int ready=0;
        var failed=new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);monitor.Failed+=e=>failed.TrySetResult(e);monitor.Ready+=_=>ready++;
        monitor.Observe("PUSH_REPLY,route 10.1.0.0 255.255.0.0,push-continuation 2");
        Assert.IsType<TimeoutException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(3)));Assert.Equal(0,ready);
    }
}
