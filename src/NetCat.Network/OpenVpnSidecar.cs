using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Network;

public sealed class OpenVpnSidecar : IDisposable
{
    private readonly string executable, folder;
    private readonly ProcessHost process = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly OpenVpnGatewayLease lease;
    private readonly OpenVpnDestinationGateway destination;
    private readonly OpenVpnDestinationLeases? routeAuthority;
    private OpenVpnGateway backend;
    private bool reallocateBackend;
    public Func<int> AllocateBackendPort { get; set; } = OpenVpnService.FreeTcpUdpPort;
    private long invalidation;
    private bool disposed;
    private int publishedPid;
    public int DnsUpstreamPort { get; init; } = 53;
    private readonly object ownershipGate = new();
    private readonly object publicationGate = new();
    private OpenVpnLink? lastUnscopedLink;
    private volatile bool routingRequested = true;
    public bool RoutingRequested
    {
        get => routingRequested;
        set { lock (ownershipGate) { routingRequested = value; if (!value) lastUnscopedLink = null; } }
    }
    public OpenVpnOwnership Ownership { get; private set; } = new([], []);
    private string? applied;
    private (Guid?, long, Guid)? appliedGeneration;
    private volatile bool active;
    public OpenVpnGateway Gateway { get; }
    public int ProcessId => process.Running ? process.Id : 0;
    public bool Running => process.Running;
    public bool Active => active && process.Running;
    public event Action<int, int>? ProcessExited { add => process.Exited += value; remove => process.Exited -= value; }
    public Action<string>? Log { get; set; }
    public Action<AppSettings, OpenVpnLink?, OpenVpnOwnership>? OwnershipPrepared { get; set; }

    public OpenVpnSidecar(string executable, string folder, OpenVpnDestinationLeases? destinationLeases = null)
    {
        this.executable = executable; this.folder = folder;
        routeAuthority = destinationLeases;
        OpenVpnRouteJournal.CheckPath(folder);
        PrivateFiles.ProtectDirectory(folder);
        lease = new();
        // A standalone sidecar starts closed until its owner publishes a verified
        // destination generation. Production shares OpenVpnService's manager.
        destination = new(destinationLeases ?? new(Path.Combine(folder,"unpublished-routes.json"),()=>[],(_,_)=>throw new IOException("No route owner.")),()=>Volatile.Read(ref publishedPid));
        destination.Diagnostic=line=>Log?.Invoke(line);
        Gateway = new(lease.Port, lease.DnsPort, Path.Combine(folder, "owned-routes.json"), "netcat", Convert.ToHexString(RandomNumberGenerator.GetBytes(24)))
            { TransportKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
              OwnedRouteKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
              ExplicitRouteKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var port = PortStartup.Distinct(OpenVpnService.FreeTcpUdpPort, Gateway.SocksPort, Gateway.DnsPort);
        backend = Gateway with { SocksPort = port };
        process.Exited += (pid, _) => { if (pid == Volatile.Read(ref publishedPid)) Invalidate(); };
        process.Line += line => Log?.Invoke("OpenVPN sidecar: " + line);
    }

    public void RememberRoutes(AppSettings settings, OpenVpnLink? link = null, Func<bool>? stillCurrent = null)
    {
        lock (ownershipGate)
        {
            if (stillCurrent?.Invoke() == false) throw new OperationCanceledException("Состояние OpenVPN изменилось.");
            ValidateOwner(link);

            if (routingRequested && link is { ProfileId: null }) lastUnscopedLink = link;
            Ownership = routingRequested ? OpenVpnOwnership.Build(settings, link) : new([], []);
            OwnershipPrepared?.Invoke(settings, link, Ownership);

            if (stillCurrent?.Invoke() == false) throw new OperationCanceledException("Состояние OpenVPN изменилось.");

            WriteRules(Gateway.DomainsPath, new JsonObject { ["version"] = 3, ["rules"] =
                !routingRequested || string.IsNullOrWhiteSpace(settings.OpenVpnDomains) ? new JsonArray() : new JsonArray(new JsonObject { ["domain_suffix"] = SingBoxConfig.Array(RuleValidation.Domains(settings.OpenVpnDomains)) }) }.ToJsonString());
            var knownRoutes = Ownership.Known.Concat(lastUnscopedLink?.LearnedRoutes ?? []).Distinct().Order().ToArray();
            var rules = new JsonObject { ["version"] = 3, ["rules"] = knownRoutes.Length == 0 ? new JsonArray() :
                new JsonArray(new JsonObject { ["ip_cidr"] = SingBoxConfig.Array(knownRoutes.Order()) }) };
            var text = rules.ToJsonString();
            WriteRules(Gateway.RulesPath, text);
        }
    }

    private static void WriteRules(string path, string text)
    {
        OpenVpnRouteJournal.CheckPath(path); OpenVpnRouteJournal.CheckPath(path + ".next");
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path + ".next", text); File.Move(path + ".next", path, true);
    }

