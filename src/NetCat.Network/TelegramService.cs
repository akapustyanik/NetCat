using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
namespace NetCat.Network;
public sealed class TelegramService(string bin, string runtime) : IDisposable
{
    private readonly ProcessHost process = new();
    private readonly List<FileStream> launchFiles = [];
    public bool Running => process.Running;
    public int Port { get; private set; }
    public string Link { get; private set; } = "";
    public event Action<string>? Log;
    public async Task StartAsync(AppSettings settings, CancellationToken ct)
    {
        if (Running) return;
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
        await WriteLockedAsync(runner, "import sys, json\nfrom proxy.tg_ws_proxy import main\nwith open(sys.argv[1], encoding='utf-8') as f: c=json.load(f)\nsys.argv=['tg-ws-proxy','--host','127.0.0.1','--port',str(c['port']),'--secret',c['secret'],'--pool-size','0']\nmain()\n", ct);
            process.Start(Path.Combine(bin,"tg-runtime","NetCat.Telegram.exe"), ["-I","-B","-u",runner,config]);
            await RouterService.WaitPortAsync(Port,process,ct);
            Link = $"tg://proxy?server=127.0.0.1&port={Port}&secret=dd{settings.TelegramWsSecret}";
            Log?.Invoke($"Telegram WS proxy запущен в фоне: 127.0.0.1:{Port}");
        }
        catch { await StopAsync(); throw; }
    }
    public async Task StopAsync()
    {
        try { await process.StopAsync(); }
        finally { Link=""; ReleaseLaunchFiles(); PrivateFiles.DeleteSecrets(runtime,"telegram.json","telegram_runner.py"); }
    }
    private async Task WriteLockedAsync(string path,string text,CancellationToken ct)
    {
        var file=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);
        launchFiles.Add(file);
        await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text),ct);await file.FlushAsync(ct);
    }
    private void ReleaseLaunchFiles(){foreach(var file in launchFiles)file.Dispose();launchFiles.Clear();}
    public void Dispose() { process.Dispose(); ReleaseLaunchFiles(); PrivateFiles.DeleteSecrets(runtime,"telegram.json","telegram_runner.py"); }
}
