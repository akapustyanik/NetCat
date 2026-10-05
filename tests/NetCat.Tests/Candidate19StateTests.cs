using System.Diagnostics;
using System.Text.RegularExpressions;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate19StateTests
{
    internal sealed class Fixture : IAsyncDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "NetCat-C19-state-" + Guid.NewGuid().ToString("N"));
        public readonly Candidate12Tests.Fixture Base;
        public readonly OpenVpnService Service;
        public readonly List<RouteRow> Routes = [];
        public readonly RuntimeCoordinator Coordinator;
        public bool AdapterReleased;
        public bool FailCleanup;
        private int launches;
        private readonly string transcript;
        private sealed class Adapter(Action release) : IDisposable { public void Dispose() => release(); }
        public Fixture(TimeProvider? time = null)
        {
            Directory.CreateDirectory(Root); transcript = Path.Combine(Root, "transcript.txt"); File.WriteAllText(transcript, "");
            Base = new(new SettingsStore(Path.Combine(Root, "settings")));
            var script = "$seen=0; while($true) { $rows=@(Get-Content -LiteralPath " + PhysicalNetwork.Literal(transcript) + "); while($seen -lt $rows.Length) { [Console]::WriteLine($rows[$seen]); $seen++ }; Start-Sleep -Milliseconds 20 }";
            Service = new("fixture-no-openvpn.exe", Path.Combine(Root, "runtime")) {
                TimeProvider=time??TimeProvider.System, CapturePhysicalPrefixes=()=>[],
                CreateAdapterOverride = () => new Adapter(() => AdapterReleased = true),
                StartProcessOverride = host => {
                    if (Interlocked.Increment(ref launches) > 1) File.WriteAllLines(transcript,["PUSH: 'PUSH_REPLY,route 10.1.0.0 255.255.0.0,route-gateway 10.1.0.1,dhcp-option DNS 10.1.0.53'","Initialization Sequence Completed"]);
                    host.Start(ProcessHost.PowerShellPath, ["-NoProfile", "-NonInteractive", "-Command", script], log: false);
                },
                CaptureLinkOverride = g => new("Fixture-OpenVPN",4242,"10.1.0.2",g.Gateway,g.Dns,g.Routes),
                DataPathProbe = (_,_) => Task.FromResult(true),
                CaptureAddressState = _=>OpenVpnAddressState.Preferred,
                CaptureRouteTableOverride = () => { lock (Routes) return Routes.ToArray(); },
                PowerShellOverride = (command, ct) => {
                    lock (Routes) {
                        var prefix = Regex.Match(command,"(?:-DestinationPrefix|DestinationPrefix -eq) '([^']+)'").Groups[1].Value;
                        var gateway = Regex.Match(command,"(?:-NextHop|NextHop -eq) '([^']+)'").Groups[1].Value;
                        if (command.StartsWith("New-NetRoute")) Routes.Add(new(4242,prefix,gateway,uint.Parse(Regex.Match(command, @"-RouteMetric (\d+)").Groups[1].Value),"NetMgmt","Manual"));
                        else { if(FailCleanup) return Task.FromResult((1,"injected cleanup failure")); Routes.RemoveAll(r=>r.DestinationPrefix==prefix && r.NextHop==gateway); }
                        return Task.FromResult((0,""));
                    }
                }
            };
            Coordinator = new(Base.Desired,Base.Router,Base.Zapret,Service) { GetSettings=()=>Base.Repo.CurrentSettings,
                PhysicalNetworkProvider=Base.Physical,TunnelInspector=Base.Tunnel,Clock=Base.Clock,
                VerifyOpenVpnRoutesInRouteTable=(_,_)=>new(RouteObservationStatus.Verified,[]),
                OnOpenVpnRoutesLearned=Base.Repo.UpdateOpenVpnLearnedRoutesAsync, BackoffIntervals=[TimeSpan.FromHours(1)] };
        }
        public void Append(params string[] lines) => File.AppendAllLines(transcript,lines);
        public async Task Start()
        {
            await Base.Repo.UpdateSettingsAsync(s=>{s.Profiles.First(p=>p.Id==Base.O.Id).OpenVpnConfig="client\ndev tun\n";return s;});
            Base.Desired.Current=Base.Desired.Current with {OpenVpnEnabled=true};
            Append("PUSH: 'PUSH_REPLY,route 10.1.0.0 255.255.0.0,route-gateway 10.1.0.1,dhcp-option DNS 10.1.0.53'","Initialization Sequence Completed");
            await Coordinator.ReconcileAsync(ReconcileReason.UserChangedSettings); Assert.True(Service.IsRunning);
        }
        public async Task Reconnect()
        { Append("SIGUSR1[soft,ping-restart] received, process restarting"); await Until(()=>Service.Link==null && Service.Reconnecting); }
        public static async Task Until(Func<bool> condition)
        { using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10)); while(!condition()) await Task.Delay(20,ct.Token); }
        public async Task Off()
        { Base.Desired.Current=Base.Desired.Current with {OpenVpnEnabled=false}; Coordinator.RequestReconcile(ReconcileReason.UserChangedSettings); await Coordinator.ReconcileAsync(ReconcileReason.UserChangedSettings); }
        public void Stable()
        { Assert.Equal(1,Base.Router.Starts);Assert.Equal(0,Base.Router.Stops);Assert.Equal(0,Base.Router.Overlays);Assert.Equal(1,Base.Router.SessionRevision);Assert.Equal(1,Base.Zapret.Starts);Assert.Equal(0,Base.Zapret.Stops); }
        public async ValueTask DisposeAsync()
        { Coordinator.Dispose();Base.Dispose();FailCleanup=false;await Service.StopAsync();Service.Dispose();Directory.Delete(Root,true); }
    }
    [Fact] public async Task DesiredOffDuringReconnectStopsOwnedOpenVpnProcess()
    { await using var f=new Fixture();await f.Start();int pid=f.Service.ProcessId;await f.Reconnect();await f.Off(); Assert.Equal(0,f.Service.ProcessId); Assert.True(f.AdapterReleased); Assert.Null(f.Service.Link);f.Stable(); }
    [Fact] public async Task DesiredOffDuringReconnectRunsExactRouteCleanup()
    { await using var f=new Fixture();await f.Start();f.FailCleanup=true;await f.Reconnect();f.FailCleanup=false;await f.Off();lock(f.Routes)Assert.Empty(f.Routes);Assert.True(f.AdapterReleased);f.Stable(); }
    [Fact] public async Task CancelPendingConnectionCannotReportStoppedWhileProcessAlive()
    { await using var f=new Fixture();await f.Start();await f.Reconnect();f.Service.CancelPendingConnection();await f.Coordinator.ReconcileAsync(ReconcileReason.OpenVpnStateChanged);Assert.True(f.Service.ProcessId==0 || f.Coordinator.CurrentObservedState!.OpenVpnStatus!=ObservedComponentState.Stopped);f.Stable(); }
    [Fact] public async Task ProfileDeletionDuringReconnectStopsOwnedProcess()
    {await using var f=new Fixture();await f.Start();await f.Reconnect();await f.Base.Repo.UpdateSettingsAsync(s=>{s.Profiles.RemoveAll(p=>p.Id==f.Base.O.Id);return s;});f.Coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);await f.Coordinator.ReconcileAsync(ReconcileReason.UserChangedSettings);Assert.Equal(0,f.Service.ProcessId);Assert.True(f.AdapterReleased);f.Stable();}
    [Fact] public async Task PolicyRestartDuringReconnectDoesNotLeakOldProcess()
    {await using var f=new Fixture();await f.Start();int old=f.Service.ProcessId;await f.Reconnect();await f.Base.Repo.UpdateSettingsAsync(s=>{s.Profiles.First(p=>p.Id==f.Base.O.Id).AllowPublicPushedRoutes=true;return s;});f.Coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);await f.Coordinator.ReconcileAsync(ReconcileReason.UserChangedSettings);Assert.True(f.Service.IsRunning);Assert.NotEqual(old,f.Service.ProcessId);Assert.DoesNotContain(Process.GetProcesses().Select(p=>{var id=p.Id;p.Dispose();return id;}),id=>id==old);f.Stable();}
    [Fact] public async Task ProfileSwitchDuringReconnectDoesNotLeakOldProcess()
    {await using var f=new Fixture();await f.Start();int old=f.Service.ProcessId;await f.Reconnect();var next=new Profile{Protocol="openvpn",OpenVpnConfig="client\ndev tun\n"};await f.Base.Repo.UpdateSettingsAsync(s=>{s.Profiles.Add(next);s.OpenVpnProfileId=next.Id;return s;});f.Base.Desired.Current=f.Base.Desired.Current with{SelectedOpenVpnProfileId=next.Id};f.Coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);await f.Coordinator.ReconcileAsync(ReconcileReason.UserChangedSettings);Assert.True(f.Service.IsRunning);Assert.Equal(next.Id,f.Service.ActiveProfileId);Assert.NotEqual(old,f.Service.ProcessId);Assert.DoesNotContain(Process.GetProcesses().Select(p=>{var id=p.Id;p.Dispose();return id;}),id=>id==old);f.Stable();}
    private sealed class ManualTime : TimeProvider
    {private long ticks;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;public override long GetTimestamp()=>ticks;public void Advance(TimeSpan elapsed)=>ticks+=elapsed.Ticks;}
    [Fact] public async Task LongReconnectBecomesDegradedAndUserOffStopsIt()
    {
        var time=new ManualTime();await using var f=new Fixture(time);await f.Start();await f.Reconnect();
        // Link invalidation is visible before the asynchronous service callback
        // installs its reconnect timestamp. Advance virtual time only after
        // that transition, preserving all lifecycle and cleanup assertions.
        await Fixture.Until(()=>f.Service.RuntimePhase==OpenVpnRuntimePhase.Reconnecting);
        time.Advance(TimeSpan.FromSeconds(65));Assert.Equal(OpenVpnRuntimePhase.LongReconnect,f.Service.RuntimePhase);
        await f.Coordinator.ReconcileAsync(ReconcileReason.OpenVpnStateChanged);Assert.Equal(ConvergencePhase.Degraded,f.Coordinator.CurrentConvergenceState.Phase);
        await f.Off();Assert.Equal(0,f.Service.ProcessId);f.Stable();
    }
    [Fact] public async Task DesiredOffAfterUnexpectedExitCompletesResidualCleanup()
    {
        await using var f=new Fixture();await f.Start();f.Base.Desired.Current=f.Base.Desired.Current with{OpenVpnEnabled=false};
        using(var owned=Process.GetProcessById(f.Service.ProcessId)){owned.Kill();await owned.WaitForExitAsync();}
        await f.Off();Assert.Equal(0,f.Service.ProcessId);Assert.True(f.AdapterReleased);lock(f.Routes)Assert.Empty(f.Routes);f.Stable();
    }
}
