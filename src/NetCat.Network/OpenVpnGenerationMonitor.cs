using NetCat.Core;
namespace NetCat.Network;

public sealed record OpenVpnGeneration(long Revision, string Gateway, string Dns, string[] Routes)
{
    public ParsedOpenVpnRoute[] ParsedRoutes { get; init; } = [];
    public OpenVpnDnsEndpoint[] DnsEndpoints { get; init; } = [];
}

// Logical PUSH transactions belong to one generation. Events leave the state
// lock before invoking external consumers; incomplete fragments never publish.
public sealed class OpenVpnGenerationMonitor(bool allowPublic, Func<long>? nextRevision = null) : IDisposable
{
    private readonly object gate = new();
    private long revision = nextRevision?.Invoke() ?? 1;
    private bool completed, invalidated, failed, continuing, disposed;
    private readonly List<string> fragments = [];
    private OpenVpnPush? push;
    private ITimer? timer;
    private readonly Queue<Action> events = new();
    private bool dispatching;
    private void DrainEvents()
    {
        lock(gate){if(dispatching)return;dispatching=true;}
        while(true)
        {
            Action next;lock(gate){if(!events.TryDequeue(out next!)){dispatching=false;return;}}
            try {next();} catch {lock(gate)dispatching=false;throw;}
        }
    }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public TimeSpan FragmentTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public long Revision { get { lock (gate) return revision; } }
    public event Action<long>? Invalidated;
    public event Action<OpenVpnGeneration>? Ready;
    public event Action<OpenVpnPush>? CandidatePrepared;
    public event Action<long, OpenVpnPush>? CandidateGenerationPrepared;
    public event Action<Exception>? Failed;
    public bool IsCurrent(long value) { lock (gate) return !disposed && completed && !failed && revision == value; }
    public bool PublishIfCurrent(long value, Action publish)
    { lock (gate) { if (!IsCurrent(value)) return false; publish(); return IsCurrent(value); } }
    private long? InvalidateLocked()
    {
        if (invalidated) return null;
        revision = nextRevision?.Invoke() ?? revision + 1; invalidated = true; completed = false; failed = false; continuing = false;
        fragments.Clear(); push = null; timer?.Dispose(); timer = null; return revision;
    }
    public void Invalidate()
    {
        lock(gate){var n=InvalidateLocked();if(n.HasValue)events.Enqueue(()=>Invalidated?.Invoke(n.Value));}
        DrainEvents();
    }
    private void OnTimeout(object? state)
    {
        long? n;
        lock (gate)
        {
            if (disposed || !continuing || state is not long expected || expected != revision) return; n = InvalidateLocked(); failed = true;
            if(n.HasValue)events.Enqueue(()=>Invalidated?.Invoke(n.Value));
            events.Enqueue(()=>Failed?.Invoke(new TimeoutException("OpenVPN: не получен последний фрагмент PUSH; корпоративный путь закрыт.")));
        }
        DrainEvents();
    }
    public void Observe(string line)
    {
        long? invalidation = null; OpenVpnGeneration? ready = null; Exception? error = null; OpenVpnPush? candidate = null;
        lock (gate)
        {
            if (disposed) return;
            if (line.Contains("SIGUSR1", StringComparison.OrdinalIgnoreCase) || line.Contains("RECONNECTING", StringComparison.OrdinalIgnoreCase) || line.Contains("Connection reset", StringComparison.OrdinalIgnoreCase) || line.Contains("Inactivity timeout", StringComparison.OrdinalIgnoreCase))
            {
                invalidation = InvalidateLocked();
                // A genuine subsequent reconnect may recover a rejected PUSH;
                // duplicate restart lines still invalidate only once.
                failed = false;
            }
            try
            {
                if (line.Contains("AUTH_FAILED")) throw new OpenVpnFailureException(OpenVpnFailureClass.AuthenticationFatal, "OpenVPN: AUTH_FAILED.");
                if (line.Contains("Exiting due to fatal error")) throw new IOException("OpenVPN: соединение отклонено или завершено с ошибкой.");
                int start = line.IndexOf("PUSH_REPLY", StringComparison.Ordinal);
                if (start >= 0)
                {
                    if (completed) invalidation = InvalidateLocked();
                    if (failed) return;
                    invalidated = false;
                    var payload = line[(start + "PUSH_REPLY".Length)..].Trim().TrimEnd('\'', '"');
                    var options = payload.TrimStart(',').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var markers = options.Where(o => o.StartsWith("push-continuation", StringComparison.Ordinal)).ToArray();
                    int marker = 0;
                    if (markers.Length > 1 || markers.Length == 1 && (OpenVpnPushParser.Tokens(markers[0]) is not ["push-continuation", var v] || !int.TryParse(v, out marker) || marker is not (1 or 2)))
                        throw new InvalidDataException("OpenVPN: неверный push-continuation.");
                    if (marker == 1 && !continuing || marker == 0 && continuing) throw new InvalidDataException("OpenVPN: нарушена последовательность фрагментов PUSH.");
                    if (!continuing) { fragments.Clear(); push = null; }
                    fragments.AddRange(options.Where(o => !o.StartsWith("push-continuation", StringComparison.Ordinal)));
                    if (fragments.Count > 4096 || fragments.Sum(s => s.Length) > 262144) throw new InvalidDataException("OpenVPN: превышен размер PUSH.");
                    continuing = marker == 2;
                    if (continuing) timer ??= TimeProvider.CreateTimer(OnTimeout, revision, FragmentTimeout, Timeout.InfiniteTimeSpan);
                    else { timer?.Dispose(); timer = null; push = OpenVpnPushParser.Parse(fragments, allowPublic); candidate = push; }
                }
                if (line.Contains("Initialization Sequence Completed") && !completed && !failed)
                {
                    if (continuing) throw new InvalidDataException("OpenVPN: initialization до завершения PUSH continuation.");
                    push ??= new("", [], []); completed = true; invalidated = false;
                    ready = new(revision, push.Gateway, push.Dns.FirstOrDefault()?.Address.ToString() ?? "", push.Routes.Select(r => r.Prefix).Distinct().Order().ToArray())
                        { ParsedRoutes = push.Routes, DnsEndpoints = push.Dns };
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            { invalidation = InvalidateLocked() ?? invalidation; failed = true; error = ex; }
            if (invalidation.HasValue) events.Enqueue(()=>Invalidated?.Invoke(invalidation.Value));
            if (error != null) events.Enqueue(()=>Failed?.Invoke(error));
            if (candidate != null)
            {
                var candidateRevision=revision;
                events.Enqueue(()=>{CandidateGenerationPrepared?.Invoke(candidateRevision,candidate);CandidatePrepared?.Invoke(candidate);});
            }
            if (ready != null) events.Enqueue(()=>{if(IsCurrent(ready.Revision))Ready?.Invoke(ready);});
        }
        DrainEvents();
    }
    public void Dispose() { lock (gate) { disposed = true; timer?.Dispose(); timer = null; } }
}
