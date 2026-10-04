using NetCat.Core;
using NetCat.Updater;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32FinalAuditTests
{
    private sealed class ProbeTrust(string probe) : IExecutableTrustPolicy
    {
        public IDisposable AcquireExecutable(string path)
        {
            Assert.Equal(probe, path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        public IDisposable AcquirePackage(string key, string folder) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ThrowingProcessExitSubscriberDoesNotCrashOwnerOrHideOtherSubscribers()
    {
        var probe = Path.Combine(RoutingTests.FindRoot(), "tests", "NetCat.LifetimeProbe", "bin",
            new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net8.0-windows", "NetCat.LifetimeProbe.exe");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await ProcessHost.RunAsync(probe, ["observer-exit"], deadline.Token, trustPolicy: new ProbeTrust(probe));
        Assert.Equal(0, result.Code);
        Assert.Contains("exit-observer-complete", result.Output);
    }

    [Fact]
    public async Task ThrowingProcessOutputSubscriberDoesNotStopDrainOrHideOtherSubscribers()
    {
        using var host = new ProcessHost();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = 0;
        host.ObserverError = _ => { Interlocked.Increment(ref errors); throw new IOException("Diagnostic observer failed"); };
        host.Line += _ => throw new InvalidOperationException("Observer failed");
        host.Line += line => { if (line == "drain-complete") completed.TrySetResult(); };
        host.Start(ProcessHost.PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command", "1..100 | ForEach-Object { Write-Output $_ }; Write-Output 'drain-complete'"]);
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { try { await host.StopAsync(); } catch { } }
        Assert.Contains("drain-complete", host.LastOutput);
        Assert.Equal(101, errors);
    }

    [Fact]
    public void FailureAfterDurableCommitPreservesCommittedVersionState()
    {
        var folder = Path.Combine(Path.GetTempPath(), "NetCat-final-update-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(folder, "root");
        var payload = Path.Combine(folder, "payload");
        const string relative = "modules/xray/xray.exe";
        Directory.CreateDirectory(Path.Combine(root, "modules/xray"));
        Directory.CreateDirectory(Path.Combine(payload, "modules/xray"));
        try
        {
            File.WriteAllText(Path.Combine(root, relative), "old");
            File.WriteAllText(Path.Combine(payload, relative), "new");
            var versions = new Dictionary<string, string> { ["xray"] = "1.0" };
            Assert.Throws<IOException>(() => DurableUpdate.Apply(root, payload,
                [new PackageComponent("xray", "2.0", [new PackageFile(relative, "")])], versions,
                (_, phase) => { if (phase == UpdatePhase.Committed) throw new IOException("Post-commit diagnostic failure"); }));
            Assert.Equal("new", File.ReadAllText(Path.Combine(root, relative)));
            Assert.Equal("2.0", versions["xray"]);
            Assert.Contains("2.0", File.ReadAllText(Path.Combine(root, "metadata/installed.json")));
            DurableUpdate.Recover(root);
            Assert.Equal("new", File.ReadAllText(Path.Combine(root, relative)));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task MissingPrimaryWithBackupCannotBeSilentlyOverwrittenBySave()
    {
        var root = Path.Combine(Path.GetTempPath(), "NetCat-final-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(root);
            await store.SaveAsync(new AppSettings { BaseColor = "#112233" });
            var primary = Path.Combine(root, "settings.dpapi");
            var backup = File.ReadAllBytes(primary + ".bak");
            File.Delete(primary);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new AppSettings { BaseColor = "#445566" }));
            Assert.False(File.Exists(primary));
            Assert.Equal(backup, File.ReadAllBytes(primary + ".bak"));
            Assert.Equal("#112233", store.Load().BaseColor);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TimedOutShowDoesNotStartDuplicateInFlightCallback()
    {
        var name = Guid.NewGuid().ToString("N");
        using var owner = new NetworkOwner("Local\\NetCat-audit-" + name);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        owner.ListenerError = error => { if (error is OperationCanceledException) timeout.TrySetResult(); };
        owner.Listen(() => { Interlocked.Increment(ref calls); return pending.Task; }, "NetCat-audit-" + name);
        try
        {
            Assert.False(await NetworkOwner.ShowExistingAsync("NetCat-audit-" + name));
            await timeout.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(await NetworkOwner.ShowExistingAsync("NetCat-audit-" + name));
            Assert.Equal(1, Volatile.Read(ref calls));
            pending.TrySetResult();
        }
        finally { pending.TrySetResult(); }
    }

    [Fact]
    public void NetworkOwnerDisposeIsIdempotentAndReleasesMutex()
    {
        var name = "Local\\NetCat-audit-" + Guid.NewGuid().ToString("N");
        var owner = new NetworkOwner(name);
        Assert.True(owner.Acquired);
        owner.Dispose();
        owner.Dispose();
        using var replacement = new NetworkOwner(name);
        Assert.True(replacement.Acquired);
    }
}
