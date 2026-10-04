using NetCat.Core;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed class OpenVpnCancellationTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task OpenVpnPrimaryAndGlobalCancelInterruptPendingServiceStart(bool global)
    {
        using var f=new OpenVpnBehaviorFixture();using var vm=new MainViewModel(new SettingsStore(f.Root),f.Settings);
        var c=vm.RuntimeCoordinator;
        foreach(var pair in new (string Name,object Value)[]{(nameof(c.OpenVpnRuntime),f.OpenVpn),(nameof(c.RouterRuntime),f.Main),(nameof(c.ZapretRuntime),f.Zapret),(nameof(c.PhysicalNetworkProvider),f.Physical),(nameof(c.TunnelInspector),new Candidate12Tests.Tunnel())})
            typeof(RuntimeCoordinator).GetProperty(pair.Name)!.SetValue(c,pair.Value);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);CancellationToken captured=default;
        f.OpenVpn.Attempt=async ct=>{captured=ct;entered.SetResult();await Task.Delay(Timeout.Infinite,ct);};
        vm.UpdateDesiredState(d=>d with{OpenVpnEnabled=true,SelectedOpenVpnProfileId=f.Profile.Id});
        var pass=c.ReconcileAsync(ReconcileReason.OpenVpnStateChanged);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(vm.CanCancelOperation);Assert.Equal("Отменить подключение",vm.OpenVpnButton);
        if(global)vm.CancelCurrentOperation();else vm.UserRequestedOpenVpnChange(false);
        Assert.False(vm.DesiredState.OpenVpnEnabled);await pass.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(captured.IsCancellationRequested);Assert.False(f.OpenVpn.IsRunning);Assert.Equal(0,f.Main.Starts);
        Assert.Null(c.OpenVpnRetryController.NextAttemptAt);
    }
    [Fact] public async Task OpenVpnDesiredOffCancelsCurrentAttempt()
    {
        using var f=new OpenVpnBehaviorFixture();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured=default;
        f.OpenVpn.Attempt=async ct=>{captured=ct;entered.SetResult();await Task.Delay(Timeout.Infinite,ct);};
        var pass=f.Pass();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));f.Off();await pass.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(captured.IsCancellationRequested);Assert.False(f.OpenVpn.IsRunning);Assert.Equal(0,f.Learned);Assert.Null(f.Coordinator.OpenVpnRetryController.NextAttemptAt);
    }
    [Fact] public async Task OpenVpnDesiredOffCancelsRetryBudget()
    {using var f=new OpenVpnBehaviorFixture();f.Fail("Connection reset");await f.Pass();f.Off();f.Clock.Advance(TimeSpan.FromHours(2));await f.Storm(1);Assert.Equal(0,f.Coordinator.OpenVpnRetryController.AttemptCount);Assert.Equal(OpenVpnRetryState.Idle,f.Coordinator.OpenVpnRetryController.State);}
    [Fact] public async Task OpenVpnDesiredOffPreventsQueuedStart()
    {using var f=new OpenVpnBehaviorFixture();f.Coordinator.Log=line=>{if(line.StartsWith("PLAN [StartOpenVpn"))f.Off();};await f.Pass();Assert.Equal(0,f.OpenVpn.Starts);}
    [Fact] public async Task OpenVpnDesiredOffStopsOwnedChild()
    {using var f=new OpenVpnBehaviorFixture();await f.Pass();f.Off();await f.Pass();Assert.False(f.OpenVpn.IsRunning);Assert.Equal(1,f.OpenVpn.Stops);}
    [Fact] public async Task OpenVpnDesiredOffPreservesMainVpnTunRevisionAndZapret()
    {
        using var f=new OpenVpnBehaviorFixture();var main=new Profile{Protocol="vless"};f.Settings.Profiles.Add(main);f.Settings.MainProfileId=main.Id;f.Settings.Tun=true;
        f.Desired.Current=f.Desired.Current with{MainVpnEnabled=true,TunEnabled=true,ZapretEnabled=true,SelectedVpnProfileId=main.Id};
        await f.Pass();long revision=f.Main.SessionRevision;int starts=f.Main.Starts;int zapretStarts=f.Zapret.Starts;
        f.Off();await f.Pass();Assert.True(f.Main.IsRunning);Assert.True(f.Main.TunActive);Assert.Equal(revision,f.Main.SessionRevision);Assert.Equal(starts,f.Main.Starts);Assert.Equal(0,f.Main.Stops);Assert.Equal(zapretStarts,f.Zapret.Starts);
        // Xray identity requires the native main-router fixture/VM; no invented PID assertion here.
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task OpenVpnStaleGenerationCannotPublishAfterCancel(bool turnBackOn)
    {
        using var f=new OpenVpnBehaviorFixture();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OpenVpn.Attempt=async _=>{entered.SetResult();await release.Task;};
        var pass=f.Pass();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));f.Off();
        if(turnBackOn)f.Desired.Current=f.Desired.Current with{OpenVpnEnabled=true};
        release.SetResult();await pass.WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(0,f.Learned);Assert.False(f.OpenVpn.IsRunning);Assert.True(f.OpenVpn.Stops>0);
        f.OpenVpn.Attempt=null;await f.Pass();Assert.Equal(turnBackOn,f.OpenVpn.IsRunning);Assert.Equal(turnBackOn?1:0,f.Learned);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task OpenVpnGlobalCancelUsesSameCancellationPath(bool global)
    {
        using var f=new OpenVpnBehaviorFixture();using var vm=new MainViewModel(new SettingsStore(f.Root),f.Settings);
        var c=vm.RuntimeCoordinator;
        foreach(var pair in new (string Name,object Value)[]{(nameof(c.OpenVpnRuntime),f.OpenVpn),(nameof(c.RouterRuntime),f.Main),(nameof(c.ZapretRuntime),f.Zapret),(nameof(c.PhysicalNetworkProvider),f.Physical),(nameof(c.TunnelInspector),new Candidate12Tests.Tunnel()),(nameof(c.Clock),f.Clock)})
            typeof(RuntimeCoordinator).GetProperty(pair.Name)!.SetValue(c,pair.Value);
        vm.UpdateDesiredState(d=>d with{OpenVpnEnabled=true,SelectedOpenVpnProfileId=f.Profile.Id});
        f.Fail("Connection reset");await c.ReconcileAsync(ReconcileReason.OpenVpnStateChanged);
        Assert.True(vm.CanCancelOperation);Assert.Equal("Отменить подключение",vm.OpenVpnButton);
        if(global)vm.CancelCurrentOperation();else vm.UserRequestedOpenVpnChange(false);
        Assert.False(vm.DesiredState.OpenVpnEnabled);await c.ReconcileAsync(ReconcileReason.OpenVpnStateChanged);
        Assert.Equal(OpenVpnRetryState.Idle,c.OpenVpnRetryController.State);Assert.Null(c.OpenVpnRetryController.NextAttemptAt);Assert.Equal(1,f.OpenVpn.Starts);
    }
    [Theory]
    [InlineData(OpenVpnRetryState.Idle,"Отключить OpenVPN",false)]
    [InlineData(OpenVpnRetryState.Starting,"Отменить подключение",true)]
    [InlineData(OpenVpnRetryState.Connected,"Отключить OpenVPN",false)]
    [InlineData(OpenVpnRetryState.RetryScheduled,"Отменить подключение",true)]
    [InlineData(OpenVpnRetryState.SuspendedFatal,"Отключить OpenVPN",false)]
    public void OpenVpnButtonAndGlobalCancelReflectDesiredAndRetryState(OpenVpnRetryState state,string label,bool cancel)
    {
        using var f=new OpenVpnBehaviorFixture();using var vm=new MainViewModel(new SettingsStore(f.Root),f.Settings);
        Assert.Equal("Подключить OpenVPN",vm.OpenVpnButton);vm.UpdateDesiredState(d=>d with{OpenVpnEnabled=true});
        var c=vm.RuntimeCoordinator.OpenVpnRetryController;var k=OpenVpnRetryKey.Empty;
        if(state!=OpenVpnRetryState.Idle)c.RecordAttemptStarted(k,f.Clock.UtcNow);
        if(state==OpenVpnRetryState.Connected)c.RecordSuccess(k);
        if(state is OpenVpnRetryState.SuspendedFatal or OpenVpnRetryState.RetryScheduled)c.RecordFailure(k,state==OpenVpnRetryState.SuspendedFatal?OpenVpnFailureClass.DeterministicLocalFatal:OpenVpnFailureClass.TransientNetwork,"test",f.Clock.UtcNow);
        Assert.Equal(label,vm.OpenVpnButton);Assert.Equal(cancel,vm.CanCancelOperation);
    }
}
