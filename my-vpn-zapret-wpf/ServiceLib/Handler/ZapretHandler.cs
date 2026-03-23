using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using ServiceLib.Common;
using ServiceLib.Models;

namespace ServiceLib.Handler;

public static class ZapretHandler
{
    private const string HiddenLaunchPrefix = "zapret-hidden-";

    public static bool IsValidZapretPath(string? path)
    {
        if (path.IsNullOrEmpty() || !Directory.Exists(path))
        {
            return false;
        }

        return File.Exists(Path.Combine(path, "bin", "winws.exe"));
    }

    public static string? FindZapretPath(string? preferredPath = null)
    {
        if (IsValidZapretPath(preferredPath))
        {
            return preferredPath;
        }

        var envPath = Environment.GetEnvironmentVariable("ZAPRET_PATH");
        if (IsValidZapretPath(envPath))
        {
            return envPath;
        }

        var current = new DirectoryInfo(Utils.StartupPath());
        for (var i = 0; i < 6 && current != null; i++)
        {
            var candidate = Path.Combine(current.FullName, "zapret");
            if (IsValidZapretPath(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        return null;
    }

    public static List<string> GetBatFiles(string zapretPath)
    {
        if (zapretPath.IsNullOrEmpty() || !Directory.Exists(zapretPath))
        {
            return [];
        }

        CleanupHiddenLaunchBats(zapretPath);

        return Directory.GetFiles(zapretPath, "*.bat")
            .Select(Path.GetFileName)
            .Where(name => !string.Equals(name, "service.bat", StringComparison.OrdinalIgnoreCase))
            .Where(name => !IsHiddenLaunchBat(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsRunning()
    {
        return Process.GetProcessesByName("winws").Length > 0;
    }

    public static bool Start(string zapretPath, string configName, out string error)
    {
        error = string.Empty;
        if (zapretPath.IsNullOrEmpty() || !Directory.Exists(zapretPath))
        {
            error = "Zapret path not found";
            return false;
        }

        if (IsHiddenLaunchBat(configName))
        {
            error = "Temporary Zapret launcher file is not a valid config";
            return false;
        }

        CleanupHiddenLaunchBats(zapretPath);

        var batPath = Path.Combine(zapretPath, configName);
        if (!File.Exists(batPath))
        {
            error = "Bat file not found";
            return false;
        }

        try
        {
            var patchedBatPath = CreateHiddenLaunchBat(batPath);
            var startInfo = new ProcessStartInfo("cmd.exe", $"/C call \"{patchedBatPath}\"")
            {
                WorkingDirectory = zapretPath,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string CreateHiddenLaunchBat(string batPath)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(batPath) ?? Path.GetTempPath(),
            $"{HiddenLaunchPrefix}{Path.GetFileNameWithoutExtension(batPath)}-{Guid.NewGuid():N}.bat");

        var content = File.ReadAllText(batPath);
        content = content.Replace("start \"zapret: %~n0\" /min", "start \"\" /b", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(tempPath, content);
        return tempPath;
    }

    public static void Stop()
    {
        try
        {
            var startInfo = new ProcessStartInfo("taskkill", "/IM winws.exe /F")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(startInfo);
        }
        catch
        {
            // ignore
        }
    }

    public static async Task<ZapretTestResult> TestConfigAsync(string zapretPath, string configName, bool keepRunning, CancellationToken cancellationToken = default)
    {
        var result = new ZapretTestResult();
        var wasRunning = IsRunning();
        if (!wasRunning)
        {
            if (!Start(zapretPath, configName, out var error))
            {
                result.Message = error;
                return result;
            }

            await Task.Delay(6000, cancellationToken);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var youtube = await ProbeServiceAsync(
                "youtube.com",
                "https://www.youtube.com/generate_204",
                cancellationToken);
            var discord = await ProbeServiceAsync(
                "discord.com",
                "https://discord.com/api/v9/experiments",
                cancellationToken);

            result.YoutubeSuccess = youtube.Success;
            result.YoutubePingMs = youtube.PingMs;
            result.YoutubeHttpMs = youtube.HttpMs;
            result.YoutubeMessage = youtube.Message;

            result.DiscordSuccess = discord.Success;
            result.DiscordPingMs = discord.PingMs;
            result.DiscordHttpMs = discord.HttpMs;
            result.DiscordMessage = discord.Message;

            result.Success = youtube.Success && discord.Success;

            var youtubeScore = youtube.HttpMs ?? youtube.PingMs;
            var discordScore = discord.HttpMs ?? discord.PingMs;
            if (youtubeScore.HasValue && discordScore.HasValue)
            {
                result.TimeMs = Math.Max(youtubeScore.Value, discordScore.Value);
            }
            else
            {
                result.TimeMs = youtubeScore ?? discordScore;
            }

            result.Message = BuildSummaryMessage(result);
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        finally
        {
            if (!keepRunning && !wasRunning)
            {
                Stop();
                await WaitForStopAsync();
                CleanupHiddenLaunchBats(zapretPath);
            }
        }

        return result;
    }

    private static bool IsHiddenLaunchBat(string? fileName)
    {
        return fileName?.StartsWith(HiddenLaunchPrefix, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static int CountHiddenLaunchBats(string zapretPath)
    {
        try
        {
            return EnumerateHiddenLaunchBats(zapretPath).Count();
        }
        catch
        {
            return 0;
        }
    }

    public static int CleanupHiddenLaunchBats(string zapretPath)
    {
        var removed = 0;
        try
        {
            foreach (var filePath in EnumerateHiddenLaunchBats(zapretPath))
            {
                File.Delete(filePath);
                removed++;
            }
        }
        catch
        {
            // ignore stale temp launcher cleanup failures
        }

        return removed;
    }

    private static IEnumerable<string> EnumerateHiddenLaunchBats(string zapretPath)
    {
        if (zapretPath.IsNullOrEmpty() || !Directory.Exists(zapretPath))
        {
            return [];
        }

        return Directory.GetFiles(zapretPath, $"{HiddenLaunchPrefix}*.bat", SearchOption.TopDirectoryOnly);
    }

    private static string BuildSummaryMessage(ZapretTestResult result)
    {
        return $"YouTube: {result.YoutubeMessage} | Discord: {result.DiscordMessage}";
    }

    private static async Task WaitForStopAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            if (!IsRunning())
            {
                return;
            }

            await Task.Delay(250);
        }
    }

    private static async Task<(bool Success, long? PingMs, long? HttpMs, string Message)> ProbeServiceAsync(
        string host,
        string url,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pingMs = await TryPingAsync(host);
        cancellationToken.ThrowIfCancellationRequested();
        var httpResult = await TryHttpAsync(url, cancellationToken);

        if (httpResult.Success && httpResult.HttpMs.HasValue)
        {
            return (true, pingMs, httpResult.HttpMs, $"http {httpResult.HttpMs.Value} ms");
        }

        if (pingMs.HasValue)
        {
            return (false, pingMs, httpResult.HttpMs, $"http failed, ping {pingMs.Value} ms");
        }

        return (false, pingMs, httpResult.HttpMs, httpResult.Detail);
    }

    private static async Task<long?> TryPingAsync(string host)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 3000);
            if (reply.Status == IPStatus.Success)
            {
                return reply.RoundtripTime;
            }
        }
        catch
        {
            // ignore ping errors
        }

        return null;
    }

    private static async Task<(bool Success, long? HttpMs, string Detail)> TryHttpAsync(string url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");

        try
        {
            var start = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, cancellationToken);
            start.Stop();

            return response.IsSuccessStatusCode
                ? (true, start.ElapsedMilliseconds, $"http {start.ElapsedMilliseconds} ms")
                : (false, null, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
