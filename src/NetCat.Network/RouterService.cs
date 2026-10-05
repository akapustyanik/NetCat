using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Network;
public sealed record RuntimeSnapshot(Guid? ActiveProfileId,int ListenPort,int LatencyPort,int HealthSourcePort,bool TunActive,bool VpnRequested,bool RequiresXray,bool RouterRequested = false);
public sealed class RouterService : IDisposable, IRouterRuntime
{
    private readonly string bin, runtime;
    public RouterService(string bin, string runtime)
    {
        this.bin = bin; this.runtime = runtime;
        OpenVpn = new(Path.Combine(bin, "openvpn", "openvpn.exe"), Path.Combine(runtime, "openvpn"));
        xray.Exited += (pid, _) =>
        {
            // ProcessHost owns this exact child; queued exits from a replaced
            // child and intentional reconfiguration must not signal a loss.
            if (pid != xray.Id || Reconfiguring || !VpnRequested || !requiresXray) return;
            Log?.Invoke("XRAY_PROCESS unexpected_exit");
            DependencyLost?.Invoke();
        };
    }
    private readonly ProcessHost core = new(), xray = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim probes = new(2);
    public NetworkSnapshot? ActivePhysical { get; private set; }
    public long NetworkRevision { get; private set; }
    public string RecoveryStatus { get; private set; } = "";
    private bool networkRefresh;
    private NetworkSnapshot? coordinatorBinding;
    private AppSettings activeSettings = new();
    public Func<string, NetworkSnapshot> CaptureBinding { get; init; } = name => PhysicalNetwork.ResolveCurrentBinding(name);
    public long SessionRevision { get; private set; }
    private bool logsAttached, requiresXray;
    private OpenVpnSidecar? openVpnSidecar;
    private GuardedDirectGateway? guardedDirect;
    private GuardedVpnGateway? guardedVpn;
    private CorporateDnsGuard? corporateDns;
    private bool invalidateInternalPorts;
    private readonly HashSet<int> retiredVpnReturns = [];
    private readonly CorporateDomainGuard domainGuard = new();
    public CorporateDomainGuard DomainGuard => domainGuard;
    private readonly object gatewayLock = new();
    // Standalone callers retain the guarded contract until intent is supplied.
    // RuntimeCoordinator always publishes authoritative ON/OFF before routing.
    private volatile bool openVpnRoutingRequested = true;
    private volatile bool openVpnOwnershipReleasePending;
    public bool OpenVpnGatewayReady => !openVpnOwnershipReleasePending && (openVpnSidecar?.Running ?? !IsRunning);
    public void InvalidateOpenVpnOverlay() => openVpnSidecar?.Invalidate();
    public bool OpenVpnOverlayReady => openVpnSidecar?.Active == true;
    public event Action? OpenVpnOverlayLost;
    public void PrepareDomainOwnership(AppSettings settings)
    {
        lock (gatewayLock)
        {
            domainGuard.Prepare(settings);
            corporateDns?.Prepare(settings);
            guardedDirect?.Update(settings, OpenVpn.Link, OpenVpnOwnership.Build(settings, OpenVpn.Link));
        }
    }
    public void PrepareDomainOwnership(AppSettings settings, bool openVpnRequested)
    {
        lock (gatewayLock)
        {
            openVpnRoutingRequested = openVpnRequested;
            if (openVpnRequested) openVpnOwnershipReleasePending = false;
            domainGuard.SetEnabled(openVpnRequested);
            guardedDirect?.SetProtectionEnabled(openVpnRequested);
            if (openVpnSidecar != null)
            {
                openVpnSidecar.RoutingRequested = openVpnRequested;
                // Existing main processes watch these rule-set files. Releasing
                // automatic domains/prefixes does not restart main VPN or TUN.
                if (!openVpnRequested)
                {
                    try { openVpnSidecar.RememberRoutes(settings); openVpnOwnershipReleasePending = false; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Keep UI intent/cancellation usable. The coordinator's
                        // existing overlay retry handles locked ownership files.
                        openVpnOwnershipReleasePending = true;
                        Log?.Invoke("OPENVPN_OFF release=pending error=" + ProcessHost.Redact(ex.Message));
                    }
                }
            }
            PrepareDomainOwnership(settings);
        }
    }
    public void PrepareDomainOwnership(AppSettings settings, Func<bool> openVpnRequested)
    {
        lock (gatewayLock) PrepareDomainOwnership(settings, openVpnRequested());
    }
    private OpenVpnSidecar GatewaySidecar
    {
        get
        {
            lock (gatewayLock)
            {
            if (openVpnSidecar != null) return openVpnSidecar;
            guardedDirect ??= new(domainGuard);
            guardedDirect.SetProtectionEnabled(openVpnRoutingRequested);
            corporateDns ??= new(domainGuard);
            if(guardedVpn == null) { guardedVpn = new(domainGuard); guardedVpn.Start(PortStartup.Distinct(OpenVpnService.FreeTcpUdpPort,
                corporateDns.DirectReturnPort,corporateDns.VpnReturnPort,corporateDns.DirectPort,corporateDns.VpnPort,guardedDirect.Port,guardedVpn.Port)); }
            openVpnSidecar = new(SingBox, Path.Combine(runtime, "openvpn-gateway"), OpenVpn.DestinationLeases) { Log = line => Log?.Invoke(line), OwnershipPrepared = (settings,link,ownership) => { domainGuard.Prepare(settings); corporateDns.Prepare(settings); guardedDirect.Update(settings,link,ownership); } };
            openVpnSidecar.RoutingRequested = openVpnRoutingRequested;
            OpenVpn.CandidateGenerationPrepared += guardedDirect.Candidate;
            OpenVpn.CandidateGenerationInvalidated += guardedDirect.ClearCandidate;
            openVpnSidecar.ProcessExited += (_, _) => OpenVpnOverlayLost?.Invoke();
            OpenVpn.ProcessExited += (_, _) => _ = DeactivateLostOpenVpnAsync();
            OpenVpn.LinkChanged += () => { openVpnSidecar?.Invalidate(); OpenVpnOverlayLost?.Invoke(); };
            return openVpnSidecar;
            }
        }
    }
    private async Task DeactivateLostOpenVpnAsync()
    {
        try { if (openVpnSidecar != null) await openVpnSidecar.DeactivateAsync(() => !OpenVpn.IsRunning).ConfigureAwait(false); }
        catch (Exception ex) { Log?.Invoke("OPENVPN_SIDECAR deactivate_error=" + ex.Message); }
    }
    public Task ApplyOpenVpnOverlayAsync(AppSettings settings, OpenVpnLink? link, CancellationToken ct)
        => GatewaySidecar.ApplyAsync(settings, link, ct);
    public Task ApplyOpenVpnOverlayAsync(AppSettings settings, OpenVpnLink? link, Func<bool> stillCurrent, CancellationToken ct)
        => GatewaySidecar.ApplyAsync(settings, link, ct, stillCurrent);
    public OpenVpnService OpenVpn { get; }
    public bool VpnRequested { get; private set; }
    private bool routerRequested;
    public Guid? ActiveProfileId { get; private set; }
    public bool ZapretAvailable { get; set; }
    public bool Running => core.Running;
    public bool IsRunning => Running;
    public bool VpnRunning => VpnRequested && core.Running && (!requiresXray || xray.Running);
    public bool DependenciesHealthy => !requiresXray || xray.Running;
    public event Action? DependencyLost;
    public int ListenPort { get; private set; }
    public int LatencyPort { get; private set; }
    public int HealthSourcePort { get; private set; }
    public bool TunActive { get; private set; }
    public bool Reconfiguring { get; private set; }
    private string? activeConfigFingerprint;
    public string? ActiveConfigFingerprint => activeConfigFingerprint;
    public int StartCount { get; private set; }

