using System.IO.Pipes;
using NetCat.Core;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32Dev5StateIpcTests
{
    private sealed class TempFolder : IDisposable
    {
        public string Location { get; } =
            Path.Combine(
                Path.GetTempPath(),
                "NetCat-C32-V5-" +
                Guid.NewGuid().ToString("N"));

        public TempFolder()
        {
            Directory.CreateDirectory(Location);
        }

        public void Dispose()
        {
            if (Directory.Exists(Location))
                Directory.Delete(Location, true);
        }
    }

    [Fact]
    public async Task FirstSaveBackupRecoversDamagedPrimary()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Location);

        await store.SaveAsync(new AppSettings
        {
            BaseColor = "#112233"
        });

        var primary = Path.Combine(
            folder.Location,
            "settings.dpapi");

        var backup = primary + ".bak";

        Assert.True(File.Exists(primary));
        Assert.True(File.Exists(backup));

        File.WriteAllBytes(
            primary,
            new byte[] { 1, 2, 3, 4, 5 });

        var restored = store.Load();

        Assert.Equal("#112233", restored.BaseColor);
        Assert.True(store.RecoveredFromBackup);

        Assert.Equal(
            "#112233",
            new SettingsStore(folder.Location)
                .Load().BaseColor);

        Assert.Single(
            Directory.GetFiles(
                folder.Location,
                "settings.dpapi.corrupt-*"));
    }

    [Fact]
    public async Task PreviousVersionRestoredAfterNewPrimaryCorrupt()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Location);

        await store.SaveAsync(new AppSettings
        {
            BaseColor = "#112233"
        });

        await store.SaveAsync(new AppSettings
        {
            BaseColor = "#445566"
        });

        var primary = Path.Combine(
            folder.Location,
            "settings.dpapi");

        Assert.Equal(
            "#445566",
            store.Load().BaseColor);

        File.WriteAllBytes(
            primary,
            new byte[] { 7, 8, 9 });

        var restored = store.Load();

        Assert.Equal("#112233", restored.BaseColor);
        Assert.True(store.RecoveredFromBackup);
    }

    [Fact]
    public async Task BothEncryptedCopiesCorruptDoNotReset()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Location);

        await store.SaveAsync(new AppSettings());

        var primary = Path.Combine(
            folder.Location,
            "settings.dpapi");

        var backup = primary + ".bak";

        var damagedPrimary =
            new byte[] { 1, 3, 5, 7 };

        var damagedBackup =
            new byte[] { 2, 4, 6, 8 };

        File.WriteAllBytes(
            primary,
            damagedPrimary);

        File.WriteAllBytes(
            backup,
            damagedBackup);

        Assert.Throws<InvalidDataException>(
            () => store.Load());

        Assert.Equal(
            damagedPrimary,
            File.ReadAllBytes(primary));

        Assert.Equal(
            damagedBackup,
            File.ReadAllBytes(backup));
    }

    [Fact]
    public async Task SaveRefusesToOverwriteCorruptPrimary()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Location);

        await store.SaveAsync(new AppSettings
        {
            BaseColor = "#112233"
        });

        var primary = Path.Combine(
            folder.Location,
            "settings.dpapi");

        var backup = primary + ".bak";

        var savedBackup =
            File.ReadAllBytes(backup);

        var damagedPrimary =
            new byte[] { 10, 20, 30 };

        File.WriteAllBytes(
            primary,
            damagedPrimary);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(
                new AppSettings
                {
                    BaseColor = "#445566"
                }));

        Assert.Equal(
            damagedPrimary,
            File.ReadAllBytes(primary));

        Assert.Equal(
            savedBackup,
            File.ReadAllBytes(backup));

        Assert.Equal(
            "#112233",
            store.Load().BaseColor);
    }

    [Fact]
    public async Task InterruptedTemporaryFileIsIgnored()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Location);

        await store.SaveAsync(new AppSettings
        {
            BaseColor = "#112233"
        });

        File.WriteAllBytes(
            Path.Combine(
                folder.Location,
                "settings.dpapi.new"),
            new byte[] { 0, 0, 0 });

        Assert.Equal(
            "#112233",
            store.Load().BaseColor);

        Assert.False(store.RecoveredFromBackup);
    }

    [Fact]
    public async Task BackupRestoresMissingPrimary()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Location);

        await store.SaveAsync(new AppSettings
        {
            BaseColor = "#112233"
        });

        var primary = Path.Combine(
            folder.Location,
            "settings.dpapi");

        File.Delete(primary);

        Assert.Equal(
            "#112233",
            store.Load().BaseColor);

        Assert.True(store.RecoveredFromBackup);
        Assert.True(File.Exists(primary));
    }

    [Fact]
    public async Task ShowCommandFailureDoesNotKillPipe()
    {
        var mutexName =
            "Local\\NetCat-C32-V5-" +
            Guid.NewGuid().ToString("N");

        var pipeName =
            "NetCat-C32-V5-" +
            Guid.NewGuid().ToString("N");

        using var owner =
            new NetworkOwner(mutexName);

        Assert.True(owner.Acquired);

        var calls = 0;
        Exception? reported = null;

        owner.ListenerError =
            error => reported = error;

        owner.Listen(
            () =>
            {
                var call =
                    Interlocked.Increment(ref calls);

                if (call == 1)
                {
                    return Task.FromException(
                        new InvalidOperationException(
                            "Injected UI handler failure"));
                }

                return Task.CompletedTask;
            },
            pipeName);

        Assert.False(
            await NetworkOwner.ShowExistingAsync(
                pipeName));

        Assert.True(
            await NetworkOwner.ShowExistingAsync(
                pipeName));

        Assert.Equal(2, calls);

        Assert.IsType<InvalidOperationException>(
            reported);
    }

    [Fact]
    public async Task DisconnectedClientDoesNotKillPipe()
    {
        var mutexName =
            "Local\\NetCat-C32-V5-" +
            Guid.NewGuid().ToString("N");

        var pipeName =
            "NetCat-C32-V5-" +
            Guid.NewGuid().ToString("N");

        using var owner =
            new NetworkOwner(mutexName);

        Assert.True(owner.Acquired);

        var calls = 0;

        owner.Listen(
            () =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            pipeName);

        using (var deadline =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(2)))
        {
            using var client =
                new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous |
                    PipeOptions.CurrentUserOnly);

            await client.ConnectAsync(
                deadline.Token);

            // Disconnect without sending the command byte.
        }

        Assert.True(
            await NetworkOwner.ShowExistingAsync(
                pipeName));

        Assert.Equal(1, calls);
    }
}