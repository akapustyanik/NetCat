using NetCat.Engine;
using Xunit;
namespace NetCat.Tests;
public sealed class Candidate19BinaryTests
{
    [Theory][InlineData("openvpn")][InlineData("sing-box")][InlineData("xray")]
    public void BundledHashMatchesEmbeddedTrustedManifest(string module)
    {using var lease=ModuleIntegrity.Acquire(module,Path.Combine(RoutingTests.ModuleRoot,module));}
    [Theory][InlineData("openvpn")][InlineData("sing-box")]
    public void ModifiedBinaryIsRejectedBeforeLaunch(string module)
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-C19-integrity-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var source=Path.Combine(RoutingTests.ModuleRoot,module);
            foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
            {var target=Path.Combine(root,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);}
            var executable=Path.Combine(root,module+".exe");using(var stream=File.OpenWrite(executable)){stream.Position=0;stream.WriteByte(0);}
            using var host=new ProcessHost();Assert.Throws<InvalidDataException>(()=>host.Start(executable,["--version"]));Assert.False(host.Running);Assert.Equal(0,host.Id);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task ReparseModulePathIsRejected()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-C19-reparse-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var target=Path.Combine(root,"target");Directory.CreateDirectory(target);var link=Path.Combine(root,"link");
        try
        {
            var command="New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+target.Replace("'","''")+"' | Out-Null";
            var result=await ProcessHost.RunAsync(ProcessHost.PowerShellPath,["-NoProfile","-NonInteractive","-Command",command]);Assert.Equal(0,result.Code);
            Assert.Throws<InvalidDataException>(()=>ModuleIntegrity.CheckPath(link));
        }
        finally{if(Directory.Exists(link))Directory.Delete(link);Directory.Delete(root,true);}
    }
}