    public static string ComputeConfigFingerprint(AppSettings settings, NetworkSnapshot? physical, Profile? mainProfile, OpenVpnLink? openVpnLink)
    {
        var desired = new DesiredRuntimeState { MainVpnEnabled = mainProfile != null, TunEnabled = settings.Tun,
            SelectedVpnProfileId = mainProfile?.Id ?? settings.MainProfileId, OpenVpnEnabled = openVpnLink != null,
            SelectedOpenVpnProfileId = settings.OpenVpnProfileId };
        var effective = EffectiveRuntimeConfigBuilder.Build(settings, desired, physical, openVpnLink?.LearnedRoutes);
        return $"{effective.VpnProfileId}|{effective.TunEnabled}|{effective.PhysicalBindingFingerprint}|{effective.RoutingRulesFingerprint}|{effective.DnsPolicyFingerprint}|{effective.OpenVpnEnabled}|{effective.LearnedOpenVpnRoutesFingerprint}";
    }

    public Task EnsureRunningAsync(AppSettings settings, NetworkSnapshot physical, string reason, CancellationToken ct)
        => EnsureRunningAsync(settings, physical, true, reason, ct);

    public async Task EnsureRunningAsync(AppSettings settings, NetworkSnapshot physical, bool mainVpnEnabled, string reason, CancellationToken ct)
    {
        var profile = mainVpnEnabled ? settings.Profiles.FirstOrDefault(p => p.Id == settings.MainProfileId && !p.IsOpenVpn)
            ?? throw new InvalidOperationException("Выберите основной VPN-профиль.") : null;
        var targetFingerprint = ComputeConfigFingerprint(settings, physical, profile, OpenVpn.Link);

        if (reason != "restart-structural-tun" && Running && DependenciesHealthy && !Reconfiguring && activeConfigFingerprint == targetFingerprint)
        {
            return;
        }

        await SetVpnAsync(settings, true, ct, reason: reason, physical: physical, mainVpnEnabled: mainVpnEnabled);
        activeConfigFingerprint = targetFingerprint;
        StartCount++;
    }

    public async Task EnsureStoppedAsync(CancellationToken ct)
    {
        if (!routerRequested && !Running) return;
        await SetVpnAsync(JsonSettings.Clone(activeSettings), false, ct, reason: "stop");
    }

    public async Task SuspendModuleAsync(string key,CancellationToken ct)
    {
        if(key=="sing-box" && openVpnSidecar!=null)await openVpnSidecar.DeactivateAsync();
        await EnsureStoppedAsync(ct);
    }

