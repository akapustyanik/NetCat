using System.Diagnostics;
using System.Text;

namespace AmazTool;

internal class Utils
{
    private const string RebootAsArgument = "rebootas";
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
                var logDir = Path.Combine(StartupPath(), "guiLogs");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, $"updater-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(logPath, text + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Ignore updater log failures to avoid breaking the update flow.
        }
    }
}
