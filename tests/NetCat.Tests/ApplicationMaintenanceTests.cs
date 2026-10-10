using NetCat.Core;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed partial class Candidate32UiAuditTests
{
    [Fact]
    public Task HiddenStartupRunsTestsAndModuleChecksWithoutCreatingAWindow() => Sta.Run(async () =>
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int tests = 0, updates = 0;
        using var scheduler = new ApplicationMaintenanceScheduler(
            () => new AppSettings { AutoTest = true, CheckModuleUpdates = true }, () => true, () => true,
            _ => { tests++; return Task.CompletedTask; },
            _ => { updates++; called.TrySetResult(); return Task.CompletedTask; }, ex => throw ex);
        scheduler.Start();
        await called.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(1, tests); Assert.Equal(1, updates);
        await scheduler.StopAsync();
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(15)]
    public Task SlowCheckDoesNotAccumulateTicksAndSchedulesFromCompletion(int seconds) => Sta.Run(async () =>
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var scheduler = new ApplicationMaintenanceScheduler(
            () => new AppSettings { AutoTest = true, TestIntervalSeconds = seconds, CheckModuleUpdates = false },
            () => true, () => true, async ct => { calls++; await finish.Task.WaitAsync(ct); },
            _ => throw new InvalidOperationException(), ex => throw ex, () => now);
        var running = scheduler.TickAsync();
        try
        {
            now = now.AddSeconds(seconds * 20);
            for (int i = 0; i < 20; i++) await scheduler.TickAsync();
            Assert.Equal(1, calls); Assert.False(running.IsCompleted);
            finish.SetResult(); await running;
            Assert.Equal(now.AddSeconds(seconds), scheduler.NextProfileTest);
            await scheduler.TickAsync(); Assert.Equal(1, calls);
            now = now.AddSeconds(seconds); await scheduler.TickAsync(); Assert.Equal(2, calls);
        }
        finally { finish.TrySetResult(); await running; }
    });

    [Fact]
    public Task ModuleChecksRunAtStartupAndEverySixHoursWhileTestsAreBusy() => Sta.Run(async () =>
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0); int updates = 0;
        using var scheduler = new ApplicationMaintenanceScheduler(
            () => new AppSettings { AutoTest = true, CheckModuleUpdates = true }, () => true, () => false,
            _ => throw new InvalidOperationException(), _ => { updates++; return Task.CompletedTask; }, ex => throw ex, () => now);
        await scheduler.TickAsync(); Assert.Equal(1, updates);
        now = now.AddHours(6).AddTicks(-1); await scheduler.TickAsync(); Assert.Equal(1, updates);
        now = now.AddTicks(1); await scheduler.TickAsync(); Assert.Equal(2, updates);
    });

    [Fact]
    public Task ExitCancelsAndDrainsPendingWorkAndDoesNotRestartIt() => Sta.Run(async () =>
    {
        int calls = 0; bool cancelled = false;
        using var scheduler = new ApplicationMaintenanceScheduler(
            () => new AppSettings { AutoTest = true, CheckModuleUpdates = false }, () => true, () => true,
            async ct => { calls++; try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } finally { cancelled = ct.IsCancellationRequested; } },
            _ => throw new InvalidOperationException(), ex => throw ex);
        var running = scheduler.TickAsync();
        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(running.IsCompleted); Assert.True(cancelled);
        await scheduler.TickAsync(); Assert.Equal(1, calls);
        Assert.Throws<ObjectDisposedException>(() => scheduler.Start());
    });

    [Fact]
    public Task FailedCheckDoesNotDisableLaterTicks() => Sta.Run(async () =>
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0); int calls = 0, errors = 0;
        using var scheduler = new ApplicationMaintenanceScheduler(
            () => new AppSettings { AutoTest = true, TestIntervalSeconds = 1, CheckModuleUpdates = false }, () => true, () => true,
            _ => ++calls == 1 ? Task.FromException(new IOException("Synthetic probe failure")) : Task.CompletedTask,
            _ => throw new InvalidOperationException(), _ => errors++, () => now);
        await scheduler.TickAsync(); Assert.Equal(1, errors);
        now = now.AddSeconds(1); await scheduler.TickAsync(); Assert.Equal(2, calls); Assert.Equal(1, errors);
    });
}