    private bool ValidateOwner(OpenVpnLink? link)
    {
        if (link == null) return false;
        if (routeAuthority != null)
        {
            OpenVpnLink current;
            try { current = routeAuthority.Capture().Link; }
            catch (IOException)
            {
                // Legacy unscoped configuration may be prepared before an owner
                // exists, but must never authorize corporate egress. A published
                // current owner still rejects this unscoped replacement below.
                if(link.ProfileId==null&&link.Generation==0&&link.RouteOwnerId==Guid.Empty)return false;
                throw new OperationCanceledException("Владелец маршрутов OpenVPN не готов.");
            }
            if (current.ProfileId != link.ProfileId || current.Generation != link.Generation || current.RouteOwnerId != link.RouteOwnerId)
                throw new OperationCanceledException("Устаревший владелец маршрутов OpenVPN.");
            // A physical epoch can replace the owner without changing control
            // revision. The lease manager, not Guid ordering, authorizes it.
            return true;
        }
        if (appliedGeneration.HasValue && link.ProfileId == appliedGeneration.Value.Item1 &&
            (link.Generation < appliedGeneration.Value.Item2 ||
             link.Generation == appliedGeneration.Value.Item2 && link.RouteOwnerId != appliedGeneration.Value.Item3))
            throw new OperationCanceledException("Устаревшее поколение OpenVPN.");
        return true;
    }

