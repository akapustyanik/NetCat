using NetCat.Core;
using NetCat.UI;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class ApplicationUpdateHandoffTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-update-resume-" + Guid.NewGuid().ToString("N"));
    private UpdateJob Job => new(root, Path.Combine(root, "stage"), 123, 456, new string('A', 64), [], Version: "1.0.4");
    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    [Theory]
    [InlineData("1.0.4", false)]
    [InlineData("1.0.4", true)]
    [InlineData("1.0.3", true)] // The authenticated helper may restart the recovered old EXE.
    public void ProtectedResumeWorksForNewAndRolledBackExeExactlyOnce(string runningVersion, bool telegram)
    {
        var store = new ApplicationUpdateResumeStore(root);
        var identity = PortableUpdate.ResumeIdentity(Job);
        var token = store.Create(root, identity, "1.0.3", "1.0.4", telegram);
        var file = Path.Combine(root, "update-resume", token + ".dpapi");
        Assert.DoesNotContain("TelegramEnabled", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        var args = PortableUpdate.RestartArguments(Job with { ResumeToken = token });
        Assert.Equal(telegram, store.ConsumeArguments(args, root, runningVersion)!.TelegramEnabled);
        Assert.Null(store.ConsumeArguments(args, root, runningVersion));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "update-resume")));
    }

    [Theory]
    [InlineData("job")]
    [InlineData("version")]
    [InlineData("root")]
    [InlineData("expired")]
    [InlineData("tampered")]
    public void RejectedResumeNeverRestoresConnectionsAndRemovesItsTicket(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var store = new ApplicationUpdateResumeStore(root, () => now);
        var identity = PortableUpdate.ResumeIdentity(Job);
        var token = store.Create(root, identity, "1.0.3", "1.0.4", true);
        if (reason == "expired") now = now.AddMinutes(31);
        if (reason == "tampered") File.WriteAllText(Path.Combine(root, "update-resume", token + ".dpapi"), "synthetic-corruption");
        if (reason == "tampered") Assert.Throws<System.Security.Cryptography.CryptographicException>(() => store.Consume(token, identity, root, "1.0.4"));
        else Assert.Null(store.Consume(token, reason == "job" ? new string('B', 64) : identity,
            reason == "root" ? Path.Combine(root, "foreign") : root, reason == "version" ? "9.0.0" : "1.0.4"));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "update-resume")));
    }

    [Fact]
    public async Task ConcurrentRestartsCannotBothConsumeTheResumeTicket()
    {
        var store = new ApplicationUpdateResumeStore(root);
        var identity = PortableUpdate.ResumeIdentity(Job);
        for (int attempt = 0; attempt < 50; attempt++)
        {
            var token = store.Create(root, identity, "1.0.3", "1.0.4", true);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.Consume(token, identity, root, "1.0.4"))));
            Assert.Single(results, result => result != null);
        }
    }

    [Fact]
    public async Task HandoffPersistsIntentThenAuthenticatesHelperBeforeStoppingAnything()
    {
        var saved = new SettingsStore(root);
        await saved.SaveAsync(new AppSettings { RestoreConnectionsOnStartup = false });
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, OpenVpnEnabled = true, ZapretEnabled = true,
            SelectedVpnProfileId = Guid.NewGuid(), SelectedOpenVpnProfileId = Guid.NewGuid() };
        saved.SaveDesiredState(desired);
        var order = new List<string>();
        var resume = new ApplicationUpdateResumeStore(root);
        string? token = null;
        var flow = new ApplicationUpdateHandoff(resume, (_, _) => Task.FromResult(Job), (_, t) =>
        { token = t; order.Add("authenticated-helper"); return Task.CompletedTask; });
        Directory.CreateDirectory(Job.Stage);
        await flow.RunAsync("job", "1.0.3", true, () => { order.Add("persist"); return Task.CompletedTask; },
            () => { order.Add("shutdown"); flow.RecordSuccessfulShutdown(); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(new[] { "persist", "authenticated-helper", "shutdown" }, order);
        Assert.Equal(desired, saved.LoadDesiredState());
        Assert.False(saved.Load().RestoreConnectionsOnStartup);
        PortableUpdate.RequireSuccessfulShutdown(Job with { ResumeToken = token! }, null);
        Assert.True(resume.Consume(token!, PortableUpdate.ResumeIdentity(Job), root, "1.0.4")!.TelegramEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedHelperOrShutdownRemovesResumeState(bool failShutdown)
    {
        int stops = 0;
        var flow = new ApplicationUpdateHandoff(new ApplicationUpdateResumeStore(root), (_, _) => Task.FromResult(Job),
            (_, _) => failShutdown ? Task.CompletedTask : Task.FromException(new IOException("synthetic authentication failure")));
        await Assert.ThrowsAsync<IOException>(() => flow.RunAsync("job", "1.0.3", true, () => Task.CompletedTask,
            () => { stops++; throw new IOException("synthetic shutdown failure"); }, CancellationToken.None));
        Assert.Equal(failShutdown ? 1 : 0, stops);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "update-resume")));
    }

    [Fact]
    public async Task CancellationBeforeHandoffLeavesConnectionsRunning()
    {
        int launches = 0, stops = 0;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var flow = new ApplicationUpdateHandoff(new ApplicationUpdateResumeStore(root), (_, _) => Task.FromResult(Job),
            (_, _) => { launches++; return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.RunAsync("job", "1.0.3", true,
            () => Task.CompletedTask, () => { stops++; return Task.CompletedTask; }, cancel.Token));
        Assert.Equal(0, launches); Assert.Equal(0, stops);
        Assert.False(Directory.Exists(Path.Combine(root, "update-resume")));
    }

    [Fact]
    public void UnsuccessfulShutdownBlocksAnyUpdateMutationButLegacyJobsRemainCompatible()
    {
        PortableUpdate.RequireSuccessfulShutdown(Job, 1);
        var active = Job with { ResumeToken = new string('C', 64) };
        Assert.Throws<IOException>(() => PortableUpdate.RequireSuccessfulShutdown(active, 0));
        Assert.Throws<IOException>(() => PortableUpdate.RequireSuccessfulShutdown(active, null));
        Directory.CreateDirectory(active.Stage);
        PortableUpdate.RecordSuccessfulShutdown(active);
        PortableUpdate.RequireSuccessfulShutdown(active, 0);
        PortableUpdate.RequireSuccessfulShutdown(active, null);
        Assert.Throws<IOException>(() => PortableUpdate.RequireSuccessfulShutdown(active, 1));
        Assert.Throws<InvalidDataException>(() => PortableUpdate.RestartArguments(Job with { ResumeToken = "../invalid" }));
        Assert.NotEqual(PortableUpdate.ResumeIdentity(Job), PortableUpdate.ResumeIdentity(Job with { ArchiveHash = new string('D', 64) }));
        Assert.Equal(PortableUpdate.ResumeIdentity(Job), PortableUpdate.ResumeIdentity(Job with { Root = root.ToUpperInvariant() + Path.DirectorySeparatorChar }));
    }
}

