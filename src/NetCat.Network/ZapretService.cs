using System.Diagnostics;
using System.Net;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Network;
public sealed class StrategyResult : System.ComponentModel.INotifyPropertyChanged
{
    public string File { get; set; } = "";
    public string Name => Path.GetFileNameWithoutExtension(File);
    private string youtube = "Не проверен", discord = "Не проверен";
    private bool active, primary, passed;
    private string details = "Проверка ещё не выполнялась.";
    public string Details { get => details; set { details=value; Changed(nameof(Details)); } }
    public void RestoreHistory(AppSettings settings)
    {
        var history=settings.ZapretResults.Where(r=>r.File==Path.GetFileName(File)).OrderByDescending(r=>r.At).ToArray();
        Details=history.Length==0 ? "Проверка ещё не выполнялась." : string.Join("\n\n",history.Select(r=>r.Details));
        var current=history.FirstOrDefault(r=>r.Scenario==settings.Scenario);
        if(current==null) { YouTube=Discord="Не проверен для этого сценария"; Passed=false; return; }
        YouTube=current.YouTube; Discord=current.Discord; Passed=current.Passed; Score=current.Score; Delay=current.Delay; Scenario=current.Scenario;
    }
    public bool IsPrimary { get => primary; set { primary = value; Changed(nameof(IsPrimary)); } }
    public bool Passed { get => passed; set { passed = value; Changed(nameof(Passed)); } }
    public string YouTube { get => youtube; set { youtube=value; Changed(nameof(YouTube)); } }
    public string Discord { get => discord; set { discord=value; Changed(nameof(Discord)); } }
    public bool IsActive { get => active; set { if(active==value)return; active=value; Changed(nameof(IsActive)); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this,new(name));
    public int Score { get; set; }
    public int Delay { get; set; } = int.MaxValue;
    public string Scenario { get; set; } = "";
    public override string ToString() => Name;
}
public class ZapretService(string bin, string runtime) : IDisposable
{
    private readonly ProcessHost process = new();
    private readonly SemaphoreSlim gate = new(1);
    private CancellationTokenSource? testCancellation;
    private CancellationTokenSource? startCancellation;
    private readonly CancellationTokenSource lifetime = new();
    private bool stopRequested, disposed, logsAttached;
    private long intent;
    private Semaphore? instanceLease;
    protected virtual string LeaseName => "Local\\NetCat.Zapret.Owner";
    protected virtual void CheckOtherInstances()
    {
        foreach(var other in Process.GetProcessesByName("winws"))
            using(other) if(other.Id!=ProcessId && !other.HasExited) throw new InvalidOperationException($"Уже запущен другой Zapret (PID {other.Id}). Остановите старую копию перед включением Zapret в NetCat.");
    }
    private void AcquireLease()
    {
        if(instanceLease!=null)return;
        var lease=new Semaphore(1,1,LeaseName);
        if(!lease.WaitOne(0)) {lease.Dispose();throw new InvalidOperationException("Zapret уже управляется другой копией NetCat.");}
        instanceLease=lease;
    }
    private void ReleaseLease() {var lease=Interlocked.Exchange(ref instanceLease,null);if(lease==null)return;try{lease.Release();}finally{lease.Dispose();}}
    public int ProcessId => process.Id;
    private static void Cancel(CancellationTokenSource? source) { try { source?.Cancel(); } catch(ObjectDisposedException) { } }
    public virtual bool Running => process.Running;
    public string ActiveStrategy { get; protected set; } = "";
    public string ActiveScenario { get; protected set; } = "";
    public event Action<string>? Log;
    public List<StrategyResult> Strategies() => Directory.Exists(Path.Combine(bin, "zapret")) ? Directory.GetFiles(Path.Combine(bin, "zapret"), "general*.bat").OrderBy(f => System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(f), "[0-9]+", m => m.Value.PadLeft(5, '0'))).Select(f => new StrategyResult { File = f }).ToList() : [];
    public async Task ApplyAsync(AppSettings settings, string file, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var revision=Interlocked.Increment(ref intent); Cancel(testCancellation);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        await gate.WaitAsync(linked.Token);
        try { if(revision!=Interlocked.Read(ref intent)) throw new OperationCanceledException("Запуск Zapret отменён."); startCancellation=linked; await StartInternal(settings, file, linked.Token); }
        finally { startCancellation=null; gate.Release(); }
    }
    protected virtual async Task StartInternal(AppSettings s, string file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed,this);
        if (!logsAttached) { process.Line += line => Log?.Invoke("winws: " + ProcessHost.Redact(line)); logsAttached = true; }
        if (!s.YouTube.Equals(ServiceRoute.Zapret) && !s.Discord.Equals(ServiceRoute.Zapret)) { await process.StopAsync(); ReleaseLease(); ActiveStrategy = ActiveScenario = ""; return; }
        var root = Path.Combine(bin, "zapret");
        if (!Path.GetFullPath(file).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Стратегия должна находиться в комплекте Flowseal.");
        Directory.CreateDirectory(runtime); var hosts = Path.Combine(runtime, "zapret-hosts.txt");
        await File.WriteAllLinesAsync(hosts, (s.YouTube == ServiceRoute.Zapret ? ServiceDomains.YouTube : []).Concat(s.Discord == ServiceRoute.Zapret ? ServiceDomains.Discord : []), ct);
        var physical = PhysicalNetwork.Capture(s.PhysicalInterface);
        var args = ZapretArguments.Build(file, root, hosts, s.YouTube == ServiceRoute.Zapret, s.Discord == ServiceRoute.Zapret, physical.Index);
        await process.StopAsync();
        ActiveStrategy=ActiveScenario="";
        try
        {
            ct.ThrowIfCancellationRequested();
            AcquireLease(); CheckOtherInstances();
            process.Start(Path.Combine(root, "bin", "winws.exe"), args, Path.Combine(root, "bin"));
            await Task.Delay(600, ct);
            if (!process.Running) throw new IOException("Zapret завершился при запуске: " + process.LastOutput);
            ActiveScenario = s.Scenario; ActiveStrategy = Path.GetFileName(file); Log?.Invoke($"Zapret: {ActiveStrategy}; {s.Scenario}; адаптер: {physical.Name} ({physical.Index})");
        }
        catch { await process.StopAsync(); ReleaseLease(); ActiveStrategy=ActiveScenario=""; throw; }
    }
    public async Task<StrategyResult?> TestAsync(AppSettings settings, IEnumerable<StrategyResult> candidates, IProgress<StrategyResult> progress, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var revision=Interlocked.Increment(ref intent);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token); ct=linked.Token;
        await gate.WaitAsync(ct);
        if(revision!=Interlocked.Read(ref intent)) {gate.Release(); throw new OperationCanceledException("Тест Zapret отменён.");}
        testCancellation = linked; stopRequested = false;
        var old = ActiveStrategy; var wasRunning = Running; var s = JsonSettings.Clone(settings); StrategyResult? best = null; StrategyResult? current = null;
        try
        {
            var snapshot = PhysicalNetwork.Capture(s.PhysicalInterface); var port = OpenVpnService.FreePort();
            var conf = SingBoxConfig.Build(new AppSettings { Fallback = RouteTarget.Direct, DirectDns = s.DirectDns }, snapshot, null, null, false, port);
            conf["route"]!["rules"] = new System.Text.Json.Nodes.JsonArray();
            Directory.CreateDirectory(runtime); var configPath = Path.Combine(runtime, "zapret-test.json");
            await File.WriteAllTextAsync(configPath, conf.ToJsonString(JsonSettings.Options), ct);
            using var test = new ProcessHost(); test.Start(Path.Combine(bin, "sing-box", "sing-box.exe"), ["run", "-c", configPath]); await RouterService.WaitPortAsync(port, test, ct);
            using var client = CreateTestClient(port,s.TestTimeoutSeconds);
            foreach (var candidate in candidates)
            {
                current = candidate;
                ct.ThrowIfCancellationRequested(); candidate.Score = 0; candidate.Delay = 0; candidate.Scenario = s.Scenario; candidate.Passed = false;
                candidate.YouTube = s.YouTube == ServiceRoute.Zapret ? "Проверяется…" : "Через VPN";
                candidate.Discord = s.Discord == ServiceRoute.Zapret ? "Проверяется…" : "Через VPN";
                var detail = new System.Text.StringBuilder($"{DateTime.Now:dd.MM HH:mm:ss} · YouTube → {RussianLabels.Of(s.YouTube)}, Discord → {RussianLabels.Of(s.Discord)}\n");
                progress.Report(candidate);
                try
                {
                    await StartInternal(s, candidate.File, ct);
                    var probes=ZapretProbes.Defaults.Where(p=>p.Service=="YouTube"?s.YouTube==ServiceRoute.Zapret:s.Discord==ServiceRoute.Zapret);
                    var outcomes=await ZapretProbes.RunAsync(client,probes,ct,(uri,token)=>ZapretProbes.GatewayHelloAsync(uri,port,s.TestTimeoutSeconds,token));
                    candidate.YouTube=ZapretProbes.Summary(outcomes,"YouTube");candidate.Discord=ZapretProbes.Summary(outcomes,"Discord");
                    foreach(var outcome in outcomes)detail.AppendLine(outcome.Service+" · "+outcome.Name+": "+outcome.Detail);
                    detail.AppendLine("YouTube playback: не проверялось. Discord Voice UDP: не проверялось.");
                    candidate.Score=outcomes.Count(p=>p.Success);candidate.Delay=outcomes.Where(p=>p.Success).Sum(p=>p.Milliseconds);
                    candidate.Passed=outcomes.Count>0 && outcomes.All(p=>p.Success);
                    if (candidate.Score > 0 && (best == null || candidate.Score > best.Score || candidate.Score == best.Score && candidate.Delay < best.Delay)) best = candidate;
                }
                catch (Exception e) when (e is not OperationCanceledException) { candidate.YouTube = "Ошибка запуска"; candidate.Discord = "Ошибка запуска"; detail.AppendLine(ProcessHost.Redact(e.ToString())); Log?.Invoke(ProcessHost.Redact(e.Message)); }
                detail.AppendLine($"YouTube: {candidate.YouTube}\nDiscord: {candidate.Discord}");
                settings.ZapretResults.RemoveAll(r=>r.File==Path.GetFileName(candidate.File) && r.Scenario==s.Scenario);
                settings.ZapretResults.Add(new(Path.GetFileName(candidate.File),s.Scenario,DateTimeOffset.Now,candidate.YouTube,candidate.Discord,candidate.Passed,candidate.Score,candidate.Delay,detail.ToString()));
                candidate.Details=string.Join("\n\n",settings.ZapretResults.Where(r=>r.File==Path.GetFileName(candidate.File)).OrderByDescending(r=>r.At).Select(r=>r.Details));
                progress.Report(candidate);
            }
            if (best?.Passed != true) best = null;
            if (best != null) settings.BestZapretByScenario[s.Scenario] = Path.GetFileName(best.File);
            return best;
        }
        finally
        {
            try
            {
                if (ct.IsCancellationRequested && current != null) { if(current.YouTube == "Проверяется…") current.YouTube = "Отменён"; if(current.Discord == "Проверяется…") current.Discord = "Отменён"; progress.Report(current); }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                if (!disposed && revision==Interlocked.Read(ref intent) && !stopRequested && !ct.IsCancellationRequested && s.ApplyBestZapret && best?.Passed == true) { await StartInternal(s, best.File, cleanup.Token); settings.ZapretStrategy = Path.GetFileName(best.File); }
                else if (!disposed && revision==Interlocked.Read(ref intent) && !stopRequested && wasRunning && old.Length > 0) await StartInternal(s, Path.Combine(bin, "zapret", old), cleanup.Token);
                else { await process.StopAsync(); ReleaseLease(); ActiveStrategy = ActiveScenario = ""; }
            }
            finally { testCancellation = null; gate.Release(); }
        }
    }
    public virtual async Task StopAsync() { Interlocked.Increment(ref intent); stopRequested = true; Cancel(startCancellation); Cancel(testCancellation); await gate.WaitAsync(); try { await process.StopAsync(); ReleaseLease(); ActiveStrategy = ActiveScenario = ""; } finally { gate.Release(); } }
    protected virtual HttpClient CreateTestClient(int port,int timeout) => new(new SocketsHttpHandler { Proxy=new WebProxy($"socks5://127.0.0.1:{port}"),UseProxy=true,PooledConnectionLifetime=TimeSpan.Zero }) { Timeout=TimeSpan.FromSeconds(timeout) };
    public void Dispose() { disposed=true; Interlocked.Increment(ref intent); stopRequested=true; Cancel(lifetime); Cancel(startCancellation); Cancel(testCancellation); process.Dispose(); ReleaseLease(); ActiveStrategy=ActiveScenario=""; }
}
