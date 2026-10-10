using NetCat.Core;
using NetCat.UI;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class AutomaticApplicationUpdateTests
{
    private static ModuleRelease Release => new("netcat", "akapustyanik/NetCat", "1.0.3", "NetCat-v1.0.3-win-x64.zip", "https://github.com/akapustyanik/NetCat/releases/download/v1.0.3/NetCat-v1.0.3-win-x64.zip", new string('a', 64));

    [Fact]
    public async Task OptInPersistsAndIsOffForExistingSettings()
    {
        var store = new SettingsStore(RoutingTests.TestArtifacts("auto-update-" + Guid.NewGuid().ToString("N")));
        Assert.False(store.Load().AutoUpdateNetCat);
        await store.SaveAsync(new AppSettings { AutoUpdateNetCat = true });
        Assert.True(new SettingsStore(store.Root).Load().AutoUpdateNetCat);
    }

    [Fact]
    public async Task DisabledOrBusySessionNeverPreparesUpdate()
    {
        int calls = 0;
        var updater = new AutomaticApplicationUpdate(() => false, () => Release, (_, _) => { calls++; return Task.FromResult("job"); }, _ => throw new Exception(), _ => throw new Exception());
        await updater.TickAsync(CancellationToken.None); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NoAvailableUpdateDoesNotRepeatCertificateAndExecutableTrustChecks()
    {
        int trustChecks = 0;
        var updater = new AutomaticApplicationUpdate(() => { trustChecks++; return true; }, () => null,
            (_, _) => throw new Exception("No update to prepare."), _ => throw new Exception(), _ => throw new Exception());
        for (int tick = 0; tick < 1000; tick++) await updater.TickAsync(CancellationToken.None);
        Assert.Equal(0, trustChecks);
    }

    [Fact]
    public async Task SlowPreparationCannotOverlapAndNewConnectionDefersRestart()
    {
        bool idle = true; int calls = 0, launches = 0; var finish = new TaskCompletionSource<string>();
        var updater = new AutomaticApplicationUpdate(() => idle, () => Release, (_, _) => { calls++; return finish.Task; }, _ => launches++, _ => throw new Exception());
        var pending = updater.TickAsync(CancellationToken.None);
        for (int i = 0; i < 20; i++) await updater.TickAsync(CancellationToken.None);
        idle = false; finish.SetResult("verified-job"); await pending;
        Assert.Equal(1, calls); Assert.Equal(0, launches);
        idle = true; await updater.TickAsync(CancellationToken.None); await updater.TickAsync(CancellationToken.None);
        Assert.Equal(1, calls); Assert.Equal(1, launches);
    }

    [Fact]
    public async Task FailedPreparationBacksOffAndCancellationNeverQueuesRestart()
    {
        var now = DateTime.UtcNow; int calls = 0, errors = 0;
        var updater = new AutomaticApplicationUpdate(() => true, () => Release, (_, _) => { calls++; throw new IOException("Synthetic failure"); }, _ => throw new Exception(), _ => errors++, () => now);
        await updater.TickAsync(CancellationToken.None); now = now.AddMinutes(4); await updater.TickAsync(CancellationToken.None);
        Assert.Equal(1, calls); now = now.AddMinutes(1); await updater.TickAsync(CancellationToken.None); Assert.Equal(2, calls); Assert.Equal(2, errors);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); now = now.AddMinutes(5);
        await updater.TickAsync(cancel.Token); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ChangedReleaseCannotReuseAJobPreparedForAnotherPackage()
    {
        bool idle = true; int calls = 0;
        var candidate = Release;
        var finish = new TaskCompletionSource<string>();
        var jobs = new List<string>();
        var updater = new AutomaticApplicationUpdate(() => idle, () => candidate,
            (_, _) => { calls++; return calls == 1 ? finish.Task : Task.FromResult("new-job"); }, jobs.Add, _ => throw new Exception());
        var pending = updater.TickAsync(CancellationToken.None);
        idle = false; finish.SetResult("old-job"); await pending;
        candidate = Release with { Version = "1.0.4", Sha256 = new string('b', 64) };
        idle = true; await updater.TickAsync(CancellationToken.None);
        Assert.Equal(2, calls); Assert.Equal(new[] { "new-job" }, jobs);
    }

}

public sealed partial class Candidate32UiAuditTests
{
    [Fact]
    public Task AutoInstallStillChecksForUpdatesWhenModuleChecksAreDisabled() => Sta.Run(async () =>
    {
        int checks = 0, maintenance = 0;
        using var scheduler = new ApplicationMaintenanceScheduler(() => new AppSettings { AutoUpdateNetCat = true, CheckModuleUpdates = false },
            () => true, () => true, _ => Task.CompletedTask, _ => { checks++; return Task.CompletedTask; }, ex => throw ex,
            maintain: _ => { maintenance++; return Task.CompletedTask; });
        await scheduler.TickAsync(); Assert.Equal(1, checks); Assert.Equal(1, maintenance);
    });
}
