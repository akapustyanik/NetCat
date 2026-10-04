using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Network;

public sealed record OpenVpnPreflightResult(bool Success, bool LegacyUsed, string? Warning, string? Error);
public sealed class OpenVpnLegacyCryptoException(string message) : InvalidOperationException(message);

public sealed class OpenVpnService(string executable, string runtime) : IDisposable, IOpenVpnRuntime
{
    private readonly ProcessHost host = new();
    private OpenVpnDestinationLeases? destinationLeases;
    public OpenVpnDestinationLeases DestinationLeases
    {
        get { lock(candidateGate) return destinationLeases ??= new(Path.Combine(runtime,"openvpn-route.json"),
            ()=> (CaptureRouteTableOverride??RouteTable.CaptureIpv4)(),
            (script,ct)=>(PowerShellOverride??PhysicalNetwork.PowerShell)(script,ct)); }
    }
    private readonly HashSet<IPAddress> transportEndpoints=[];
    private OpenVpnCryptoScope? cryptoScope;
    public int NativeModuleRevision => ModuleIntegrity.GetModuleRevision("openvpn");
    public event Action<int, int>? ProcessExited { add => host.Exited += value; remove => host.Exited -= value; }
    private IDisposable? adapter;
    private readonly SemaphoreSlim transitionGate = new(1);
    private readonly SemaphoreSlim stopGate = new(1);
    private volatile OpenVpnRuntimePhase phase = OpenVpnRuntimePhase.NoProcess;
    private long reconnectStarted;
    private ITimer? reconnectWatchdog;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public TimeSpan LongReconnectThreshold { get; init; } = TimeSpan.FromSeconds(60);
    public OpenVpnRuntimePhase RuntimePhase => phase == OpenVpnRuntimePhase.Reconnecting &&
        TimeProvider.GetElapsedTime(Interlocked.Read(ref reconnectStarted)) >= LongReconnectThreshold ? OpenVpnRuntimePhase.LongReconnect : phase;
    public bool RequiresStop => host.Running || adapter != null || phase is not (OpenVpnRuntimePhase.NoProcess or OpenVpnRuntimePhase.FullyStopped) || File.Exists(Path.Combine(runtime,"openvpn-route.json"));
    private OpenVpnGenerationMonitor? monitor;
    private long dataPathEpoch;
    private long controlRevision;
    private OpenVpnGeneration? lastControlGeneration;
    public bool DataPathRecoveryRequired { get; private set; }
    public Func<OpenVpnLink,CancellationToken,Task<bool>> DataPathProbe { get; init; } = OpenVpnDataPathProbe.CheckAsync;
    public TimeSpan DataPathProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public Func<OpenVpnLink,OpenVpnAddressState> CaptureAddressState { get; init; } = OpenVpnAddressReadiness.Capture;
    public TimeSpan AddressReadyTimeout { get; init; } = OpenVpnAddressReadiness.DefaultTimeout;
    public Func<TimeSpan,CancellationToken,Task>? AddressReadinessDelay { get; init; }
    private readonly object readinessGate=new();
    private CancellationTokenSource? readinessLifetime;
    private void CancelAddressReadiness(){lock(readinessGate)readinessLifetime?.Cancel();}
    private Action? revalidateDataPath;
    public void PhysicalNetworkChanged()
    {
        destinationLeases?.Invalidate();
        lock (candidateGate) Interlocked.Increment(ref dataPathEpoch);
        CancelAddressReadiness();
        revalidateDataPath?.Invoke();
    }
    private Action<string>? parser;
    private CancellationTokenSource? processLifetime;
    private TaskCompletionSource<OpenVpnLink> readyLink = NewReady();
    private static TaskCompletionSource<OpenVpnLink> NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action? LinkChanged;
    public event Action<Guid, IReadOnlyList<string>>? CandidateRoutesPrepared;
    public event Action<Guid,long,IReadOnlyList<string>>? CandidateGenerationPrepared;
    public event Action<Guid,long>? CandidateGenerationInvalidated;
    private Action? clearPendingCandidate;
    private readonly object candidateGate = new();
    public int ProcessId => host.Running ? host.Id : 0;
    public bool Reconnecting => !DataPathRecoveryRequired && host.Running && Link == null && ActiveProfileId.HasValue && processLifetime?.IsCancellationRequested != true && !readyLink.Task.IsFaulted && !readyLink.Task.IsCanceled;
    public void CancelPendingConnection() { destinationLeases?.Invalidate(); CancelAddressReadiness(); if (!RequiresStop) return; lock (candidateGate) { phase = OpenVpnRuntimePhase.Stopping; processLifetime?.Cancel(); } monitor?.Invalidate(); readyLink.TrySetCanceled(); }
    private string? activePolicy;
    private static string Policy(Profile profile) => $"{profile.Id}|{profile.AllowPublicPushedRoutes}|{profile.OpenVpnConfig}|{profile.Username}|{profile.Password}|{profile.OpenVpnLegacyProviderRequired}";
    public bool NeedsRestart(Profile profile) => activePolicy != null && activePolicy != Policy(profile);
    public Func<IDisposable>? CreateAdapterOverride { get; init; }
    public Action<ProcessHost>? StartProcessOverride { get; init; }
    public Action<ProcessHost, IReadOnlyList<string>, IDictionary<string,string>?>? StartChildOverride { get; init; }
    public Func<OpenVpnGeneration, OpenVpnLink>? CaptureLinkOverride { get; init; }
    private string? routeJournal;
    private int managementPort;
    private string managementPassword = "";
    public Func<int> AllocateManagementPort { get; init; } = FreePort;
    private OpenVpnLink? activeLink;
    public OpenVpnLink? Link { get => activeLink is {} link && (monitor?.IsCurrent(link.Generation) == true || monitor == null && link.ProfileId == null && link.Generation == 0) ? link : null; private set => activeLink = value; }
    public Guid? ActiveProfileId { get; private set; }
    public bool Running => host.Running && Link != null;
    public bool IsRunning => Running;
    public event Action<string>? Warning;
    public event Action<string>? Log;
    public Func<IReadOnlyList<RouteRow>>? CaptureRouteTableOverride { get; set; }
    public Func<string, CancellationToken, Task<(int Code, string Output)>>? PowerShellOverride { get; set; }
    public Func<IReadOnlyList<string>> CapturePhysicalPrefixes { get; init; } = OpenVpnLanPolicy.PhysicalConnectedPrefixes;

