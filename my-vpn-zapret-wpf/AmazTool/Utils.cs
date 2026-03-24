using System.Diagnostics;
using System.Text;

namespace AmazTool;

internal class Utils
{
    private const string RebootAsArgument = "rebootas";
    private const long MaxUpdaterLogSizeBytes = 1024 * 1024;
    private static readonly object _logLock = new();
    private static string? _targetStartupPath;

    public static string AppProcessName => "NetCat";

    public static string GetExePath()
    {
        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    }

    public static void SetTargetStartupPath(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        _targetStartupPath = Path.GetFullPath(targetPath);
    }

    public static string StartupPath()
    {
        return _targetStartupPath ?? AppDomain.CurrentDomain.BaseDirectory;
    }

    public static string GetPath(string fileName)
    {
        var startupPath = StartupPath();
        if (string.IsNullOrEmpty(fileName))
        {
            return startupPath;
        }
        return Path.Combine(startupPath, fileName);
    }

    public static string GetAppExePath()
    {
        return GetPath($"{AppProcessName}.exe");
    }

    public static bool StartApp(int retryCount = 5, int delayMs = 1000)
    {
        var appExePath = GetAppExePath();
        for (var i = 1; i <= retryCount; i++)
        {
            try
            {
                if (!File.Exists(appExePath))
                {
                    Thread.Sleep(delayMs * i);
                    continue;
                }

                TryUnblockFile(appExePath);
                Process process = new()
                {
                    StartInfo = new()
                    {
                        UseShellExecute = true,
                        FileName = appExePath,
                        Arguments = RebootAsArgument,
                        WorkingDirectory = StartupPath()
                    }
                };
                process.Start();
                Log($"Started app from {appExePath}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Restart attempt {i} failed: {ex.Message}");
                Thread.Sleep(delayMs * i);
            }
        }

        return false;
    }

    public static void Waiting(int second)
    {
        for (var i = second; i > 0; i--)
        {
            Console.WriteLine(i);
            Thread.Sleep(1000);
        }
    }

    public static void Log(string message)
    {
        try
        {
            var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
            Console.WriteLine(text);

            lock (_logLock)
            {
                var logDir = GetUpdaterLogDirectory();
                CleanupOldUpdaterLogs(logDir);
                var logPath = GetUpdaterLogPath(logDir);
                File.AppendAllText(logPath, text + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Ignore updater log failures to avoid breaking the update flow.
        }
    }

    public static void ScheduleCleanup(string? packagePath, string? updaterDirectory, bool deletePackage)
    {
        try
        {
            List<string> cleanupCommands = [];
            if (deletePackage && !string.IsNullOrWhiteSpace(packagePath))
            {
                cleanupCommands.Add($"del /f /q \"{packagePath}\" > nul 2>nul");
            }

            if (!string.IsNullOrWhiteSpace(updaterDirectory))
            {
                cleanupCommands.Add($"rmdir /s /q \"{updaterDirectory}\" > nul 2>nul");
            }

            if (cleanupCommands.Count == 0)
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping 127.0.0.1 -n 6 > nul & {string.Join(" & ", cleanupCommands)}",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            Log($"Failed to schedule updater cleanup: {ex.Message}");
        }
    }

    private static string GetUpdaterLogDirectory()
    {
        var userDataLogDir = Path.Combine(StartupPath(), "userdata", "guiLogs");
        if (Directory.Exists(Path.Combine(StartupPath(), "userdata")) || Directory.Exists(userDataLogDir))
        {
            Directory.CreateDirectory(userDataLogDir);
            return userDataLogDir;
        }

        var legacyLogDir = Path.Combine(StartupPath(), "guiLogs");
        Directory.CreateDirectory(legacyLogDir);
        return legacyLogDir;
    }

    private static string GetUpdaterLogPath(string logDir)
    {
        var baseName = $"updater-{DateTime.Now:yyyy-MM-dd}";
        var primaryPath = Path.Combine(logDir, $"{baseName}.log");
        if (!File.Exists(primaryPath))
        {
            return primaryPath;
        }

        var info = new FileInfo(primaryPath);
        if (info.Length < MaxUpdaterLogSizeBytes)
        {
            return primaryPath;
        }

        var index = 1;
        while (true)
        {
            var candidate = Path.Combine(logDir, $"{baseName}.{index}.log");
            if (!File.Exists(candidate) || new FileInfo(candidate).Length < MaxUpdaterLogSizeBytes)
            {
                return candidate;
            }

            index++;
        }
    }

    private static void CleanupOldUpdaterLogs(string logDir)
    {
        try
        {
            foreach (var filePath in Directory.GetFiles(logDir, "updater-*.log", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(filePath);
                if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14))
                {
                    File.Delete(filePath);
                }
            }
        }
        catch
        {
            // ignore updater log cleanup failures
        }
    }

    public static bool TryUnblockFile(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete($"{path}:Zone.Identifier");
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log($"Failed to unblock {path}: {ex.Message}");
            return false;
        }
    }
}
