using System.Diagnostics;
using System.Text.Json;
using NetCat.Engine;
using NetCat.Network;
using NetCat.Core;
using Xunit;
namespace NetCat.Tests;
public sealed class LifetimeTests
{
    private sealed class IsolatedZapret : ZapretService
    {
        private readonly string leaseName;
        public IsolatedZapret(string bin, string runtime) : base(bin, runtime,new FixtureTrust()) => leaseName = "Local\\NetCat.Test.Zapret." + Path.GetFileName(bin);
        protected override void CheckOtherInstances() { }
        protected override string LeaseName => leaseName;
        public override Task WaitForZapretReadyAsync(TimeSpan timeout, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class FixtureTrust : IExecutableTrustPolicy
    {
        public IDisposable AcquireExecutable(string path)
        {
            var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            try
            {
                Assert.Equal(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Probe)),System.Security.Cryptography.SHA256.HashData(stream));
                return stream;
            }
            catch{stream.Dispose();throw;}
        }
        public IDisposable AcquirePackage(string key,string folder)
        {
            Assert.Equal("zapret",key);
            Assert.Equal("\"%BIN%winws.exe\" --filter-tcp=443 --dpi-desync=fake",File.ReadAllText(Path.Combine(folder,"general.bat")));
            foreach(var file in Directory.GetFiles(Path.Combine(folder,"bin")))
            {
                var original=Path.GetFileName(file)=="winws.exe"?Probe:Path.Combine(Path.GetDirectoryName(Probe)!,Path.GetFileName(file));
                Assert.Equal(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original)),System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
            }
            return AcquireExecutable(Path.Combine(folder,"bin","winws.exe"));
        }
    }
    private static string Probe => Path.Combine(RoutingTests.FindRoot(),"tests","NetCat.LifetimeProbe","bin",new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,"net8.0-windows","NetCat.LifetimeProbe.exe");
    private static bool Alive(int id) {try {using var process=Process.GetProcessById(id);return !process.HasExited;}catch(ArgumentException){return false;}}
    [Fact]
    public async Task ReadinessFileIsInvisibleUntilWriterCloses()
    {
        var file=RoutingTests.TestArtifacts("publication-"+Guid.NewGuid().ToString("N")+".json");
        using var host=new ProcessHost(new FixtureTrust());host.Start(Probe,["publication-race",file]);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(!File.Exists(file+".writing"))await Task.Delay(10,deadline.Token);
        Assert.False(File.Exists(file)); // The fixture deliberately holds its writer open.
        File.WriteAllText(file+".release","");
        while(!File.Exists(file))await Task.Delay(10,deadline.Token);
        Assert.Equal(new[]{1,2},JsonSerializer.Deserialize<int[]>(File.ReadAllText(file)));
    }
    [Fact]
    public async Task CompletedOneShotCommandDoesNotLeaveDetachedDescendants()
    {
        var file=RoutingTests.TestArtifacts("one-shot-"+Guid.NewGuid().ToString("N")+".json");
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result=await ProcessHost.RunAsync(Probe,["child-exit",file],ct.Token,trustPolicy:new FixtureTrust());
        Assert.Equal(0,result.Code);
        foreach(var id in JsonSerializer.Deserialize<int[]>(File.ReadAllText(file))!) Assert.False(Alive(id));
    }
    [Fact]
    public async Task PowerShellUsesSystemBinaryAndPreservesRussianOutput()
    {
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.System),ProcessHost.PowerShellPath,StringComparison.OrdinalIgnoreCase);
        var result=await PhysicalNetwork.PowerShell("'Проверка русского вывода'");Assert.Equal(0,result.Code);Assert.Contains("Проверка русского вывода",result.Output);
    }
    [Fact]
    public async Task OnlyOneZapretOwnerCanRunAndLeaseAllowsRestart()
    {
        var root=RoutingTests.TestArtifacts("zapret-owner-"+Guid.NewGuid().ToString("N"));var bin=Path.Combine(root,"zapret","bin");Directory.CreateDirectory(bin);
        foreach(var path in Directory.GetFiles(Path.GetDirectoryName(Probe)!))File.Copy(path,Path.Combine(bin,Path.GetFileName(path)));
        File.Copy(Probe,Path.Combine(bin,"winws.exe"));var strategy=Path.Combine(root,"zapret","general.bat");File.WriteAllText(strategy,"\"%BIN%winws.exe\" --filter-tcp=443 --dpi-desync=fake");
        using var first=new IsolatedZapret(root,Path.Combine(root,"first"));using var second=new IsolatedZapret(root,Path.Combine(root,"second"));
        await first.ApplyAsync(new(),strategy);var firstId=first.ProcessId;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>second.ApplyAsync(new(),strategy));Assert.True(first.Running);Assert.False(second.Running);
        await first.StopAsync();Assert.False(Alive(firstId));
        for(int i=0;i<3;i++) {await second.ApplyAsync(new(),strategy);var id=second.ProcessId;await second.StopAsync();Assert.False(Alive(id));}
    }
    [Fact]
    public async Task RealWinwsAcceptsEveryPackagedStrategyAndScenario()
    {
        var report=RoutingTests.TestArtifacts("zapret-dry-run-"+Guid.NewGuid().ToString("N")+".txt");
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result=await ProcessHost.RunAsync(Probe,["dry-run",report,RoutingTests.ModuleRoot],ct.Token,trustPolicy:new FixtureTrust());
        Assert.True(result.Code==0,result.Output);Assert.Equal(Directory.GetFiles(Path.Combine(RoutingTests.ModuleRoot,"zapret"),"general*.bat").Length*3,int.Parse(File.ReadAllText(report)));
    }
    [Theory][InlineData("cancel")][InlineData("stop")][InlineData("dispose")]
    public async Task CancelledZapretStartupCannotLeaveAnInvisibleProcess(string action)
    {
        var root=RoutingTests.TestArtifacts("zapret-cancel-"+Guid.NewGuid().ToString("N"));var bin=Path.Combine(root,"zapret","bin");Directory.CreateDirectory(bin);
        foreach(var path in Directory.GetFiles(Path.GetDirectoryName(Probe)!))File.Copy(path,Path.Combine(bin,Path.GetFileName(path)));
        File.Copy(Probe,Path.Combine(bin,"winws.exe"));var strategy=Path.Combine(root,"zapret","general.bat");File.WriteAllText(strategy,"\"%BIN%winws.exe\" --filter-tcp=443 --dpi-desync=fake");
        using var service=new IsolatedZapret(root,Path.Combine(root,"runtime"));using var ct=new CancellationTokenSource();
        var start=service.ApplyAsync(new AppSettings(),strategy,ct.Token);
        var limit=DateTime.UtcNow.AddSeconds(10);while(service.ProcessId==0&&!start.IsCompleted&&DateTime.UtcNow<limit)await Task.Delay(5);
        var pid=service.ProcessId;Assert.True(pid>0);
        if(action=="cancel")ct.Cancel();else if(action=="stop")await service.StopAsync();else service.Dispose();
        try{await start;}catch(Exception e)when(e is OperationCanceledException or ObjectDisposedException){}
        await service.StopAsync();
        limit=DateTime.UtcNow.AddSeconds(3);while(Alive(pid)&&DateTime.UtcNow<limit)await Task.Delay(10);
        Assert.False(Alive(pid));Assert.False(service.Running);Assert.Empty(service.ActiveStrategy);Assert.Empty(service.ActiveScenario);
        if(action=="dispose")await Assert.ThrowsAsync<ObjectDisposedException>(()=>service.ApplyAsync(new(),strategy));
    }
    [Fact]
    public async Task DisposeDuringZapretReadinessCompletesAsCancellation()
    {
        var root=RoutingTests.TestArtifacts("zapret-ready-dispose-"+Guid.NewGuid().ToString("N"));
        var bin=Path.Combine(root,"zapret","bin");Directory.CreateDirectory(bin);
        foreach(var path in Directory.GetFiles(Path.GetDirectoryName(Probe)!))File.Copy(path,Path.Combine(bin,Path.GetFileName(path)));
        File.Copy(Probe,Path.Combine(bin,"winws.exe"));var strategy=Path.Combine(root,"zapret","general.bat");
        File.WriteAllText(strategy,"\"%BIN%winws.exe\" --filter-tcp=443 --dpi-desync=fake");
        using var service=new IsolatedZapret(root,Path.Combine(root,"runtime"));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.WaitForZapretReadyOverride=(_,_)=>{entered.TrySetResult();return release.Task;};
        var start=service.ApplyAsync(new(),strategy);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var pid=service.ProcessId;
            service.Dispose();release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>start);
            Assert.False(Alive(pid));Assert.Empty(service.ActiveStrategy);Assert.Empty(service.ActiveScenario);
            Assert.NotEqual(true,service.ObservedState?.IsRunning);
        }
        finally {release.TrySetResult();}
    }

    [Fact]
    public async Task DisposeAtZapretOwnershipPublicationCannotResurrectActiveState()
    {
        var root=RoutingTests.TestArtifacts("zapret-publish-dispose-"+Guid.NewGuid().ToString("N"));
        var bin=Path.Combine(root,"zapret","bin");Directory.CreateDirectory(bin);
        foreach(var path in Directory.GetFiles(Path.GetDirectoryName(Probe)!))File.Copy(path,Path.Combine(bin,Path.GetFileName(path)));
        File.Copy(Probe,Path.Combine(bin,"winws.exe"));var strategy=Path.Combine(root,"zapret","general.bat");
        File.WriteAllText(strategy,"\"%BIN%winws.exe\" --filter-tcp=443 --dpi-desync=fake");
        using var service=new IsolatedZapret(root,Path.Combine(root,"runtime"));var calls=0;
        service.OwnerFileOverride=()=>{if(Interlocked.Increment(ref calls)==1)service.Dispose();return Path.Combine(root,"owner.json");};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.ApplyAsync(new(),strategy));
        Assert.True(calls>0);Assert.False(service.Running);Assert.Empty(service.ActiveStrategy);Assert.Empty(service.ActiveScenario);
        Assert.NotEqual(true,service.ObservedState?.IsRunning);Assert.False(File.Exists(Path.Combine(root,"owner.json")));
    }
    [Theory]
    [InlineData("stop","owner")][InlineData("dispose","owner")][InlineData("kill","owner")][InlineData("exit","owner")][InlineData("stop","exited-root")]
    public async Task ChildrenAndGrandchildrenCannotOutliveOwner(string command,string mode)
    {
        var root=RoutingTests.FindRoot();
        var file=RoutingTests.TestArtifacts("lifetime-"+Guid.NewGuid().ToString("N")+".json");
        var configuration=new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var exe=Path.Combine(root,"tests","NetCat.LifetimeProbe","bin",configuration,"net8.0-windows","NetCat.LifetimeProbe.exe");
        var psi=new ProcessStartInfo(exe) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}; psi.ArgumentList.Add(mode); psi.ArgumentList.Add(file);
        using var owner=Process.Start(psi)!; int[] ids=[];
        static bool Alive(int id) { try {using var p=Process.GetProcessById(id); return !p.HasExited;}catch(ArgumentException){return false;} }
        async Task Until(Func<bool> ready) {var limit=DateTime.UtcNow.AddSeconds(25);while(!ready() && DateTime.UtcNow<limit) await Task.Delay(20);Assert.True(ready());}
        try
        {
            await Until(()=>File.Exists(file)); ids=JsonSerializer.Deserialize<int[]>(File.ReadAllText(file))!;
            Assert.Equal(2,ids.Length); Assert.True(Alive(ids[1]));
            if(command=="kill") { owner.Kill(false); await owner.WaitForExitAsync(); }
            else { File.WriteAllText(file+".command.pending",command); File.Move(file+".command.pending",file+".command"); await Until(()=>File.Exists(file+".done")); }
            await Until(()=>ids.All(id=>!Alive(id)));
            if(command is "stop" or "dispose") Assert.False(owner.HasExited);
        }
        finally {if(!owner.HasExited) {owner.Kill(true);await owner.WaitForExitAsync();} foreach(var id in ids) if(Alive(id)) {using var p=Process.GetProcessById(id);p.Kill(true);} }
    }
    [Fact]
    public async Task RepeatedStartsAndQuotedArgumentsRemainReliable()
    {
        var exe=Path.Combine(RoutingTests.ModuleRoot,"tg-runtime","NetCat.Telegram.exe");
        var values=new[]{"with spaces","quote\"here",@"C:\path with spaces\", "", "юникод"};
        for(var i=0;i<5;i++)
        {
            var result=await ProcessHost.RunAsync(exe,new[]{"-c","import json,sys; print(json.dumps(sys.argv[1:]))"}.Concat(values));
            Assert.Equal(0,result.Code); Assert.Equal(values,JsonSerializer.Deserialize<string[]>(result.Output.Trim()));
        }
    }
}
