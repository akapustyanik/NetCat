using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Network;
public sealed record RuntimeSnapshot(Guid? ActiveProfileId,int ListenPort,int LatencyPort,int HealthSourcePort,bool TunActive,bool VpnRequested,bool RequiresXray);
public sealed class RouterService(string bin, string runtime) : IDisposable
{
    private readonly ProcessHost core = new(), xray = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim probes = new(2);
    public long SessionRevision { get; private set; }
    private bool logsAttached, requiresXray, suppressOpenVpn;
    public OpenVpnService OpenVpn { get; } = new(Path.Combine(bin, "openvpn", "openvpn.exe"), Path.Combine(runtime, "openvpn"));
    public bool VpnRequested { get; private set; }
    public Guid? ActiveProfileId { get; private set; }
    public bool ZapretAvailable { get; set; }
    public bool Running => core.Running;
    public bool VpnRunning => VpnRequested && core.Running && (!requiresXray || xray.Running);
    public int ListenPort { get; private set; }
    public int LatencyPort { get; private set; }
    public int HealthSourcePort { get; private set; }
    public bool TunActive { get; private set; }
    public bool Reconfiguring { get; private set; }
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
        if(clearRequest) VpnRequested=false;
    }
    public RuntimeSnapshot CaptureRuntime() => new(ActiveProfileId,ListenPort,LatencyPort,HealthSourcePort,TunActive,VpnRequested,requiresXray);
    private void RestoreRuntime(RuntimeSnapshot snapshot)
    {
        ActiveProfileId=snapshot.ActiveProfileId; ListenPort=snapshot.ListenPort; LatencyPort=snapshot.LatencyPort;
        HealthSourcePort=snapshot.HealthSourcePort; TunActive=snapshot.TunActive; VpnRequested=snapshot.VpnRequested; requiresXray=snapshot.RequiresXray;
    }
    public string SingBox => Path.Combine(bin, "sing-box", "sing-box.exe");
    public async Task SetVpnAsync(AppSettings s, bool enabled, CancellationToken ct = default)
    {
        SessionRevision++;
        await gate.WaitAsync(ct);
        var old = VpnRequested; var previous=CaptureRuntime();
        try { VpnRequested = enabled; await ReconfigureInternal(s, ct,previous); }
        catch { VpnRequested = core.Running && old; Changed?.Invoke(); throw; }
        finally { gate.Release(); }
    }
    public async Task SetOpenVpnAsync(AppSettings s, bool enabled, CancellationToken ct = default)
    {
        SessionRevision++;
        await gate.WaitAsync(ct);
        bool wasRunning=OpenVpn.Running;
        try
        {
            if (enabled)
            {
                var p = s.Profiles.FirstOrDefault(p => p.Id == s.OpenVpnProfileId && p.IsOpenVpn) ?? throw new InvalidOperationException("Выберите OpenVPN профиль.");
                await OpenVpn.StartAsync(p, s.OpenVpnDns, ct);
                await ReconfigureInternal(s, ct);
            }
            else
            {
                // Keep the old adapter alive until the replacement router has passed
                // validation AND started. Rollback can still use the old configuration.
                suppressOpenVpn=true;
                try { await ReconfigureInternal(s,ct); } finally { suppressOpenVpn=false; }
                await OpenVpn.StopAsync();
            }
        }
        catch { if (enabled && !wasRunning) await OpenVpn.StopAsync(); throw; }
        finally { suppressOpenVpn=false; gate.Release(); Changed?.Invoke(); }
    }

    public async Task ApplyAsync(AppSettings s, CancellationToken ct = default)
    {
        SessionRevision++;
        await gate.WaitAsync(ct); try { await ReconfigureInternal(s, ct); } finally { gate.Release(); }
    }
    public async Task SwitchProfileAsync(AppSettings s, CancellationToken ct = default, bool preflight = true, Func<bool>? canCommit = null)
    {
        var revision = ++SessionRevision;
        if (!VpnRequested) return;
        var snapshot = JsonSettings.Clone(s);
        if (preflight)
        {
            var profile = snapshot.Profiles.FirstOrDefault(p=>p.Id==snapshot.MainProfileId && !p.IsOpenVpn) ?? throw new InvalidOperationException("Выберите VPN-профиль.");
            var tested = PreflightOverride is {} test ? await test(profile,snapshot,ct) : await TestProfileAsync(profile,snapshot,ct);
            if (!tested.Success) throw new InvalidDataException("Новый профиль не прошёл проверку: " + TestFeedback.Summary(tested) + ". Текущее подключение сохранено.\n" + tested.Error);
        }
        await gate.WaitAsync(ct);
        try
        {
            if (!VpnRequested || SessionRevision != revision || canCommit?.Invoke() == false) throw new OperationCanceledException("Подключение изменено во время проверки.");
            await ReconfigureInternal(snapshot, ct);
        }
        finally { gate.Release(); }
    }
    private async Task ReconfigureInternal(AppSettings s, CancellationToken ct,RuntimeSnapshot? previous=null)
    {
        Reconfiguring = true; Changed?.Invoke();
        var requested=VpnRequested;
        previous ??= CaptureRuntime();
        try { await PortStartup.RetryAsync(async _=>{VpnRequested=requested;await ReconfigureCore(s,ct,previous);return true;},ct); }
        finally { Reconfiguring = false; Changed?.Invoke(); }
    }
    private async Task ReconfigureCore(AppSettings s, CancellationToken ct,RuntimeSnapshot previousState)
    {
        if (!logsAttached)
        {
            core.Line += line => Log?.Invoke("sing-box: " + line);
            xray.Line += line => Log?.Invoke("Xray: " + line);
            logsAttached = true;
        }
        if (!VpnRequested && (!OpenVpn.Running || suppressOpenVpn)) { await core.StopAsync(); await xray.StopAsync(); ClearRuntimeState(true); Changed?.Invoke(); return; }
        var physical = PhysicalNetwork.Capture(s.PhysicalInterface);
        var selected = VpnRequested ? s.Profiles.FirstOrDefault(p => p.Id == s.MainProfileId && !p.IsOpenVpn) ?? throw new InvalidOperationException("Выберите основной VPN-профиль.") : null;
        if (selected != null) SettingsMigration.NormalizeCore(selected);
        if (s.TelegramSocks && !await SocksAvailableAsync(s.TelegramSocksHost,s.TelegramSocksPort,ct))
        {
            Log?.Invoke("Внешний SOCKS5 Telegram недоступен. Маршрут Telegram сохранён; автоматического перехода на VPN нет. Проверьте сервер или отключите внешний SOCKS5.");
        }
        Directory.CreateDirectory(runtime);
        var localPort = s.SocksPort;
        var owner = LocalListener.Owner(localPort);
        if (owner != 0 && (!core.Running || owner != core.Id))
        {
            localPort = AllocatePort();
            Log?.Invoke($"Порт {s.SocksPort} занят другим процессом (PID {owner}). Для этой сессии выбран 127.0.0.1:{localPort}.");
        }
        int? bridge = selected?.Core == "Xray" ? AllocateTcpUdpPort() : null;
        int? latencyPort = null;
        if (selected != null) latencyPort = PortStartup.Distinct(AllocatePort,localPort,bridge);
        var healthPort = s.Tun && selected != null ? PortStartup.Distinct(AllocatePort,localPort,bridge,latencyPort) : 0;
        var config = await Task.Run(() => SingBoxConfig.Build(s, physical, selected, OpenVpn.Running && !suppressOpenVpn ? OpenVpn.Link : null, s.Tun, port: localPort, xrayPort: bridge, zapretRunning: ZapretAvailable, latencyPort: latencyPort, healthSourcePort: healthPort, geodataDirectory: bin), ct);
        var next = Path.Combine(runtime, "router.next.json");
        await File.WriteAllTextAsync(next, config.ToJsonString(JsonSettings.Options), ct);
        await ValidateAsync(next, ct);
        var active = Path.Combine(runtime, "router.json"); var old = File.Exists(active) ? await File.ReadAllTextAsync(active, ct) : null;
        var oldRunning = core.Running; var oldXrayRunning = xray.Running;
        var xrayFile = Path.Combine(runtime, "xray.json");
        var oldXray = File.Exists(xrayFile) ? await File.ReadAllTextAsync(xrayFile, ct) : null;
        await core.StopAsync(); await xray.StopAsync(); LatencyPort = 0;
        try
        {
            if (bridge.HasValue) await StartXrayAsync(selected!, bridge.Value, physical, xray, Path.Combine(runtime, "xray.json"), ct, RuleValidation.Domains(s.LocalDomains));
            File.Move(next, active, true);
            StartCore(active);
            await WaitPortAsync(localPort, core, ct); ListenPort = localPort;
            if (latencyPort.HasValue) { await WaitPortAsync(latencyPort.Value, core, ct); LatencyPort = latencyPort.Value; }
            ActiveProfileId = selected?.Id;
            requiresXray = bridge.HasValue;
            HealthSourcePort = healthPort; TunActive = s.Tun;
            Log?.Invoke($"Маршрутизатор запущен; физический адаптер: {physical.Name}; DNS: {physical.Dns}; OpenVPN: {(OpenVpn.Running ? "подключён" : "отключён")}");
        }
        catch
        {
            await core.StopAsync(); await xray.StopAsync();
            VpnRequested = false;
            if (oldRunning && old != null)
            {
                try
                {
                    var previous = JsonNode.Parse(old)!;
                    var previousOpenVpn = previous["outbounds"]!.AsArray().FirstOrDefault(n => n?["tag"]?.ToString() == "openvpn");
                    if (previousOpenVpn != null && (!OpenVpn.Running || previousOpenVpn["inet4_bind_address"]?.ToString() != OpenVpn.Link?.Address)) throw new IOException("Старый OpenVPN-адаптер уже отключён.");
                    if (oldXrayRunning && oldXray != null)
                    {
                        await File.WriteAllTextAsync(xrayFile, oldXray);
                        xray.Start(Path.Combine(bin, "xray", "xray.exe"), ["run", "-c", xrayFile]);
                        await WaitPortAsync((int)JsonNode.Parse(oldXray)!["inbounds"]![0]!["port"]!, xray, CancellationToken.None);
                    }
                    await File.WriteAllTextAsync(active, old); StartCore(active);
                    await WaitPortAsync(previousState.ListenPort, core, CancellationToken.None);
                    if(previousState.LatencyPort>0) await WaitPortAsync(previousState.LatencyPort,core,CancellationToken.None);
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
    private async Task<DelayResult> TestProfileOnceAsync(Profile profile, AppSettings settings, CancellationToken ct, bool verbose)
    {
        SettingsMigration.NormalizeCore(profile);
        var testGate = profile.IsOpenVpn ? gate : probes;
        await testGate.WaitAsync(ct);
        var testRuntime = Path.Combine(runtime, "probe-" + Guid.NewGuid().ToString("N"));
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        bool temporaryOvpn = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(settings.TestTimeoutSeconds + (profile.IsOpenVpn ? 60 : 10)));
            var token = timeout.Token; var physical = PhysicalNetwork.Capture(settings.PhysicalInterface); var port = AllocatePort();
            var s = new AppSettings { SocksPort = port, Mode = RoutingMode.Global, DirectDns = settings.DirectDns, PhysicalInterface = settings.PhysicalInterface };
            using var testCore = new ProcessHost(); using var testXray = new ProcessHost();
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
            return await ConnectionLatency.MeasureAsync(port, settings.TestUrl, token, settings.TestTimeoutSeconds);
        }
        catch (PortCollisionException) { throw; }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message=e is OperationCanceledException ? $"Нет HTTP-ответа за {settings.TestTimeoutSeconds} с" : e.Message;
            if (e is not OperationCanceledException)
                for (var inner = e.InnerException; inner != null; inner = inner.InnerException) message += " · " + inner.Message;
            if(!diagnostics.IsEmpty) message+=" · "+string.Join(" · ",diagnostics);
            return new(false,-1,ProcessHost.Redact(message));
        }
        finally
        {
            try { if (temporaryOvpn) await OpenVpn.StopAsync(); }
            finally { if (Directory.Exists(testRuntime)) Directory.Delete(testRuntime, true); testGate.Release(); }
        }
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
    public async Task StopAllAsync()
    {
        SessionRevision++; VpnRequested=false; await gate.WaitAsync();
        try { await core.StopAsync(); await xray.StopAsync(); await OpenVpn.StopAsync(); }
        finally { ClearRuntimeState(true); gate.Release(); Changed?.Invoke(); }
    }
    public void Dispose() { try { core.Dispose(); } finally { try { xray.Dispose(); } finally { OpenVpn.Dispose(); ClearRuntimeState(true); } } }
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
