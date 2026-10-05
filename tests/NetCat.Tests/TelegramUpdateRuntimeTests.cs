using System.Reflection;
using System.Security.Cryptography;
using NetCat.Engine;
using NetCat.Network;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class TelegramUpdateRuntimeTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"NetCat-Telegram-Update-"+Guid.NewGuid().ToString("N"));

    public TelegramUpdateRuntimeTests()
    {
        foreach(var key in new[]{"tg-runtime","tg-ws-proxy"})
        {
            var source=Path.Combine(RoutingTests.ModuleRoot,key);
            foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
            {
                var destination=Path.Combine(root,key,Path.GetRelativePath(source,file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file,destination);
            }
        }
    }

    private string Candidate(string body)
    {
        var candidate=Path.Combine(root,"candidate");
        Directory.CreateDirectory(Path.Combine(candidate,"proxy"));
        File.WriteAllText(Path.Combine(candidate,"proxy","__init__.py"),"");
        File.WriteAllText(Path.Combine(candidate,"proxy","tg_ws_proxy.py"),body);
        return candidate;
    }

    private async Task Validate(string candidate)
    {
        using var updater=new ModuleUpdater(root);
        var method=typeof(ModuleUpdater).GetMethod("ValidateTelegramAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)method.Invoke(updater,[candidate,CancellationToken.None])!;
    }

    private Dictionary<string,string> Inventory()=>Directory.GetFiles(root,"*",SearchOption.AllDirectories)
        .ToDictionary(p=>Path.GetRelativePath(root,p),p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    [Fact]
    public async Task TelegramRuntimeSupportsRequiredHttp2Dependencies()
    {
        var candidate=Candidate("import httpx, httpcore, anyio, h2, hpack, hyperframe\nfrom h2.config import H2Configuration\nfrom h2.connection import H2Connection\nassert httpx.__version__ == '0.28.1'\nclient=httpx.AsyncClient(http2=True, trust_env=False)\nc=H2Connection(config=H2Configuration(client_side=True))\nc.initiate_connection()\nassert c.data_to_send().startswith(b'PRI * HTTP/2.0')\n");
        await Validate(candidate);
    }

    [Fact]
    public async Task TelegramValidationImportsCandidateWithoutChangingRuntimeOrWritingBytecode()
    {
        var candidate=Candidate("import sys, pathlib, httpx, h2\nassert sys.flags.isolated == 1\nassert sys.dont_write_bytecode\nassert pathlib.Path(__file__).parent.parent.resolve() == pathlib.Path(sys.argv[1]).resolve()\n");
        var before=Inventory();
        await Validate(candidate);
        var after=Inventory();
        Assert.Equal(before.Count,after.Count);
        foreach(var pair in before)Assert.Equal(pair.Value,after[pair.Key]);
        Assert.Empty(Directory.GetFiles(candidate,"*.pyc",SearchOption.AllDirectories));
        using var lease=ReviewedRuntimeTrust.AcquireTelegram(Path.Combine(root,"tg-runtime"));
    }

    [Fact]
    public async Task MissingTelegramDependencyLeavesInstalledModuleAndRuntimeIntact()
    {
        var candidate=Candidate("import netcat_missing_dependency_fixture\n");
        var before=Inventory();
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>Validate(candidate));
        Assert.Contains("netcat_missing_dependency_fixture",error.Message);
        Assert.Contains("Обновите NetCat",error.Message);
        var after=Inventory();
        Assert.Equal(before.Count,after.Count);
        foreach(var pair in before)Assert.Equal(pair.Value,after[pair.Key]);
        using var lease=ReviewedRuntimeTrust.AcquireTelegram(Path.Combine(root,"tg-runtime"));
    }

    [Fact]
    public async Task IncompatibleTelegramCandidateDoesNotFallBackToInstalledModuleOrExposeOutput()
    {
        var candidate=Candidate("raise RuntimeError('private-diagnostic-fixture')\n");
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>Validate(candidate));
        Assert.Contains("Обновление не применено",error.Message);
        Assert.DoesNotContain("private-diagnostic-fixture",error.Message);
    }

    [Fact]
    public async Task TelegramRunnerPreservesUpstreamWebSocketPoolAndLoopbackArguments()
    {
        var candidate=Candidate("import sys, pathlib\ndef main():\n assert '--pool-size' not in sys.argv\n assert sys.argv[sys.argv.index('--host')+1] == '127.0.0.1'\n assert sys.argv[sys.argv.index('--port')+1] == '1443'\n assert sys.argv[sys.argv.index('--secret')+1] == '0'*32\nroot=pathlib.Path(sys.argv[1])\nsys.argv=['runner',str(root/'config.json')]\nexec((root/'runner.py').read_text(encoding='utf-8'))\n");
        var script=(string)typeof(TelegramService).GetField("RunnerScript",BindingFlags.Static|BindingFlags.NonPublic)!.GetRawConstantValue()!;
        File.WriteAllText(Path.Combine(candidate,"runner.py"),script);
        File.WriteAllText(Path.Combine(candidate,"config.json"),"{\"port\":1443,\"secret\":\"00000000000000000000000000000000\"}");
        await Validate(candidate);
    }

    [Fact]
    public async Task ChangedHttp2DependencyCannotPassRuntimeTrust()
    {
        var candidate=Candidate("import httpx\n");
        File.AppendAllText(Path.Combine(root,"tg-runtime","Lib","site-packages","httpx","__init__.py"),"\nraise RuntimeError('untrusted')\n");
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>Validate(candidate));
        Assert.Contains("Нарушена целостность",error.Message);
    }

    public void Dispose()
    {
        if(Directory.Exists(root))Directory.Delete(root,true);
    }
}
