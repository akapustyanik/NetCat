using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;

// Runs reviewed upstream BAT, so execution is restricted to a disposable guest.
// Snapshot the VM first. This developer probe never changes NICs/routes/settings.
if(args.Length!=3||!PhysicalNetwork.DetectHyperVGuest())throw new InvalidOperationException("Disposable Hyper-V guest only: <modules> <batch filename> <new output directory>");
if(Process.GetProcessesByName("winws").Length!=0)throw new IOException("Stop guest Zapret explicitly before parity; never kill an unknown owner.");
var bin=Path.GetFullPath(args[0]);var batch=Path.GetFileName(args[1]);var root=Path.GetFullPath(args[2]);
if(batch!=args[1]||batch.IndexOfAny(['"','&','|','<','>','%','!','^'])>=0)throw new ArgumentException("Simple reviewed batch filename required.");
if(Directory.Exists(root))throw new IOException("Evidence directory must be new.");Directory.CreateDirectory(root);
var settings=new AppSettings {YouTube=ServiceRoute.Zapret,Discord=ServiceRoute.Zapret,ApplyBestZapret=false,TestTimeoutSeconds=8};var physical=PhysicalNetwork.Capture(settings.PhysicalInterface);
var port=OpenVpnService.FreePort();var config=Path.Combine(root,"physical-probe.json");
File.WriteAllText(config,ZapretProbes.BuildPhysicalConfig(settings,physical,port).ToJsonString());
using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(4));var ct=deadline.Token;
using var proxy=new ProcessHost();proxy.Start(Path.Combine(bin,"sing-box","sing-box.exe"),["run","-c",config]);
await RouterService.WaitPortAsync(port,proxy,ct);
using var client=new HttpClient(new SocketsHttpHandler {Proxy=new WebProxy($"socks5://127.0.0.1:{port}"),UseProxy=true,PooledConnectionLifetime=TimeSpan.Zero}){Timeout=TimeSpan.FromSeconds(8)};
var logs=new List<string>();var phases=new List<object>();var baseline=await Probe("baseline",null);
string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
async Task<List<ProbeOutcome>> Probe(string phase,List<ProbeOutcome>? previous)
{
    if(!ZapretProbes.SamePhysicalPath(physical,PhysicalNetwork.TryCapture(settings.PhysicalInterface)))throw new IOException("Physical path changed.");
    var outcomes=await ZapretProbes.RunAsync(client,ZapretProbes.Defaults,ct,(uri,token)=>ZapretProbes.GatewayHelloAsync(uri,port,8,token));
    if(!ZapretProbes.SamePhysicalPath(physical,PhysicalNetwork.TryCapture(settings.PhysicalInterface)))throw new IOException("Physical path changed.");
    foreach(var o in outcomes)logs.Add(ZapretStrategyValidation.Diagnostic(phase,physical.Index,previous?.FirstOrDefault(b=>b.Service==o.Service&&b.Name==o.Name),o));
    phases.Add(new{Phase=phase,Validation=ZapretStrategyValidation.Evaluate(previous??outcomes,outcomes,["YouTube","Discord"]),Outcomes=outcomes.Select(o=>new{o.Service,o.Name,o.Success,o.Milliseconds,o.HttpStatus,o.FailureClass})});
    return outcomes;
}
var batchPath=Path.Combine(bin,"zapret",batch);var lists=Path.Combine(bin,"zapret","lists");
Dictionary<string,string> ListHashes()=>Directory.GetFiles(lists).ToDictionary(p=>Hash(Path.GetFileName(p)),p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
var beforeLists=ListHashes();string? manualCommand=null;List<ProbeOutcome> manual;
using(var host=new ProcessHost())
{
    // `start` children remain in ProcessHost's kill-on-close job. Hold the cmd
    // parent until probes finish; all windows stay hidden via ProcessHost.
    var wrapper=Path.Combine(root,"manual.cmd");
    File.WriteAllText(wrapper,$"@echo off\r\ncall \"{batchPath}\"\r\nping -n 241 127.0.0.1 >nul\r\n",Encoding.Default);
    host.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"),["/d","/c",wrapper],Path.Combine(bin,"zapret"));
    for(int i=0;i<100&&Process.GetProcessesByName("winws").Length==0;i++)await Task.Delay(100,ct);
    using var winws=Process.GetProcessesByName("winws").Single();manualCommand=ProcessIdentity.CommandFingerprint(winws);
    await Task.Delay(1500,ct);manual=await Probe("manual",baseline);
    await host.StopAsync();
}
for(int i=0;i<50&&Process.GetProcessesByName("winws").Length!=0;i++)await Task.Delay(100,ct);
if(Process.GetProcessesByName("winws").Length!=0)throw new IOException("Manual child cleanup incomplete.");
var afterManualLists=ListHashes();List<ProbeOutcome> managed;string? managedCommand;
using(var zapret=new ZapretService(bin,Path.Combine(root,"managed-runtime")))
{
    await zapret.ApplyAsync(settings,batchPath,ct);
    using var child=Process.GetProcessById(zapret.ProcessId);managedCommand=ProcessIdentity.CommandFingerprint(child);
    managed=await Probe("netcat",baseline);await zapret.StopAsync();
}
var manualAccepted=ZapretStrategyValidation.Evaluate(baseline,manual,["YouTube","Discord"]).Accepted;
var managedAccepted=ZapretStrategyValidation.Evaluate(baseline,managed,["YouTube","Discord"]).Accepted;
var comparison=new{DeveloperOnly=true,IndependentAcceptance=false,PhysicalIfIndex=physical.Index,MainVpnWasNotStopped=true,
    YouTubeRoute=settings.YouTube.ToString(),DiscordRoute=settings.Discord.ToString(),
    BatchSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(batchPath))),ManualCommandHash=manualCommand,ManagedCommandHash=managedCommand,
    WorkingDirectoryManual="zapret/bin (BAT cd)",WorkingDirectoryManaged="zapret/bin",Environment="same inherited process environment; BAT local variables differ",
    AdapterBindingManual="upstream filters",AdapterBindingManaged="physical ifIndex",ProbeDns="same physical proxy",BeforeLists=beforeLists,AfterManualLists=afterManualLists,
    AfterManagedLists=ListHashes(),ManualWorksNetCatFails=manualAccepted&&!managedAccepted,ZeroRemainingWinws=Process.GetProcessesByName("winws").Length==0,Phases=phases};
File.WriteAllText(Path.Combine(root,"parity.json"),JsonSerializer.Serialize(comparison,new JsonSerializerOptions{WriteIndented=true}));
File.WriteAllLines(Path.Combine(root,"probe-diagnostics.log"),logs);
if(manualAccepted&&!managedAccepted)Environment.ExitCode=1;
