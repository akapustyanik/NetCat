using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace AmazTool;

internal class UpgradeApp
{
    private static readonly HashSet<string> _preservedTopLevelDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "binConfigs",
        "guiBackups",
        "guiConfigs",
        "guiLogs",
        "guiTemps",
    };

    private static readonly HashSet<string> _preservedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "domains.lst",
        "guiNConfig.json",
    };

    public static void Upgrade(string fileName)
    {
        Console.WriteLine($"{Resx.Resource.StartUnzipping}\n{fileName}");

        Utils.Waiting(5);

        if (!File.Exists(fileName))
        {
            Console.WriteLine(Resx.Resource.UpgradeFileNotFound);
            return;
        }

        Console.WriteLine(Resx.Resource.TryTerminateProcess);
        try
        {
            var appExePath = Utils.GetAppExePath();
            var existing = Process.GetProcessesByName(Utils.AppProcessName);
            foreach (var pp in existing)
            {
                var path = pp.MainModule?.FileName ?? string.Empty;
                if (string.Equals(path, appExePath, StringComparison.OrdinalIgnoreCase))
                {
                    pp.Kill();
                    pp.WaitForExit(1000);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(Resx.Resource.FailedTerminateProcess + ex.StackTrace);
        }

        Console.WriteLine(Resx.Resource.StartUnzipping);
        StringBuilder sb = new();
        var currentAppPath = Utils.GetAppExePath();
        var appBackupPath = $"{currentAppPath}.tmp";
        var currentUpdaterPath = Utils.GetExePath();
        var appEntryUpdated = false;
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

                    Console.WriteLine(entryOutputPath);
                }
                catch (Exception ex)
                {
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
            Console.WriteLine(Resx.Resource.FailedUpgrade + ex.StackTrace);
            RestoreAppFromBackup(currentAppPath, appBackupPath);
        }

        if (sb.Length > 0)
        {
            Console.WriteLine(Resx.Resource.FailedUpgrade + sb);
            RestoreAppFromBackup(currentAppPath, appBackupPath);
        }

        Console.WriteLine(Resx.Resource.Restartv2rayN);
        Utils.Waiting(2);

        if (!Utils.StartApp())
        {
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

    private static void BackupCurrentApp(string currentAppPath, string appBackupPath)
    {
        if (!File.Exists(currentAppPath))
        {
            return;
        }

        DeleteFileIfExists(appBackupPath);
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
