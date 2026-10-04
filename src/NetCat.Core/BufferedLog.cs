using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;

namespace NetCat.Core;

public enum SemanticLogCategory
{
    None,
    UnreachableHost,
    ConnectionAborted,
    RawReadClosed,
    DnsBadQuestionSize,
    DnsOverflowUnpacking,
    DirectRouteUnavailable,
    DnsMalformedAggregate,
    VpnUpstreamTimeout
}

public sealed class SemanticLogCoalescer
{
    public sealed class CategoryState
    {
        public SemanticLogCategory Category { get; set; }
        public string FirstMessage { get; set; } = "";
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public int Count { get; set; }
        public int DiagnosticRawEmitted { get; set; }
        public bool SummaryEmitted { get; set; }
        public int DnsBadQuestionCount { get; set; }
        public int DnsOverflowCount { get; set; }
        public int DnsBadRdataCount { get; set; }
    }

    private readonly Dictionary<SemanticLogCategory, CategoryState> states = new();
    private readonly object sync = new();
    private readonly TimeSpan? customWindow;

    public SemanticLogCoalescer(TimeSpan? window = null)
    {
        this.customWindow = window;
    }

    private TimeSpan GetWindow(SemanticLogCategory cat) =>
        customWindow ?? (
            cat == SemanticLogCategory.DnsMalformedAggregate ? TimeSpan.FromSeconds(60.0) :
            cat == SemanticLogCategory.VpnUpstreamTimeout ? TimeSpan.FromSeconds(10.0) :
            TimeSpan.FromSeconds(2.0));

