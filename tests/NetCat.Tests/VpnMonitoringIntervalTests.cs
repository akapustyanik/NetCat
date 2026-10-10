using System.Reflection;
using System.Threading.Channels;
using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class VpnMonitoringIntervalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-Interval-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class ControlledClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public Channel<(TimeSpan Duration, TaskCompletionSource Complete)> Waits { get; } = Channel.CreateUnbounded<(TimeSpan, TaskCompletionSource)>();
        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Waits.Writer.TryWrite((duration, complete));
            return complete.Task.WaitAsync(ct);
        }
        public async Task<(TimeSpan Duration, TaskCompletionSource Complete)> NextAsync() =>
            await Waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static VpnHealthMonitorService Monitor(Candidate12Tests.Fixture f, IClock clock) =>
        new(f.Repo, f.Router, f.Coordinator, () => f.Desired.Current, change => f.Desired.Current = change(f.Desired.Current)) { Clock = clock };
    private static async Task StopAsync(VpnHealthMonitorService monitor)
    {
        monitor.Stop();
        if (typeof(VpnHealthMonitorService).GetField("loopTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(monitor) is Task loop)
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DefaultIntervalIsFifteenSeconds() => Assert.Equal(15, new AppSettings().TestIntervalSeconds);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(15)]
    public async Task ProtectedSettingsRoundTripPreservesSelectedInterval(int seconds)
    {
        var store = new SettingsStore(root);
        var settings = new AppSettings { TestIntervalSeconds = seconds };
        SettingsValidation.Validate(settings);
        await store.SaveAsync(settings);
        var restarted = new SettingsStore(root).Load();
        SettingsMigration.Apply(restarted);
        Assert.Equal(seconds, restarted.TestIntervalSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidationRejectsNonPositiveIntervals(int seconds) =>
        Assert.Throws<FormatException>(() => SettingsValidation.Validate(new() { TestIntervalSeconds = seconds }));

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(15, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(15, true)]
    public async Task MonitorUsesSelectedIntervalWithOrWithoutAutoSwitch(int seconds, bool autoSwitch)
    {
        using var f = new Candidate12Tests.Fixture(new SettingsStore(root));
        await f.Reconcile();
        await f.Repo.UpdateSettingsAsync(s => { s.TestIntervalSeconds = seconds; s.AutoSwitch = autoSwitch; s.TestTimeoutSeconds = 9; return s; });
        var clock = new ControlledClock();
        using var monitor = Monitor(f, clock);
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.MeasureLatencyFunc = (_, _, _, timeout) =>
        {
            Assert.Equal(autoSwitch ? 4 : 9, timeout);
            probed.TrySetResult(); return Task.FromResult(new DelayResult(true, 10));
        };
        monitor.Start();
        try
        {
            var first = await clock.NextAsync(); Assert.Equal(TimeSpan.FromSeconds(seconds), first.Duration);
            first.Complete.SetResult(); await probed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var next = await clock.NextAsync(); Assert.Equal(TimeSpan.FromSeconds(seconds), next.Duration);
        }
        finally { await StopAsync(monitor); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(15)]
    public async Task LongProbeDoesNotAccumulateOrOverlapAndNextDelayStartsAfterCompletion(int seconds)
    {
        using var f = new Candidate12Tests.Fixture(new SettingsStore(root)); await f.Reconcile();
        await f.Repo.UpdateSettingsAsync(s => { s.TestIntervalSeconds = seconds; s.AutoSwitch = false; return s; });
        var clock = new ControlledClock(); using var monitor = Monitor(f, clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<DelayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        monitor.MeasureLatencyFunc = (_, _, ct, _) =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult(); return finish.Task.WaitAsync(ct);
        };
        monitor.Start();
        try
        {
            (await clock.NextAsync()).Complete.SetResult(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.UtcNow += TimeSpan.FromSeconds(seconds * 5);
            await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => monitor.ProbeOnceAsync()));
            Assert.Equal(1, calls); Assert.False(clock.Waits.Reader.TryPeek(out _));
            finish.SetResult(new(true, 10));
            var next = await clock.NextAsync(); Assert.Equal(TimeSpan.FromSeconds(seconds), next.Duration);
            Assert.Equal(1, calls); next.Complete.SetResult();
            await clock.NextAsync(); Assert.Equal(2, calls);
        }
        finally { finish.TrySetResult(new(true, 10)); await StopAsync(monitor); }
    }

    [Fact]
    public async Task VeryLargeIntervalDoesNotProbeEarlyOrOverflowTheDelayTimer()
    {
        using var f = new Candidate12Tests.Fixture(new SettingsStore(root)); await f.Reconcile();
        await f.Repo.UpdateSettingsAsync(s => { s.TestIntervalSeconds = int.MaxValue; return s; });
        var clock = new ControlledClock(); using var monitor = Monitor(f, clock); int calls = 0;
        monitor.MeasureLatencyFunc = (_, _, _, _) => { calls++; return Task.FromResult(new DelayResult(true, 10)); };
        monitor.Start();
        try
        {
            var first = await clock.NextAsync();
            Assert.InRange(first.Duration, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(uint.MaxValue - 1));
            first.Complete.SetResult();
            var remaining = await clock.NextAsync();
            Assert.InRange(remaining.Duration, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(uint.MaxValue - 1));
            Assert.Equal(0, calls); // Completing one bounded delay must not shorten the whole interval.
        }
        finally { await StopAsync(monitor); }
    }

    [Fact]
    public async Task CommittedIntervalChangeIsUsedForNextCompletedProbeCycle()
    {
        using var f = new Candidate12Tests.Fixture(new SettingsStore(root)); await f.Reconcile();
        await f.Repo.UpdateSettingsAsync(s => { s.TestIntervalSeconds = 15; return s; });
        var clock = new ControlledClock(); using var monitor = Monitor(f, clock);
        monitor.MeasureLatencyFunc = (_, _, _, _) => Task.FromResult(new DelayResult(true, 10));
        monitor.Start();
        try
        {
            var first = await clock.NextAsync(); Assert.Equal(TimeSpan.FromSeconds(15), first.Duration);
            await f.Repo.UpdateSettingsAsync(s => { s.TestIntervalSeconds = 1; return s; });
            first.Complete.SetResult(); Assert.Equal(TimeSpan.FromSeconds(1), (await clock.NextAsync()).Duration);
        }
        finally { await StopAsync(monitor); }
    }

    [Fact]
    public async Task MonitoringResumesAfterStopAndRestart()
    {
        using var f = new Candidate12Tests.Fixture(new SettingsStore(root)); await f.Reconcile();
        var clock = new ControlledClock(); using var monitor = Monitor(f, clock);
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.MeasureLatencyFunc = (_, _, _, _) => { probed.TrySetResult(); return Task.FromResult(new DelayResult(true, 10)); };
        monitor.Start(); await clock.NextAsync(); await StopAsync(monitor);
        Assert.False(monitor.IsRunning);
        monitor.Start();
        try
        {
            (await clock.NextAsync()).Complete.SetResult();
            await probed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(monitor.IsRunning);
        }
        finally { await StopAsync(monitor); }
    }

    [Fact]
    public async Task RestartWaitsForPreviousSlowProbeBeforeStartingAnotherCycle()
    {
        using var f = new Candidate12Tests.Fixture(new SettingsStore(root)); await f.Reconcile();
        var clock = new ControlledClock(); using var monitor = Monitor(f, clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<DelayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        monitor.MeasureLatencyFunc = (_, _, _, _) => { Interlocked.Increment(ref calls); entered.TrySetResult(); return finish.Task; };
        monitor.Start();
        try
        {
            (await clock.NextAsync()).Complete.SetResult(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            monitor.Stop(); monitor.Start(); monitor.Start();
            await Task.Delay(100);
            Assert.Equal(1, calls); Assert.False(clock.Waits.Reader.TryPeek(out _));
            finish.TrySetResult(new(true, 10));
            (await clock.NextAsync()).Complete.SetResult();
            await clock.NextAsync(); Assert.Equal(2, calls);
        }
        finally { finish.TrySetResult(new(true, 10)); await StopAsync(monitor); }
    }

    [Fact]
    public void DisposedMonitorRejectsRestart()
    {
        using var f = new Candidate12Tests.Fixture(new SettingsStore(root));
        var monitor = Monitor(f, new ControlledClock()); monitor.Dispose();
        Assert.Throws<ObjectDisposedException>(monitor.Start);
    }
}
