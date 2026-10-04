using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NetCat.Core;

namespace NetCat.Engine;

// These pins are part of the NetCat image. A writable runtime.lock.json or
// upstream-account digest is never an authorization to execute new code.
public static class ReviewedRuntimeTrust
{
    private static readonly Dictionary<string,Dictionary<string,string>> Inventories=Load();
    private static Dictionary<string,Dictionary<string,string>> Load()
    {
        using var stream=typeof(ReviewedRuntimeTrust).Assembly.GetManifestResourceStream("NetCat.ReviewedRuntimes")!;
        return JsonSerializer.Deserialize<Dictionary<string,Dictionary<string,string>>>(stream)!;
    }
    public static bool RequiresReviewedPackage(string key)=>key is "zapret" or "tg-runtime" or "tg-ws-proxy";
    private static bool Covered(string key,string relative)=>key!="zapret" ||
        Path.GetExtension(relative).ToLowerInvariant() is ".exe" or ".dll" or ".sys" or ".bat" or ".cmd" or ".ps1";
    private static bool Receipt(string path)=>path is "netcat-source.json" or "prepared.json";
    public static IDisposable Acquire(string key,string folder)
    {
        if ((key is "zapret" or "tg-ws-proxy") &&
            UpstreamRuntimeTrust.Exists(folder,key))
        {
            return UpstreamRuntimeTrust.Acquire(
                key,
                folder);
        }
        if(!Inventories.TryGetValue(key,out var expected))throw new InvalidDataException("Нет доверенного пакета компонента.");
        ModuleIntegrity.CheckPath(folder);
        var handles=new List<IDisposable>();
        try
        {
            using var identity=WindowsIdentity.GetCurrent();
            bool elevated=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            void LockDirectory(string directory)
            {
                ModuleIntegrity.CheckPath(directory);
                var handle=OpenDirectory(directory,0,3,0,3,0x02000000,0);
                if(handle.IsInvalid){handle.Dispose();throw new System.ComponentModel.Win32Exception();}
                handles.Add(handle);
                // Hashes/held file handles prevent replacement; protected directory
                // ACLs also prevent planting new Python imports after verification.
                if(elevated)PrivateFiles.ProtectDirectory(directory,administratorsOnly:true);
                foreach(var child in Directory.EnumerateDirectories(directory))LockDirectory(child);
            }
            LockDirectory(folder);
            var files=ModuleIntegrity.SafeFiles(folder).Select(p=>Path.GetRelativePath(folder,p).Replace('\\','/'))
                .Where(p=>Covered(key,p)&&!Receipt(p)).ToArray();
            if(!new HashSet<string>(files,StringComparer.OrdinalIgnoreCase).SetEquals(expected.Keys))
                throw new InvalidDataException("Состав доверенного runtime изменён: "+key+". Нужен проверенный пакет NetCat.");
            foreach(var pair in expected)
            {
                var path=Path.Combine(folder,pair.Key);ModuleIntegrity.CheckPath(path);
                var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);handles.Add(file);
                if(!Convert.ToHexString(SHA256.HashData(file)).Equals(pair.Value,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Нарушена целостность доверенного runtime: "+key+". Нужен проверенный пакет NetCat.");
            }
            return new Lease(handles);
        }
        catch{foreach(var handle in handles)handle.Dispose();throw;}
    }
    public static IDisposable AcquireTelegram(string runtimeFolder)
    {
        var runtime=Acquire("tg-runtime",runtimeFolder);
        try{return new Pair(runtime,Acquire("tg-ws-proxy",Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runtimeFolder))!,"tg-ws-proxy")));}
        catch{runtime.Dispose();throw;}
    }
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle OpenDirectory(string path,uint access,uint share,nint security,uint disposition,uint flags,nint template);
    private sealed class Lease(List<IDisposable> handles):IDisposable
    {public void Dispose(){foreach(var handle in handles)handle.Dispose();}}
    private sealed class Pair(IDisposable first,IDisposable second):IDisposable
    {public void Dispose(){try{second.Dispose();}finally{first.Dispose();}}}
}
