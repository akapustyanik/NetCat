using System.Diagnostics;

namespace AmazTool;

internal class Utils
{
    public static string AppProcessName => "NetCat";

    public static string GetExePath()
    {
        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    }

    public static string StartupPath()
    {
        return AppDomain.CurrentDomain.BaseDirectory;
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
                        WorkingDirectory = StartupPath()
                    }
                };
                process.Start();
                return true;
            }
            catch
            {
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
}
