namespace NetCat.Core;
// Only consecutive, recent samples count. A successful current profile never
// gets replaced merely because another server has a smaller latency.
public sealed class FailoverPolicy
{
    private readonly Dictionary<Guid, Queue<(DateTimeOffset At, DelayResult Result)>> samples = [];
    private DateTimeOffset lastSwitch = DateTimeOffset.MinValue;
    private long session=-1;
    public void ObserveSession(long revision) {if(session==revision)return;session=revision;samples.Clear();}
    public void Record(Guid id, DelayResult result, DateTimeOffset now, bool activeConnection = true)
    {
        if(!activeConnection)return;
        if (!samples.TryGetValue(id, out var history)) samples[id] = history = new();
        history.Enqueue((now,result)); while (history.Count > 20) history.Dequeue();
    }
    public bool ShouldRecover(Guid active, int failures, DateTimeOffset now, TimeSpan freshness)
    {
        if (now-lastSwitch < TimeSpan.FromMinutes(2) || !samples.TryGetValue(active,out var history)) return false;
        var recent=history.Where(s=>now-s.At<=freshness).Reverse().ToArray();
        return recent.Take(Math.Max(2,failures)).Count(s=>!s.Result.Success) == Math.Max(2,failures);
    }
    public Guid? Best(IEnumerable<Guid> candidates, DateTimeOffset now, TimeSpan freshness) => candidates
        .Where(id=>samples.TryGetValue(id,out var h) && h.Count>=2 && h.Reverse().Take(2).All(s=>s.Result.Success && now-s.At<=freshness))
        .OrderBy(id=>samples[id].Where(s=>s.Result.Success && now-s.At<=freshness).Select(s=>s.Result.Milliseconds).Order().ElementAt(samples[id].Count(s=>s.Result.Success && now-s.At<=freshness)/2))
        .Select(id=>(Guid?)id).FirstOrDefault();
    public void Switched(DateTimeOffset now) { lastSwitch=now; samples.Clear(); }
    public void Reset() => samples.Clear();
}
