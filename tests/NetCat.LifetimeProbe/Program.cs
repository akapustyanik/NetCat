using System.Diagnostics;
using System.Text.Json;
using NetCat.Engine;
using NetCat.Network;
using NetCat.Core;

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
if(mode is "child" or "child-exit")
{
    var psi=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}; psi.ArgumentList.Add("leaf"); psi.ArgumentList.Add(file);
    using var leaf=Process.Start(psi)!;
    File.WriteAllText(file,JsonSerializer.Serialize(new[]{Environment.ProcessId,leaf.Id}));
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
var owner=new ProcessHost(); owner.Start(Environment.ProcessPath!,[mode=="exited-root"?"child-exit":"child",file]);
while(!File.Exists(file+".command")) await Task.Delay(20);
var command=File.ReadAllText(file+".command");
if(command=="stop") await owner.StopAsync();
else if(command=="dispose") owner.Dispose();
File.WriteAllText(file+".done","done");
GC.KeepAlive(owner);
if(command=="exit") Environment.Exit(0);
await Task.Delay(TimeSpan.FromMinutes(2));