public sealed class SelectedUpdateInstallerTests
{
    private static ModuleRelease Release(string key) => ModuleUpdateAvailabilityTests.Supported(key).Release!;

    [Fact]
    public async Task OneActionDownloadsAndVerifiesEverythingBeforeInstallingModules()
    {
        var order = new List<string>();
        var installer = new SelectedUpdateInstaller((_, _) => { order.Add("prepare-netcat"); return Task.FromResult("verified-job"); },
            (r, _) => { order.Add("prepare-" + r.Key); return Task.CompletedTask; },
            (r, _) => { order.Add("install-" + r.Key); return Task.CompletedTask; });
        var result = await installer.RunAsync([Release("zapret"), Release("netcat")], CancellationToken.None);
        Assert.Equal(new[] { "prepare-zapret", "prepare-netcat", "install-zapret" }, order);
        Assert.Equal(1, result.InstalledModules); Assert.Equal("verified-job", result.ApplicationJob); Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task FailedPackageVerificationCannotReachInstallOrShutdown()
    {
        int installs = 0;
        var installer = new SelectedUpdateInstaller((_, _) => throw new InvalidDataException("synthetic signature failure"),
            (_, _) => throw new InvalidDataException("synthetic hash failure"),
            (_, _) => { installs++; return Task.CompletedTask; });
        var result = await installer.RunAsync([Release("zapret"), Release("netcat")], CancellationToken.None);
        Assert.Equal(0, installs); Assert.Null(result.ApplicationJob); Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task CancelledDownloadCannotInstallAnyPreparedModule()
    {
        int installs = 0;
        using var cancel = new CancellationTokenSource();
        var installer = new SelectedUpdateInstaller((_, _) => { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); },
            (_, _) => Task.CompletedTask, (_, _) => { installs++; return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.RunAsync([Release("zapret"), Release("netcat")], cancel.Token));
        Assert.Equal(0, installs);
    }
}

public sealed partial class Candidate32UiAuditTests
{
    [Fact]
    public Task UpdateCenterOffersOneDownloadAndInstallAction() => Sta.Run(() =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
        var window = new MainWindow(vm);
        try
        {
            var button = Assert.IsType<System.Windows.Controls.Button>(window.FindName("UpdateSelectedButton"));
            Assert.Equal("Обновить выбранные", button.Content);
            Assert.Null(typeof(MainWindow).GetMethod("InstallDownloaded_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });
}
