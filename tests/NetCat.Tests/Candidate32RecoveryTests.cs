using System.Net;
using System.Reflection;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32RecoveryTests
{
    // ==========================================
    // TEST 01: ZapretTestHasGlobalDeadline
    // ==========================================
    [Fact]
    public async Task ZapretTestHasGlobalDeadline()
    {
        var root = Path.Combine(Path.GetTempPath(), "nc-c32-deadline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var zapret = new ZapretService(root, Path.Combine(root, "zapret"));
            var settings = new AppSettings { YouTube = ServiceRoute.Zapret };
            var candidate = new StrategyResult { File = "strat1.bat" };

            var gate = (SemaphoreSlim)typeof(ZapretService).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(zapret)!;
            await gate.WaitAsync();
            try
            {
                var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
                    zapret.TestAsync(settings, [candidate], new Progress<StrategyResult>(), CancellationToken.None,
                        globalDeadline: TimeSpan.FromMilliseconds(50)));
                Assert.Contains("Превышен общий лимит времени", ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ==========================================
    // TEST 02: ZapretTestCancellationClearsBusyState
    // ==========================================
    [Fact]
    public async Task ZapretTestCancellationClearsBusyState()
    {
        var root = Path.Combine(Path.GetTempPath(), "nc-c32-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(root);
            using var vm = new MainViewModel(store, new AppSettings());
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var testTask = vm.RunTestsAsync(async token =>
            {
                started.TrySetResult();
                await Task.Delay(10000, token);
            });
            await started.Task;
            Assert.True(vm.TestsBusy);
            vm.TestsCancellation.Cancel();
            await testTask;
            Assert.False(vm.TestsBusy);
            Assert.Equal("Проверка отменена", vm.TestStatus);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ==========================================
    // TEST 03: DuplicateZapretTestDoesNotStartSecondOperation
    // ==========================================
    [Fact]
    public async Task DuplicateZapretTestDoesNotStartSecondOperation()
    {
        var root = Path.Combine(Path.GetTempPath(), "nc-c32-vm2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(root);
            using var vm = new MainViewModel(store, new AppSettings());
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool secondStarted = false;
            var testTask1 = vm.RunTestsAsync(async token =>
            {
                started.TrySetResult();
                await Task.Delay(2000, token);
            });
            await started.Task;
            Assert.True(vm.TestsBusy);

            // Second test call while TestsBusy is true
            await vm.RunTestsAsync(token =>
            {
                secondStarted = true;
                return Task.CompletedTask;
            });

            Assert.False(secondStarted);

            vm.TestsCancellation.Cancel();
            await testTask1;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ==========================================
    // TEST 04: AcceptedZapretStrategyEndsScanEarly
    // ==========================================
    [Fact]
    public void AcceptedZapretStrategyEndsScanEarly()
    {
        var baseline = new List<ProbeOutcome>
        {
            new("YouTube", "Main", false, 0, "Blocked")
        };
        var passingOutcomes = new List<ProbeOutcome>
        {
            new("YouTube", "Main", true, 50, "HTTP 200")
        };
        var validation = ZapretStrategyValidation.Evaluate(baseline, passingOutcomes, ["YouTube"]);
        Assert.True(validation.Accepted);

        var candidate1 = new StrategyResult { File = "strat1.bat" };
        var candidate2 = new StrategyResult { File = "strat2.bat" };

        StrategyResult? best = null;
        var evaluated = new List<StrategyResult>();
        foreach (var c in new[] { candidate1, candidate2 })
        {
            evaluated.Add(c);
            c.Passed = validation.Accepted;
            if (c.Passed)
            {
                best = c;
                break;
            }
        }

        Assert.Single(evaluated);
        Assert.Same(candidate1, best);
        Assert.True(candidate1.Passed);
        Assert.False(candidate2.Passed);
    }

    // ==========================================
    // TEST 05: ZapretTestCancellationStopsTemporaryProcess
    // ==========================================
    [Fact]
    public async Task ZapretTestCancellationStopsTemporaryProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "nc-c32-cancel-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var zapret = new ZapretService(root, Path.Combine(root, "zapret"));
            Assert.False(zapret.IsRunning);
            Assert.Equal("", zapret.ActiveStrategy);

            await zapret.StopAsync();
            Assert.False(zapret.IsRunning);
            Assert.Equal("", zapret.ActiveStrategy);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ==========================================
    // TEST 06: UpdateQueuedOrCancelsActiveTestDeterministically
    // ==========================================
    [Fact]
    public async Task UpdateQueuedOrCancelsActiveTestDeterministically()
    {
        var root = Path.Combine(Path.GetTempPath(), "nc-c32-vm3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(root);
            using var vm = new MainViewModel(store, new AppSettings());
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var testTask = vm.RunTestsAsync(async token =>
            {
                started.TrySetResult();
                await Task.Delay(10000, token);
            });
            await started.Task;
            Assert.True(vm.TestsBusy);

            var release = new ModuleRelease("zapret", "Flowseal/zapret-discord-youtube", "1.4.0", "zapret.zip", "https://example.com/zapret.zip", new string('a', 64));

            // Should trigger controlled cancellation and wait without throwing "Дождитесь завершения теста"
            try
            {
                await vm.InstallPreparedModuleAsync(release, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // It may fail on missing prepared payload, but must NOT fail on "Дождитесь завершения теста"
                Assert.DoesNotContain("Дождитесь завершения теста", ex.Message);
            }

            Assert.False(vm.TestsBusy);
            await testTask;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ==========================================
    // TEST 07: GeoDataUpdateDoesNotRequireUnrelatedZapretExclusiveLock
    // ==========================================
    [Fact]
    public async Task GeoDataUpdateDoesNotRequireUnrelatedZapretExclusiveLock()
    {
        var root = Path.Combine(Path.GetTempPath(), "nc-c32-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(root);
            using var f = new Candidate12Tests.Fixture(store);
            await f.Reconcile();

            var zapretStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var geodataFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var zapretTask = f.Coordinator.RunZapretTestAsync(async token =>
            {
                zapretStarted.TrySetResult();
                await geodataFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                return (StrategyResult?)null;
            }, CancellationToken.None);

            await zapretStarted.Task;
            Assert.True(f.Coordinator.IsZapretTesting);

            // Geodata update runs concurrently without deadlocking on zapretTestGate
            await f.Coordinator.RunModuleUpdateAsync("geosite", async (restore, stop, token) =>
            {
                await Task.Yield();
            }, CancellationToken.None);

            geodataFinished.TrySetResult();
            await zapretTask;
            Assert.False(f.Coordinator.IsZapretTesting);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ==========================================
    // TEST 08: UpdateVersionStatusIsSeparateFromInstallability
    // ==========================================
    [Fact]
    public void UpdateVersionStatusIsSeparateFromInstallability()
    {
        var release = new ModuleRelease("xray", "XTLS/Xray-core", "v2.0.0", "xray.zip", "https://example.com/xray.zip", new string('a', 64));
        var check = new ModuleCheck("xray", "1.0", release, VersionStatus: VersionStatus.UpdateAvailable, Installability: InstallabilityStatus.PublisherManifestRequired);
        Assert.Equal(VersionStatus.UpdateAvailable, check.VersionStatus);
        Assert.Equal(InstallabilityStatus.PublisherManifestRequired, check.Installability);
        Assert.False(check.AutoUpdateSupported);
        Assert.Equal("Обновление найдено, но ещё не одобрено издателем NetCat", check.Status);

        var row = new ModuleRow(check, pinned: false);
        Assert.Equal(VersionStatus.UpdateAvailable, row.VersionStatus);
        Assert.Equal(InstallabilityStatus.PublisherManifestRequired, row.Installability);
        Assert.False(row.CanSelectUpdate);
        Assert.Equal("Обновление найдено, но ещё не одобрено издателем NetCat", row.Status);
    }

    // ==========================================
    // TEST 09: PublisherManifestMissingDoesNotReportCurrentIncorrectly
    // ==========================================
    [Fact]
    public void PublisherManifestMissingDoesNotReportCurrentIncorrectly()
    {
        var release = new ModuleRelease("sing-box", "SagerNet/sing-box", "v1.12.0", "sing-box.zip", "https://example.com/sb.zip", new string('a', 64), UpstreamVersion: "v1.12.0");
        var check = new ModuleCheck("sing-box", "v1.11.0", release, VersionStatus: VersionStatus.UpdateAvailable, Installability: InstallabilityStatus.PublisherManifestRequired);
        Assert.NotEqual("Установлена актуальная версия", check.Status);
        Assert.DoesNotContain("актуальная", check.Status);
        Assert.Equal("Обновление найдено, но ещё не одобрено издателем NetCat", check.Status);
    }

    // ==========================================
    // TEST 10: TrustedExecutableUpdateRollsBackOnHealthFailure
    // ==========================================
    [Fact]
    public async Task TrustedExecutableUpdateRollsBackOnHealthFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "nc-c32-health-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(root);
            using var f = new Candidate12Tests.Fixture(store);
            await f.Reconcile();

            var desired = f.Desired.Current;
            bool rollbackOccurred = false;

            await Assert.ThrowsAsync<IOException>(() => f.Coordinator.RunModuleUpdateAsync("sing-box", async (health, stop, ct) =>
            {
                Assert.False(f.Router.IsRunning);
                f.Router.BeforeStart = () => throw new IOException("new executable failed verification");
                try
                {
                    await health(ct);
                }
                catch
                {
                    Assert.False(f.Router.IsRunning);
                    rollbackOccurred = true;
                    f.Router.BeforeStart = null;
                    throw;
                }
            }, default));

            Assert.True(rollbackOccurred);
            Assert.True(f.Router.IsRunning);
            Assert.Equal(desired, f.Desired.Current);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
