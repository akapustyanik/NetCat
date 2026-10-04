using System.Text.Json;
using NetCat.Core;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32UpdaterRecoveryTests
{
    [Fact]
    public void CommittedUpdateSurvivesLockedRollbackBackup()
    {
        var root = RoutingTests.TestArtifacts(
            "c32-cleanup-" + Guid.NewGuid().ToString("N"));

        var installed = Path.Combine(root, "installed");
        var payload = Path.Combine(root, "payload");

        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(payload);

        File.WriteAllText(
            Path.Combine(installed, "NetCat.exe"),
            "old");

        File.WriteAllText(
            Path.Combine(payload, "NetCat.exe"),
            "new");

        var plan = new List<PackageComponent>
        {
            new(
                "netcat",
                "2.0.0",
                [new PackageFile("NetCat.exe", "")])
        };

        var versions = new Dictionary<string,string>
        {
            ["netcat"] = "1.0.0"
        };

        FileStream? blocker = null;

        try
        {
            PortableUpdate.ApplyFiles(
                installed,
                payload,
                plan,
                versions,
                afterMutation: (_, phase) =>
                {
                    if (phase != UpdatePhase.Committed)
                        return;

                    var journal =
                        DurableUpdate.Read(installed);

                    Assert.NotNull(journal);

                    var backupFile = Path.Combine(
                        installed,
                        "metadata",
                        "rollback",
                        journal!.UpdateId,
                        "NetCat.exe");

                    // On Windows the locked backup cannot be deleted.
                    blocker = new FileStream(
                        backupFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None);
                });

            Assert.Equal(
                "new",
                File.ReadAllText(
                    Path.Combine(installed, "NetCat.exe")));

            Assert.Equal(
                "2.0.0",
                versions["netcat"]);

            var pending =
                DurableUpdate.Read(installed);

            Assert.NotNull(pending);

            Assert.Equal(
                UpdatePhase.CleanupPending,
                pending!.Phase);
        }
        finally
        {
            blocker?.Dispose();
        }

        // Cleanup is retried after the external lock is released.
        DurableUpdate.Recover(installed);

        Assert.Null(DurableUpdate.Read(installed));

        Assert.Equal(
            "new",
            File.ReadAllText(
                Path.Combine(installed, "NetCat.exe")));
    }


    [Fact]
    public void JournalRootComparisonIgnoresWindowsPathCase()
    {
        var root = RoutingTests.TestArtifacts(
            "c32-rootcase-" + Guid.NewGuid().ToString("N"));

        var meta = Path.Combine(root, "metadata");
        Directory.CreateDirectory(meta);

        var journal = new UpdateJournal(
            1,
            root.ToUpperInvariant(),
            Guid.NewGuid().ToString("N"),
            null,
            UpdatePhase.Committed,
            new Dictionary<string,string>(),
            new Dictionary<string,string>(),
            new List<UpdateOperation>());

        File.WriteAllText(
            Path.Combine(
                root,
                DurableUpdate.JournalPath.Replace(
                    '/',
                    Path.DirectorySeparatorChar)),
            JsonSerializer.Serialize(
                journal,
                JsonSettings.Options));

        var loaded = DurableUpdate.Read(root);

        Assert.NotNull(loaded);

        Assert.Equal(
            UpdatePhase.Committed,
            loaded!.Phase);

        DurableUpdate.Recover(root);

        Assert.Null(DurableUpdate.Read(root));
    }


    [Fact]
    public void ApplyingFailureRestoresOriginalExecutable()
    {
        var root = RoutingTests.TestArtifacts(
            "c32-rollback-" + Guid.NewGuid().ToString("N"));

        var installed = Path.Combine(root, "installed");
        var payload = Path.Combine(root, "payload");

        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(payload);

        File.WriteAllText(
            Path.Combine(installed, "NetCat.exe"),
            "original");

        File.WriteAllText(
            Path.Combine(payload, "NetCat.exe"),
            "replacement");

        var plan = new List<PackageComponent>
        {
            new(
                "netcat",
                "2.0.0",
                [new PackageFile("NetCat.exe", "")])
        };

        var versions = new Dictionary<string,string>
        {
            ["netcat"] = "1.0.0"
        };

        Assert.Throws<InvalidOperationException>(
            () => PortableUpdate.ApplyFiles(
                installed,
                payload,
                plan,
                versions,
                afterMutation: (_, phase) =>
                {
                    if (phase == UpdatePhase.Applying)
                        throw new InvalidOperationException(
                            "Injected applying failure");
                }));

        Assert.Equal(
            "original",
            File.ReadAllText(
                Path.Combine(installed, "NetCat.exe")));

        Assert.Equal(
            "1.0.0",
            versions["netcat"]);

        Assert.Null(DurableUpdate.Read(installed));
    }


    [Fact]
    public void HelperRecoveryReleasesLockBeforeRestart()
    {
        var root = RoutingTests.FindRoot();

        var source = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "NetCat.Updater",
                "PortableUpdate.cs"));

        var recover = source.IndexOf(
            "DurableUpdate.Recover(job.Root)",
            StringComparison.Ordinal);

        var release = source.IndexOf(
            "updateLock.Dispose()",
            recover,
            StringComparison.Ordinal);

        var restart = source.IndexOf(
            "PublisherTrust.AcquireRestart(",
            release,
            StringComparison.Ordinal);

        Assert.True(recover >= 0);
        Assert.True(release > recover);
        Assert.True(restart > release);

        Assert.Contains(
            "catch(InvalidOperationException)",
            source);

        Assert.Contains(
            "ex.NativeErrorCode is 6 or 87 or 1168",
            source);

        Assert.Contains(
            "TryWriteUpdateStatusAsync",
            source);
    }
}