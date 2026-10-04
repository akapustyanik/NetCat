using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

internal sealed class OpenVpnBehaviorFixture : IDisposable
{
    internal sealed class Runtime : IOpenVpnRuntime
    {
        public int Starts, Stops, Cancels;
        public int NativeModuleRevision { get; set; }
        public bool DataPathRecoveryRequired { get; set; }
        public bool IsRunning { get; set; }
        public Guid? ActiveProfileId { get; set; }
        public OpenVpnLink? Link { get; set; }
        public Func<CancellationToken, Task>? Attempt;
        public event Action<int,int>? ProcessExited;
        public event Action? LinkChanged;
        public async Task<OpenVpnLink> EnsureRunningAsync(Profile p, string dns, CancellationToken ct)
        {
            Starts++;
            if (Attempt != null) await Attempt(ct);
            DataPathRecoveryRequired=false;
            IsRunning = true; ActiveProfileId = p.Id;
            return Link = new("fixture", 40, "10.0.0.2", "10.0.0.1", dns, []) { ProfileId = p.Id };
        }
        public Task EnsureStoppedAsync(CancellationToken ct) { Stops++; IsRunning=false; Link=null; ActiveProfileId=null; return Task.CompletedTask; }
        public void CancelPendingConnection() => Cancels++;
        public void Exit() { IsRunning=false; Link=null; ProcessExited?.Invoke(123,1); }
        public void SidecarLost() => LinkChanged?.Invoke();
        public RouteObservationResult VerifyRoutes() => new(RouteObservationStatus.Verified, []);
    }
    public readonly string Root = Path.Combine(Path.GetTempPath(), "NetCat-retry-"+Guid.NewGuid().ToString("N"));
    public readonly Candidate12Tests.Router Main = new();
    public readonly Candidate12Tests.Zapret Zapret = new();
    public readonly Runtime OpenVpn = new();
    public readonly Candidate12Tests.TestClock Clock = new();
    public readonly Candidate9Tests.FakePhysicalProvider Physical = new();
    public readonly Candidate9Tests.FakeDesiredProvider Desired;
    public readonly Profile Profile = new(){Protocol="openvpn",OpenVpnConfig="client\ndev tun\nremote 127.0.0.1 9\n"};
    public readonly AppSettings Settings;
    public readonly RuntimeCoordinator Coordinator;
    public int Learned;
    public OpenVpnBehaviorFixture()
    {
        Settings=new(){Profiles=[Profile],OpenVpnProfileId=Profile.Id};
        Desired=new(new(){OpenVpnEnabled=true,SelectedOpenVpnProfileId=Profile.Id});
        Coordinator=new(Desired,Main,Zapret,OpenVpn){GetSettings=()=>Settings,Clock=Clock,PhysicalNetworkProvider=Physical,
            TunnelInspector=new Candidate12Tests.Tunnel(),OnOpenVpnRoutesLearned=(_,_,_)=>{Learned++;return Task.CompletedTask;}};
    }
    public Task Pass(ReconcileReason reason=ReconcileReason.OpenVpnStateChanged)=>Coordinator.ReconcileAsync(reason);
    public void Fail(string message)=>OpenVpn.Attempt=_=>Task.FromException(new IOException(message));
    public void Off(){Desired.Current=Desired.Current with{OpenVpnEnabled=false};Coordinator.CancelOpenVpnOperation();}
    public async Task Storm(int expected)
    { for(int i=0;i<12;i++)await Pass(); Assert.Equal(expected,OpenVpn.Starts); }
    public void Dispose(){Coordinator.Dispose();if(Directory.Exists(Root))Directory.Delete(Root,true);}
}

