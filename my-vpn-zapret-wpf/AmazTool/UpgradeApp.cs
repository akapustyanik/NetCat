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
        try
        {
            var currentAppPath = Utils.GetAppExePath();
            var appBackupPath = $"{currentAppPath}.tmp";
            var currentUpdaterPath = Utils.GetExePath();
            File.Delete(appBackupPath);

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
                        File.Move(currentAppPath, appBackupPath, true);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(entryOutputPath)!);
                    TryExtractToFile(entry, entryOutputPath);

                    Console.WriteLine(entryOutputPath);
                }
                catch (Exception ex)
                {
                    sb.Append(ex.StackTrace);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(Resx.Resource.FailedUpgrade + ex.StackTrace);
        }

        if (sb.Length > 0)
        {
            Console.WriteLine(Resx.Resource.FailedUpgrade + sb);
        }

        Console.WriteLine(Resx.Resource.Restartv2rayN);
        Utils.Waiting(2);

        Utils.StartApp();
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
}