    public static JsonObject BuildConfig(OpenVpnGateway gateway, OpenVpnLink? link, string dnsOverride = "", OpenVpnOwnership? ownership = null, int dnsPort = 53, OpenVpnDestinationEndpoint? destination = null)
    {
        var outbounds = new JsonArray();
        var dns = string.IsNullOrWhiteSpace(dnsOverride) ? link?.Dns : dnsOverride;
        if (link != null)
        {
            ArgumentNullException.ThrowIfNull(destination);
            foreach(var role in new[]{"explicit","owned"})outbounds.Add(new JsonObject {
                ["type"]="socks",["tag"]=role=="explicit"?"corp":"corp-owned",["server"]="127.0.0.1",["server_port"]=destination.Port,
                ["version"]="5",["username"]=role,["password"]=role=="explicit"?destination.ExplicitPassword:destination.OwnedPassword });
        }
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray(
                new JsonObject { ["type"] = "socks", ["listen"] = "127.0.0.1", ["listen_port"] = gateway.SocksPort,
                    ["users"] = new JsonArray(new JsonObject { ["username"] = gateway.Username, ["password"] = gateway.Password }) },
                new JsonObject { ["type"] = "direct", ["tag"] = "dns-in", ["listen"] = "127.0.0.1", ["listen_port"] = gateway.DnsPort,
                    ["override_address"] = dns ?? "127.0.0.1", ["override_port"] = 53 }),
            ["outbounds"] = outbounds,
            ["route"] = link == null
                ? new JsonObject { ["rules"] = new JsonArray(new JsonObject { ["action"] = "reject" }) }
                : new JsonObject { ["final"] = "corp", ["default_domain_resolver"] = "corp-dns" }
        };
        if (link != null) config["dns"] = new JsonObject
        {
            ["servers"] = new JsonArray(new JsonObject { ["type"] = "udp", ["tag"] = "corp-dns", ["server"] = dns,
                ["bind_interface"] = link.Name, ["inet4_bind_address"] = link.Address }), ["final"] = "corp-dns"
        };
        if (link != null && ownership != null)
        {
            var rules = new JsonArray();
            if (ownership.Active.Length > 0) rules.Add(new JsonObject { ["ip_cidr"] = SingBoxConfig.Array(ownership.Active), ["outbound"] = "corp" });
            if (ownership.Blocked.Length > 0) rules.Add(new JsonObject { ["ip_cidr"] = SingBoxConfig.Array(ownership.Blocked), ["action"] = "reject" });
            config["route"]!["rules"] = rules;
        }
        if (gateway.TransportKey != null)
        {
            config["inbounds"] = new JsonArray(new JsonObject { ["type"] = "shadowsocks", ["tag"] = "gateway-in",
                ["listen"] = "127.0.0.1", ["listen_port"] = gateway.SocksPort,
                ["method"] = "2022-blake3-aes-256-gcm", ["password"] = gateway.TransportKey,
                ["users"] = new JsonArray(new JsonObject { ["name"] = "owned", ["password"] = gateway.OwnedRouteKey },
                    new JsonObject { ["name"] = "explicit", ["password"] = gateway.ExplicitRouteKey }) });
            if (link != null)
            {
                config["dns"]!["servers"]![0]!["server_port"] = dnsPort;
                var rules = new JsonArray(); config["route"]!["rules"] = rules;
                // DNS address is a virtual target inside authenticated transport;
                // it is never sent as plain UDP to an unowned loopback socket.
                rules.Add(new JsonObject { ["auth_user"] = SingBoxConfig.Array(["explicit"]), ["ip_cidr"] = SingBoxConfig.Array(["127.0.0.1/32"]), ["port"] = gateway.DnsPort, ["action"] = "hijack-dns" });
                // An independently authenticated channel preserves intentional user
                // routes. Automatic ownership is allowlisted to this generation,
                // so even a delayed main rule-set reload cannot leak old prefixes.
                rules.Add(new JsonObject { ["auth_user"] = SingBoxConfig.Array(["explicit"]), ["outbound"] = "corp" });
                if (ownership?.Active.Length > 0) rules.Add(new JsonObject { ["auth_user"] = SingBoxConfig.Array(["owned"]), ["ip_cidr"] = SingBoxConfig.Array(ownership.Active), ["outbound"] = "corp-owned" });
                rules.Add(new JsonObject { ["action"] = "reject" });
                config["dns"]!["disable_cache"] = true;
            }
        }
        return config;
    }

    public async Task ApplyAsync(AppSettings settings, OpenVpnLink? link, CancellationToken ct, Func<bool>? stillCurrent = null)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        bool transitioned = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Obsolete queued work is not a runtime failure. It must leave an
            // existing valid backend and ownership files completely untouched.
            ct.ThrowIfCancellationRequested();
            if (stillCurrent?.Invoke() == false)
            {
                // Legacy unscoped callers provide a global validity predicate,
                // not a profile/generation identity. A false predicate revokes
                // that whole legacy lease. Production scoped stale work cannot
                // revoke a newer, independently identified backend.
                if (link is { ProfileId: null }) Invalidate();
                throw new OperationCanceledException("Состояние OpenVPN изменилось.");
            }
            ValidateOwner(link);

            RememberRoutes(settings, link, stillCurrent);
            var dnsPort = DnsUpstreamPort != 53 ? DnsUpstreamPort : string.IsNullOrWhiteSpace(settings.OpenVpnDns) ? link?.DnsPort ?? 53 : 53;
            destination.VerifyOwnership();
            var config = BuildConfig(backend, link, settings.OpenVpnDns, Ownership, dnsPort, destination.Endpoint).ToJsonString();
            (Guid?, long, Guid)? generation = link == null ? null : (link.ProfileId, link.Generation,link.RouteOwnerId);
            long revision;
            lock (publicationGate)
            {
                if (stillCurrent?.Invoke() == false) throw new OperationCanceledException("Состояние OpenVPN изменилось.");
                if (process.Running && applied == config && appliedGeneration == generation) return;
                revision = invalidation; publishedPid = 0;
            }
            if (reallocateBackend)
            {
                backend = Gateway with { SocksPort = PortStartup.Distinct(AllocateBackendPort, Gateway.SocksPort, Gateway.DnsPort, backend.SocksPort) };
                reallocateBackend = false;
            }
            transitioned = true;
            active = false; destination.Invalidate(); lease.SetBackend(null); await process.StopAsync().ConfigureAwait(false); applied = null;
            var failedBackendPorts = new HashSet<int>();
            await PortStartup.RetryAsync(async attempt =>
            {
                if (attempt > 0) backend = Gateway with { SocksPort = PortStartup.Distinct(AllocateBackendPort,
                    [Gateway.SocksPort, Gateway.DnsPort, backend.SocksPort, .. failedBackendPorts.Select(port => (int?)port)]) };
                config = BuildConfig(backend, link, settings.OpenVpnDns, Ownership, dnsPort, destination.Endpoint).ToJsonString();
                var file = Path.Combine(folder, "gateway.json");
                OpenVpnRouteJournal.CheckPath(file);
                await File.WriteAllTextAsync(file, config, ct).ConfigureAwait(false);
                var check = await ProcessHost.RunAsync(executable, ["check", "-c", file], ct).ConfigureAwait(false);
                if (check.Code != 0) throw new IOException("OpenVPN sidecar configuration: " + ProcessHost.Redact(check.Output));
                try
                {
                    process.Start(executable, ["run", "-c", file]);
                    await RouterService.WaitPortAsync(backend.SocksPort, process, ct).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    bool collision = PortStartup.IsCollision(process.LastOutput) || error is PortCollisionException;
                    await process.StopAsync().ConfigureAwait(false);
                    if (collision) { failedBackendPorts.Add(backend.SocksPort); reallocateBackend = true; throw new PortCollisionException("Внутренний порт шлюза OpenVPN занят; при локальном повторе будет выбран новый порт."); }
                    throw;
                }
                return true;
            }, ct, attempts: 3).ConfigureAwait(false);
            Publish(revision, link, config, stillCurrent, ct);
            // A successful start consumed the pending port reallocation.
            reallocateBackend = false;
            Log?.Invoke($"OPENVPN_SIDECAR state={(Active ? "started" : "rejecting")} pid={ProcessId} {RuntimeIdentityDiagnostic.Owner(link)} appliedOwner={RuntimeIdentityDiagnostic.Alias(appliedGeneration?.Item3)}");
            Log?.Invoke(link == null
                ? "OPENVPN_OVERLAY_REMOVE dnsDetached=true mainRouterRestart=false tunRestart=false"
                : $"OPENVPN_OVERLAY_APPLY interface={link.Name} routes={string.Join(",", link.LearnedRoutes)} dns={(string.IsNullOrWhiteSpace(settings.OpenVpnDns) ? link.Dns : settings.OpenVpnDns)} mainRouterRestart=false tunRestart=false");
        }
        catch (OperationCanceledException)
        {
            if (link is { ProfileId: null } && stillCurrent?.Invoke() == false)
            {
                Invalidate();
            }
            throw;
        }
        catch
        {
            if (transitioned) { active = false; destination.Invalidate(); lease.SetBackend(null); await process.StopAsync().ConfigureAwait(false); applied = null; }
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task DeactivateAsync(Func<bool>? stillRequired = null)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (stillRequired != null && !stillRequired()) return; Invalidate(); await process.StopAsync().ConfigureAwait(false); applied = null; Log?.Invoke("OPENVPN_SIDECAR state=stopped mainRouterRestart=false tunRestart=false"); }
        finally { gate.Release(); }
    }
    private void Publish(long revision, OpenVpnLink? link, string config, Func<bool>? stillCurrent, CancellationToken ct)
    {
        lock (publicationGate)
        {
            ct.ThrowIfCancellationRequested();
            if (disposed || revision != invalidation || stillCurrent?.Invoke() == false || !process.Running)
                throw new OperationCanceledException("Поколение OpenVPN изменилось во время запуска шлюза.");
            var authorized=ValidateOwner(link);
            destination.VerifyOwnership(); publishedPid=process.Id;
            if(authorized)destination.Activate();else destination.Invalidate();
            lease.SetBackend(backend.SocksPort); applied = config; appliedGeneration = link == null ? null : (link.ProfileId, link.Generation,link.RouteOwnerId); active = authorized;
        }
    }
    public void Invalidate() { lock (publicationGate) { invalidation++; publishedPid = 0; active = false; applied = null; destination.Invalidate(); lease.SetBackend(null); } }
    public void Dispose() { disposed = true; Invalidate(); try{destination.Dispose();}finally{lease.Dispose();process.Dispose();} }
}
