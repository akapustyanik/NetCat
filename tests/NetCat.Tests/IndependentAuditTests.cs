using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Updater;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class IndependentAuditTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-IndependentAudit-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class Tasks : IStartupTasks
    {
        public StartupTask? Value;
        public int Writes;
        public StartupTask? Read() => Value;
        public void Write(StartupTask value) { Value = value; Writes++; }
        public void Delete() { Value = null; Writes++; }
        public void RemoveLegacyEntries() { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OtherCopyCannotReconcileAwayAutostartOwner(bool desired)
    {
        var tasks = new Tasks();
        var a = new AutostartService(tasks, @"C:\NetCat-A\NetCat.exe", "S-1-5-21-test");
        var b = new AutostartService(tasks, @"C:\NetCat-B\NetCat.exe", "S-1-5-21-test");
        await a.SetAsync(true, _ => Task.CompletedTask);
        var registered = tasks.Value; var writes = tasks.Writes;
        await b.ReconcileAsync(desired, _ => Task.CompletedTask);
        Assert.Equal(registered, tasks.Value); Assert.Equal(writes, tasks.Writes);
        await b.SetAsync(true, _ => Task.CompletedTask); // explicit transfer is allowed
        Assert.True(b.Enabled); Assert.False(a.Enabled);
    }

    [Fact]
    public void ShowingAutostartSettingsOnlyReadsScheduler()
    {
        var source = File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "src/NetCat.UI/MainWindow.xaml.cs"));
        var start = source.IndexOf("private async Task RefreshAutostartAsync()", StringComparison.Ordinal);
        var end = source.IndexOf("private async void Autostart_Click", start, StringComparison.Ordinal);
        Assert.DoesNotContain("ReconcileAsync", source[start..end]);
        Assert.DoesNotContain("SetAsync", source[start..end]);
        Assert.DoesNotContain("UpdateSettingsAsync", source[start..end]);
    }

    [Fact]
    public void CorruptDesiredIntentRecoversProtectedIntentFromBackup()
    {
        var store = new SettingsStore(root);
        var intent = new DesiredRuntimeState { MainVpnEnabled = true, OpenVpnEnabled = true, SelectedVpnProfileId = Guid.NewGuid() };
        store.SaveDesiredState(intent);
        File.WriteAllText(Path.Combine(root, "desired-state.json"), "{truncated");
        Assert.Equal(intent, new SettingsStore(root).LoadDesiredState());
        Assert.Equal(intent, new SettingsStore(root).LoadDesiredState());
    }

    [Theory]
    [InlineData("{truncated")]
    [InlineData("null")]
    [InlineData("{}")]
    public void UnrecoverableDesiredIntentDoesNotSilentlyDisableVpn(string content)
    {
        var store = new SettingsStore(root);
        File.WriteAllText(Path.Combine(root, "desired-state.json"), content);
        Assert.Throws<InvalidDataException>(() => store.LoadDesiredState());
        Assert.Equal(content, File.ReadAllText(Path.Combine(root, "desired-state.json")));
    }

    [Theory]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.254")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("240.0.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:2::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("3fff::1")]
    [InlineData("::ffff:198.18.0.1")]
    public void SubscriptionRejectsSpecialPurposeAddress(string address) => Assert.False(SubscriptionClient.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public void IdenticalQueryDuplicatesAreAcceptedButConflictingValuesAreRejected()
    {
        var profile = ProfileImporter.ParseLink("trojan://fixture@vpn.example:443?sni=example.org&SNI=example.org");
        Assert.Equal("trojan", profile.Protocol);
        Assert.Throws<InvalidDataException>(() => ProfileImporter.ParseLink("trojan://fixture@vpn.example:443?sni=example.org&SNI=other.example"));
    }

    [Fact]
    public async Task DelayedExitFromPreviousProcessCannotSignalReplacement()
    {
        using var host = new ProcessHost();
        var field = typeof(ProcessHost).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!;
        host.Start(ProcessHost.PowerShellPath, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep 30"]);
        var previous = (Process)field.GetValue(host)!;
        var callbackField = typeof(Process).GetField("_onExited", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var delayed = (EventHandler)callbackField.GetValue(previous)!;
        await host.StopAsync();
        host.Start(ProcessHost.PowerShellPath, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep 30"]);
        int notifications = 0;
        host.Exited += (_, _) => notifications++;
        try
        {
            delayed(previous, EventArgs.Empty); // deterministic delivery of the old real OS event handler
            Assert.Equal(0, notifications);
            Assert.True(host.Running);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public void DesiredIntentRecoveryReportsAndPreservesCorruptOriginal()
    {
        var store = new SettingsStore(root); var log = new List<string>(); store.Diagnostic += log.Add;
        store.SaveDesiredState(new() { MainVpnEnabled = true });
        File.WriteAllText(Path.Combine(root, "desired-state.json"), "invalid");
        Assert.True(store.LoadDesiredState().MainVpnEnabled);
        Assert.True(store.DesiredRecoveredFromBackup);
        Assert.Contains(log, x => x.Contains("recovered-backup"));
        Assert.Equal("invalid", File.ReadAllText(Directory.GetFiles(root, "desired-state.json.corrupt-*").Single()));
    }

    [Fact]
    public void FailedDesiredSavePreservesBackupAndDoesNotOverwriteBrokenPrimary()
    {
        var store = new SettingsStore(root); store.SaveDesiredState(new() { MainVpnEnabled = true });
        var backup = File.ReadAllBytes(Path.Combine(root, "desired-state.json.bak"));
        File.WriteAllText(Path.Combine(root, "desired-state.json"), "{");
        Assert.ThrowsAny<Exception>(() => store.SaveDesiredState(new()));
        Assert.Equal(backup, File.ReadAllBytes(Path.Combine(root, "desired-state.json.bak")));
        Assert.Equal("{", File.ReadAllText(Path.Combine(root, "desired-state.json")));
        Assert.Empty(Directory.GetFiles(root, "*.new"));
    }

    [Fact]
    public void PresentationWriteFailureIsObservableAndPreservesPreviousState()
    {
        var store = new SettingsStore(root); var log = new List<string>(); store.Diagnostic += log.Add;
        store.SavePresentationState(WindowPresentationState.VisibleMaximized);
        using (var locked = new FileStream(Path.Combine(root, "ui-state.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            store.SavePresentationState(WindowPresentationState.HiddenToTray);
        Assert.Contains(log, x => x.Contains("operation=write result=failed"));
        Assert.Equal(WindowPresentationState.VisibleMaximized, store.LoadPresentationState());
        Assert.Empty(Directory.GetFiles(root, "*.new"));
    }

    [Fact]
    public void InvalidPresentationLoadReportsWithoutBreakingStartup()
    {
        var store = new SettingsStore(root); var log = new List<string>(); store.Diagnostic += log.Add;
        File.WriteAllText(Path.Combine(root, "ui-state.json"), "999");
        Assert.Equal(WindowPresentationState.VisibleNormal, store.LoadPresentationState());
        Assert.Contains(log, x => x.Contains("operation=read result=invalid"));
    }

    [Fact]
    public async Task PackageStreamIsBoundedWithoutContentLength()
    {
        using var input = new MemoryStream(new byte[4097]); using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageLimits.CopyAsync(input, output, 4096, CancellationToken.None));
        Assert.True(output.Length <= 4096);
    }

    [Fact]
    public async Task OversizeDownloadHeaderIsRejectedBeforeCreatingFile()
    {
        Directory.CreateDirectory(root);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.ContentLength = PackageLimits.DownloadBytes + 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageLimits.DownloadAsync(response, Path.Combine(root, "archive"), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZipBudgetRejectsArchiveBeforeExtraction(bool sizeBudget)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "input.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            for (int i = 0; i < 3; i++) { using var data = zip.CreateEntry(i + ".bin").Open(); data.Write(new byte[1024]); }
        var destination = Path.Combine(root, "output");
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageLimits.ExtractAsync(path, destination, CancellationToken.None,
            byteLimit: sizeBudget ? 2048 : 4096, entryLimit: sizeBudget ? 10 : 2));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ValidBoundedZipPreservesBytes()
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "input.zip"); var bytes = RandomNumberGenerator.GetBytes(1031);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var data = zip.CreateEntry("folder/data.bin").Open(); data.Write(bytes); }
        var destination = Path.Combine(root, "output");
        await PackageLimits.ExtractAsync(path, destination, CancellationToken.None, 2048, 10);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(destination, "folder/data.bin")));
    }

    [Fact]
    public async Task HugeDeclaredZipEntryIsRejectedWithoutAllocatingItsSize()
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "input.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var data = zip.CreateEntry("huge.bin").Open(); data.WriteByte(1); }
        var bytes = File.ReadAllBytes(path);
        var central = bytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        Assert.True(central >= 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24, 4), checked((uint)PackageLimits.ExpandedBytes + 1));
        File.WriteAllBytes(path, bytes);
        Assert.True(bytes.Length < 1024);
        var destination = Path.Combine(root, "output");
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageLimits.ExtractAsync(path, destination, CancellationToken.None));
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicSubscriptionAddressStillAllowed(string address) => Assert.True(SubscriptionClient.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public void ReleaseVersionCannotDisagreeWithExecutableOrSigningTool()
    {
        ReleaseTrust.ValidateBuildVersion(ReleaseTrust.CurrentVersion, ReleaseTrust.CurrentVersion);
        Assert.Throws<InvalidDataException>(() => ReleaseTrust.ValidateBuildVersion(ReleaseTrust.CurrentVersion, "0.0.1-beta.1"));
        Assert.Throws<InvalidDataException>(() => ReleaseTrust.ValidateBuildVersion("0.0.1-beta.1", "0.0.1-beta.1"));
        Assert.Throws<InvalidDataException>(() => ReleaseTrust.ValidateBuildVersion(ReleaseTrust.CurrentVersion, null));
    }

    [Fact]
    public void RecoveryJobBindsExactPendingJournalAndCannotBecomeUpdate()
    {
        var payload = Path.Combine(root, "payload"); var installed = Path.Combine(root, "installed");
        Directory.CreateDirectory(payload); Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(payload, "NetCat.exe"), "new"); File.WriteAllText(Path.Combine(installed, "NetCat.exe"), "old");
        Assert.Throws<SimulatedUpdateCrash>(() => DurableUpdate.Apply(installed, payload,
            [new("netcat", "9.0.0", [new("NetCat.exe", "")])], new() { ["netcat"] = "1.0.0" }, (_, _) => throw new SimulatedUpdateCrash()));
        var journal = Path.Combine(installed, DurableUpdate.JournalPath);
        var job = new UpdateJob(installed, "stage", 1, 1, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(journal))), [], RecoveryOnly: true);
        Assert.True(File.Exists(UpdateChannel.ValidateRecoveryJob(job)));
        Assert.Throws<InvalidDataException>(() => UpdateChannel.ValidateRecoveryJob(job with { RecoveryOnly = false }));
        Assert.Throws<InvalidDataException>(() => UpdateChannel.ValidateRecoveryJob(job with { Version = "9.0.0" }));
        File.AppendAllText(journal, " ");
        Assert.Throws<InvalidDataException>(() => UpdateChannel.ValidateRecoveryJob(job));
        DurableUpdate.Recover(installed);
        Assert.Equal("old", File.ReadAllText(Path.Combine(installed, "NetCat.exe")));
    }

    [Fact]
    public async Task CanceledZapretModuleUpdateWaitingForReconcileReleasesZapretTestGate()
    {
        using var fixture = new Candidate12Tests.Fixture(new SettingsStore(root));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Router.BeforeStart = async () => { started.TrySetResult(); await release.Task; };
        var reconcile = fixture.Reconcile(ReconcileReason.Startup);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Coordinator.RunModuleUpdateAsync("zapret", (_, _, _) => Task.CompletedTask, cts.Token));
        fixture.Router.BeforeStart = null;
        release.TrySetResult();
        await reconcile.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ok", await fixture.Coordinator.RunZapretTestAsync(_ => Task.FromResult("ok"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PortableUpdateExtractVerifiedRejectsSpoofedEntrySizeAndSymlink(bool symlink)
    {
        Directory.CreateDirectory(root); var zip = Path.Combine(root, "package.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("NetCat.exe");
            if (symlink) entry.ExternalAttributes = unchecked((int)(0xa000u << 16));
            using var data = entry.Open(); data.Write(new byte[32]);
        }
        if (!symlink)
        {
            var bytes = File.ReadAllBytes(zip);
            var central = bytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24, 4), 64u);
            File.WriteAllBytes(zip, bytes);
        }
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdate.ExtractVerifiedAsync(root, hash, CancellationToken.None));
    }
}

