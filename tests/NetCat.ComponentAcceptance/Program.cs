using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Updater;

// Developer acceptance uses an isolated directory. Never an installed NetCat.
if(args.Length!=2)throw new ArgumentException("<signed component package directory> <new isolated work directory>");
var package=Path.GetFullPath(args[0]);var work=Path.GetFullPath(args[1]);
if(Directory.Exists(work))throw new IOException("Acceptance work directory must not already exist.");
Directory.CreateDirectory(work);
var bytes=File.ReadAllBytes(Path.Combine(package,ComponentTrust.ManifestName));
var sig=File.ReadAllBytes(Path.Combine(package,ComponentTrust.SignatureName));
var manifest=ComponentTrust.Verify(bytes,sig,"sing-box");
var release=new ModuleRelease(manifest.Key,manifest.Provider,manifest.Version,manifest.Asset,
    ModuleUpdater.ComponentReleaseBase(manifest.Key,manifest.Version)+manifest.Asset,manifest.ArchiveSha256)
    {ComponentManifestBase64=Convert.ToBase64String(bytes),ComponentSignatureBase64=Convert.ToBase64String(sig)};
var archive=Path.Combine(package,manifest.Asset);
if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)))!=manifest.ArchiveSha256)throw new InvalidDataException("Archive mismatch.");
var results=new List<object>();
async Task Run(string name,Func<string,Task> test)
{
    var folder=Path.Combine(work,name);Directory.CreateDirectory(folder);
    try{await test(folder);results.Add(new{Name=name,Passed=true});}
    catch(Exception e){results.Add(new{Name=name,Passed=false,ErrorClass=e.GetType().Name});}
}
void Stage(string bin)
{
    var folder=Path.Combine(bin,".prepared","sing-box");Directory.CreateDirectory(folder);
    ZipFile.ExtractToDirectory(archive,folder);
    File.WriteAllBytes(Path.Combine(folder,ComponentTrust.ManifestName),bytes);File.WriteAllBytes(Path.Combine(folder,ComponentTrust.SignatureName),sig);
    File.WriteAllText(Path.Combine(folder,"netcat-source.json"),JsonSerializer.Serialize(release,JsonSettings.Options));
    var files=ModuleIntegrity.SafeFiles(folder).ToDictionary(p=>Path.GetRelativePath(folder,p).Replace('\\','/'),p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    File.WriteAllText(Path.Combine(folder,"prepared.json"),JsonSerializer.Serialize(new{Release=release,Files=files},JsonSettings.Options));
}
void Previous(string bin)
{
    // The binary is the reviewed current package; an older receipt is a version
    // transition fixture, not evidence of compatibility with an actual old build.
    var folder=Path.Combine(bin,"sing-box");Directory.CreateDirectory(folder);ZipFile.ExtractToDirectory(archive,folder);
    File.WriteAllText(Path.Combine(folder,"netcat-source.json"),JsonSerializer.Serialize(release with {Version="0.1.0"},JsonSettings.Options));
}
void Require(bool value){if(!value)throw new InvalidDataException("Acceptance assertion failed.");}
await Run("fresh-install",async root=>
{
    var bin=Path.Combine(root,"modules");Stage(bin);using var updater=new ModuleUpdater(bin);int health=0;
    await updater.InstallPreparedAsync(release,new(),default,async ct=>
    {
        using var lease=ModuleIntegrity.Acquire("sing-box",Path.Combine(bin,"sing-box"));
        var result=await ProcessHost.RunAsync(Path.Combine(bin,"sing-box","sing-box.exe"),["version"],ct);Require(result.Code==0);health++;
    });
    Require(health==1&&updater.InstalledVersion("sing-box")==manifest.Version);
    updater.RecoverInterruptedInstalls();Require(!File.Exists(Path.Combine(bin,".transactions","sing-box.json")));
});
await Run("health-failure-rollback",async root=>
{
    var bin=Path.Combine(root,"modules");Previous(bin);Stage(bin);using var updater=new ModuleUpdater(bin);bool stopped=false,failed=false;
    try{await updater.InstallPreparedAsync(release,new(),default,_=>throw new IOException("controlled health failure"),()=>{stopped=true;return Task.CompletedTask;});}
    catch(IOException){failed=true;}
    Require(failed&&stopped&&updater.InstalledVersion("sing-box")=="0.1.0");
    using var lease=ModuleIntegrity.Acquire("sing-box",Path.Combine(bin,"sing-box"));
    Require(!File.Exists(Path.Combine(bin,"sing-box",ComponentTrust.ManifestName)));
});
await Run("tampered-companion-rejected",async root=>
{
    var bin=Path.Combine(root,"modules");Previous(bin);Stage(bin);using var updater=new ModuleUpdater(bin);
    File.AppendAllText(Path.Combine(bin,".prepared","sing-box","libcronet.dll"),"tampered");bool failed=false;
    try{await updater.InstallPreparedAsync(release,new(),default);}catch(InvalidDataException){failed=true;}
    Require(failed&&updater.InstalledVersion("sing-box")=="0.1.0");
    using var lease=ModuleIntegrity.Acquire("sing-box",Path.Combine(bin,"sing-box"));
});
File.WriteAllText(Path.Combine(work,"result.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
