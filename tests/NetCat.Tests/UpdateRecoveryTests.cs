using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Core;
using NetCat.Updater;
using Xunit;
namespace NetCat.Tests;

public sealed class UpdateRecoveryTests : IDisposable
{
    private readonly string folder=Path.Combine(Path.GetTempPath(),"NetCat-UpdateTest-"+Guid.NewGuid().ToString("N"));
    private string Put(string root,string path,string text) { var file=PortableUpdate.SafePath(root,path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file,text); return file; }
    private static Dictionary<string,string> Files(string root) => Directory.GetFiles(root,"*",SearchOption.AllDirectories)
        .ToDictionary(f=>Path.GetRelativePath(root,f).Replace('\\','/'),f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void RecoveryAfterEveryMutationRestoresBytesAndMetadata(int crash)
    {
        var root=Path.Combine(folder,"root");var payload=Path.Combine(folder,"payload");
        var old=new PackageComponent("xray","1.0",[new("modules/xray/old.dll",""),new("modules/xray/xray.exe","")]);
        Put(root,"modules/xray/old.dll","obsolete"); Put(root,"modules/xray/xray.exe","old exe");
        Put(root,"metadata/components/xray.json",JsonSerializer.Serialize(old,JsonSettings.Options));
        Put(root,"metadata/installed.json","{\"xray\":\"1.0\"}");
        Put(payload,"modules/xray/xray.exe","new exe"); Put(payload,"modules/xray/new.dll","new dll");
        var before=Files(root);
        var next=new PackageComponent("xray","2.0",[new("modules/xray/xray.exe",""),new("modules/xray/new.dll","")]);
        Assert.Throws<SimulatedUpdateCrash>(()=>PortableUpdate.ApplyFiles(root,payload,[next],new(){{"xray","1.0"}},(n,_)=>{if(n==crash)throw new SimulatedUpdateCrash();}));
        DurableUpdate.Recover(root); DurableUpdate.Recover(root); // Recovery is idempotent.
        if(crash<6) Assert.Equal(before.OrderBy(p=>p.Key),Files(root).OrderBy(p=>p.Key));
        else
        {
            Assert.Equal("new exe",File.ReadAllText(Path.Combine(root,"modules/xray/xray.exe")));
            Assert.False(File.Exists(Path.Combine(root,"modules/xray/old.dll")));
            Assert.Contains("2.0",File.ReadAllText(Path.Combine(root,"metadata/installed.json")));
            Assert.Contains("new.dll",File.ReadAllText(Path.Combine(root,"metadata/components/xray.json")));
        }
        Assert.False(File.Exists(Path.Combine(root,DurableUpdate.JournalPath)));
        Assert.Empty(Directory.GetFiles(root,"*.netcat-new",SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root,"metadata/rollback")));
    }
    [Fact]
    public void OnlyWintunUpdateReplacesAllThreeCopies()
    {
        var root=Path.Combine(folder,"root");var payload=Path.Combine(folder,"payload");
        var paths=new[]{"modules/wintun/wintun.dll","modules/sing-box/wintun.dll","modules/openvpn/wintun.dll"};
        foreach(var path in paths) { Put(root,path,"old"); Put(payload,path,"new"); Assert.True(PortableUpdate.Owns("wintun",path)); }
        Assert.False(PortableUpdate.Owns("openvpn",paths[2])); Assert.False(PortableUpdate.Owns("sing-box",paths[1]));
        Put(root,"modules/openvpn/openvpn.exe","unchanged");
        var manifest=new PackageManifest(1,"1.0",[new("wintun","2.0",paths.Select(p=>new PackageFile(p,"")).ToList()),new("openvpn","1.0",[new("modules/openvpn/openvpn.exe","")])]);
        var plan=PortableUpdate.Plan(manifest,_=>"1.0",new HashSet<string>()); Assert.Single(plan);
        PortableUpdate.ApplyFiles(root,payload,plan,new(){{"wintun","1.0"},{"openvpn","1.0"}});
        foreach(var path in paths) Assert.Equal("new",File.ReadAllText(Path.Combine(root,path)));
        Assert.Equal("unchanged",File.ReadAllText(Path.Combine(root,"modules/openvpn/openvpn.exe")));
    }
    [Fact]
    public void StageCleanupProtectsActiveAndRecentStages()
    {
        Directory.CreateDirectory(folder);
        string Stage() {var p=Path.Combine(folder,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); Put(p,"payload/data","test");Directory.SetLastWriteTimeUtc(p,DateTime.UtcNow.AddDays(-8));return p;}
        var completed=Stage();var stale=Stage();var active=Stage();var recent=Stage();var locked=Stage();
        Directory.SetLastWriteTimeUtc(recent,DateTime.UtcNow);
        using(var lease=new FileStream(Path.Combine(locked,"active.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
        {
            Directory.SetLastWriteTimeUtc(locked,DateTime.UtcNow.AddDays(-8));
            Assert.True(UpdateCleanup.TryRemoveStage(completed,folder));
            UpdateCleanup.RetainRecent(folder,activeStages:new HashSet<string>{active});
            Assert.False(Directory.Exists(stale));Assert.True(Directory.Exists(active));Assert.True(Directory.Exists(recent));Assert.True(Directory.Exists(locked));
        }
        Assert.Throws<InvalidDataException>(()=>UpdateCleanup.TryRemoveStage(folder,folder));
    }
    [Fact]
    public void InvalidStaleJournalIsRetainedAndDiagnosed()
    {
        var stage=Path.Combine(folder,Guid.NewGuid().ToString("N"));var root=Path.Combine(folder,"installation");
        Put(root,DurableUpdate.JournalPath,JsonSerializer.Serialize(new UpdateJournal(999,root,"wrong",stage,UpdatePhase.Applying,new(),new(),[]),JsonSettings.Options));
        Put(stage,"job.json",JsonSerializer.Serialize(new UpdateJob(root,stage,123,456,new string('a',64),[]),JsonSettings.Options));
        Directory.SetLastWriteTimeUtc(stage,DateTime.UtcNow.AddDays(-10));var messages=new List<string>();
        UpdateCleanup.RetainRecent(folder,diagnostic:messages.Add);
        Assert.True(Directory.Exists(stage));Assert.Single(messages);
    }
    [Fact]
    public async Task ReparsePointCannotRedirectExtractionOrCleanup()
    {
        var outside=Path.Combine(folder,"outside");Put(outside,"keep.txt","keep");var root=Path.Combine(folder,"package");Directory.CreateDirectory(root);var link=Path.Combine(root,"link");
        var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe")) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden,Arguments=$"/d /c mklink /J \"{link}\" \"{outside}\""};
        using var process=System.Diagnostics.Process.Start(start)!;await process.WaitForExitAsync();Assert.Equal(0,process.ExitCode);
        try {Assert.Throws<InvalidDataException>(()=>PortableUpdate.SafePath(root,"link/keep.txt"));Assert.Equal("keep",File.ReadAllText(Path.Combine(outside,"keep.txt")));}
        finally {Directory.Delete(link);}
    }
    [Fact]
    public void AuthenticatedPeerRejectsEveryForgedIdentityField()
    {
        Directory.CreateDirectory(folder); var stage=Path.Combine(folder,Guid.NewGuid().ToString("N"));
        var hash=SHA256.HashData([1,2,3]); var peer=new UpdatePeer(100,200,Path.Combine(folder,"NetCat.exe"),true,hash);
        var job=new UpdateJob(folder,stage,100,200,new string('a',64),[]);
        UpdateAuthentication.Job(job,peer,stage,hash);
        foreach(var bad in new[]{job with {ParentId=101},job with {ParentStart=201},job with {Root=Path.Combine(folder,"other")},job with {Stage=folder},job with {Smoke=true},job with {ArchiveHash="wrong"},job with {Pinned=["evil"]}})
            Assert.Throws<InvalidDataException>(()=>UpdateAuthentication.Job(bad,peer,stage,hash));
        Assert.Throws<InvalidDataException>(()=>UpdateAuthentication.Job(job,peer with {Elevated=false},stage,hash));
        Assert.Throws<InvalidDataException>(()=>UpdateAuthentication.Job(job,peer,stage,SHA256.HashData([4])));
        Assert.Throws<InvalidDataException>(()=>UpdateAuthentication.Receiver(100,101));
        Assert.Throws<InvalidDataException>(()=>UpdateAuthentication.PipeName("job.json"));
        Assert.Throws<InvalidDataException>(()=>UpdateAuthentication.PipeName("NetCat.Update.not-a-guid"));
        UpdateAuthentication.PipeName("NetCat.Update."+Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidDataException>(()=>UpdateCleanup.ValidateStage(folder,folder));
        Assert.Throws<InvalidDataException>(()=>PublisherTrust.RequireIdentity(true,true,"publisher A","publisher B"));
        Assert.Throws<InvalidDataException>(()=>PublisherTrust.RequireIdentity(true,false,"publisher A","publisher A"));
        var unsigned=Put(folder,"unsigned.exe","fake executable");
        Assert.Throws<InvalidDataException>(()=>PublisherTrust.RequireSamePublisher(unsigned,unsigned));
    }
    [Theory]
    [InlineData("../outside.txt")] [InlineData("modules/xray/../../outside")] [InlineData("NetCat.exe:stream")]
    public async Task ExtractRejectsTraversal(string entry)
    {
        Directory.CreateDirectory(folder);var zip=Path.Combine(folder,"package.zip");
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)) {using var writer=new StreamWriter(archive.CreateEntry(entry).Open());writer.Write("unsafe");}
        var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        await Assert.ThrowsAsync<InvalidDataException>(()=>PortableUpdate.ExtractVerifiedAsync(folder,hash,CancellationToken.None));
    }
    [Fact]
    public async Task ExtractRejectsDuplicateAndArchiveHashMismatch()
    {
        Directory.CreateDirectory(folder);var zip=Path.Combine(folder,"package.zip");
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)) { archive.CreateEntry("NetCat.exe");archive.CreateEntry("NETCAT.EXE"); }
        await Assert.ThrowsAsync<InvalidDataException>(()=>PortableUpdate.ExtractVerifiedAsync(folder,new string('0',64),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(()=>PortableUpdate.ExtractVerifiedAsync(folder,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))),CancellationToken.None));
    }
    public void Dispose() { if(Directory.Exists(folder)) Directory.Delete(folder,true); }
}
