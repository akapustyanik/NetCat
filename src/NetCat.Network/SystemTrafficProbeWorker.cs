using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Network;

public static class SystemTrafficProbeWorker
{
    public const string Argument = "--probe-system-traffic";
    public const string ExecutableName = "NetCat.TrafficProbe.exe";
    public static bool IsValidInvocation(string[] args, string? executable) =>
        args is [Argument] && string.Equals(Path.GetFileName(executable), ExecutableName, StringComparison.OrdinalIgnoreCase);

    public static async Task<TrafficTestResult> MeasureAsync(string runtime, CancellationToken ct)
    {
        var current = Environment.ProcessPath;
        if(!string.Equals(Path.GetFileName(current), "NetCat.exe", StringComparison.OrdinalIgnoreCase))
            return TrafficTestResult.Failed("Проверка TUN доступна в установленном NetCat");
        return await MeasureVerifiedImageAsync(current!, runtime, ct).ConfigureAwait(false);
    }
    private static async Task<TrafficTestResult> MeasureVerifiedImageAsync(string current, string runtime, CancellationToken ct)
    {
        var folder = Path.Combine(runtime, "traffic-worker-" + Guid.NewGuid().ToString("N"));
        PrivateFiles.ProtectDirectory(folder);
        var executable = Path.Combine(folder, ExecutableName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(50));
        try
        {
            // A verified copy of this application, under a different process name,
            // performs only measurements. It never acquires network ownership or IPC.
            using var source = new FileStream(current!, FileMode.Open, FileAccess.Read, FileShare.Read);
            using(var destination = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(destination, timeout.Token);
            source.Position = 0;
            using var verified = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            var expected = SHA256.HashData(source);
            if(!expected.AsSpan().SequenceEqual(SHA256.HashData(verified)))
                throw new IOException("Traffic worker verification failed");
            // Existing atomic Windows job assignment also kills the worker if
            // NetCat exits or crashes; no unowned Process.Start helper is added.
            var response = await ProcessHost.RunAsync(executable, [Argument], timeout.Token,
                trustPolicy: new VerifiedWorker(executable, expected));
            if(response.Code != 0) throw new IOException("Traffic worker failed");
            if(response.Output.Length > 32768) throw new IOException("Traffic worker response exceeds limit");
            return JsonSerializer.Deserialize<TrafficTestResult>(response.Output) ?? throw new IOException("Empty traffic worker response");
        }
        catch(Exception ex) when(!ct.IsCancellationRequested) { return TrafficTestResult.Failed(ProfileTrafficProbe.Describe(ex)); }
        finally
        {
            try { File.Delete(executable); Directory.Delete(folder); } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
    }
    private sealed class VerifiedWorker(string executable, byte[] expected) : IExecutableTrustPolicy
    {
        public IDisposable AcquirePackage(string module, string folder) => throw new InvalidDataException("Traffic worker trust cannot authorize modules");
        public IDisposable AcquireExecutable(string path)
        {
            if(!string.Equals(path, executable, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected traffic worker path");
            ModuleIntegrity.CheckPath(path);
            var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if(!SHA256.HashData(lease).AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Traffic worker changed");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
    }
}
