using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class AutoSwitchConfirmationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-AutoSwitch-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private Candidate12Tests.Fixture Fixture() => new(new SettingsStore(root));
    private static VpnHealthMonitorService Monitor(Candidate12Tests.Fixture f) => new(f.Repo, f.Router, f.Coordinator,
        () => f.Desired.Current, change => f.Desired.Current = change(f.Desired.Current))
    {
        MeasureLatencyFunc = (_, _, _, _) => Task.FromResult(new DelayResult(false, -1))
    };

    [Fact]
    public async Task FirstSuccessCannotSwitchWhileSecondConfirmationIsPending()
    {
        using var f = Fixture(); await f.Reconcile();
        using var monitor = Monitor(f);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<DelayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        monitor.TestProfileFunc = (_, _, ct) =>
        {
            if (++calls == 1) return Task.FromResult(new DelayResult(true, 20));
            secondStarted.TrySetResult(); return second.Task.WaitAsync(ct);
        };
        await monitor.ProbeOnceAsync();
        var attempt = monitor.ProbeOnceAsync();
        try
        {
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(f.A.Id, f.Desired.Current.SelectedVpnProfileId);
            Assert.Equal(f.A.Id, f.Repo.CurrentSettings.MainProfileId);
            Assert.Equal(1, f.Router.Starts);
            second.SetResult(new(true, 21)); await attempt;
            Assert.Equal(2, calls);
            Assert.Equal(f.B.Id, f.Desired.Current.SelectedVpnProfileId);
        }
        finally { second.TrySetResult(new(false, -1)); await attempt; }
    }

    [Fact]
    public async Task FailureBetweenSuccessesCannotBeCountedAcrossAttempts()
    {
        using var f = Fixture(); await f.Reconcile(); using var monitor = Monitor(f);
        var results = new Queue<bool>([true, false, true, false]);
        monitor.TestProfileFunc = (_, _, _) => Task.FromResult(new DelayResult(results.Dequeue(), 20));
        await monitor.ProbeOnceAsync(); await monitor.ProbeOnceAsync(); await monitor.ProbeOnceAsync();
        Assert.Empty(results);
        Assert.Equal(f.A.Id, f.Desired.Current.SelectedVpnProfileId);
        Assert.Equal(1, f.Router.Starts);
    }

    [Fact]
    public async Task SuccessesFromDifferentCandidatesCannotBeCombined()
    {
        using var f = Fixture(); await f.Reconcile();
        var c = JsonSettings.Clone(f.B); c.Id = Guid.NewGuid(); c.Name = "C";
        await f.Repo.UpdateSettingsAsync(s => { s.Profiles.Add(c); return s; });
        using var monitor = Monitor(f); var calls = new List<Guid>();
        monitor.TestProfileFunc = (p, _, _) => { calls.Add(p.Id); return Task.FromResult(new DelayResult(calls.Count(x => x == p.Id) == 1, 20)); };
        await monitor.ProbeOnceAsync(); await monitor.ProbeOnceAsync();
        Assert.Equal(new[] { f.B.Id, f.B.Id, c.Id, c.Id }, calls);
        Assert.Equal(f.A.Id, f.Desired.Current.SelectedVpnProfileId);
    }

    [Fact]
    public async Task FailedFirstProbeSkipsConfirmationAndTriesNextCandidateTwice()
    {
        using var f = Fixture(); await f.Reconcile();
        var c = JsonSettings.Clone(f.B); c.Id = Guid.NewGuid(); c.Name = "C";
        await f.Repo.UpdateSettingsAsync(s => { s.Profiles.Add(c); return s; });
        using var monitor = Monitor(f); var calls = new List<Guid>();
        monitor.TestProfileFunc = (p, _, _) => { calls.Add(p.Id); return Task.FromResult(new DelayResult(p.Id == c.Id, 20)); };
        await monitor.ProbeOnceAsync(); await monitor.ProbeOnceAsync();
        Assert.Equal(new[] { f.B.Id, c.Id, c.Id }, calls);
        Assert.Equal(c.Id, f.Desired.Current.SelectedVpnProfileId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OffOrCancellationDuringConfirmationCannotSwitch(bool cancel)
    {
        using var f = Fixture(); await f.Reconcile(); using var monitor = Monitor(f);
        using var stop = new CancellationTokenSource(); int calls = 0;
        monitor.TestProfileFunc = (_, _, ct) =>
        {
            if (++calls == 2)
            {
                if (cancel) stop.Cancel();
                else f.Desired.Current = f.Desired.Current with { MainVpnEnabled = false };
            }
            return Task.FromResult(new DelayResult(true, 20));
        };
        await monitor.ProbeOnceAsync();
        try { await monitor.ProbeOnceAsync(stop.Token); }
        catch (OperationCanceledException) { }
        Assert.Equal(2, calls);
        Assert.Equal(f.A.Id, f.Desired.Current.SelectedVpnProfileId);
        Assert.Equal(f.A.Id, f.Repo.CurrentSettings.MainProfileId);
    }

    [Fact]
    public async Task FailureThresholdAndTwoMinuteCooldownRemainInForce()
    {
        using var f = Fixture(); await f.Reconcile();
        await f.Repo.UpdateSettingsAsync(s => { s.FailureThreshold = 3; return s; });
        using var monitor = Monitor(f); int calls = 0;
        monitor.TestProfileFunc = (_, _, _) => { calls++; return Task.FromResult(new DelayResult(true, 20)); };
        await monitor.ProbeOnceAsync(); await monitor.ProbeOnceAsync(); Assert.Equal(0, calls);
        await monitor.ProbeOnceAsync(); await f.Reconcile(ReconcileReason.VpnFailover);
        Assert.Equal(2, calls); Assert.Equal(f.B.Id, f.Router.ActiveProfileId);
        for (int i = 0; i < 4; i++) await monitor.ProbeOnceAsync();
        Assert.Equal(2, calls); Assert.Equal(f.B.Id, f.Desired.Current.SelectedVpnProfileId);
        var policy = new FailoverPolicy(); var now = DateTimeOffset.UtcNow; policy.Switched(now);
        for (int i = 0; i < 3; i++) policy.Record(f.B.Id, new(false, -1), now.AddSeconds(110 + i));
        Assert.False(policy.ShouldRecover(f.B.Id, 3, now.AddSeconds(119), TimeSpan.FromMinutes(2)));
        Assert.True(policy.ShouldRecover(f.B.Id, 3, now.AddSeconds(120), TimeSpan.FromMinutes(2)));
    }
}