    public async Task RefreshPhysicalAsync(AppSettings settings, NetworkSnapshot physical, string reason, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            coordinatorBinding = physical;
            SessionRevision++;
            await ReconfigureInternal(settings, ct, reason: reason);
            activeSettings = JsonSettings.Clone(settings);
            activeConfigFingerprint = ComputeConfigFingerprint(settings, physical, settings.Profiles.FirstOrDefault(p => p.Id == ActiveProfileId), OpenVpn.Link);
        }
        finally { coordinatorBinding = null; gate.Release(); }
    }
    public event Action<string>? Log;
    public event Action? Changed;
    public Action<ProcessHost,string,string[]>? StartProcessOverride { get; init; }
    public Func<Profile,AppSettings,CancellationToken,Task<DelayResult>>? PreflightOverride {get;init;}
    public Func<int> AllocatePort {get;init;} = OpenVpnService.FreePort;
    public Func<int> AllocateTcpUdpPort {get;init;} = OpenVpnService.FreeTcpUdpPort;
    private void StartCore(string config) { if(StartProcessOverride is {} start) start(core,SingBox,["run","-c",config]); else core.Start(SingBox,["run","-c",config]); }
    private void ClearRuntimeState(bool clearRequest)
    {
        ActiveProfileId=null; ListenPort=LatencyPort=HealthSourcePort=0; TunActive=false; requiresXray=false;
        activeConfigFingerprint = null;
        if(clearRequest) { routerRequested=false; VpnRequested=false; ActivePhysical=null; RecoveryStatus=""; }
    }
    public RuntimeSnapshot CaptureRuntime() => new(ActiveProfileId,ListenPort,LatencyPort,HealthSourcePort,TunActive,VpnRequested,requiresXray,routerRequested);
    public ActiveTrafficStamp CaptureTrafficStamp() => new(ActiveProfileId, SessionRevision, NetworkRevision, core.Id, TunActive && VpnRunning && !Reconfiguring);
    public async Task<ActiveTrafficTest> TestActiveTrafficAsync(CancellationToken ct,
        Func<CancellationToken, Task<TrafficTestResult>>? measure = null)
    {
        ct.ThrowIfCancellationRequested();
        var stamp = CaptureTrafficStamp();
        if(!stamp.TunActive || stamp.ProfileId == null)
            return new(stamp, null, "VPN/TUN не готов");
        var result = await (measure?.Invoke(ct) ?? SystemTrafficProbeWorker.MeasureAsync(runtime, ct)).ConfigureAwait(false);
        return new ActiveTrafficTest(stamp, result).Validate(CaptureTrafficStamp());
    }
    private void RestoreRuntime(RuntimeSnapshot snapshot)
    {
        ActiveProfileId=snapshot.ActiveProfileId; ListenPort=snapshot.ListenPort; LatencyPort=snapshot.LatencyPort;
        HealthSourcePort=snapshot.HealthSourcePort; TunActive=snapshot.TunActive; VpnRequested=snapshot.VpnRequested; requiresXray=snapshot.RequiresXray; routerRequested=snapshot.RouterRequested;
    }
    public string SingBox => Path.Combine(bin, "sing-box", "sing-box.exe");
    public async Task SetVpnAsync(AppSettings s, bool enabled, CancellationToken ct = default, string reason = "vpn-toggle", NetworkSnapshot? physical = null, bool? mainVpnEnabled = null)
    {
        SessionRevision++;
        await gate.WaitAsync(ct);
        var old = VpnRequested; var oldRouterRequested=routerRequested; var previous=CaptureRuntime();
        try
        {
            coordinatorBinding = physical;
            routerRequested = enabled;
            VpnRequested = enabled && (mainVpnEnabled ?? true);
            await ReconfigureInternal(s, ct, previous, reason: reason);
            activeSettings = JsonSettings.Clone(s);
            if (enabled)
            {
                var profile = s.Profiles.FirstOrDefault(p => p.Id == s.MainProfileId && !p.IsOpenVpn);
                activeConfigFingerprint = ComputeConfigFingerprint(s, ActivePhysical, profile, OpenVpn.Link);
            }
            else
            {
                activeConfigFingerprint = null;
            }
        }
        catch { routerRequested = core.Running && oldRouterRequested; VpnRequested = core.Running && old; Changed?.Invoke(); throw; }
        finally { coordinatorBinding = null; gate.Release(); }
    }
    public async Task ApplyAsync(AppSettings s, CancellationToken ct = default, string reason = "apply")
    {
        SessionRevision++;
        await gate.WaitAsync(ct); try { await ReconfigureInternal(s, ct, reason: reason); } finally { gate.Release(); }
    }
    private async Task ReconfigureInternal(AppSettings s, CancellationToken ct, RuntimeSnapshot? previous = null, string reason = "manual")
    {
        Reconfiguring = true; Changed?.Invoke();
        Log?.Invoke($"ROUTER_RECONFIG reason={reason}");
        var requested=VpnRequested; var requestedRouter=routerRequested;
        previous ??= CaptureRuntime();
        try { await PortStartup.RetryAsync(async attempt=>{
            if(invalidateInternalPorts && corporateDns != null)
            {
                // A free-port probe is not a lease. Invalidate the entire failed
                // return allocation before generating the next runtime config.
                corporateDns.ReallocateReturnPorts(s.SocksPort, ListenPort, LatencyPort,
                    openVpnSidecar?.Gateway.SocksPort ?? 0, openVpnSidecar?.Gateway.DnsPort ?? 0);
                if(guardedVpn != null)
                {
                    retiredVpnReturns.Add(guardedVpn.ReturnPort);
                    guardedVpn.SetReturnPort(PortStartup.Distinct(OpenVpnService.FreeTcpUdpPort,retiredVpnReturns
                        .Concat([corporateDns.DirectReturnPort,corporateDns.VpnReturnPort,s.SocksPort]).Select(p=>(int?)p).ToArray()));
                }
                invalidateInternalPorts=false;
                Log?.Invoke($"INTERNAL_PORT_REALLOCATE attempt={attempt+1} direct={corporateDns.DirectReturnPort} vpn={corporateDns.VpnReturnPort}");
            }
            try { routerRequested=requestedRouter; VpnRequested=requested;await ReconfigureCore(s,ct,previous);return true; }
            catch(Exception e) when(e is PortCollisionException || e is SocketException se && se.SocketErrorCode==SocketError.AddressAlreadyInUse)
            { invalidateInternalPorts=true;throw; }
        },ct); }
        finally { Reconfiguring = false; Changed?.Invoke(); }
    }
    private async Task ReconfigureCore(AppSettings s, CancellationToken ct,RuntimeSnapshot previousState)
    {
        if (!logsAttached)
        {
            core.Line += line => Log?.Invoke("sing-box: " + line);
            xray.Line += line => Log?.Invoke("Xray: " + line);
            OpenVpn.Log += line => Log?.Invoke("OpenVPN: " + line);
            OpenVpn.Warning += line => Log?.Invoke("OpenVPN: " + line);
            logsAttached = true;
        }
        // A local router can own TUN for OpenVPN without enabling a main VPN outbound.
        if (!routerRequested) { await core.StopAsync(); await xray.StopAsync(); ClearRuntimeState(true); Changed?.Invoke(); return; }
        var physical = coordinatorBinding ?? await Task.Run(() => CaptureBinding(s.PhysicalInterface), ct);
        var selected = VpnRequested ? s.Profiles.FirstOrDefault(p => p.Id == s.MainProfileId && !p.IsOpenVpn) ?? throw new InvalidOperationException("Выберите основной VPN-профиль.") : null;
        if (selected != null) SettingsMigration.NormalizeCore(selected);
        if (s.TelegramSocks && !await SocksAvailableAsync(s.TelegramSocksHost,s.TelegramSocksPort,ct))
        {
            Log?.Invoke("Внешний SOCKS5 Telegram недоступен. Маршрут Telegram сохранён; автоматического перехода на VPN нет. Проверьте сервер или отключите внешний SOCKS5.");
        }
        OpenVpnRouteJournal.CheckPath(runtime); PrivateFiles.ProtectDirectory(runtime);
        var gateway = GatewaySidecar;
        domainGuard.Prepare(s);
        corporateDns?.Prepare(s);
        if (!gateway.Running)
        {
            try { await gateway.ApplyAsync(s, null, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log?.Invoke("OPENVPN_GATEWAY_REJECTING error=" + ProcessHost.Redact(ex.Message)); OpenVpnOverlayLost?.Invoke(); }
        }
        var localPort = s.SocksPort;
        int?[] reserved=[gateway.Gateway.SocksPort,gateway.Gateway.DnsPort,corporateDns!.DirectPort,corporateDns.VpnPort,
            corporateDns.DirectReturnPort,corporateDns.VpnReturnPort,guardedDirect!.Port,guardedVpn!.Port,guardedVpn.ReturnPort];
        var owner = LocalListener.Owner(localPort);
        if (reserved.Contains(localPort) || owner != 0 && (!core.Running || owner != core.Id))
        {
            localPort = PortStartup.Distinct(AllocatePort, reserved);
            Log?.Invoke($"Порт {s.SocksPort} занят другим процессом (PID {owner}). Для этой сессии выбран 127.0.0.1:{localPort}.");
        }
        int? bridge = selected?.Core == "Xray" ? PortStartup.Distinct(AllocateTcpUdpPort, reserved.Append(localPort).ToArray()) : null;
        int? latencyPort = null;
        if (selected != null) latencyPort = PortStartup.Distinct(AllocatePort,reserved.Concat([localPort,bridge]).ToArray());
        var healthPort = s.Tun && selected != null ? PortStartup.Distinct(AllocatePort,reserved.Concat([localPort,bridge,latencyPort]).ToArray()) : 0;
        bool ovpnRunning = OpenVpn.Running;
        if (!ovpnRunning)
        {
            var explicitOvpn = s.Rules.Where(r => r.Enabled && r.Target == RouteTarget.OpenVpn && r.Kind == RuleKind.IpCidr).Select(r => r.Value);
            var ovpnProf = s.Profiles.FirstOrDefault(p => p.Id == s.OpenVpnProfileId && p.IsOpenVpn);
            var learned = openVpnRoutingRequested ? ovpnProf?.LearnedRoutes ?? (IEnumerable<string>)Array.Empty<string>() : [];
            var blockedRoutes = explicitOvpn.Concat(learned).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var r in blockedRoutes)
            {
                Log?.Invoke($"Маршрут {r} требует OpenVPN, но OpenVPN отключён.");
            }
        }
        gateway.RememberRoutes(s, ovpnRunning ? OpenVpn.Link : null);
        guardedDirect!.Bind(physical);
        var stableGateway = gateway.Gateway with { GuardedDirectPort = guardedDirect.Port, GuardedDirectUsername = guardedDirect.Username, GuardedDirectPassword = guardedDirect.Password,
            GuardedVpnPort=guardedVpn!.Port,VpnReturnPort=guardedVpn.ReturnPort,GuardedVpnUsername=guardedVpn.Username,GuardedVpnPassword=guardedVpn.Password,
            GuardedDirectDnsPort=corporateDns!.DirectPort,GuardedVpnDnsPort=corporateDns.VpnPort,DirectDnsReturnPort=corporateDns.DirectReturnPort,VpnDnsReturnPort=corporateDns.VpnReturnPort };
        var config = await Task.Run(() => SingBoxConfig.Build(s, physical, selected, null, s.Tun, port: localPort, xrayPort: bridge, zapretRunning: ZapretAvailable, latencyPort: latencyPort, healthSourcePort: healthPort, geodataDirectory: bin, openVpnGateway: stableGateway), ct);
        var next = Path.Combine(runtime, "router.next.json");
        OpenVpnRouteJournal.CheckPath(next); OpenVpnRouteJournal.CheckPath(Path.Combine(runtime, "router.json"));
        await File.WriteAllTextAsync(next, config.ToJsonString(JsonSettings.Options), ct);
        await ValidateAsync(next, ct);
        var active = Path.Combine(runtime, "router.json"); var old = File.Exists(active) ? await File.ReadAllTextAsync(active, ct) : null;
        var oldRunning = core.Running; var oldXrayRunning = xray.Running;
        var xrayFile = Path.Combine(runtime, "xray.json");
        var oldXray = File.Exists(xrayFile) ? await File.ReadAllTextAsync(xrayFile, ct) : null;
        await core.StopAsync(); await xray.StopAsync(); LatencyPort = 0;
        if (previousState.TunActive && StartProcessOverride == null) await TunnelInspection.WaitReleasedAsync(ct);
        try
        {
            if (bridge.HasValue) await StartXrayAsync(selected!, bridge.Value, physical, xray, Path.Combine(runtime, "xray.json"), ct, RuleValidation.Domains(s.LocalDomains));
            File.Move(next, active, true);
            StartCore(active);
            await WaitPortAsync(localPort, core, ct); ListenPort = localPort;
            if (latencyPort.HasValue) { await WaitPortAsync(latencyPort.Value, core, ct); LatencyPort = latencyPort.Value; }
            ActiveProfileId = selected?.Id;
            requiresXray = bridge.HasValue;
            HealthSourcePort = healthPort; TunActive = s.Tun; ActivePhysical = physical; NetworkRevision++; RecoveryStatus = "";
            Log?.Invoke($"Маршрутизатор запущен; физический адаптер: {physical.Name}; DNS: {physical.Dns}; OpenVPN: {(OpenVpn.Running ? "подключён" : "отключён")}");
        }
        catch
        {
            await core.StopAsync(); await xray.StopAsync();
            VpnRequested = false;
            if (oldRunning && old != null && !networkRefresh)
            {
                try
                {
                    var previous = JsonNode.Parse(old)!;
                    var previousOpenVpn = previous["outbounds"]!.AsArray().FirstOrDefault(n => n?["tag"]?.ToString() == "openvpn");
                    if (previousOpenVpn?["type"]?.ToString() == "direct" && (!OpenVpn.Running || previousOpenVpn["inet4_bind_address"]?.ToString() != OpenVpn.Link?.Address)) throw new IOException("Старый OpenVPN-адаптер уже отключён.");
                    if (oldXrayRunning && oldXray != null)
                    {
                        await File.WriteAllTextAsync(xrayFile, oldXray);
                        xray.Start(Path.Combine(bin, "xray", "xray.exe"), ["run", "-c", xrayFile]);
                        await WaitPortAsync((int)JsonNode.Parse(oldXray)!["inbounds"]![0]!["port"]!, xray, CancellationToken.None);
                    }
                    await File.WriteAllTextAsync(active, old); StartCore(active);
                    await WaitPortAsync(previousState.ListenPort, core, CancellationToken.None);
                    if(previousState.LatencyPort>0) await WaitPortAsync(previousState.LatencyPort,core,CancellationToken.None);
                    var previousInbounds=previous["inbounds"]!.AsArray();
                    int PreviousPort(string tag)=>(int)previousInbounds.First(n=>n?["tag"]?.ToString()==tag)!["listen_port"]!;
                    corporateDns?.RestoreReturnPorts(PreviousPort("dns-direct-return"),PreviousPort("dns-vpn-return"));
                    if (previousState.VpnRequested) guardedVpn?.SetReturnPort(PreviousPort("vpn-return"));
                    RestoreRuntime(previousState);
                    Log?.Invoke("Новая конфигурация не запустилась. Восстановлена предыдущая рабочая конфигурация.");
                }
                catch (Exception restoreError) { await core.StopAsync(); await xray.StopAsync(); ClearRuntimeState(true); Log?.Invoke("Восстановление подключения: " + ProcessHost.Redact(restoreError.Message)); }
            }
            else ClearRuntimeState(true);
            throw;
        }
        finally { Changed?.Invoke(); }
    }
    public static async Task<bool> SocksAvailableAsync(string host,int port,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { using var tcp=new TcpClient(); await tcp.ConnectAsync(host,port,timeout.Token); using var stream=tcp.GetStream(); await stream.WriteAsync(new byte[] {5,1,0},timeout.Token); var response=new byte[2]; await stream.ReadExactlyAsync(response,timeout.Token); return response[0]==5 && response[1]==0; }
        catch(Exception e) when(e is SocketException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }
    public async Task ValidateAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await ProcessHost.RunAsync(SingBox, ["check", "-c", path], timeout.Token);
        if (result.Code != 0) throw new InvalidDataException("sing-box отклонил конфигурацию: " + ProcessHost.Redact(result.Output));
    }
    public async Task ValidateProfileConfigurationAsync(Profile profile,AppSettings settings,CancellationToken ct)
    {
        if(profile.IsOpenVpn) {OpenVpnConfiguration.Validate(profile.OpenVpnConfig);return;}
        var folder=Path.Combine(runtime,"validate-"+Guid.NewGuid().ToString("N")); PrivateFiles.ProtectDirectory(folder);
        try
        {
            var physical=PhysicalNetwork.Capture(settings.PhysicalInterface);var file=Path.Combine(folder,"profile.json");
            if(profile.Core=="Xray")
            {
                await File.WriteAllTextAsync(file,XrayConfig.Build(profile,AllocateTcpUdpPort(),physical.Address).ToJsonString(),ct);
                var result=await ProcessHost.RunAsync(Path.Combine(bin,"xray","xray.exe"),["run","-test","-c",file],ct);
                if(result.Code!=0)throw new InvalidDataException("Xray отклонил профиль: "+ProcessHost.Redact(result.Output));
            }
            else
            {
                await File.WriteAllTextAsync(file,SingBoxConfig.Build(settings,physical,profile,null,false,AllocatePort(),geodataDirectory:bin).ToJsonString(),ct);
                await ValidateAsync(file,ct);
            }
        }
        finally {if(Directory.Exists(folder)) Directory.Delete(folder,true);}
    }
    public async Task<DelayResult> TestProfileAsync(Profile profile, AppSettings settings, CancellationToken ct, bool verbose = false)
    {
        try{return await PortStartup.RetryAsync(_=>TestProfileOnceAsync(profile,settings,ct,verbose),ct);}
        catch(PortCollisionException e){return new(false,-1,e.Message);}
    }
    private Task<DelayResult> TestProfileOnceAsync(Profile profile, AppSettings settings, CancellationToken ct, bool verbose) =>
        RunIsolatedProfileProbeAsync(profile, settings, ct, verbose, settings.TestTimeoutSeconds,
            (port, token) => ConnectionLatency.MeasureAsync(port, settings.TestUrl, token, settings.TestTimeoutSeconds),
            error => new DelayResult(false, -1, error));

    public async Task<TrafficTestResult> TestProfileTrafficAsync(Profile profile, AppSettings settings, CancellationToken ct)
    {
        // OpenVPN has a shared lifecycle; a manual data test must never start/stop it.
        if(profile.IsOpenVpn) return TrafficTestResult.Failed("Эта проверка доступна для профилей основного VPN");
        try
        {
            return await PortStartup.RetryAsync(_ => RunIsolatedProfileProbeAsync(profile, settings, ct, false, 45,
                (port, token) => ProfileTrafficProbe.MeasureAsync(port, token), TrafficTestResult.Failed, traffic: true), ct);
        }
        catch(PortCollisionException) { return TrafficTestResult.Failed("Не удалось выделить локальные порты проверки"); }
    }

    private async Task<T> RunIsolatedProfileProbeAsync<T>(Profile profile, AppSettings settings, CancellationToken ct, bool verbose,
        int timeoutSeconds, Func<int, CancellationToken, Task<T>> measure, Func<string, T> failure, bool traffic = false)
    {
        SettingsMigration.NormalizeCore(profile);
        var testGate = profile.IsOpenVpn ? gate : probes;
        await testGate.WaitAsync(ct);
        var testRuntime = Path.Combine(runtime, "probe-" + Guid.NewGuid().ToString("N"));
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        bool temporaryOvpn = false;
        ProcessHost? testCore = null, testXray = null;
        try
        {
            OpenVpnRouteJournal.CheckPath(testRuntime);
            PrivateFiles.ProtectDirectory(testRuntime);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + (profile.IsOpenVpn ? 60 : 10)));
            var token = timeout.Token; var physical = PhysicalNetwork.Capture(settings.PhysicalInterface); var port = AllocatePort();
            var s = new AppSettings { SocksPort = port, Mode = RoutingMode.Global, DirectDns = settings.DirectDns, PhysicalInterface = settings.PhysicalInterface };
            testCore = new ProcessHost(); testXray = new ProcessHost();
            void Trace(string line) { diagnostics.Enqueue(line); while(diagnostics.Count>(verbose?16:3))diagnostics.TryDequeue(out _); }
            testCore.Line+=Trace; testXray.Line+=Trace;
            JsonObject conf; int? bridge = profile.Core == "Xray" ? AllocateTcpUdpPort() : null;
            if (profile.IsOpenVpn)
            {
                if (OpenVpn.Running && OpenVpn.ActiveProfileId != profile.Id) throw new InvalidOperationException("Сначала остановите текущий OpenVPN-профиль.");
                if (!OpenVpn.Running) { await OpenVpn.StartAsync(profile, settings.OpenVpnDns, token); temporaryOvpn = true; }
                s.Mode = RoutingMode.Rules; s.Fallback = RouteTarget.OpenVpn;
                conf = SingBoxConfig.Build(s, physical, null, OpenVpn.Link, false, port);
            }
            else
            {
                if (bridge.HasValue) await StartXrayAsync(profile, bridge.Value, physical, testXray, Path.Combine(testRuntime, "xray.json"), token, RuleValidation.Domains(settings.LocalDomains));
                conf = SingBoxConfig.Build(s, physical, profile, null, false, port, bridge);
            }
            // Testing is a dedicated proxy: no TUN, no changes to the active selector.
            conf["route"]!["rules"] = new JsonArray();
            if (profile.IsOpenVpn) conf["route"]!["final"] = "openvpn";
            if(verbose) conf["log"]!["level"]="debug";
            var path = Path.Combine(testRuntime, "test.json"); Directory.CreateDirectory(testRuntime);
            await File.WriteAllTextAsync(path, conf.ToJsonString(JsonSettings.Options), token); await ValidateAsync(path, token);
            testCore.Start(SingBox, ["run", "-c", path]); await WaitPortAsync(port, testCore, token);
            return await measure(port, token);
        }
        catch (PortCollisionException) { throw; }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if(traffic) return failure(e is OperationCanceledException ? "Истекло время запуска проверки" : $"Не удалось запустить ядро проверки ({e.GetType().Name})");
            var message=e is OperationCanceledException ? $"Нет HTTP-ответа за {settings.TestTimeoutSeconds} с" : e.Message;
            if (e is not OperationCanceledException)
                for (var inner = e.InnerException; inner != null; inner = inner.InnerException) message += " · " + inner.Message;
            if(!diagnostics.IsEmpty) message+=" · "+string.Join(" · ",diagnostics);
            return failure(ProcessHost.Redact(message));
        }
        finally
        {
            // Wait for owned children and their output readers before deleting
            // configs. A Windows sharing/ACL failure must never consume a probe
            // slot permanently, including cancellation and failed startup.
            try
            {
                try { await StopProbeCoreAsync(testCore); }
                finally { await StopProbeCoreAsync(testXray); }
            }
            finally
            {
                try { if (temporaryOvpn) await OpenVpn.StopAsync(); }
                finally
                {
                    try { if (Directory.Exists(testRuntime)) Directory.Delete(testRuntime, true); }
                    finally { testGate.Release(); }
                }
            }
        }
    }
    private static async Task StopProbeCoreAsync(ProcessHost? process)
    {
        if (process == null) return;
        try { await process.StopAsync().ConfigureAwait(false); }
        finally { process.Dispose(); }
    }
    public static async Task WaitPortAsync(int port, ProcessHost process, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            ct.ThrowIfCancellationRequested();
            var owner=LocalListener.Owner(port);
            if(owner!=0 && owner!=process.Id)throw new PortCollisionException($"Порт {port} занят другим процессом (PID {owner}).");
            if (!process.Running)
            {
                var output=process.LastOutput;
                if(PortStartup.IsCollision(output))throw new PortCollisionException("Внутренний порт занят: "+output);
                throw new IOException("Ядро завершилось до запуска: " + output);
            }
            if (LocalListener.Owner(port) == process.Id)
            {
                // The OS confirms ownership of a listening socket. Empty TCP probes caused
                // spurious SOCKS handshake failures in Xray's log on every start/test.
                await Task.Delay(120, ct);
                if (process.Running && LocalListener.Owner(port) == process.Id) return;
            }
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("Ядро не открыло локальный порт.");
    }
    private async Task StartXrayAsync(Profile p, int port, NetworkSnapshot net, ProcessHost process, string file, CancellationToken ct, IEnumerable<string>? localDomains = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var localHost = !p.Host.Contains('.') || net.Suffixes.Concat(localDomains ?? []).Concat(new[] { "local", "lan", "home.arpa" }).Any(s => p.Host.Equals(s,StringComparison.OrdinalIgnoreCase) || p.Host.EndsWith("."+s,StringComparison.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(file, XrayConfig.Build(p, port, net.Address, localHost ? net.Dns : null).ToJsonString(JsonSettings.Options), ct);
        var exe = Path.Combine(bin, "xray", "xray.exe");
        var validation = await ProcessHost.RunAsync(exe, ["run", "-test", "-c", file], ct);
        if (validation.Code != 0) throw new InvalidDataException("Xray отклонил конфигурацию: " + ProcessHost.Redact(validation.Output));
        process.Start(exe, ["run", "-c", file]); await WaitPortAsync(port, process, ct);
    }
    public async Task<bool> RefreshNetworkAsync(AppSettings settings, bool force, string reason, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!routerRequested && !OpenVpn.Running) return false;
            NetworkSnapshot current;
            try { current = await Task.Run(() => CaptureBinding(settings.PhysicalInterface), ct); }
            catch (InvalidOperationException)
            {
                RecoveryStatus = "Прямой маршрут временно недоступен: физический интерфейс изменился";
                // Preserve interception with a rejecting router when possible. Merely
                // stopping TUN would let Windows send traffic through another adapter.
                if (ActivePhysical != null) await BlockUnavailableNetworkAsync(ct);
                ActivePhysical = null;
                Log?.Invoke(RecoveryStatus); Changed?.Invoke(); return false;
            }
            if (!force && Running && PhysicalNetwork.SameBinding(ActivePhysical, current)) return false;
            var requested = VpnRequested;
            var health = await TunnelInspection.ReadAsync(Running, ListenPort, TunActive, ct);
            Log?.Invoke($"NETWORK_REBUILD revision={NetworkRevision}; reason={reason}; interface={current.Name}; index={current.Index}; IPv4={current.Address}; configured={ActivePhysical?.Address}; default={current.DefaultRoute}; IPv6={current.HasIpv6DefaultRoute}; DNS={current.Dns}; TUN={health}; VPN={ActiveProfileId}; OpenVPN={OpenVpn.ActiveProfileId}");
            networkRefresh = true;
            try { await ReconfigureInternal(settings, ct, reason: $"network-refresh: {reason}"); return true; }
            catch { VpnRequested = requested; RecoveryStatus = "Восстановление сети отложено; доступна повторная попытка"; throw; }
            finally { networkRefresh = false; }
        }
        finally { gate.Release(); }
    }
    private async Task BlockUnavailableNetworkAsync(CancellationToken ct)
    {
        var active = Path.Combine(runtime, "router.json");
        if (!File.Exists(active)) return;
        var prior = JsonNode.Parse(await File.ReadAllTextAsync(active, ct))!;
        var blocked = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
            ["inbounds"] = prior["inbounds"]!.DeepClone(),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "unavailable", ["bind_interface"] = ActivePhysical!.Name }),
            ["route"] = new JsonObject { ["rules"] = new JsonArray(new JsonObject { ["action"] = "reject" }), ["final"] = "unavailable", ["default_interface"] = ActivePhysical.Name }
        };
        var file = Path.Combine(runtime, "router.blocked.json"); await File.WriteAllTextAsync(file, blocked.ToJsonString(), ct); await ValidateAsync(file, ct);
        await core.StopAsync(); await xray.StopAsync();
        if (TunActive && StartProcessOverride == null) await TunnelInspection.WaitReleasedAsync(ct);
        File.Move(file, active, true); StartCore(active);
        await WaitPortAsync(ListenPort, core, ct);
        if (LatencyPort > 0) await WaitPortAsync(LatencyPort, core, ct);
        requiresXray = false;
    }
    public async Task StopAllAsync()
    {
        SessionRevision++; routerRequested=false; VpnRequested=false; await gate.WaitAsync();
        try { if (openVpnSidecar != null) await openVpnSidecar.DeactivateAsync(); await core.StopAsync(); await xray.StopAsync(); await OpenVpn.StopAsync(); }
        finally { ClearRuntimeState(true); gate.Release(); Changed?.Invoke(); }
    }
    public void Dispose() { try { corporateDns?.Dispose(); guardedDirect?.Dispose(); guardedVpn?.Dispose(); openVpnSidecar?.Dispose(); } finally { try { core.Dispose(); } finally { try { xray.Dispose(); } finally { OpenVpn.Dispose(); ClearRuntimeState(true); } } } }
}
public static class XrayConfig
{
    public static JsonObject Build(Profile p, int port, string bindAddress, string? endpointDns = null)
    {
        var src = JsonNode.Parse(p.OutboundJson)!.AsObject(); JsonObject output;
        if (src["protocol"] != null)
        {
            output = src;
            var server = output["settings"]?["vnext"]?[0] ?? output["settings"]?["servers"]?[0];
            if (server != null) { server["address"] = p.Host; server["port"] = p.Port; }
        }
        else
        {
            if (p.Protocol is not ("vless" or "vmess" or "trojan")) throw new NotSupportedException("Для Xray выберите VLESS, VMess, Trojan или вставьте Xray outbound JSON.");
            var user = new JsonObject { ["id"] = src["uuid"]?.ToString(), ["encryption"] = "none", ["flow"] = src["flow"]?.ToString() ?? "" };
            if (p.Protocol == "vmess") { user.Remove("encryption"); user["security"] = "auto"; }
            var server = new JsonObject { ["address"] = p.Host, ["port"] = p.Port, ["users"] = new JsonArray(user) };
            var settings = new JsonObject { ["vnext"] = new JsonArray(server) };
            if (p.Protocol == "trojan") settings = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = p.Host, ["port"] = p.Port, ["password"] = src["password"]?.ToString() }) };
            var stream = new JsonObject { ["network"] = "tcp", ["security"] = "none" };
            if (src["tls"] is JsonObject tls && (bool?)tls["enabled"] == true)
            {
                var reality = tls["reality"] is JsonObject; stream["security"] = reality ? "reality" : "tls";
                var detail = new JsonObject { ["serverName"] = tls["server_name"]?.ToString() ?? p.Host, ["fingerprint"] = tls["utls"]?["fingerprint"]?.ToString() ?? "chrome" };
                if (reality) { detail["publicKey"] = tls["reality"]!["public_key"]?.ToString(); detail["shortId"] = tls["reality"]!["short_id"]?.ToString() ?? ""; }
                else { detail["allowInsecure"] = (bool?)tls["insecure"] ?? false; if(tls["alpn"]!=null)detail["alpn"]=tls["alpn"]!.DeepClone(); }
                stream[reality ? "realitySettings" : "tlsSettings"] = detail;
            }
            if (src["transport"] is JsonObject transport)
            {
                var type = transport["type"]!.ToString(); stream["network"] = type;
                stream[type + "Settings"] = type switch { "ws" => new JsonObject { ["path"] = transport["path"]?.DeepClone(), ["headers"] = transport["headers"]?.DeepClone() }, "grpc" => new JsonObject { ["serviceName"] = transport["service_name"]?.DeepClone() }, _ => throw new NotSupportedException("Xray transport: поддерживаются TCP, WS, gRPC.") };
            }
            output = new JsonObject { ["protocol"] = p.Protocol, ["settings"] = settings, ["streamSettings"] = stream };
        }
        output["tag"] = "proxy"; output["sendThrough"] = bindAddress;
        // A separate gRPC stream per UDP source remains idle after a SOCKS UDP
        // association closes. Real nginx frontends advertise 128 streams; the
        // 129th source then stalls while existing TCP still works. Bound UDP
        // carriers with XUDP instead of increasing the frontend limit. Negative
        // TCP concurrency disables TCP mux; UDP/443 remains allowed. Explicit
        // native Xray mux settings belong to the user and must be preserved.
        if (output["protocol"]?.ToString() == "trojan" &&
            output["streamSettings"]?["network"]?.ToString() == "grpc" &&
            !output.ContainsKey("mux"))
        {
            output["mux"] = new JsonObject
            {
                ["enabled"] = true, ["concurrency"] = -1,
                ["xudpConcurrency"] = 16, ["xudpProxyUDP443"] = "allow"
            };
        }
        var streamOptions = output["streamSettings"] as JsonObject ?? new JsonObject();
        if (output["streamSettings"] == null) output["streamSettings"] = streamOptions;
        var sockets = streamOptions["sockopt"] as JsonObject ?? new JsonObject();
        if (streamOptions["sockopt"] == null) streamOptions["sockopt"] = sockets;
        sockets["domainStrategy"] = "ForceIPv4";
        // Resolve the server through a dedicated direct outbound, never through system
        // DNS/FakeIP left by another tunnel, and never through the VPN being bootstrapped.
        return new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["dns"] = new JsonObject { ["servers"] = new JsonArray(endpointDns ?? "https://1.1.1.1/dns-query"), ["queryStrategy"] = "UseIPv4", ["tag"] = "bootstrap" },
            ["routing"] = new JsonObject { ["rules"] = new JsonArray(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("bootstrap"), ["outboundTag"] = "dns-direct" }) },
            ["inbounds"] = new JsonArray(new JsonObject { ["listen"] = "127.0.0.1", ["port"] = port, ["protocol"] = "socks", ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true } }),
            ["outbounds"] = new JsonArray(output, new JsonObject { ["tag"] = "dns-direct", ["protocol"] = "freedom", ["sendThrough"] = bindAddress })
        };
    }
}