public sealed class OpenVpnRetryTests
{
    [Fact] public async Task OpenVpnManualTransientRetryGrantsOnlyOneNewAttempt()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Connection reset");await f.Pass();await f.Pass(ReconcileReason.ManualRetry);f.Clock.Advance(TimeSpan.FromDays(1));await f.Storm(2);Assert.Equal(OpenVpnRetryState.WaitingForRelevantChange,f.Coordinator.OpenVpnRetryController.State);}
    [Fact] public async Task OpenVpnLinkReturnOnSameNicResumesRetry()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Network is unreachable");await f.Pass();var physical=f.Physical.Current;f.Physical.Current=null;await f.Pass(ReconcileReason.PhysicalNetworkChanged);f.Physical.Current=physical;await f.Pass(ReconcileReason.PhysicalNetworkChanged);Assert.Equal(2,f.OpenVpn.Starts);}
    [Fact] public void AuthenticationTranscriptRetainsTypedFailure()
    {using var monitor=new OpenVpnGenerationMonitor(false);Exception? failure=null;monitor.Failed+=ex=>failure=ex;monitor.Observe("AUTH_FAILED: synthetic");Assert.IsType<OpenVpnFailureException>(failure);Assert.Equal(OpenVpnFailureClass.AuthenticationFatal,OpenVpnFailureClassifier.Classify(null,failure));}
    [Fact] public async Task OpenVpnStateChangedCannotBypassRetryDeadline()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Connection reset");await f.Pass();f.Clock.Advance(TimeSpan.FromSeconds(4));await f.Storm(1);}
    [Fact] public async Task OpenVpnRepeatedStateChangesDoNotIncreaseStartCount()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Connection refused");await f.Pass();await f.Storm(1);Assert.Equal(1,f.Coordinator.OpenVpnRetryController.AttemptCount);}
    [Fact] public async Task OpenVpnRetryDeadlineActuallyBlocksServiceStart()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Connection reset");await f.Pass();f.Clock.Advance(TimeSpan.FromSeconds(5));await f.Pass();Assert.Equal(2,f.OpenVpn.Starts);await f.Storm(2);}
    [Fact] public async Task OpenVpnChildExitDoesNotResetRetryKey()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Connection reset");await f.Pass();var key=f.Coordinator.OpenVpnRetryController.CurrentKey;f.OpenVpn.Exit();await f.Storm(1);Assert.Equal(key,f.Coordinator.OpenVpnRetryController.CurrentKey);}
    [Fact] public async Task OpenVpnSidecarStopDoesNotResetRetryKey()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Connection reset");await f.Pass();var key=f.Coordinator.OpenVpnRetryController.CurrentKey;f.OpenVpn.SidecarLost();await f.Storm(1);Assert.Equal(key,f.Coordinator.OpenVpnRetryController.CurrentKey);}
    [Fact] public async Task OpenVpnDeterministicFailureSuspendsAfterOneAttempt()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Algorithm (RC2-40-CBC : 0) unsupported");await f.Pass();Assert.Equal(OpenVpnRetryState.SuspendedFatal,f.Coordinator.OpenVpnRetryController.State);Assert.Null(f.Coordinator.OpenVpnRetryController.NextAttemptAt);await f.Storm(1);}
    [Fact] public async Task OpenVpnFatalFailureDoesNotCreateRestartStorm()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("legacy provider missing");await f.Pass();f.Clock.Advance(TimeSpan.FromDays(2));await f.Storm(1);Assert.False(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);}
    [Fact] public async Task OpenVpnTransientFailureUsesBoundedRetryBudget()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Connection reset");await f.Pass();foreach(int seconds in new[]{5,15,60}){f.Clock.Advance(TimeSpan.FromSeconds(seconds));await f.Pass();}Assert.Equal(4,f.OpenVpn.Starts);Assert.Equal(OpenVpnRetryState.WaitingForRelevantChange,f.Coordinator.OpenVpnRetryController.State);}
    [Fact] public async Task OpenVpnRetryExhaustionWaitsForRelevantChange()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Connection reset");for(int i=0;i<4;i++){await f.Pass();f.Clock.Advance(TimeSpan.FromHours(1));}await f.Storm(4);}
    [Fact] public async Task OpenVpnRelevantNetworkChangeResumesRetry()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Network is unreachable");await f.Pass();f.Physical.Current=f.Physical.Current! with{Address="192.168.1.101"};await f.Pass(ReconcileReason.PhysicalNetworkChanged);Assert.Equal(2,f.OpenVpn.Starts);}
    [Fact] public async Task OpenVpnUnrelatedEventDoesNotResumeRetry()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("legacy provider missing");await f.Pass();await f.Pass(ReconcileReason.UserChangedSettings);await f.Pass(ReconcileReason.PhysicalNetworkChanged);await f.Pass(ReconcileReason.UserToggledOpenVpn);Assert.Equal(1,f.OpenVpn.Starts);}
    [Fact] public async Task OpenVpnProfileRevisionResetsRetryBudget()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("legacy provider missing");await f.Pass();f.Profile.OpenVpnConfig+="verb 3\n";await f.Pass();Assert.Equal(2,f.OpenVpn.Starts);Assert.Equal(1,f.Coordinator.OpenVpnRetryController.AttemptCount);}
    [Fact] public async Task OpenVpnCredentialsAndModuleRevisionsResetRetryBudget()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("AUTH_FAILED");await f.Pass();f.Profile.Password="synthetic-new";await f.Pass();Assert.Equal(2,f.OpenVpn.Starts);f.OpenVpn.NativeModuleRevision++;await f.Pass();Assert.Equal(3,f.OpenVpn.Starts);}
    [Fact] public async Task OpenVpnManualRetryPerformsOneAttempt()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("legacy provider missing");await f.Pass();await f.Pass(ReconcileReason.ManualRetry);Assert.Equal(2,f.OpenVpn.Starts);await f.Storm(2);}
    [Fact] public async Task OpenVpnManualRetryDoesNotEnableInfiniteLoop()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("RC2-40-CBC");await f.Pass();var attempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.OpenVpn.Attempt=_=>{attempted.TrySetResult();throw new IOException("RC2-40-CBC");};f.Coordinator.TriggerRetry();await attempted.Task.WaitAsync(TimeSpan.FromSeconds(3));await f.Pass();f.Clock.Advance(TimeSpan.FromHours(1));await f.Storm(2);}
    [Fact] public async Task OpenVpnStalePlanCannotStartAfterRetryGateCloses()
    {using var f=new OpenVpnBehaviorFixture();f.Coordinator.Log=line=>{if(line.StartsWith("PLAN [StartOpenVpn")){var c=f.Coordinator.OpenVpnRetryController;var k=f.Coordinator.GetOpenVpnRetryKey(f.Desired.Current,f.Settings,f.Physical.Current);c.RecordAttemptStarted(k,f.Clock.UtcNow);c.RecordFailure(k,OpenVpnFailureClass.DeterministicLocalFatal,"fatal",f.Clock.UtcNow);}};await f.Pass();Assert.Equal(0,f.OpenVpn.Starts);}
}
