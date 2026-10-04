using NetCat.Core;
using NetCat.Engine;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate31RuntimeTrustTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"NetCat-C31-Trust-"+Guid.NewGuid().ToString("N"));
    private string Copy(string key)
    {
        var source=Path.Combine(RoutingTests.ModuleRoot,key);var target=Path.Combine(root,key);
        foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
        {var path=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.Copy(file,path);}
        return target;
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    [Theory]
    [InlineData("zapret", "bin/winws.exe")]
    [InlineData("tg-runtime", "python313.dll")]
    [InlineData("tg-runtime", "python313._pth")]
    [InlineData("tg-ws-proxy", "proxy/tg_ws_proxy.py")]
    public void ChangedReviewedRuntimeFileIsBlocked(string key,string file)
    {
        var folder=Copy(key);using(ReviewedRuntimeTrust.Acquire(key,folder)){}
        File.AppendAllText(Path.Combine(folder,file),"tampered");
        Assert.Throws<InvalidDataException>(()=>ReviewedRuntimeTrust.Acquire(key,folder));
    }
    [Fact] public void AdjacentLockCannotAuthorizeChangedPythonCode()
    {
        var folder=Copy("tg-runtime");File.WriteAllText(Path.Combine(folder,"runtime.lock.json"),"{}");
        Assert.Throws<InvalidDataException>(()=>ReviewedRuntimeTrust.Acquire("tg-runtime",folder));
    }
    [Fact] public void ExtraPythonModuleIsBlocked()
    {
        var folder=Copy("tg-runtime");File.WriteAllText(Path.Combine(folder,"sitecustomize.py"),"raise RuntimeError('untrusted')");
        Assert.Throws<InvalidDataException>(()=>ReviewedRuntimeTrust.Acquire("tg-runtime",folder));
    }
    [Fact] public void RuntimeLeasePreventsReplacementOfPythonSource()
    {
        var folder=Copy("tg-ws-proxy");using var lease=ReviewedRuntimeTrust.Acquire("tg-ws-proxy",folder);
        Assert.Throws<IOException>(()=>File.AppendAllText(Path.Combine(folder,"proxy/tg_ws_proxy.py"),"changed"));
    }
    [Fact] public void RuntimeLeasePreventsDirectoryReplacement()
    {
        var folder=Copy("tg-ws-proxy");using var lease=ReviewedRuntimeTrust.Acquire("tg-ws-proxy",folder);
        Assert.Throws<IOException>(()=>Directory.Move(folder,folder+".replaced"));
    }
    [Fact] public async Task ReviewedPythonStartsWithIsolatedEnvironmentAndNoBytecodeWrites()
    {
        Copy("tg-runtime");Copy("tg-ws-proxy");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result=await ProcessHost.RunAsync(Path.Combine(root,"tg-runtime","NetCat.Telegram.exe"),["-I","-B","-c","import sys, proxy.tg_ws_proxy; print(sys.flags.isolated, sys.dont_write_bytecode)"],deadline.Token);
        Assert.Equal(0,result.Code);Assert.Contains("1 True",result.Output);
        using var intact=ReviewedRuntimeTrust.AcquireTelegram(Path.Combine(root,"tg-runtime"));
    }
    [Theory]
    [InlineData("zapret")]
    [InlineData("tg-ws-proxy")]
    public async Task UpstreamDigestCannotInstallPrivilegedRuntime(string key)
    {
        var release=new ModuleRelease(key,"Flowseal/"+key,"9.0.0","runtime.zip","https://github.com/Flowseal/"+key+"/releases/download/9.0.0/runtime.zip",new string('a',64));
        Assert.False(new ModuleCheck(key,"1.0.0",release).AutoUpdateSupported);
        Directory.CreateDirectory(root);using var updater=new ModuleUpdater(root);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>updater.InstallAsync(release,new(),default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>updater.InstallPreparedAsync(release,new(),default));
    }
    [Fact] public void UnknownExecutableCannotReachSpawn()
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,"unknown.exe");File.WriteAllText(path,"not trusted");
        using var host=new ProcessHost();Assert.Throws<InvalidDataException>(()=>host.Start(path,[]));Assert.Equal(0,host.Id);
    }
    [Theory][InlineData("powershell.exe")][InlineData("sing-box.cmd")]
    public void RelativeOrScriptExecutableCannotBypassVerification(string executable)
    {using var host=new ProcessHost();Assert.Throws<InvalidDataException>(()=>host.Start(executable,[]));Assert.Equal(0,host.Id);}
    [Fact] public void CopiedSystemExecutableIsNotTrustedByName()
    {
        Directory.CreateDirectory(root);var copy=Path.Combine(root,"powershell.exe");File.Copy(ProcessHost.PowerShellPath,copy);
        Assert.Throws<InvalidDataException>(()=>WindowsExecutableTrust.Acquire(copy));
    }
    [Fact] public void ElevatedShellAssociationIsBlocked()
    {
        Assert.Throws<InvalidOperationException>(()=>WindowsExecutableTrust.RequireShellAssociationAllowed(true));
        WindowsExecutableTrust.RequireShellAssociationAllowed(false);
    }
    [Fact] public async Task TrustedWindowsPowerShellStillRuns()
    {
        var result=await ProcessHost.RunAsync(ProcessHost.PowerShellPath,["-NoProfile","-NonInteractive","-Command","Write-Output 'verified'"]);
        Assert.Equal(0,result.Code);Assert.Contains("verified",result.Output);
    }
    [Fact] public void UnsignedRestartTargetIsBlocked()
    {
        Directory.CreateDirectory(root);var next=Path.Combine(root,"NetCat.exe");File.WriteAllText(next,"untrusted");
        Assert.Throws<InvalidDataException>(()=>PublisherTrust.AcquireRestart(ProcessHost.PowerShellPath,next));
    }
}
