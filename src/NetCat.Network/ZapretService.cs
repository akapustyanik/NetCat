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
    private bool active, primary, passed, fresh;
    private string testDepth = "Не проверено";
    private string details = "Проверка ещё не выполнялась.";
    public string Details { get => details; set { details=value; Changed(nameof(Details)); } }
    public void RestoreHistory(AppSettings settings)
    {
        var history=settings.ZapretResults.Where(r=>r.File==Path.GetFileName(File)).OrderByDescending(r=>r.At).ToArray();
        Details=history.Length==0 ? "Проверка ещё не выполнялась." : string.Join("\n\n",history.Select(r=>r.Details));
        var current=history.FirstOrDefault(r=>r.Scenario==settings.Scenario);
        if(current==null) { YouTube=Discord="Не проверен для этого сценария"; Passed=false; Fresh=false; TestDepth="Не проверено"; return; }
        YouTube=current.YouTube; Discord=current.Discord;
        Passed=current.Passed; Fresh=false; TestDepth="Ранее проверено · Cached"; Score=current.Score; Delay=current.Delay; Scenario=current.Scenario;
    }
    public bool IsPrimary { get => primary; set { primary = value; Changed(nameof(IsPrimary)); } }
    public bool Passed { get => passed; set { passed = value; Fresh=value; Changed(nameof(Passed)); } }
    public bool Fresh { get => fresh; set { fresh=value; Changed(nameof(Fresh)); } }
    public string TestDepth { get => testDepth; set { testDepth=value; Changed(nameof(TestDepth)); } }
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
public sealed record ZapretProcessInfo(int Id, string? ExecutablePath, int SessionId, DateTimeOffset StartTime, bool HasExited, string? CommandFingerprint = null);

public sealed record ZapretOwnerRecord(
    int Pid,
    DateTimeOffset ProcessStartTime,
    int SessionId,
    string ExecutablePath,
    string CommandFingerprint,
    string OwnerInstanceId
);

public class ZapretService : IDisposable, IZapretRuntime
{
    private readonly string bin;
    private readonly string runtime;
    private readonly ProcessHost process;
    private readonly IExecutableTrustPolicy? trustPolicy;
    private readonly string ownerInstanceId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim gate = new(1);
    private readonly object lifecycleGate = new();
    private CancellationTokenSource? testCancellation;
    private CancellationTokenSource? startCancellation;
    private readonly CancellationTokenSource lifetime = new();
    private bool stopRequested, disposed, logsAttached;
    private long intent;
    private Semaphore? instanceLease;
    public event Action<int, int>? ProcessExited;
    public ZapretObservedState? ObservedState { get; protected set; }
    public Func<IEnumerable<ZapretProcessInfo>>? EnumerateProcessesOverride { get; set; }
    public Action<int>? KillProcessOverride { get; set; }
    public Func<string>? OwnerFileOverride { get; set; }
    public Func<TimeSpan, CancellationToken, Task>? WaitForZapretReadyOverride { get; set; }
    protected virtual string LeaseName => "Local\\NetCat.Zapret.Owner";

    public ZapretService(string bin, string runtime,IExecutableTrustPolicy? trustPolicy=null)
    {
        this.trustPolicy=trustPolicy;
        process=new ProcessHost(trustPolicy);
        this.bin = bin;
        this.runtime = runtime;
        process.Exited += (pid, code) =>
        {
            var lastObs = ObservedState;
            ObservedState = new ZapretObservedState(
                IsRunning: false,
                IsReady: false,
                Owned: false,
                ProcessId: pid,
                ProcessStartTime: lastObs?.ProcessStartTime ?? DateTimeOffset.MinValue,
                BoundPhysicalInterfaceIndex: lastObs?.BoundPhysicalInterfaceIndex ?? 0,
                ConfigFingerprint: lastObs?.ConfigFingerprint ?? "",
                LastExitReason: $"exitCode={code}"
            );
            Log?.Invoke($"ZAPRET_PROCESS state=exited pid={pid} exitCode={code} desired=on");
            ProcessExited?.Invoke(pid, code);
        };
    }

