using System.Reflection;
using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32RetryOwnershipTests
{
    [Theory]
    [InlineData("Could not find a part of the path")]
    [InlineData("Не удалось найти часть пути")]
    public async Task MissingOpenVpnModuleDirectorySuspendsWithoutDependingOnMessageLanguage(string message)
    {
        using var f = new OpenVpnBehaviorFixture();
        f.OpenVpn.Attempt = _ => throw new DirectoryNotFoundException(message);
        Assert.Equal(TimeSpan.Zero, await Pass(f));
        Assert.Equal(OpenVpnRetryState.SuspendedFatal, f.Coordinator.OpenVpnRetryController.State);
        Assert.Null(f.Coordinator.OpenVpnRetryController.NextAttemptAt);
        f.Clock.Advance(TimeSpan.FromDays(1));
        for (int i = 0; i < 8; i++) Assert.Equal(TimeSpan.Zero, await Pass(f));
        Assert.Equal(1, f.OpenVpn.Starts);
    }
    // Execute the production pass without starting its background loop so a
    // manual clock can inspect the exact next wake deadline deterministically.
    private static Task<TimeSpan> Pass(OpenVpnBehaviorFixture f, ReconcileReason reason = ReconcileReason.ExternalConditionResolved) =>
        (Task<TimeSpan>)typeof(RuntimeCoordinator).GetMethod("ExecuteReconcilePassAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Coordinator, [reason, CancellationToken.None])!;

    [Theory]
    [InlineData("missing module", OpenVpnRetryState.SuspendedFatal)]
    [InlineData("AUTH_FAILED", OpenVpnRetryState.SuspendedAuth)]
    [InlineData("Network is unreachable", OpenVpnRetryState.WaitingForRelevantChange)]
    public async Task SuspendedOpenVpnDoesNotSpinMainRouterBackoff(string error, OpenVpnRetryState expected)
    {
        using var f = new OpenVpnBehaviorFixture();
        f.Desired.Current = f.Desired.Current with { MainVpnEnabled = true };
        int mainAttempts = 0;
        f.Main.BeforeStart = () => { mainAttempts++; throw new IOException("router failure"); };
        f.Fail(error);
        Assert.Equal(TimeSpan.FromSeconds(5), await Pass(f));
        Assert.Equal(expected, f.Coordinator.OpenVpnRetryController.State);
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(15), await Pass(f));
        for (int i = 0; i < 8; i++) Assert.Equal(TimeSpan.FromSeconds(15), await Pass(f));
        Assert.Equal(2, mainAttempts);
        Assert.Equal(1, f.OpenVpn.Starts);
    }

    [Fact]
    public async Task ExhaustedOpenVpnRetryDoesNotLeaveImmediateWake()
    {
        using var f = new OpenVpnBehaviorFixture(); f.Fail("Connection reset");
        Assert.Equal(TimeSpan.FromSeconds(5), await Pass(f));
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(15), await Pass(f));
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(60), await Pass(f));
        f.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.Zero, await Pass(f));
        Assert.Equal(OpenVpnRetryState.WaitingForRelevantChange, f.Coordinator.OpenVpnRetryController.State);
        Assert.Equal(4, f.OpenVpn.Starts);
        Assert.False(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }

    [Fact]
    public async Task SuspendedOpenVpnBackgroundLoopBecomesIdleAndRelevantChangeRecovers()
    {
        using var f = new OpenVpnBehaviorFixture(); f.Fail("missing module");
        f.Coordinator.RequestReconcile(ReconcileReason.Startup);
        await WaitUntil(() => f.Coordinator.OpenVpnRetryController.State == OpenVpnRetryState.SuspendedFatal);
        var loopField = typeof(RuntimeCoordinator).GetField("loopTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await WaitUntil(() => loopField.GetValue(f.Coordinator) is null or Task { IsCompleted: true });
        Assert.Equal(1, f.OpenVpn.Starts);
        f.OpenVpn.Attempt = null; f.OpenVpn.NativeModuleRevision++;
        f.Coordinator.RequestReconcile(ReconcileReason.ExternalConditionResolved);
        await WaitUntil(() => f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
        Assert.Equal(2, f.OpenVpn.Starts);
        Assert.True(f.OpenVpn.IsRunning);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }
}
