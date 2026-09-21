using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Network;
public sealed class OpenVpnService(string executable, string runtime) : IDisposable
{
    private readonly ProcessHost host = new();
    private WintunAdapter? adapter;
    private string? routeJournal;
    private int managementPort;
    private string managementPassword = "";
    public Func<int> AllocateManagementPort {get;init;} = FreePort;
    public OpenVpnLink? Link { get; private set; }
    public Guid? ActiveProfileId { get; private set; }
    public bool Running => host.Running && Link != null;
    public async Task<OpenVpnLink> StartAsync(Profile profile, string dnsOverride, CancellationToken ct)
    {
        if(Running) { if(ActiveProfileId==profile.Id) return Link!; throw new InvalidOperationException("Сначала остановите текущий OpenVPN-профиль."); }
        PrivateFiles.ProtectDirectory(runtime);
        try { var link=await PortStartup.RetryAsync(_=>StartCoreAsync(profile,dnsOverride,ct),ct); ActiveProfileId=profile.Id; return link; }
        catch { ActiveProfileId=null; CleanupSecrets(runtime); throw; }
    }
    public static void CleanupSecrets(string folder) => PrivateFiles.DeleteSecrets(folder,"auth.pass","management.pass","openvpn.conf");
    private async Task<OpenVpnLink> StartCoreAsync(Profile profile, string dnsOverride, CancellationToken ct)
    {
        if (Running) return Link!;
        Directory.CreateDirectory(runtime);
        var config = OpenVpnConfiguration.Prepare(profile.OpenVpnConfig);
        var configPath = Path.Combine(runtime, "openvpn.conf"); await File.WriteAllTextAsync(configPath, config, ct);
        managementPort = AllocateManagementPort(); managementPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var passwordFile = Path.Combine(runtime, "management.pass"); await File.WriteAllTextAsync(passwordFile, managementPassword, ct);
        var args = new List<string> { "--config", configPath, "--management", "127.0.0.1", managementPort.ToString(), passwordFile, "--verb", "3" };
        if (profile.Username.Length > 0) { var auth = Path.Combine(runtime, "auth.pass"); await File.WriteAllLinesAsync(auth, [profile.Username, profile.Password], ct); args.AddRange(["--auth-user-pass", auth, "--auth-nocache"]); }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); string gateway = "", dns = dnsOverride;
        void ParseLine(string line)
        {
            if(PortStartup.IsCollision(line)) ready.TrySetException(new PortCollisionException("OpenVPN management port занят: "+line));
            var gw = Regex.Match(line, @"route-gateway\s+([0-9.]+)"); if (gw.Success) gateway = gw.Groups[1].Value;
            var dhcp = Regex.Match(line, @"dhcp-option DNS\s+([0-9.]+)"); if (dhcp.Success && string.IsNullOrWhiteSpace(dnsOverride)) dns = dhcp.Groups[1].Value;
            if (line.Contains("Initialization Sequence Completed")) ready.TrySetResult();
            if (line.Contains("AUTH_FAILED")) ready.TrySetException(new InvalidOperationException("OpenVPN: сервер отклонил авторизацию."));
            if (line.Contains("Exiting due to fatal error")) ready.TrySetException(new InvalidOperationException("OpenVPN: ошибка конфигурации или запуска драйвера."));
        }
        host.Line += ParseLine;
        try
        {
            if(LocalListener.Owner(managementPort)!=0)throw new PortCollisionException("Порт управления OpenVPN уже занят.");
            adapter?.Dispose();
            adapter = new WintunAdapter(Path.Combine(Path.GetDirectoryName(executable)!, "wintun.dll"));
            host.Start(executable, args, Path.GetDirectoryName(executable), false);
            await RouterService.WaitPortAsync(managementPort,host,ct);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == "NetCat-OpenVPN" && n.OperationalStatus == OperationalStatus.Up) ?? throw new IOException("OpenVPN не создал адаптер NetCat-OpenVPN.");
            var props = nic.GetIPProperties(); var address = props.UnicastAddresses.First(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Address.ToString();
            if (!IPAddress.TryParse(gateway, out _)) gateway = props.GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? "0.0.0.0";
            if (!IPAddress.TryParse(dns, out _)) throw new IOException("Сервер OpenVPN не передал DNS. Укажите DNS корпоративного туннеля в настройках маршрутизации.");
            Link = new(nic.Name, props.GetIPv4Properties().Index, address, gateway, dns);
            // The high-metric route is usable by sockets bound to THIS interface, but must not replace the physical default.
            routeJournal = Path.Combine(runtime, "openvpn-route.json");
            var existing = await PhysicalNetwork.PowerShell($"@(Get-NetRoute -InterfaceIndex {Link.Index} -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue).Count", ct);
            if (existing.Code != 0 || existing.Output.Trim() != "0") throw new IOException("Неожиданный default route на OpenVPN-адаптере; запуск остановлен.");
            await File.WriteAllTextAsync(routeJournal, JsonSerializer.Serialize(Link), ct);
            var add = await PhysicalNetwork.PowerShell($"New-NetRoute -InterfaceIndex {Link.Index} -DestinationPrefix '0.0.0.0/0' -NextHop {PhysicalNetwork.Literal(gateway)} -RouteMetric 9999 -PolicyStore ActiveStore | Out-Null", ct);
            if (add.Code != 0) throw new IOException("Не удалось создать изолированный маршрут OpenVPN.");
            return Link;
        }
        catch { await StopAsync(); throw; }
        finally { host.Line -= ParseLine; }
    }
    public async Task StopAsync()
    {
        try
        {
            if (host.Running)
            {
                try { using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, managementPort).WaitAsync(TimeSpan.FromSeconds(2)); using var writer = new StreamWriter(tcp.GetStream()) { AutoFlush = true }; await writer.WriteLineAsync(managementPassword); await writer.WriteLineAsync("signal SIGTERM"); await Task.Delay(500); } catch (Exception e) when (e is IOException or SocketException or TimeoutException) { }
            }
            await host.StopAsync();
            await RecoverAsync(runtime);
        }
        finally { Link=null; ActiveProfileId=null; adapter?.Dispose(); adapter=null; managementPassword=""; managementPort=0; CleanupSecrets(runtime); }
    }
    public static async Task RecoverAsync(string runtime)
    {
        var path = Path.Combine(runtime, "openvpn-route.json"); if (!File.Exists(path)) return;
        var link = JsonSerializer.Deserialize<OpenVpnLink>(await File.ReadAllTextAsync(path));
        if (link != null)
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == "NetCat-OpenVPN");
            if (nic != null && nic.GetIPProperties().GetIPv4Properties().Index == link.Index)
            {
                var result = await PhysicalNetwork.PowerShell($"Get-NetRoute -InterfaceIndex {link.Index} -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object {{ $_.NextHop -eq {PhysicalNetwork.Literal(link.Gateway)} -and $_.RouteMetric -eq 9999 }} | Remove-NetRoute -Confirm:$false");
                if (result.Code != 0) throw new IOException("Не удалось убрать маршрут NetCat OpenVPN; журнал восстановления сохранён.");
            }
        }
        File.Delete(path);
    }
    public static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    public static int FreeTcpUdpPort()
    {
        for (var i=0;i<100;i++)
        {
            using var tcp = new TcpListener(IPAddress.Loopback,0); tcp.Start(); var port=((IPEndPoint)tcp.LocalEndpoint).Port;
            using var udp = new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp) { ExclusiveAddressUse=true };
            try { udp.Bind(new IPEndPoint(IPAddress.Loopback,port)); return port; } catch(SocketException) { }
        }
        throw new IOException("Не найден свободный TCP/UDP-порт для Xray.");
    }
    public void Dispose() { try { host.Dispose(); adapter?.Dispose(); } finally { adapter = null; Link=null; ActiveProfileId=null; managementPort=0; managementPassword=""; CleanupSecrets(runtime); } }
}