    public static bool IsProcessOwned(ZapretProcessInfo process, ZapretOwnerRecord? ownerRecord, string bundledWinws, int currentSessionId)
    {
        if (process.HasExited || ownerRecord == null || process.StartTime == DateTimeOffset.MinValue ||
            ownerRecord.ProcessStartTime == DateTimeOffset.MinValue || process.Id != ownerRecord.Pid ||
            process.StartTime.UtcTicks != ownerRecord.ProcessStartTime.UtcTicks ||
            currentSessionId < 0 || process.SessionId != currentSessionId || ownerRecord.SessionId != currentSessionId ||
            string.IsNullOrWhiteSpace(ownerRecord.OwnerInstanceId) || string.IsNullOrWhiteSpace(process.ExecutablePath) ||
            string.IsNullOrWhiteSpace(ownerRecord.ExecutablePath) || string.IsNullOrWhiteSpace(ownerRecord.CommandFingerprint) ||
            string.IsNullOrWhiteSpace(process.CommandFingerprint) ||
            process.CommandFingerprint != ownerRecord.CommandFingerprint)
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(process.ExecutablePath), Path.GetFullPath(ownerRecord.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public string GetOwnerFilePath()
    {
        if (OwnerFileOverride != null) return OwnerFileOverride();
        return Path.Combine(runtime, "owner.json");
    }

    public void WriteOwnerRecord(int pid, DateTimeOffset startTime, int sessionId, string exePath, string commandFingerprint)
    {
        try
        {
            var record = new ZapretOwnerRecord(pid, startTime, sessionId, exePath, commandFingerprint, ownerInstanceId);
            var path = GetOwnerFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(record, JsonSettings.Options));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Не удалось записать owner.json: {ex.Message}");
        }
    }

    public void DeleteOwnerRecord()
    {
        try
        {
            var path = GetOwnerFilePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public ZapretOwnerRecord? ReadOwnerRecord()
    {
        try
        {
            var path = GetOwnerFilePath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return System.Text.Json.JsonSerializer.Deserialize<ZapretOwnerRecord>(json, JsonSettings.Options);
            }
        }
        catch { }
        return null;
    }

    public void RunCheckOtherInstances() => CheckOtherInstances();

    protected virtual void CheckOtherInstances()
    {
        var currentPid = Environment.ProcessId;
        int currentSessionId;
        try { currentSessionId = Process.GetCurrentProcess().SessionId; }
        catch { currentSessionId = -1; }
        var bundledWinws = Path.GetFullPath(Path.Combine(bin, "zapret", "bin", "winws.exe"));
        var ownerRecord = ReadOwnerRecord();

        IEnumerable<ZapretProcessInfo> processes;
        if (EnumerateProcessesOverride != null)
        {
            processes = EnumerateProcessesOverride();
        }
        else
        {
            processes = Process.GetProcessesByName("winws").Select(p =>
            {
                string? pPath = null;
                try { pPath = p.MainModule?.FileName; } catch { }
                int pSession = -1;
                try { pSession = p.SessionId; } catch { }
                DateTimeOffset pStart = DateTimeOffset.MinValue;
                try { pStart = p.StartTime.ToUniversalTime(); } catch { }
                bool pExited = false;
                try { pExited = p.HasExited; } catch { pExited = true; }
                var info = new ZapretProcessInfo(p.Id, pPath, pSession, pStart, pExited, ProcessIdentity.CommandFingerprint(p));
                p.Dispose();
                return info;
            }).ToList();
        }

        foreach (var other in processes)
        {
            if (other.Id == ProcessId || other.Id == currentPid || other.HasExited)
                continue;

            if (other.StartTime == DateTimeOffset.MinValue)
            {
                var path = string.IsNullOrEmpty(other.ExecutablePath) ? "путь недоступен" : other.ExecutablePath;
                throw new InvalidOperationException(
                    $"Уже запущен сторонний или недоступный Zapret (PID {other.Id}, сессия {other.SessionId}, {path}). " +
                    "Время запуска не может быть проверено; остановите процесс вручную.");
            }

            bool isNetCatOwned = IsProcessOwned(other, ownerRecord, bundledWinws, currentSessionId);

            if (isNetCatOwned)
            {
                try
                {
                    Log?.Invoke($"Завершение осиротевшего процесса NetCat winws.exe (PID {other.Id})...");
                    if (KillProcessOverride != null)
                    {
                        KillProcessOverride(other.Id);
                    }
                    else
                    {
                        using var proc = Process.GetProcessById(other.Id);
                        // Recheck against the same opened handle immediately before termination.
                        var fresh = new ZapretProcessInfo(proc.Id, proc.MainModule?.FileName, proc.SessionId,
                            proc.StartTime.ToUniversalTime(), proc.HasExited, ProcessIdentity.CommandFingerprint(proc));
                        if (!IsProcessOwned(fresh, ownerRecord, bundledWinws, currentSessionId))
                            throw new InvalidOperationException("Идентичность процесса изменилась; завершение отменено.");
                        proc.Kill();
                        if (!proc.WaitForExit(2000)) throw new IOException("Процесс Zapret не завершился.");
                    }
                    DeleteOwnerRecord();
                    continue;
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Не удалось завершить осиротевший winws.exe (PID {other.Id}): {ex.Message}");
                }
            }

            var pathInfo = string.IsNullOrEmpty(other.ExecutablePath) ? "путь недоступен" : other.ExecutablePath;
            throw new InvalidOperationException(
                $"Уже запущен сторонний или недоступный Zapret (PID {other.Id}, сессия {other.SessionId}, {pathInfo}). " +
                "Остановите старую копию перед включением Zapret в NetCat.");
        }
    }

    public virtual async Task WaitForZapretReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnLine(string line)
        {
            if (line.Contains("windivert initialized. capture is started.", StringComparison.OrdinalIgnoreCase))
            {
                tcs.TrySetResult(true);
            }
        }

        process.Line += OnLine;

        if (process.LastOutput.Contains("windivert initialized. capture is started.", StringComparison.OrdinalIgnoreCase))
        {
            tcs.TrySetResult(true);
        }

        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (!process.Running)
                {
                    throw new IOException("Zapret завершился до инициализации перехвата: " + process.LastOutput);
                }

                if (tcs.Task.IsCompleted)
                {
                    await tcs.Task;
                    return;
                }

                await Task.WhenAny(tcs.Task, Task.Delay(50, ct)).ConfigureAwait(false);
                if (tcs.Task.IsCompleted)
                {
                    await tcs.Task;
                    return;
                }
            }

            if (!process.Running)
            {
                throw new IOException("Zapret завершился при запуске: " + process.LastOutput);
            }

            throw new TimeoutException($"Zapret не подтвердил инициализацию WinDivert в течение {timeout.TotalSeconds}с.");
        }
        finally
        {
            process.Line -= OnLine;
        }
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
        FunctionalValidation = null;
        ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed,this);
        if (!logsAttached) { process.Line += line => Log?.Invoke("winws: " + ProcessHost.Redact(line)); logsAttached = true; }
        if (!s.YouTube.Equals(ServiceRoute.Zapret) && !s.Discord.Equals(ServiceRoute.Zapret)) { await process.StopAsync(); ReleaseLease(); ActiveStrategy = ActiveScenario = ""; return; }
        var root = Path.Combine(bin, "zapret");
        using var trustedStrategy = trustPolicy?.AcquirePackage("zapret",root) ?? ReviewedRuntimeTrust.Acquire("zapret", root);
        if (!Path.GetFullPath(file).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Стратегия должна находиться в комплекте Flowseal.");
        Directory.CreateDirectory(runtime); var hosts = Path.Combine(runtime, "zapret-hosts.txt");
        await File.WriteAllLinesAsync(hosts, (s.YouTube == ServiceRoute.Zapret ? ServiceDomains.YouTube : []).Concat(s.Discord == ServiceRoute.Zapret ? ServiceDomains.Discord : []), ct);
        var physical = PhysicalNetwork.Capture(s.PhysicalInterface);
        var args = ZapretArguments.Build(file, root, hosts, s.YouTube == ServiceRoute.Zapret, s.Discord == ServiceRoute.Zapret, physical.Index);
        await process.StopAsync();
        ActiveStrategy=ActiveScenario="";
        try
        {
            lock(lifecycleGate)
            {
                ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed,this);
                AcquireLease(); CheckOtherInstances();
                process.Start(Path.Combine(root, "bin", "winws.exe"), args, Path.Combine(root, "bin"));
            }
            if (WaitForZapretReadyOverride != null)
            {
                await WaitForZapretReadyOverride(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            else
            {
                await WaitForZapretReadyAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            lock(lifecycleGate)
            {
            ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed,this);
            var pid = process.Id;
            if (!process.Running || pid == 0) throw new IOException("Zapret завершился при запуске: " + process.LastOutput);
            DateTimeOffset pStart = DateTimeOffset.MinValue;
            try
            {
                using var p = Process.GetProcessById(pid);
                pStart = p.StartTime.ToUniversalTime();
                var identity = ProcessIdentity.CommandFingerprint(p);
                if (identity != null) WriteOwnerRecord(pid, pStart, p.SessionId, Path.Combine(root, "bin", "winws.exe"), identity);
            }
            catch { }
            // Ownership publication may invoke a callback; cancellation must still
            // win before publishing an active strategy/observed state.
            ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed,this);
            if (!process.Running || process.Id != pid) throw new IOException("Zapret завершился при запуске: " + process.LastOutput);
            ActiveScenario = s.Scenario; ActiveStrategy = Path.GetFileName(file);
            var activeSettings=JsonSettings.Clone(s);
            activeSettings.ZapretStrategy=ActiveStrategy;
            ObservedState = new ZapretObservedState(
                IsRunning: true,
                IsReady: true,
                Owned: true,
                ProcessId: pid,
                ProcessStartTime: pStart,
                BoundPhysicalInterfaceIndex: physical.Index,
                ConfigFingerprint: DesiredFingerprint(activeSettings, physical),
                LastExitReason: null
            );
            }
            Log?.Invoke($"ZAPRET_PROCESS state=started pid={process.Id} ifIndex={physical.Index} fingerprint={ActiveStrategy}");
            Log?.Invoke($"Zapret: {ActiveStrategy}; {s.Scenario}; адаптер: {physical.Name} ({physical.Index})");
        }
        catch
        {
            await process.StopAsync(); DeleteOwnerRecord(); ReleaseLease(); ActiveStrategy=ActiveScenario="";
            ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed,this);
            throw;
        }
    }
    public async Task<StrategyResult?> TestAsync(AppSettings settings, IEnumerable<StrategyResult> candidates, IProgress<StrategyResult> progress, CancellationToken ct, Action<string>? statusCallback = null, TimeSpan? globalDeadline = null, bool stopAfterFirstAccepted = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var testId = Guid.NewGuid().ToString("N")[..8];
        var testWatch = Stopwatch.StartNew();
        var revision = Interlocked.Increment(ref intent);
        var candidateRows = candidates.ToArray();
        var s = JsonSettings.Clone(settings);
        var budget = globalDeadline ?? TimeSpan.FromSeconds(Math.Clamp(candidateRows.Length * (s.TestTimeoutSeconds + 4) + 30, 45, 180));
        using var globalCts = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token, globalCts.Token);
        var testToken = linked.Token;

        Log?.Invoke($"ZAPRET_TEST_BEGIN testId={testId} candidates={candidateRows.Length} scenario={s.Scenario} budgetSeconds={budget.TotalSeconds:F0}");
        statusCallback?.Invoke("Подготовка к проверке Zapret…");

        bool lockAcquired = false;
        var old = ActiveStrategy; var wasRunning = Running; StrategyResult? best = null; StrategyResult? current = null;
        try
        {
            Log?.Invoke($"ZAPRET_TEST_WAIT_LOCK_BEGIN testId={testId}");
            await gate.WaitAsync(testToken);
            lockAcquired = true;
            Log?.Invoke($"ZAPRET_TEST_WAIT_LOCK_END testId={testId} elapsedMs={testWatch.ElapsedMilliseconds}");

            if (revision != Interlocked.Read(ref intent))
            {
                Log?.Invoke($"ZAPRET_TEST_FAILED testId={testId} elapsedMs={testWatch.ElapsedMilliseconds} reason=superseded");
                throw new OperationCanceledException("Тест Zapret отменён.");
            }
            testCancellation = linked; stopRequested = false;
            var snapshot = PhysicalNetwork.Capture(s.PhysicalInterface); var port = OpenVpnService.FreePort();
            var conf = ZapretProbes.BuildPhysicalConfig(s, snapshot, port);
            Directory.CreateDirectory(runtime); var configPath = Path.Combine(runtime, "zapret-test.json");
            await File.WriteAllTextAsync(configPath, conf.ToJsonString(JsonSettings.Options), testToken);
            using var test = new ProcessHost(); test.Start(Path.Combine(bin, "sing-box", "sing-box.exe"), ["run", "-c", configPath]); await RouterService.WaitPortAsync(port, test, testToken);
            using var client = CreateTestClient(port, s.TestTimeoutSeconds);
            var probes = ZapretProbes.Defaults.Where(p => p.Service == "YouTube" ? s.YouTube == ServiceRoute.Zapret : s.Discord == ServiceRoute.Zapret).ToArray();
            // Baseline uses exactly the same explicitly physical-bound proxy as
            // enabled probes. Existing main VPN routing cannot satisfy this test.
            await process.StopAsync(); ReleaseLease(); ActiveStrategy = ActiveScenario = "";
            current = candidateRows.FirstOrDefault();
            if (current != null) { current.YouTube = s.YouTube == ServiceRoute.Zapret ? "Проверяется…" : "Через VPN"; current.Discord = s.Discord == ServiceRoute.Zapret ? "Проверяется…" : "Через VPN"; progress.Report(current); }

            statusCallback?.Invoke("Проверка базового соединения…");
            Log?.Invoke($"ZAPRET_TEST_BASELINE_BEGIN testId={testId} timeoutSec={s.TestTimeoutSeconds} elapsedMs={testWatch.ElapsedMilliseconds}");
            var baseline = await ZapretProbes.RunAsync(client, probes, testToken, (uri, token) => ZapretProbes.GatewayHelloAsync(uri, port, s.TestTimeoutSeconds, token));
            Log?.Invoke($"ZAPRET_TEST_BASELINE_END testId={testId} elapsedMs={testWatch.ElapsedMilliseconds} outcomes={baseline.Count}");
            foreach (var outcome in baseline) Log?.Invoke(ZapretStrategyValidation.Diagnostic("baseline", snapshot.Index, outcome, outcome));

            for (int i = 0; i < candidateRows.Length; i++)
            {
                var candidate = candidateRows[i];
                current = candidate;
                if (!ZapretProbes.SamePhysicalPath(snapshot, PhysicalNetwork.TryCapture(s.PhysicalInterface)))
                { best = null; throw new IOException("Физическое подключение изменилось. Повторите тест Zapret для нового подключения."); }
                testToken.ThrowIfCancellationRequested();
                var strategyAlias = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(candidate.File))))[..12];
                Log?.Invoke($"ZAPRET_TEST_STRATEGY_BEGIN testId={testId} strategy={strategyAlias} alias={Path.GetFileName(candidate.File)} index={i + 1}/{candidateRows.Length} elapsedMs={testWatch.ElapsedMilliseconds}");
                statusCallback?.Invoke($"Стратегия {i + 1}/{candidateRows.Length}: {candidate.Name}…");

                candidate.Score = 0; candidate.Delay = 0; candidate.Scenario = s.Scenario; candidate.Passed = false;
                candidate.TestDepth = "Full · HTTPS/Gateway/WebSocket; Voice UDP не проверяется";
                candidate.YouTube = s.YouTube == ServiceRoute.Zapret ? "Проверяется…" : "Через VPN";
                candidate.Discord = s.Discord == ServiceRoute.Zapret ? "Проверяется…" : "Через VPN";
                var detail = new System.Text.StringBuilder($"{DateTime.Now:dd.MM HH:mm:ss} · YouTube → {RussianLabels.Of(s.YouTube)}, Discord → {RussianLabels.Of(s.Discord)}\n");
                progress.Report(candidate);
                try
                {
                    await StartInternal(s, candidate.File, testToken);
                    if (s.YouTube == ServiceRoute.Zapret) statusCallback?.Invoke($"Стратегия {i + 1}/{candidateRows.Length}: {candidate.Name} · Проверка YouTube…");
                    else if (s.Discord == ServiceRoute.Zapret) statusCallback?.Invoke($"Стратегия {i + 1}/{candidateRows.Length}: {candidate.Name} · Проверка Discord…");

                    var outcomes = await ZapretProbes.RunAsync(client, probes, testToken, (uri, token) => ZapretProbes.GatewayHelloAsync(uri, port, s.TestTimeoutSeconds, token));
                    if (!ZapretProbes.SamePhysicalPath(snapshot, PhysicalNetwork.TryCapture(s.PhysicalInterface)))
                    { best = null; throw new IOException("Физическое подключение изменилось во время проверки Zapret."); }
                    var validation = ZapretStrategyValidation.Evaluate(baseline, outcomes, probes.Select(p => p.Service));
                    foreach (var outcome in outcomes) Log?.Invoke(ZapretStrategyValidation.Diagnostic(strategyAlias, snapshot.Index, baseline.FirstOrDefault(b => b.Service == outcome.Service && b.Name == outcome.Name), outcome));
                    Log?.Invoke($"ZAPRET_VALIDATION strategy={strategyAlias} accepted={validation.Accepted} reason={validation.Reason} improved={validation.ImprovedTargets} groups={validation.AvailableGroups}");
                    candidate.YouTube = ZapretProbes.Summary(outcomes, "YouTube"); candidate.Discord = ZapretProbes.Summary(outcomes, "Discord");
                    foreach (var outcome in outcomes) detail.AppendLine(outcome.Service + " · " + outcome.Name + ": " + outcome.Detail);
                    detail.AppendLine("YouTube playback: не проверялось. Discord Voice UDP: автоматически не проверяется.");
                    candidate.Score = outcomes.Count(p => p.Success); candidate.Delay = outcomes.Where(p => p.Success).Sum(p => p.Milliseconds);
                    candidate.Passed = validation.Accepted;
                    detail.AppendLine("Результат: " + validation.Reason + ". Доступность HTTPS не подтверждает воспроизведение видео или голос.");
                    Log?.Invoke($"ZAPRET_TEST_STRATEGY_END testId={testId} strategy={strategyAlias} elapsedMs={testWatch.ElapsedMilliseconds} passed={candidate.Passed}");

                    if (candidate.Passed)
                    {
                        if (best == null || !best.Passed || candidate.Score > best.Score ||
                            candidate.Score == best.Score && candidate.Delay < best.Delay) best = candidate;
                        Log?.Invoke($"ZAPRET_TEST_ACCEPTED testId={testId} strategy={strategyAlias} elapsedMs={testWatch.ElapsedMilliseconds} reason={validation.Reason} improved={validation.ImprovedTargets} groups={validation.AvailableGroups}");
                        statusCallback?.Invoke($"Найдена рабочая стратегия: {candidate.Name}");
                    }
                    if (!candidate.Passed && (best == null || !best.Passed &&
                        (candidate.Score > best.Score || candidate.Score == best.Score && candidate.Delay < best.Delay))) best = candidate;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    candidate.YouTube = "Ошибка запуска"; candidate.Discord = "Ошибка запуска"; detail.AppendLine(ProcessHost.Redact(e.ToString()));
                    Log?.Invoke(ProcessHost.Redact(e.Message));
                    Log?.Invoke($"ZAPRET_TEST_STRATEGY_END testId={testId} strategy={strategyAlias} elapsedMs={testWatch.ElapsedMilliseconds} passed=false error={e.GetType().Name}");
                }
                detail.AppendLine($"YouTube: {candidate.YouTube}\nDiscord: {candidate.Discord}");
                settings.ZapretResults.RemoveAll(r => r.File == Path.GetFileName(candidate.File) && r.Scenario == s.Scenario);
                settings.ZapretResults.Add(new(Path.GetFileName(candidate.File), s.Scenario, DateTimeOffset.Now, candidate.YouTube, candidate.Discord, candidate.Passed, candidate.Score, candidate.Delay, detail.ToString()));
                candidate.Details = string.Join("\n\n", settings.ZapretResults.Where(r => r.File == Path.GetFileName(candidate.File)).OrderByDescending(r => r.At).Select(r => r.Details));
                progress.Report(candidate);
                // Persist the accepted row before leaving Auto Select. The old break
                // skipped this history block and made a fresh PASS look inconsistent.
                if (candidate.Passed && stopAfterFirstAccepted) break;
            }
            if (best?.Passed != true) best = null;
            if (best != null) settings.BestZapretByScenario[s.Scenario] = Path.GetFileName(best.File);
            return best;
        }
        finally
        {
            try
            {
                // StopAsync cancels the test-owned linked CTS rather than the
                // caller's token. Treat that path as cancellation too, so UI
                // rows cannot remain "Проверяется…" after an explicit stop.
                bool wasCanceled = testToken.IsCancellationRequested || ct.IsCancellationRequested || globalCts.IsCancellationRequested;
                if (wasCanceled)
                {
                    Log?.Invoke($"ZAPRET_TEST_CANCEL_BEGIN testId={testId} elapsedMs={testWatch.ElapsedMilliseconds}");
                    if (globalCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        statusCallback?.Invoke("Превышено время проверки (таймаут)");
                        Log?.Invoke($"ZAPRET_TEST_FAILED testId={testId} elapsedMs={testWatch.ElapsedMilliseconds} reason=global-deadline-exceeded");
                    }
                    else
                    {
                        statusCallback?.Invoke("Проверка отменена");
                        Log?.Invoke($"ZAPRET_TEST_FAILED testId={testId} elapsedMs={testWatch.ElapsedMilliseconds} reason=canceled");
                    }
                    if (current != null)
                    {
                        if (current.YouTube == "Проверяется…") current.YouTube = "Отменён";
                        if (current.Discord == "Проверяется…") current.Discord = "Отменён";
                        progress.Report(current);
                    }
                }
                else if (best?.Passed == true)
                {
                    Log?.Invoke($"ZAPRET_TEST_COMPLETE testId={testId} elapsedMs={testWatch.ElapsedMilliseconds} best={best.Name} state=accepted");
                }
                else
                {
                    Log?.Invoke($"ZAPRET_TEST_COMPLETE testId={testId} elapsedMs={testWatch.ElapsedMilliseconds} best=none state=no-strategy-passed");
                }

                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                if (!disposed && revision == Interlocked.Read(ref intent) && !stopRequested && !wasCanceled && s.ApplyBestZapret && best?.Passed == true)
                {
                    await StartInternal(s, best.File, cleanup.Token);
                    FunctionalValidation = "HTTPS доступен; видео и голос не проверены";
                    settings.ZapretStrategy = Path.GetFileName(best.File);
                }
                else if (!disposed && revision == Interlocked.Read(ref intent) && !stopRequested && wasRunning && old.Length > 0)
                {
                    await StartInternal(s, Path.Combine(bin, "zapret", old), cleanup.Token);
                }
                else
                {
                    await process.StopAsync();
                    ReleaseLease();
                    ActiveStrategy = ActiveScenario = "";
                }
                if (wasCanceled)
                {
                    Log?.Invoke($"ZAPRET_TEST_CANCEL_END testId={testId} elapsedMs={testWatch.ElapsedMilliseconds}");
                }
                if (globalCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Превышен общий лимит времени проверки Zapret ({budget.TotalSeconds:F0}с).");
                }
            }
            finally
            {
                testCancellation = null;
                if (lockAcquired) gate.Release();
            }
        }
    }
    public bool IsRunning => Running;
    public string? FunctionalValidation { get; private set; }

    public bool ConfirmValidatedStrategy(AppSettings settings, string acceptedFile)
    {
        var physical=PhysicalNetwork.TryCapture(settings.PhysicalInterface);
        bool matches=Running && physical!=null && ObservedState is { IsReady:true, Owned:true } observed &&
            string.Equals(ActiveStrategy,Path.GetFileName(acceptedFile),StringComparison.OrdinalIgnoreCase) &&
            string.Equals(settings.ZapretStrategy,ActiveStrategy,StringComparison.OrdinalIgnoreCase) &&
            string.Equals(observed.ConfigFingerprint,DesiredFingerprint(settings,physical),StringComparison.Ordinal);
        FunctionalValidation=matches ? "обход подтверждён · HTTPS; видео и голос не проверены" : null;
        return matches;
    }

    public string? ConfigurationFingerprint(AppSettings settings, NetworkSnapshot physical) => DesiredFingerprint(settings, physical);
    private string DesiredFingerprint(AppSettings settings, NetworkSnapshot physical)
    {
        var file = settings.ZapretStrategy.Length > 0 ? Path.Combine(bin, "zapret", settings.ZapretStrategy) : Strategies().FirstOrDefault()?.File;
        var strategy = file != null && System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : "";
        var hosts = string.Join("\\n", (settings.YouTube == ServiceRoute.Zapret ? ServiceDomains.YouTube : [])
            .Concat(settings.Discord == ServiceRoute.Zapret ? ServiceDomains.Discord : []));
        var args = file != null && System.IO.File.Exists(file)
            ? ZapretArguments.Build(file, Path.Combine(bin, "zapret"), Path.Combine(runtime, "zapret-hosts.txt"),
                settings.YouTube == ServiceRoute.Zapret, settings.Discord == ServiceRoute.Zapret, physical.Index)
            : [];
        var dependencies = args.Where(a => a.Contains('=')).Select(a => a[(a.IndexOf('=') + 1)..])
            .Where(path => !path.Equals(Path.Combine(runtime, "zapret-hosts.txt"), StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(path)).Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => path + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(path))));
        var material = $"{file}|{strategy}|{settings.Scenario}|{physical.Index}|{hosts}|{string.Join(" ", args)}|{string.Join("|", dependencies)}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
    }

    public async Task EnsureRunningAsync(AppSettings settings, string? strategyFile, CancellationToken ct)
    {
        var file = strategyFile ?? (settings.ZapretStrategy.Length > 0 ? Path.Combine(bin, "zapret", settings.ZapretStrategy) : Strategies().FirstOrDefault()?.File);
        if (file == null) throw new InvalidOperationException("Нет доступных стратегий Zapret.");
        var physical = PhysicalNetwork.TryCapture(settings.PhysicalInterface);
        if (Running && physical != null && ObservedState is { IsReady: true, Owned: true } &&
            ObservedState.ConfigFingerprint == DesiredFingerprint(settings, physical))
            return;
        await ApplyAsync(settings, file, ct);
    }

    public async Task EnsureStoppedAsync(CancellationToken ct)
    {
        if (!Running) return;
        await StopAsync();
    }

    public virtual async Task StopAsync()
    {
        Interlocked.Increment(ref intent);
        stopRequested = true;
        Cancel(startCancellation);
        Cancel(testCancellation);
        await gate.WaitAsync();
        try
        {
            await process.StopAsync();
            DeleteOwnerRecord();
            ReleaseLease();
            ActiveStrategy = ActiveScenario = "";
            ObservedState = new ZapretObservedState(
                IsRunning: false,
                IsReady: false,
                Owned: false,
                ProcessId: 0,
                ProcessStartTime: DateTimeOffset.MinValue,
                BoundPhysicalInterfaceIndex: 0,
                ConfigFingerprint: "",
                LastExitReason: "StoppedNormally"
            );
        }
        finally
        {
            gate.Release();
        }
    }
    protected virtual HttpClient CreateTestClient(int port,int timeout) => new(new SocketsHttpHandler { Proxy=new WebProxy($"socks5://127.0.0.1:{port}"),UseProxy=true,PooledConnectionLifetime=TimeSpan.Zero }) { Timeout=TimeSpan.FromSeconds(timeout) };
    public void Dispose()
    {
        lock(lifecycleGate)
        {
            if(disposed)return;
            disposed=true; Interlocked.Increment(ref intent); stopRequested=true;
            Cancel(lifetime); Cancel(startCancellation); Cancel(testCancellation);
            process.Dispose(); DeleteOwnerRecord(); ReleaseLease(); ActiveStrategy=ActiveScenario="";
            ObservedState=new(false,false,false,0,DateTimeOffset.MinValue,0,"","Disposed");
        }
    }
}
