using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace AmazTool;

internal class UpgradeApp
{
    private static readonly HashSet<string> _preservedTopLevelDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "PrivateHub",
        "binConfigs",
        "guiBackups",
        "guiConfigs",
        "guiFonts",
        "guiLogs",
        "guiTemps",
        "userdata",
    };

    private static readonly HashSet<string> _preservedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "domains.lst",
        "guiNConfig.json",
    };

    public static void Upgrade(string fileName)
    {
        Utils.Log($"Upgrade started. Target path: {Utils.StartupPath()}");
        Console.WriteLine($"{Resx.Resource.StartUnzipping}\n{fileName}");

        Utils.Waiting(5);

        if (!File.Exists(fileName))
        {
            Utils.Log($"Upgrade package not found: {fileName}");
            Console.WriteLine(Resx.Resource.UpgradeFileNotFound);
            return;
        }

        Console.WriteLine(Resx.Resource.TryTerminateProcess);
        try
        {
            var appExePath = Utils.GetAppExePath();
            TerminateRunningAppInstances(appExePath);
        }
        catch (Exception ex)
        {
            Utils.Log($"Failed to terminate process: {ex}");
            Console.WriteLine(Resx.Resource.FailedTerminateProcess + ex.StackTrace);
        }

        Console.WriteLine(Resx.Resource.StartUnzipping);
        StringBuilder sb = new();
        var currentAppPath = Utils.GetAppExePath();
        var appBackupPath = $"{currentAppPath}.tmp";
        var currentUpdaterPath = Utils.GetExePath();
        var targetUpdaterDirectory = Path.GetFullPath(Utils.GetPath("updater"));
        var runningUpdaterDirectory = Path.GetFullPath(Path.GetDirectoryName(currentUpdaterPath) ?? Utils.StartupPath());
        var skipTargetUpdaterReplacement = string.Equals(runningUpdaterDirectory.TrimEnd('\\'), targetUpdaterDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var cleanupUpdaterDirectory = !skipTargetUpdaterReplacement
            && Path.GetFileName(runningUpdaterDirectory).StartsWith("updater-", StringComparison.OrdinalIgnoreCase);
        var appEntryUpdated = false;
        var hadFatalError = false;
        try
        {
            DeleteFileIfExists(appBackupPath);

            using var archive = ZipFile.OpenRead(fileName);
            var archiveRootFolder = GetArchiveRootFolder(archive);
            foreach (var entry in archive.Entries)
            {
                try
                {
                    if (entry.Length == 0)
                    {
                        continue;
                    }

                    Utils.Log($"Extracting entry: {entry.FullName}");
                    Console.WriteLine(entry.FullName);

                    var relativePath = GetEntryRelativePath(entry.FullName, archiveRootFolder);
                    if (string.IsNullOrWhiteSpace(relativePath) || ShouldPreservePath(relativePath))
                    {
                        continue;
                    }

                    var entryOutputPath = Path.GetFullPath(Utils.GetPath(relativePath));
                    var startupPath = Path.GetFullPath(Utils.StartupPath());
                    if (!entryOutputPath.StartsWith(startupPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(currentUpdaterPath, entryOutputPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (skipTargetUpdaterReplacement && IsPathUnderDirectory(entryOutputPath, targetUpdaterDirectory))
                    {
                        Utils.Log($"Skipping updater file replacement for running in-place updater: {entryOutputPath}");
                        continue;
                    }

                    if (string.Equals(currentAppPath, entryOutputPath, StringComparison.OrdinalIgnoreCase))
                    {
                        BackupCurrentApp(currentAppPath, appBackupPath);
                        appEntryUpdated = true;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(entryOutputPath)!);
                    if (!TryExtractToFile(entry, entryOutputPath))
                    {
                        throw new IOException($"Failed to extract {entry.FullName} to {entryOutputPath}");
                    }

                    Utils.TryUnblockFile(entryOutputPath);

                    Utils.Log($"Updated file: {entryOutputPath}");
                    Console.WriteLine(entryOutputPath);
                }
                catch (Exception ex)
                {
                    Utils.Log($"Entry extraction failed: {ex}");
                    sb.Append(ex.StackTrace);
                }
            }

            if (appEntryUpdated)
            {
                FinalizeAppReplacement(currentAppPath, appBackupPath);
            }
        }
        catch (Exception ex)
        {
            hadFatalError = true;
            Utils.Log($"Upgrade failed: {ex}");
            Console.WriteLine(Resx.Resource.FailedUpgrade + ex.StackTrace);
            RestoreAppFromBackup(currentAppPath, appBackupPath);
        }

        if (sb.Length > 0)
        {
            Utils.Log($"Upgrade completed with entry failures. Rolling back app executable.");
            Console.WriteLine(Resx.Resource.FailedUpgrade + sb);
            RestoreAppFromBackup(currentAppPath, appBackupPath);
        }

        var upgradeSucceeded = !hadFatalError && sb.Length == 0;
        Utils.ScheduleCleanup(
            fileName,
            cleanupUpdaterDirectory ? runningUpdaterDirectory : null,
            deletePackage: upgradeSucceeded);

        Utils.Log("Restarting NetCat after update.");
        Console.WriteLine(Resx.Resource.Restartv2rayN);
        Utils.Waiting(2);

        if (!Utils.StartApp())
        {
            Utils.Log("Failed to restart application after update.");
            Console.WriteLine("Failed to restart application after update.");
        }
    }

    private static bool TryExtractToFile(ZipArchiveEntry entry, string outputPath)
    {
        var retryCount = 5;
        var delayMs = 1000;

        for (var i = 1; i <= retryCount; i++)
        {
            try
            {
                entry.ExtractToFile(outputPath, true);
                return true;
            }
            catch
            {
                Thread.Sleep(delayMs * i);
            }
        }

        return false;
    }

    private static string? GetArchiveRootFolder(ZipArchive archive)
    {
        var normalizedEntries = archive.Entries
            .Select(entry => entry.FullName.Replace('\\', '/').Trim('/'))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        if (normalizedEntries.Count == 0 || normalizedEntries.Any(name => !name.Contains('/')))
        {
            return null;
        }

        var rootFolders = normalizedEntries
            .Select(name => name.Split('/', StringSplitOptions.RemoveEmptyEntries)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return rootFolders.Count == 1 ? rootFolders[0] : null;
    }

    private static string GetEntryRelativePath(string entryName, string? archiveRootFolder)
    {
        var normalized = entryName.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(archiveRootFolder))
        {
            return normalized;
        }

        var prefix = archiveRootFolder + "/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : normalized;
    }

    private static bool ShouldPreservePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return true;
        }

        if (segments.Length == 1)
        {
            return _preservedFiles.Contains(segments[0]);
        }

        return _preservedTopLevelDirectories.Contains(segments[0]);
    }

    private static void TerminateRunningAppInstances(string appExePath)
    {
        foreach (var process in Process.GetProcessesByName(Utils.AppProcessName))
        {
            try
            {
                var processPath = process.MainModule?.FileName ?? string.Empty;
                if (!string.Equals(processPath, appExePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Utils.Log($"Stopping running app instance: {processPath}");
                process.Kill();
                process.WaitForExit(1000);
            }
            catch (Exception ex)
            {
                Utils.Log($"Failed to inspect or terminate process {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static bool IsPathUnderDirectory(string filePath, string directoryPath)
    {
        var normalizedDirectory = Path.GetFullPath(directoryPath).TrimEnd('\\') + "\\";
        var normalizedFilePath = Path.GetFullPath(filePath);
        return normalizedFilePath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void BackupCurrentApp(string currentAppPath, string appBackupPath)
    {
        if (!File.Exists(currentAppPath))
        {
            return;
        }

        DeleteFileIfExists(appBackupPath);
        Utils.Log($"Backed up current app to {appBackupPath}");
        File.Move(currentAppPath, appBackupPath, true);
    }

    private static void FinalizeAppReplacement(string currentAppPath, string appBackupPath)
    {
        if (!File.Exists(currentAppPath))
        {
            RestoreAppFromBackup(currentAppPath, appBackupPath);
            throw new FileNotFoundException("Updated application executable was not created.", currentAppPath);
        }

        DeleteFileIfExists(appBackupPath);
        Utils.Log("App replacement finalized.");
    }

    private static void RestoreAppFromBackup(string currentAppPath, string appBackupPath)
    {
        try
        {
            if (!File.Exists(appBackupPath))
            {
                return;
            }

            DeleteFileIfExists(currentAppPath);
            File.Move(appBackupPath, currentAppPath, true);
            Utils.Log($"Restored app from backup: {appBackupPath}");
        }
        catch
        {
            // ignore restore failures, caller will print restart failure
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
