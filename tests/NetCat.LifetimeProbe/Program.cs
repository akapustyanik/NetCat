using System.Diagnostics;
using System.Text.Json;
using NetCat.Engine;
using NetCat.Network;
using NetCat.Core;

if(args is ["cleanup-driver", var modules])
{
    Console.WriteLine(WinDivertDriverCleanup.TryCleanup(modules));
    return;
}

if(args is ["unsigned-update-admission"])
{
    var root = Path.Combine(Path.GetTempPath(), "NetCat-UnsignedAdmission-" + Guid.NewGuid().ToString("N"));
    using var proxy = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); proxy.Start();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    bool rejected = false;
    try
    {
        await NetCat.Updater.PortableUpdate.PrepareAsync(new("netcat", "akapustyanik/NetCat", "9.0.0", "NetCat-v9.0.0-win-x64.zip",
            "https://github.com/akapustyanik/NetCat/releases/download/v9.0.0/NetCat-v9.0.0-win-x64.zip", new string('A', 64)),
            root, new HashSet<string>(), deadline.Token, ((System.Net.IPEndPoint)proxy.LocalEndpoint).Port);
    }
    catch (InvalidDataException error) { rejected = error.Message.Contains("подписанную"); }
    if (!rejected || proxy.Pending() || Directory.Exists(root)) throw new InvalidOperationException("Unsigned application was not rejected before update work.");
    Console.WriteLine("unsigned-update-rejected-before-work");
    return;
}

if(args is ["observer-exit"])
{
    using var host = new ProcessHost();
    var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    host.Exited += (_, _) => throw new InvalidOperationException("Exit observer failed");
    host.Exited += (_, code) => { if(code == 0) exited.TrySetResult(); };
    host.Start(ProcessHost.PowerShellPath, ["-NoProfile", "-NonInteractive", "-Command", "exit 0"]);
    await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await host.StopAsync();
    Console.WriteLine("exit-observer-complete");
    return;
}

if(args.Length>=2 && args[0]=="--config")
{
    // Harmless preflight stand-in: records that the client would have launched.
    const string marker="# NETCAT_TEST_PREFLIGHT_MARKER ";
    var line=File.ReadLines(args[1]).Single(s=>s.StartsWith(marker,StringComparison.Ordinal));
    File.WriteAllText(line[marker.Length..],"launched");return;
}
if(args[0].StartsWith("--wf-",StringComparison.Ordinal)) {await Task.Delay(TimeSpan.FromMinutes(2));return;} // Harmless winws stand-in for cancellation tests.
var mode=args[0]; var file=Path.GetFullPath(args[1]);
if(mode=="dry-run")
{
    Environment.SetEnvironmentVariable("__COMPAT_LAYER","RunAsInvoker");
    var root=Path.Combine(args[2],"zapret");var checkedCount=0;
    var hosts=file+".hosts.txt";File.WriteAllLines(hosts,ServiceDomains.YouTube.Concat(ServiceDomains.Discord));
    foreach(var batch in Directory.GetFiles(root,"general*.bat")) foreach(var scenario in new[]{(true,true),(true,false),(false,true)})
    {
        var result=await ProcessHost.RunAsync(Path.Combine(root,"bin","winws.exe"),ZapretArguments.Build(batch,root,hosts,scenario.Item1,scenario.Item2,24).Append("--dry-run"));
        if(result.Code!=0 && !result.Output.Contains("already running with the same filter")) throw new Exception(Path.GetFileName(batch)+" "+scenario+": "+result.Output);checkedCount++;
    }
    File.WriteAllText(file,checkedCount.ToString());return;
}
if(mode=="leaf") { await Task.Delay(TimeSpan.FromMinutes(2)); return; }
if(mode=="publication-race")
{
    await PublishAsync(file,"[1,2]",async()=>
    {
        File.WriteAllText(file+".writing","");
        while(!File.Exists(file+".release"))await Task.Delay(20);
    });
    return;
}
if(mode is "child" or "child-exit")
{
    var psi=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}; psi.ArgumentList.Add("leaf"); psi.ArgumentList.Add(file);
    using var leaf=Process.Start(psi)!;
    await PublishAsync(file,JsonSerializer.Serialize(new[]{Environment.ProcessId,leaf.Id}));
    if(mode=="child") await Task.Delay(TimeSpan.FromMinutes(2));
    return;
}
if(mode=="winws-owner")
{
    var host=new ProcessHost(); host.Start(args[2],JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[3]))!);
    await Task.Delay(800); if(!host.Running) throw new Exception(host.LastOutput);
    File.WriteAllText(file,JsonSerializer.Serialize(new[]{host.Id}));
    while(!File.Exists(file+".command")) await Task.Delay(20);
    var action=File.ReadAllText(file+".command");
    if(action=="stop") await host.StopAsync(); else if(action=="dispose")host.Dispose();
    File.WriteAllText(file+".done","done"); GC.KeepAlive(host);
    if(action=="exit") Environment.Exit(0);
    await Task.Delay(TimeSpan.FromMinutes(2)); return;
}
if(mode=="service-cancel")
{
    using var service=new ZapretService(args[2],args[3]);
    using var cancel=new CancellationTokenSource();
    var started=service.ApplyAsync(new AppSettings {YouTube=ServiceRoute.Zapret,Discord=ServiceRoute.Zapret},args[4],cancel.Token);
    var until=DateTime.UtcNow.AddSeconds(10); while(service.ProcessId==0 && !started.IsCompleted && DateTime.UtcNow<until) await Task.Delay(5);
    var id=service.ProcessId; cancel.Cancel();
    try {await started;} catch(OperationCanceledException) { }
    await service.StopAsync();
    if(service.Running || service.ActiveStrategy.Length>0)throw new Exception("Zapret survives cancellation");
    File.WriteAllText(file,JsonSerializer.Serialize(new[]{id})); return;
}
var owner=new ProcessHost(new SelfTrust()); owner.Start(Environment.ProcessPath!,[mode=="exited-root"?"child-exit":"child",file]);
while(!File.Exists(file+".command")) await Task.Delay(20);
var command=File.ReadAllText(file+".command");
if(command=="stop") await owner.StopAsync();
else if(command=="dispose") owner.Dispose();
File.WriteAllText(file+".done","done");
GC.KeepAlive(owner);
if(command=="exit") Environment.Exit(0);
await Task.Delay(TimeSpan.FromMinutes(2));

// File existence is the parent's readiness signal. Publish only after the writer
// is closed; a visible Create/Write file can still be locked or contain partial JSON.
static async Task PublishAsync(string path,string contents,Func<Task>? whileWriting=null)
{
    var temporary=path+"."+Guid.NewGuid().ToString("N")+".pending";
    try
    {
        await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            await using var writer=new StreamWriter(stream);
            await writer.WriteAsync(contents);await writer.FlushAsync();
            if(whileWriting!=null)await whileWriting();
        }
        File.Move(temporary,path);
    }
    finally{if(File.Exists(temporary))File.Delete(temporary);}
}

sealed class SelfTrust : IExecutableTrustPolicy
{
    public IDisposable AcquireExecutable(string path)
    {
        if(Path.GetFullPath(path)!=Environment.ProcessPath)throw new InvalidDataException("Unexpected fixture executable");
        return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
    }
    public IDisposable AcquirePackage(string key,string folder)=>throw new NotSupportedException();
}
