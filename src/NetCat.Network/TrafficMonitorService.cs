using System.Diagnostics;
using System.Net.NetworkInformation;
using NetCat.Core;

namespace NetCat.Network;

public sealed record TrafficSnapshot(
    double DownloadMbps,
    double UploadMbps,
    int InterfaceIndex,
    string InterfaceName,
    bool HasBaseline
);

public interface ITrafficStatisticsProvider
{
    (long BytesReceived, long BytesSent)? GetInterfaceStatistics(int interfaceIndex);
    string? GetInterfaceName(int interfaceIndex);
}

public sealed class SystemTrafficStatisticsProvider : ITrafficStatisticsProvider
{
    public static readonly SystemTrafficStatisticsProvider Instance = new();

    public (long BytesReceived, long BytesSent)? GetInterfaceStatistics(int interfaceIndex)
    {
        try
        {
            var iface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => {
                    try { return n.GetIPProperties().GetIPv4Properties()?.Index == interfaceIndex; }
                    catch { return false; }
                });
            if (iface == null) return null;
            var stats = iface.GetIPStatistics();
            return (stats.BytesReceived, stats.BytesSent);
        }
        catch
        {
            return null;
        }
    }

    public string? GetInterfaceName(int interfaceIndex)
    {
        try
        {
            var iface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => {
                    try { return n.GetIPProperties().GetIPv4Properties()?.Index == interfaceIndex; }
                    catch { return false; }
                });
            return iface?.Name;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class TrafficMonitorService : IDisposable
{
    private readonly Func<NetworkSnapshot?> getActivePhysical;
    private readonly ITrafficStatisticsProvider statsProvider;
    private readonly object syncLock = new();
    private CancellationTokenSource? samplingCts;
    private Task? samplingTask;
    private bool disposed;

    private int lastInterfaceIndex = -1;
    private long lastRxBytes = -1;
    private long lastTxBytes = -1;
    private long lastTimestamp = 0;
    private TrafficSnapshot currentSnapshot = new(0, 0, -1, "", false);

    public event Action<TrafficSnapshot>? SampleUpdated;

    public TrafficSnapshot CurrentSnapshot
    {
        get { lock (syncLock) return currentSnapshot; }
    }

    public double DownloadMbps => CurrentSnapshot.DownloadMbps;
    public double UploadMbps => CurrentSnapshot.UploadMbps;
    public string ActiveInterfaceName => CurrentSnapshot.InterfaceName;

    public TrafficMonitorService(
        Func<NetworkSnapshot?> getActivePhysical,
        ITrafficStatisticsProvider? statsProvider = null)
    {
        this.getActivePhysical = getActivePhysical;
        this.statsProvider = statsProvider ?? SystemTrafficStatisticsProvider.Instance;
    }

    public void StartSampling(TimeSpan? interval = null)
    {
        var sampleInterval = interval ?? TimeSpan.FromSeconds(1);
        if (sampleInterval.TotalMilliseconds < 1 || sampleInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval));
        lock (syncLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (samplingCts != null) return;
            var previous = samplingTask;
            var source = new CancellationTokenSource();
            samplingCts = source;
            var token = source.Token;

            samplingTask = Task.Run(async () =>
            {
                try
                {
                    // A restart must not overlap a subscriber/provider still
                    // finishing the preceding sample after StopSampling.
                    if (previous != null) await previous.ConfigureAwait(false);
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var snapshot = Sample();
                            SampleUpdated?.Invoke(snapshot);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch
                        {
                            // Back off below even if the provider/subscriber throws.
                        }
                        try { await Task.Delay(sampleInterval, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
                finally
                {
                    lock (syncLock)
                    {
                        if (ReferenceEquals(samplingCts, source)) samplingCts = null;
                        source.Dispose();
                    }
                }
            });
        }
    }

    public void StopSampling()
    {
        lock (syncLock)
        {
            samplingCts?.Cancel();
            samplingCts = null;
        }
    }

    public void Dispose()
    {
        lock (syncLock)
        {
            if (disposed) return;
            disposed = true;
            StopSampling();
        }
    }

    public TrafficSnapshot Sample(long? overrideTimestamp = null)
    {
        lock (syncLock)
        {
            var physical = getActivePhysical();
            int targetIndex = physical?.Index ?? -1;
            string targetName = physical?.Name ?? "";

            long now = overrideTimestamp ?? Stopwatch.GetTimestamp();

            if (targetIndex <= 0)
            {
                lastInterfaceIndex = -1;
                lastRxBytes = -1;
                lastTxBytes = -1;
                lastTimestamp = 0;
                currentSnapshot = new TrafficSnapshot(0, 0, -1, "", false);
                return currentSnapshot;
            }

            // Reset baseline on interface change
            if (targetIndex != lastInterfaceIndex)
            {
                lastInterfaceIndex = targetIndex;
                var stats = statsProvider.GetInterfaceStatistics(targetIndex);
                if (stats.HasValue)
                {
                    lastRxBytes = stats.Value.BytesReceived;
                    lastTxBytes = stats.Value.BytesSent;
                    lastTimestamp = now;
                }
                else
                {
                    lastRxBytes = -1;
                    lastTxBytes = -1;
                    lastTimestamp = 0;
                }
                currentSnapshot = new TrafficSnapshot(0, 0, targetIndex, targetName, false);
                return currentSnapshot;
            }

            // First sample for this interface
            if (lastTimestamp == 0 || lastRxBytes < 0 || lastTxBytes < 0)
            {
                var stats = statsProvider.GetInterfaceStatistics(targetIndex);
                if (stats.HasValue)
                {
                    lastRxBytes = stats.Value.BytesReceived;
                    lastTxBytes = stats.Value.BytesSent;
                    lastTimestamp = now;
                }
                currentSnapshot = new TrafficSnapshot(0, 0, targetIndex, targetName, false);
                return currentSnapshot;
            }

            var currentStats = statsProvider.GetInterfaceStatistics(targetIndex);
            if (!currentStats.HasValue)
            {
                lastRxBytes = -1;
                lastTxBytes = -1;
                lastTimestamp = 0;
                currentSnapshot = new TrafficSnapshot(0, 0, targetIndex, targetName, false);
                return currentSnapshot;
            }

            long curRx = currentStats.Value.BytesReceived;
            long curTx = currentStats.Value.BytesSent;
            double elapsedSeconds = Stopwatch.GetElapsedTime(lastTimestamp, now).TotalSeconds;

            // Prevent giant spikes after window restore / system sleep (> 3.0s) or invalid duration (<= 0)
            if (elapsedSeconds <= 0 || elapsedSeconds > 3.0)
            {
                lastRxBytes = curRx;
                lastTxBytes = curTx;
                lastTimestamp = now;
                currentSnapshot = new TrafficSnapshot(0, 0, targetIndex, targetName, true);
                return currentSnapshot;
            }

            // Prevent negative spikes on counter rollback / interface recreation
            if (curRx < lastRxBytes || curTx < lastTxBytes)
            {
                lastRxBytes = curRx;
                lastTxBytes = curTx;
                lastTimestamp = now;
                currentSnapshot = new TrafficSnapshot(0, 0, targetIndex, targetName, true);
                return currentSnapshot;
            }

            long deltaRx = curRx - lastRxBytes;
            long deltaTx = curTx - lastTxBytes;

            // RX = delta physical BytesReceived / elapsed monotonic time
            // TX = delta physical BytesSent / elapsed monotonic time
            double downMbps = Math.Max(0, (deltaRx * 8.0) / (1_000_000.0 * elapsedSeconds));
            double upMbps = Math.Max(0, (deltaTx * 8.0) / (1_000_000.0 * elapsedSeconds));

            lastRxBytes = curRx;
            lastTxBytes = curTx;
            lastTimestamp = now;

            currentSnapshot = new TrafficSnapshot(downMbps, upMbps, targetIndex, targetName, true);
            return currentSnapshot;
        }
    }
}
