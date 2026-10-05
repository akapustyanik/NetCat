using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
namespace NetCat.Network;
public sealed class TelegramService(string bin, string runtime) : IDisposable
{
    private readonly ProcessHost process = new();
    private readonly List<FileStream> launchFiles = [];
    // Let upstream choose its connection-pool default. v1.11 uses the pool
    // for the primary DC WebSocket path, so forcing zero disables that path.
    private const string RunnerScript = "import sys, json\nfrom proxy.tg_ws_proxy import main\nwith open(sys.argv[1], encoding='utf-8') as f: c=json.load(f)\nsys.argv=['tg-ws-proxy','--host','127.0.0.1','--port',str(c['port']),'--secret',c['secret']]\nmain()\n";
    public bool Running => process.Running;
    private sealed record ProxyConnection(int Port, string Link);
    private volatile ProxyConnection? connection;
    public bool Ready => Running && connection != null;
    public int Port { get; private set; }
    public string Link => Running ? connection?.Link ?? "" : "";
    public bool TryGetProxyLink(out string link, out int port)
    {
        // Read a single published generation: Stop/update may clear readiness
        // between the button binding and its already queued click handler.
        var current = connection;
        if (current == null || !Running) { link = ""; port = 0; return false; }
        link = current.Link; port = current.Port; return true;
    }
    public event Action<string>? Log;
    public async Task StartAsync(AppSettings settings, CancellationToken ct)
    {
        if (Running) return;
        connection = null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(settings.TelegramWsSecret, "^[a-fA-F0-9]{32}$")) settings.TelegramWsSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Port = settings.TelegramWsPort;
        if (Port is < 1 or > 65535) throw new FormatException("Порт Telegram: от 1 до 65535.");
        if (LocalListener.Owner(Port) != 0) throw new IOException($"Порт Telegram {Port} занят. Выберите другой порт WS proxy.");
        using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
        PrivateFiles.ProtectDirectory(runtime,new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
        try
        {
        var config = Path.Combine(runtime, "telegram.json");
        PrivateFiles.DeleteSecrets(runtime,"telegram.json","telegram_runner.py");
        await WriteLockedAsync(config, JsonSerializer.Serialize(new { port = Port, secret = settings.TelegramWsSecret }), ct);
        var runner = Path.Combine(runtime, "telegram_runner.py");
        // Upstream CLI only; no tray/UI modules are imported. Secret is not placed in the process command line.
        await WriteLockedAsync(runner, RunnerScript, ct);
            process.Start(Path.Combine(bin,"tg-runtime","NetCat.Telegram.exe"), ["-I","-B","-u",runner,config]);
            await RouterService.WaitPortAsync(Port,process,ct);
            connection = new(Port, $"tg://proxy?server=127.0.0.1&port={Port}&secret=dd{settings.TelegramWsSecret}");
            Log?.Invoke($"Telegram WS proxy запущен в фоне: 127.0.0.1:{Port}");
        }
        catch { await StopAsync(); throw; }
    }
    public async Task StopAsync()
    {
        connection = null;
        try { await process.StopAsync(); }
        finally { ReleaseLaunchFiles(); PrivateFiles.DeleteSecrets(runtime,"telegram.json","telegram_runner.py"); }
    }
    private async Task WriteLockedAsync(string path,string text,CancellationToken ct)
    {
        var file=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);
        launchFiles.Add(file);
        await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text),ct);await file.FlushAsync(ct);
    }
    private void ReleaseLaunchFiles(){foreach(var file in launchFiles)file.Dispose();launchFiles.Clear();}
    public void Dispose() { connection = null; process.Dispose(); ReleaseLaunchFiles(); PrivateFiles.DeleteSecrets(runtime,"telegram.json","telegram_runner.py"); }
}