    public static string? FindLegacyProviderDirectory(string executablePath)
    {
        var dir = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(dir)) return null;
        var sslMod = Path.Combine(dir, "ssl", "modules");
        if (File.Exists(Path.Combine(sslMod, "legacy.dll"))) return sslMod;
        var osslMod = Path.Combine(dir, "ossl-modules");
        if (File.Exists(Path.Combine(osslMod, "legacy.dll"))) return osslMod;
        if (File.Exists(Path.Combine(dir, "legacy.dll"))) return dir;

        return null;
    }

    public static bool IsLegacyCryptoError(string text) =>
        text.Contains("Algorithm (RC2-40-CBC", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("error:0308010C", StringComparison.OrdinalIgnoreCase) ||
        (text.Contains("PKCS12", StringComparison.OrdinalIgnoreCase) && text.Contains("unsupported", StringComparison.OrdinalIgnoreCase));

    // Offline syntax/policy validation only. PKCS#12 profiles receive the private
    // provider scope before their one child start; fatal crypto errors never restart it.
    public Task<OpenVpnPreflightResult> PreflightAsync(Profile profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            OpenVpnConfiguration.Validate(profile.OpenVpnConfig);
            return Task.FromResult(new OpenVpnPreflightResult(true, profile.OpenVpnLegacyProviderRequired, null, null));
        }
        catch (InvalidDataException error)
        {
            return Task.FromResult(new OpenVpnPreflightResult(false, false, null, error.Message));
        }
    }
    public async Task<OpenVpnLink> StartAsync(Profile profile, string dnsOverride, CancellationToken ct)
    {
        if (Running) { if (ActiveProfileId == profile.Id) return Link!; throw new InvalidOperationException("Сначала остановите текущий OpenVPN-профиль."); }
        if (host.Running && (readyLink.Task.IsFaulted || readyLink.Task.IsCanceled)) await StopAsync().ConfigureAwait(false);
        if (host.Running)
        {
            if (ActiveProfileId != profile.Id) throw new InvalidOperationException("Сначала остановите текущий OpenVPN-профиль.");
            return await readyLink.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        // Retire the previous process-lifetime parser before installing a new
        // one. Late buffered stdout must not mutate the next process generation.
        if (parser != null) await StopAsync().ConfigureAwait(false);
        OpenVpnRouteJournal.CheckPath(runtime); PrivateFiles.ProtectDirectory(runtime);
        foreach (var name in new[] { "auth.pass", "management.pass", "openvpn.conf" }) OpenVpnRouteJournal.CheckPath(Path.Combine(runtime, name));
        try
        {
            var link = await PortStartup.RetryAsync(_ => StartCoreAsync(profile, dnsOverride, ct, profile.OpenVpnLegacyProviderRequired || Regex.IsMatch(profile.OpenVpnConfig, @"(?im)^\s*(?:<pkcs12>|pkcs12\s)")), ct);
            ActiveProfileId = profile.Id;
            return link;
        }
        catch { ActiveProfileId = null; activePolicy = null; cryptoScope?.Dispose(); cryptoScope = null; CleanupSecrets(runtime); throw; }
    }

    public static void CleanupSecrets(string folder) => PrivateFiles.DeleteSecrets(folder, "auth.pass", "management.pass", "openvpn.conf", "openssl-netcat.cnf");

    private async Task<OpenVpnLink> StartCoreAsync(Profile profile, string dnsOverride, CancellationToken ct, bool legacy)
    {
        if (Running) return Link!;
        // Wintun is loaded in this process before the child starts. Verify and
        // hold the module bundle before either native load or privileged launch.
        using var trustedModule = StartProcessOverride == null ? ModuleIntegrity.AcquireForExecutable(executable) : null;
        phase = OpenVpnRuntimePhase.Starting;
        DataPathRecoveryRequired = false; lastControlGeneration = null;
        lock(candidateGate)transportEndpoints.Clear();
        Directory.CreateDirectory(runtime);

        // Offline policy validation; legacy crypto is detected by real startup.
        if (!profile.OpenVpnLegacyProviderRequired)
        {
            var preflight = await PreflightAsync(profile, ct);
            if (!preflight.Success)
            {
                throw new OpenVpnFailureException(OpenVpnFailureClass.DeterministicLocalFatal, "OpenVPN: проверка конфигурации не удалась. " + ProcessHost.Redact(preflight.Error ?? ""));
            }
        }

        var config = OpenVpnConfiguration.Prepare(profile.OpenVpnConfig);
        var configPath = Path.Combine(runtime, "openvpn.conf"); await File.WriteAllTextAsync(configPath, config, ct);
        managementPort = AllocateManagementPort(); managementPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var passwordFile = Path.Combine(runtime, "management.pass"); await File.WriteAllTextAsync(passwordFile, managementPassword, ct);

        var args = new List<string>();
        Dictionary<string, string>? env = null;

        if (legacy && StartProcessOverride == null)
        {
            cryptoScope?.Dispose();
            cryptoScope = OpenVpnCryptoScope.Create(executable, runtime);
            env = new Dictionary<string,string>(cryptoScope.Environment);
        }
        args.AddRange(["--config", configPath, "--management", "127.0.0.1", managementPort.ToString(), passwordFile, "--verb", "3"]);
        if (profile.Username.Length > 0) { var auth = Path.Combine(runtime, "auth.pass"); await File.WriteAllLinesAsync(auth, [profile.Username, profile.Password], ct); args.AddRange(["--auth-user-pass", auth, "--auth-nocache"]); }
        readyLink = NewReady();
        processLifetime?.Dispose(); processLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lifetime = processLifetime.Token;
        monitor = new OpenVpnGenerationMonitor(profile.AllowPublicPushedRoutes,()=>Interlocked.Increment(ref controlRevision));
        activePolicy = Policy(profile);
        var currentMonitor = monitor;
        revalidateDataPath = () =>
        {
            var generation = lastControlGeneration;
            if (generation == null || !currentMonitor.IsCurrent(generation.Revision) || lifetime.IsCancellationRequested || DataPathRecoveryRequired) return;
            Link = null; phase = OpenVpnRuntimePhase.Reconnecting;
            if (readyLink.Task.IsCompleted) readyLink = NewReady();
            LinkChanged?.Invoke();
            _ = CompleteGenerationAsync(profile, dnsOverride, currentMonitor, generation, lifetime);
        };
        long pendingCandidate=0;
        void ClearCandidate()
        {
            lock(candidateGate)
            {
                var previous=Interlocked.Exchange(ref pendingCandidate,0);
                if(previous!=0)CandidateGenerationInvalidated?.Invoke(profile.Id,previous);
            }
        }
        clearPendingCandidate=ClearCandidate;
        currentMonitor.CandidateGenerationPrepared += (revision,push) =>
        {
            lock(candidateGate)
            {
                if(!ReferenceEquals(currentMonitor,monitor)||lifetime.IsCancellationRequested)return;
                ClearCandidate();Interlocked.Exchange(ref pendingCandidate,revision);
                var routes=push.Routes.Select(r=>r.Prefix).ToArray();
                CandidateGenerationPrepared?.Invoke(profile.Id,revision,routes);CandidateRoutesPrepared?.Invoke(profile.Id,routes);
            }
        };
        ActiveProfileId = profile.Id;
        currentMonitor.Invalidated += revision =>
        {
            if(!ReferenceEquals(currentMonitor,monitor))return;
            destinationLeases?.Invalidate();
            Interlocked.Increment(ref dataPathEpoch);
            CancelAddressReadiness();
            ClearCandidate();
            if(!ReferenceEquals(currentMonitor,monitor))return;
            Link = null;
            if (phase != OpenVpnRuntimePhase.Stopping)
            {
                Interlocked.Exchange(ref reconnectStarted, TimeProvider.GetTimestamp());
                phase = host.Running ? OpenVpnRuntimePhase.Reconnecting : OpenVpnRuntimePhase.CleanupPending;
                reconnectWatchdog?.Dispose();
                if (phase == OpenVpnRuntimePhase.Reconnecting)
                    reconnectWatchdog = TimeProvider.CreateTimer(_ =>
                    {
                        if (!ReferenceEquals(currentMonitor, monitor) || phase != OpenVpnRuntimePhase.Reconnecting) return;
                        phase = OpenVpnRuntimePhase.LongReconnect;
                        Log?.Invoke("OPENVPN_RECONNECT state=long-reconnect corporate=closed"); LinkChanged?.Invoke();
                    }, null, LongReconnectThreshold, Timeout.InfiniteTimeSpan);
            }
            if (readyLink.Task.IsCompleted) readyLink = NewReady();
            LinkChanged?.Invoke();
            _ = CleanupGenerationAsync(currentMonitor);
        };
        currentMonitor.Failed += error => { ClearCandidate();if(!ReferenceEquals(currentMonitor,monitor))return;phase = OpenVpnRuntimePhase.CleanupPending; readyLink.TrySetException(error); LinkChanged?.Invoke(); };
        currentMonitor.Ready += generation =>
        {
            if (!ReferenceEquals(currentMonitor, monitor) || lifetime.IsCancellationRequested || DataPathRecoveryRequired) return;
            lastControlGeneration = generation;
            _ = CompleteGenerationAsync(profile, dnsOverride, currentMonitor, generation, lifetime);
        };
        parser = line =>
        {
            if(line.Contains("remote",StringComparison.OrdinalIgnoreCase) || line.Contains("connection established",StringComparison.OrdinalIgnoreCase))
            {
                var peer=Regex.Match(line,@"\[AF_INET\]((?:\d{1,3}\.){3}\d{1,3}):\d+");
                if(peer.Success && IPAddress.TryParse(peer.Groups[1].Value,out var ip))lock(candidateGate)transportEndpoints.Add(ip);
            }
            if (PortStartup.IsCollision(line)) readyLink.TrySetException(new PortCollisionException("Порт OpenVPN занят."));
            if (IsLegacyCryptoError(line)) readyLink.TrySetException(new OpenVpnLegacyCryptoException("OpenVPN: требуется OpenSSL legacy provider."));
            if (line.StartsWith("Options error:", StringComparison.OrdinalIgnoreCase)) readyLink.TrySetException(new OpenVpnFailureException(OpenVpnFailureClass.DeterministicLocalFatal, "OpenVPN: invalid local configuration."));
            if (line.Contains("WARNING:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Options warning:", StringComparison.OrdinalIgnoreCase)) Warning?.Invoke(ProcessHost.Redact(line));
            currentMonitor.Observe(line); Log?.Invoke(ProcessHost.Redact(line));
        };
        host.Line += parser; host.Exited += OnProcessExited;
        try
        {
            await transitionGate.WaitAsync(ct).ConfigureAwait(false);
            try { if (PowerShellOverride == null) await RecoverAsync(runtime).ConfigureAwait(false); else await RecoverOwnedRoutesAsync().ConfigureAwait(false); }
            finally { transitionGate.Release(); }
            if (LocalListener.Owner(managementPort) != 0) throw new PortCollisionException("Порт управления OpenVPN уже занят.");
            adapter?.Dispose();
            adapter = CreateAdapterOverride?.Invoke() ?? new WintunAdapter(Path.Combine(Path.GetDirectoryName(executable)!, "wintun.dll"));
            ct.ThrowIfCancellationRequested();
            if (StartChildOverride != null) StartChildOverride(host, args, env);
            else if (StartProcessOverride != null) StartProcessOverride(host);
            else
            {
                host.Start(executable, args, Path.GetDirectoryName(executable), false, env);
                try { await RouterService.WaitPortAsync(managementPort, host, ct).ConfigureAwait(false); }
                catch (IOException ex) when (IsLegacyCryptoError(ex.Message) || (readyLink.Task.IsFaulted && readyLink.Task.Exception?.InnerExceptions.Any(e => e is OpenVpnLegacyCryptoException) == true))
                { throw new OpenVpnLegacyCryptoException("OpenVPN: требуется OpenSSL legacy provider. " + ex.Message); }
            }
            return await readyLink.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        catch { await StopAsync().ConfigureAwait(false); throw; }
        // Process-lifetime subscription ends only on stop/exit, not first ready.
    }

    private void OnProcessExited(int pid, int code)
    {
        if (host.Id != pid) return;
        monitor?.Invalidate();
        readyLink.TrySetException(new IOException("OpenVPN завершился до готовности соединения."));
    }
    private async Task CleanupGenerationAsync(OpenVpnGenerationMonitor owner)
    {
        await transitionGate.WaitAsync().ConfigureAwait(false);
        try { if (ReferenceEquals(monitor, owner) && Link == null) await RecoverOwnedRoutesAsync().ConfigureAwait(false); }
        catch (Exception ex) { phase = OpenVpnRuntimePhase.FailedCleanup; Log?.Invoke("OPENVPN_ROUTE_CLEANUP_FAILED " + ProcessHost.Redact(ex.Message)); }
        finally { transitionGate.Release(); }
    }
    private async Task CompleteGenerationAsync(Profile profile, string dnsOverride, OpenVpnGenerationMonitor owner, OpenVpnGeneration generation, CancellationToken ct)
    {
        var epoch = Interlocked.Read(ref dataPathEpoch);
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stage="setup";
        bool Current() => !operation.IsCancellationRequested && ReferenceEquals(owner, monitor) && owner.IsCurrent(generation.Revision) && epoch == Interlocked.Read(ref dataPathEpoch);
        await transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Current()) return;
            lock(readinessGate)
            {
                if(!Current())return;
                readinessLifetime=operation;
            }
            await RecoverOwnedRoutesAsync().ConfigureAwait(false);
            OpenVpnLink link;
            if (CaptureLinkOverride != null) link = CaptureLinkOverride(generation);
            else
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == "NetCat-OpenVPN" && n.OperationalStatus == OperationalStatus.Up)
                    ?? throw new IOException("OpenVPN не создал адаптер NetCat-OpenVPN.");
                var props = nic.GetIPProperties();
                var address = props.UnicastAddresses.First(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Address.ToString();
                var gateway = IPAddress.TryParse(generation.Gateway, out _) ? generation.Gateway : props.GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? "0.0.0.0";
                var dns = string.IsNullOrWhiteSpace(dnsOverride) ? generation.Dns : dnsOverride;
                if (!IPAddress.TryParse(dns, out var dnsIp) || dnsIp.AddressFamily != AddressFamily.InterNetwork)
                    throw new IOException("Сервер OpenVPN не передал IPv4 DNS. Укажите DNS корпоративного туннеля.");
                link = new(nic.Name, props.GetIPv4Properties().Index, address, gateway, dns, generation.Routes);
            }
            link = link with { ProfileId = profile.Id, Generation = generation.Revision, RouteOwnerId=Guid.NewGuid(),
                DnsPort = string.IsNullOrWhiteSpace(dnsOverride) ? generation.DnsEndpoints.FirstOrDefault()?.Port ?? 53 : 53 };
            OpenVpnLanPolicy.Validate(link.LearnedRoutes.Concat(OpenVpnRouteJournal.RequiredDnsPrefixes(link)).ToArray(), CapturePhysicalPrefixes());
            lock(candidateGate)
                if(transportEndpoints.Any(ip=>OpenVpnRouteJournal.Covers(link.LearnedRoutes.Concat(OpenVpnRouteJournal.RequiredDnsPrefixes(link)),ip)))
                    throw new IOException("Corporate routes would capture the OpenVPN transport endpoint.");
            var capture = CaptureRouteTableOverride ?? RouteTable.CaptureIpv4;
            if (RouteTable.IsOpenVpnDefaultTakeover(capture().Where(r => r.InterfaceIndex == link.Index), out _))
                throw new IOException("Неожиданный default route на OpenVPN-адаптере.");
            Log?.Invoke("OPENVPN_ADDRESS_WAIT start");
            stage="address";
            await OpenVpnAddressReadiness.WaitAsync(link,Current,operation.Token,AddressReadyTimeout,CaptureAddressState,TimeProvider,AddressReadinessDelay,
                state=>Log?.Invoke("OPENVPN_ADDRESS state="+state)).ConfigureAwait(false);
            if(!Current())throw new OperationCanceledException("Obsolete address readiness completion.");
            routeJournal = Path.Combine(runtime, "openvpn-route.json");
            await OpenVpnRouteJournal.InstallAsync(routeJournal, link, capture, PowerShellOverride ?? PhysicalNetwork.PowerShell, operation.Token).ConfigureAwait(false);
            Log?.Invoke("OPENVPN_DATAPATH_PROBE start");
            stage="datapath";
            await OpenVpnDataPathProbe.ValidateAsync(link, DataPathProbe, Current, operation.Token, DataPathProbeTimeout).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!Current())
            { await RecoverOwnedRoutesAsync().ConfigureAwait(false); return; }
            if (!owner.PublishIfCurrent(generation.Revision, () =>
            {
                lock (candidateGate)
                {
                ct.ThrowIfCancellationRequested();
                if (!Current()) throw new OperationCanceledException("Obsolete OpenVPN probe.");
                reconnectWatchdog?.Dispose(); reconnectWatchdog=null;
                Link = link; phase = OpenVpnRuntimePhase.Connected;
                DestinationLeases.Activate(link,()=>ReferenceEquals(activeLink,link)&&host.Running&&phase==OpenVpnRuntimePhase.Connected,transportEndpoints.ToArray());
                readyLink.TrySetResult(link);
                }
            })) await RecoverOwnedRoutesAsync().ConfigureAwait(false);
            else if (!ct.IsCancellationRequested && owner.IsCurrent(generation.Revision))
            {
                Log?.Invoke("OPENVPN_DATAPATH_PROBE healthy");
                Log?.Invoke($"OPENVPN_GENERATION_READY pid={ProcessId} generation={link.Generation} interface={link.Index} {RuntimeIdentityDiagnostic.Owner(link)} physicalEpoch={epoch} journalOwner={RuntimeIdentityDiagnostic.Alias(link.RouteOwnerId)} state=ready");
                LinkChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            try { await RecoverOwnedRoutesAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { Log?.Invoke("OPENVPN_ROUTE_CLEANUP_FAILED " + ProcessHost.Redact(cleanup.Message)); }
            if (!Current()) return;
            Link = null;
            DataPathRecoveryRequired = true; phase = OpenVpnRuntimePhase.CleanupPending;
            Log?.Invoke(stage=="datapath"?"OPENVPN_DATAPATH_PROBE failed":"OPENVPN_GENERATION_FAILURE stage="+stage+" type="+ex.GetType().Name);
            Log?.Invoke("OPENVPN_RECOVERY escalation=full-restart");
            clearPendingCandidate?.Invoke();
            readyLink.TrySetException(ex); LinkChanged?.Invoke();
        }
        finally { lock(readinessGate){if(ReferenceEquals(readinessLifetime,operation))readinessLifetime=null;} transitionGate.Release(); }
    }
    private Task RecoverOwnedRoutesAsync()
    {
        Task Cleanup()=>OpenVpnRouteJournal.CleanupAsync(Path.Combine(runtime,"openvpn-route.json"),PowerShellOverride??PhysicalNetwork.PowerShell,CaptureRouteTableOverride??RouteTable.CaptureIpv4);
        return destinationLeases is {} leases ? leases.RetireAsync(Cleanup) : Cleanup();
    }

    public Task<OpenVpnLink> EnsureRunningAsync(Profile profile, string dnsOverride, CancellationToken ct)
    {
        if (Running && ActiveProfileId == profile.Id)
            return Task.FromResult(Link!);
        return StartAsync(profile, dnsOverride, ct);
    }

    public Task EnsureStoppedAsync(CancellationToken ct) => StopAsync();
    public RouteObservationResult VerifyRoutes()
    {
        if (Link is not {} link) return new(RouteObservationStatus.Missing, []);
        try { OpenVpnLanPolicy.Validate(link.LearnedRoutes, CapturePhysicalPrefixes()); }
        catch (InvalidDataException error) { return new(RouteObservationStatus.Missing, [], error.Message); }
        return RouteTable.VerifyOpenVpn(link, (CaptureRouteTableOverride ?? RouteTable.CaptureIpv4)());
    }

    public async Task EnsureRoutesAsync(CancellationToken ct)
    {
        await transitionGate.WaitAsync(ct).ConfigureAwait(false);
        var link=Link;
        var owner=monitor;var epoch=Interlocked.Read(ref dataPathEpoch);
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(ct,processLifetime?.Token??CancellationToken.None);
        bool Current()=>!operation.IsCancellationRequested && ReferenceEquals(owner,monitor) && epoch==Interlocked.Read(ref dataPathEpoch) &&
            link!=null && (owner?.IsCurrent(link.Generation)==true || owner==null && link.ProfileId==null);
        bool replacement=false;
        try
        {
            if(link==null)throw new OperationCanceledException("Интерфейс OpenVPN ещё не готов.");
            if(!Current())throw new OperationCanceledException("Obsolete OpenVPN repair.");
            lock(readinessGate)readinessLifetime=operation;
            OpenVpnLanPolicy.Validate(link.LearnedRoutes, CapturePhysicalPrefixes());
            var path=Path.Combine(runtime,"openvpn-route.json");var prior=await OpenVpnRouteJournal.ReadAsync(path).ConfigureAwait(false);
            replacement=prior!=null && !OpenVpnRouteJournal.SameGeneration(prior,link);
            if(replacement)
            {
                Log?.Invoke("OPENVPN_OWNERSHIP transition=retiring reason=journal-mismatch");
                Link=null;phase=OpenVpnRuntimePhase.Reconnecting;readyLink=NewReady();destinationLeases?.Invalidate();
                await RecoverOwnedRoutesAsync().ConfigureAwait(false);
                if(!Current())throw new OperationCanceledException("Obsolete ownership retirement.");
                await OpenVpnAddressReadiness.WaitAsync(link,Current,operation.Token,AddressReadyTimeout,CaptureAddressState,TimeProvider,AddressReadinessDelay).ConfigureAwait(false);
            }
            if(!Current())throw new OperationCanceledException("Obsolete route install.");
            await OpenVpnRouteJournal.InstallAsync(path,link,CaptureRouteTableOverride??RouteTable.CaptureIpv4,PowerShellOverride??PhysicalNetwork.PowerShell,operation.Token).ConfigureAwait(false);
            if(replacement)await OpenVpnDataPathProbe.ValidateAsync(link,DataPathProbe,Current,operation.Token,DataPathProbeTimeout).ConfigureAwait(false);
            if(!Current())throw new OperationCanceledException("Obsolete route completion.");
            if(replacement)
            {
                void Publish(){lock(candidateGate){if(!Current())throw new OperationCanceledException();Link=link;phase=OpenVpnRuntimePhase.Connected;DestinationLeases.Activate(link,()=>ReferenceEquals(activeLink,link)&&host.Running&&phase==OpenVpnRuntimePhase.Connected,transportEndpoints.ToArray());readyLink.TrySetResult(link);}}
                if(owner!=null){if(!owner.PublishIfCurrent(link.Generation,Publish))throw new OperationCanceledException();}else Publish();
                Log?.Invoke("OPENVPN_OWNERSHIP transition=active");LinkChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            if(!Current()) {if(replacement)await RecoverOwnedRoutesAsync().ConfigureAwait(false);throw;}
            Link = null;destinationLeases?.Invalidate();DataPathRecoveryRequired=true;phase=OpenVpnRuntimePhase.CleanupPending;
            try{await RecoverOwnedRoutesAsync().ConfigureAwait(false);}
            catch(Exception cleanup){phase=OpenVpnRuntimePhase.FailedCleanup;Log?.Invoke("OPENVPN_ROUTE_CLEANUP_FAILED "+ProcessHost.Redact(cleanup.Message));}
            readyLink = NewReady(); readyLink.TrySetException(ex);
            LinkChanged?.Invoke(); throw;
        }
        finally {lock(readinessGate){if(ReferenceEquals(readinessLifetime,operation))readinessLifetime=null;}transitionGate.Release();}
    }

    public async Task StopAsync()
    {
        destinationLeases?.Invalidate();
        CancelAddressReadiness();
        await stopGate.WaitAsync().ConfigureAwait(false);
        try {
        lock (candidateGate)
        {
            phase = OpenVpnRuntimePhase.Stopping;
            revalidateDataPath = null; Interlocked.Increment(ref dataPathEpoch);
            processLifetime?.Cancel();
        }
        reconnectWatchdog?.Dispose(); reconnectWatchdog=null;
        clearPendingCandidate?.Invoke();clearPendingCandidate=null;
        monitor?.Invalidate(); monitor?.Dispose(); monitor = null;
        if (parser != null) { host.Line -= parser; parser = null; }
        host.Exited -= OnProcessExited; readyLink.TrySetCanceled();
        try
        {
            if (host.Running)
            {
                try { using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, managementPort).WaitAsync(TimeSpan.FromSeconds(2)); using var writer = new StreamWriter(tcp.GetStream()) { AutoFlush = true }; await writer.WriteLineAsync(managementPassword); await writer.WriteLineAsync("signal SIGTERM"); await Task.Delay(500); } catch (Exception e) when (e is IOException or SocketException or TimeoutException) { }
            }
            await host.StopAsync();
            await transitionGate.WaitAsync().ConfigureAwait(false);
            try { phase = OpenVpnRuntimePhase.CleanupPending; await RecoverOwnedRoutesAsync().ConfigureAwait(false); phase = OpenVpnRuntimePhase.FullyStopped; }
            catch { phase = OpenVpnRuntimePhase.FailedCleanup; throw; }
            finally { transitionGate.Release(); }
        }
        finally { Link = null; ActiveProfileId = null; activePolicy = null; adapter?.Dispose(); adapter = null; managementPassword = ""; managementPort = 0; cryptoScope?.Dispose(); cryptoScope = null; CleanupSecrets(runtime); }
        } finally { stopGate.Release(); }
    }

    public static async Task RecoverAsync(string runtime)
    {
        var path = Path.Combine(runtime, "openvpn-route.json");
        var journal = await OpenVpnRouteJournal.ReadAsync(path).ConfigureAwait(false);
        if (journal == null) return;
        // An index can be recycled after reboot: do not touch another adapter.
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == "NetCat-OpenVPN");
        if (nic == null || nic.GetIPProperties().GetIPv4Properties().Index != journal.InterfaceIndex)
        { File.Delete(path); return; }
        await OpenVpnRouteJournal.CleanupAsync(path, PhysicalNetwork.PowerShell).ConfigureAwait(false);
    }

    public static bool TryGetContiguousCidr(IPAddress mask, out int cidr)
    {
        cidr = 0;
        var b = mask.GetAddressBytes();
        if (b.Length != 4) return false;
        uint v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b);
        if (v == 0) { cidr = 0; return true; }
        uint inverted = ~v;
        if ((inverted & (inverted + 1)) != 0) return false;
        cidr = System.Numerics.BitOperations.PopCount(v);
        return true;
    }

    public static IPAddress GetCanonicalNetwork(IPAddress dest, IPAddress mask)
    {
        var d = dest.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (d.Length != 4 || m.Length != 4) return dest;
        var net = new byte[4];
        for (int i = 0; i < 4; i++) net[i] = (byte)(d[i] & m[i]);
        return new IPAddress(net);
    }

    public static bool IsPrefixContainedInAllowedRanges(IPAddress network, int cidr)
    {
        var b = network.GetAddressBytes();
        if (b.Length != 4 || cidr < 0 || cidr > 32) return false;
        uint net = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b);

        // 10.0.0.0/8
        if (cidr >= 8 && (net & 0xFF000000u) == 0x0A000000u) return true;
        // 172.16.0.0/12
        if (cidr >= 12 && (net & 0xFFF00000u) == 0xAC100000u) return true;
        // 192.168.0.0/16
        if (cidr >= 16 && (net & 0xFFFF0000u) == 0xC0A80000u) return true;
        // 100.64.0.0/10
        if (cidr >= 10 && (net & 0xFFC00000u) == 0x64400000u) return true;
        // 169.254.0.0/16
        if (cidr >= 16 && (net & 0xFFFF0000u) == 0xA9FE0000u) return true;

        return false;
    }

    public static bool IsPrivateOrLinkLocal(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
        return false;
    }

    public static int NetmaskToCidr(IPAddress mask)
    {
        return TryGetContiguousCidr(mask, out var cidr) ? cidr : 0;
    }

    public static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }

    public static int FreeTcpUdpPort()
    {
        var pair = PortStartup.BindTcpUdp();
        try { return ((IPEndPoint)pair.Tcp.LocalEndpoint).Port; }
        finally { pair.Tcp.Stop(); pair.Udp.Dispose(); }
    }

    public void Dispose() { destinationLeases?.Invalidate(); CancelAddressReadiness(); reconnectWatchdog?.Dispose(); processLifetime?.Cancel(); monitor?.Invalidate(); monitor?.Dispose(); monitor = null; if (parser != null) host.Line -= parser; host.Exited -= OnProcessExited; try { host.Dispose(); adapter?.Dispose(); } finally { adapter = null; Link = null; ActiveProfileId = null; managementPort = 0; managementPassword = ""; cryptoScope?.Dispose(); cryptoScope = null; CleanupSecrets(runtime); } }
}