    public static SemanticLogCategory Classify(string line)
    {
        if (line.Contains("bad question size", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("overflow unpacking", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("bad rdata", StringComparison.OrdinalIgnoreCase))
            return SemanticLogCategory.DnsMalformedAggregate;
        if (line.Contains("unreachable host", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("WSAEHOSTUNREACH", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("10065", StringComparison.OrdinalIgnoreCase))
            return SemanticLogCategory.UnreachableHost;
        if (line.Contains("connection was aborted", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("WSAECONNABORTED", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("10053", StringComparison.OrdinalIgnoreCase))
            return SemanticLogCategory.ConnectionAborted;
        if (line.Contains("raw-read closed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("connection reset by peer", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("WSAECONNRESET", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("10054", StringComparison.OrdinalIgnoreCase))
            return SemanticLogCategory.RawReadClosed;
        if (line.Contains("Прямой маршрут временно недоступен", StringComparison.OrdinalIgnoreCase))
            return SemanticLogCategory.DirectRouteUnavailable;
        if ((line.Contains("outbound/vless[vpn]", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("outbound/trojan[vpn]", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("[vpn]", StringComparison.OrdinalIgnoreCase)) &&
            (line.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("deadline exceeded", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("EOF", StringComparison.OrdinalIgnoreCase)))
            return SemanticLogCategory.VpnUpstreamTimeout;
        if (line.Contains("read tcp", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("context deadline exceeded", StringComparison.OrdinalIgnoreCase)))
            return SemanticLogCategory.VpnUpstreamTimeout;
        return SemanticLogCategory.None;
    }

    public static string FormatSummary(SemanticLogCategory category, int count, double seconds, CategoryState? state = null)
    {
        var timeStr = seconds.ToString("F1", CultureInfo.InvariantCulture);
        return category switch
        {
            SemanticLogCategory.UnreachableHost =>
                $"сеть: хост недоступен (повторено {count} раз за {timeStr} с)",
            SemanticLogCategory.ConnectionAborted =>
                $"сеть: соединение разорвано удалённым узлом (повторено {count} раз за {timeStr} с)",
            SemanticLogCategory.RawReadClosed =>
                $"сеть: поток закрыт (повторено {count} раз за {timeStr} с)",
            SemanticLogCategory.DnsBadQuestionSize =>
                $"dns: bad question size: 0 (повторено {count} раз за {timeStr} с)",
            SemanticLogCategory.DnsOverflowUnpacking =>
                $"dns: ошибка распаковки (повторено {count} раз за {timeStr} с)",
            SemanticLogCategory.DirectRouteUnavailable =>
                $"маршрутизация: прямой маршрут временно недоступен (повторено {count} раз за {timeStr} с)",
            SemanticLogCategory.DnsMalformedAggregate =>
                $"dns: bad question size / malformed (повторено {count} раз за {timeStr} с; bad_question_size={state?.DnsBadQuestionCount ?? 0} overflow_unpacking={state?.DnsOverflowCount ?? 0} bad_rdata={state?.DnsBadRdataCount ?? 0} window={Math.Max(1, (int)Math.Round(seconds))}s)",
            SemanticLogCategory.VpnUpstreamTimeout =>
                $"vpn: таймаут вышестоящего сервера (повторено {count} раз за {timeStr} с)",
            _ => $"сообщение повторено {count} раз за {timeStr} с"
        };
    }

    public static string FormatSummary(SemanticLogCategory category, int count, double seconds) =>
        FormatSummary(category, count, seconds, null);

    private static void RecordDnsSubCounts(CategoryState state, string line)
    {
        if (state.Category != SemanticLogCategory.DnsMalformedAggregate) return;
        if (line.Contains("bad question size", StringComparison.OrdinalIgnoreCase)) state.DnsBadQuestionCount++;
        if (line.Contains("overflow unpacking", StringComparison.OrdinalIgnoreCase)) state.DnsOverflowCount++;
        if (line.Contains("bad rdata", StringComparison.OrdinalIgnoreCase)) state.DnsBadRdataCount++;
    }

    public bool Ingest(string line, DateTimeOffset now, out string? uiOutput, out string? diagOutput, out string? flushedSummary)
    {
        flushedSummary = null;
        var category = Classify(line);
        if (category == SemanticLogCategory.None)
        {
            uiOutput = line;
            diagOutput = line;
            return true;
        }

        lock (sync)
        {
            var win = GetWindow(category);
            if (states.TryGetValue(category, out var state))
            {
                if (now - state.FirstSeen <= win)
                {
                    state.Count++;
                    RecordDnsSubCounts(state, line);
                    state.LastSeen = now;
                    uiOutput = null;
                    if (state.DiagnosticRawEmitted < 5)
                    {
                        state.DiagnosticRawEmitted++;
                        diagOutput = line;
                    }
                    else
                    {
                        diagOutput = null;
                    }
                    return false;
                }
                // Window expired: flush previous summary before starting new window
                if (state.Count > 1 || state.Category == SemanticLogCategory.DnsMalformedAggregate)
                {
                    flushedSummary = FormatSummary(category, state.Count, (state.LastSeen - state.FirstSeen).TotalSeconds, state);
                }
                states.Remove(category);
            }

            var newState = new CategoryState
            {
                Category = category,
                FirstMessage = line,
                FirstSeen = now,
                LastSeen = now,
                Count = 1,
                DiagnosticRawEmitted = 1
            };
            RecordDnsSubCounts(newState, line);
            states[category] = newState;

            uiOutput = line;
            diagOutput = line;
            return true;
        }
    }

    public List<string> FlushExpired(DateTimeOffset now, bool forceAll = false)
    {
        var result = new List<string>();
        lock (sync)
        {
            var expired = new List<SemanticLogCategory>();
            foreach (var (cat, state) in states)
            {
                var win = GetWindow(cat);
                if (forceAll || now - state.LastSeen > win || now - state.FirstSeen >= win)
                {
                    if (state.Count > 1 || state.Category == SemanticLogCategory.DnsMalformedAggregate)
                    {
                        result.Add(FormatSummary(cat, state.Count, (state.LastSeen - state.FirstSeen).TotalSeconds, state));
                    }
                    expired.Add(cat);
                }
            }
            foreach (var cat in expired) states.Remove(cat);
        }
        return result;
    }
}

public sealed class BatchedLog : ObservableCollection<string>
{
    public void Prepend(IEnumerable<string> lines, int limit = 2000)
    {
        foreach (var line in lines) Items.Insert(0, line);
        while (Items.Count > limit) Items.RemoveAt(Items.Count - 1);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class BufferedLog(int capacity = 4096)
{
    private readonly Queue<string> queue = new();
    private readonly object sync = new();
    private long received, dropped;
    public long Received => Interlocked.Read(ref received);
    public long Dropped => Interlocked.Read(ref dropped);
    public int Count { get { lock (sync) return queue.Count; } }

    public void Add(string line)
    {
        Interlocked.Increment(ref received);
        lock (sync)
        {
            // Keep the first evidence of a flood; later overflow is counted explicitly.
            if (queue.Count >= capacity) { Interlocked.Increment(ref dropped); return; }
            queue.Enqueue(line.Length <= 16384 ? line : line[..16384] + " [truncated]");
        }
    }

    public string[] Drain(int max = 512)
    {
        lock (sync)
        {
            var lines = new List<string>();
            while (lines.Count < max && queue.TryDequeue(out var line)) lines.Add(line);
            return lines.ToArray();
        }
    }
}
