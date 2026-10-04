using System.Diagnostics;
using System.Text.Json;
using NetCat.Core;

namespace NetCat.Updater;

public static partial class UpdateChannel
{
    public static string ValidateRecoveryJob(UpdateJob job)
    {
        var journalPath = PortableUpdate.SafePath(job.Root, DurableUpdate.JournalPath);
        if (!job.RecoveryOnly || job.Version.Length != 0 || job.Asset.Length != 0 || job.Pinned.Length != 0 ||
            !Convert.ToHexString(Hash(journalPath)).Equals(job.ArchiveHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Задание восстановления устарело или изменено.");
        var journal = DurableUpdate.Read(job.Root);
        if (journal?.Phase != UpdatePhase.Applying || !journal.Operations.Any(o => o.Target == "NetCat.exe" && o.Existed))
            throw new InvalidDataException("Нет незавершённой замены EXE для восстановления.");
        return PortableUpdate.SafePath(job.Root, "metadata/rollback/" + journal.UpdateId + "/NetCat.exe");
    }

    public static async Task<string> PrepareRecoveryAsync(string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!root.Equals(Path.GetDirectoryName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Восстановление относится к другой копии NetCat.");
        var hash = Convert.ToHexString(Hash(PortableUpdate.SafePath(root, DurableUpdate.JournalPath)));
        var job = new UpdateJob(root, "", Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            hash, [], RecoveryOnly: true);
        var backup = ValidateRecoveryJob(job);
        using var backupLease = PublisherTrust.AcquireRestart(Environment.ProcessPath!, backup);
        PrivateFiles.ProtectDirectory(Path.GetDirectoryName(SecureRoot)!, true);
        PrivateFiles.ProtectDirectory(SecureRoot, true);
        var stage = Path.Combine(SecureRoot, Guid.NewGuid().ToString("N"));
        PrivateFiles.ProtectDirectory(stage, true);
        try
        {
            using var active = new FileStream(Path.Combine(stage, "active.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            File.Copy(Environment.ProcessPath!, Path.Combine(stage, "NetCat.Update.exe"));
            job = job with { Stage = stage };
            var path = Path.Combine(stage, "job.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(job, JsonSettings.Options));
            return path;
        }
        catch { UpdateCleanup.TryRemoveStage(stage); throw; }
    }

    private static async Task ApplyRecoveryAsync(UpdateJob job)
    {
        using var active = new FileStream(Path.Combine(job.Stage, "active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // The authenticated sender must exit before any executable replacement.
        try
        {
            using var parent = Process.GetProcessById(job.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks == job.ParentStart)
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        }
        catch (ArgumentException) { }
        using (var updateLock = new FileStream(PortableUpdate.SafePath(job.Root, "metadata/update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var backup = ValidateRecoveryJob(job);
            using var backupLease = PublisherTrust.AcquireRestart(Environment.ProcessPath!, backup);
            DurableUpdate.Recover(job.Root);
        }
        var executable = PortableUpdate.SafePath(job.Root, "NetCat.exe");
        using var restartLease = PublisherTrust.AcquireRestart(Environment.ProcessPath!, executable);
        using var restarted = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })
            ?? throw new IOException("Не удалось открыть восстановленный NetCat.");
    }
}
