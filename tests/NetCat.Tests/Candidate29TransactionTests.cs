using NetCat.Updater;
using NetCat.Core;
using System.Text.Json;
using System.Security.Cryptography;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate29TransactionTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"NetCat-C29-Transaction-"+Guid.NewGuid().ToString("N"));
    private string Target=>Path.Combine(root,"sing-box");
    private string Next=>Path.Combine(root,"next");
    private string Journal=>Path.Combine(root,".transactions","sing-box.json");
    private static void WritePackage(string folder,string version)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"sing-box.exe"),version+"-exe");
        File.WriteAllText(Path.Combine(folder,"libcronet.dll"),version+"-dll");
    }
    private void AssertOld()
    {
        Assert.Equal("old-exe",File.ReadAllText(Path.Combine(Target,"sing-box.exe")));
        Assert.Equal("old-dll",File.ReadAllText(Path.Combine(Target,"libcronet.dll")));
        Assert.False(File.Exists(Journal));
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public void CrashAtEachSwapBoundaryRecoversWholePreviousPackage(int stage)
    {
        WritePackage(Target,"old");WritePackage(Next,"new");
        ComponentInstallJournal.Begin(root,"sing-box",Next);
        if(stage>=1)Directory.Move(Target,Target+".previous");
        if(stage>=2)Directory.Move(Next,Target);
        ComponentInstallJournal.RecoverAll(root);AssertOld();
        ComponentInstallJournal.RecoverAll(root);AssertOld();
    }
    [Fact] public void CommittedPackageSurvivesRestart()
    {
        WritePackage(Target,"old");WritePackage(Next,"new");ComponentInstallJournal.Begin(root,"sing-box",Next);
        Directory.Move(Target,Target+".previous");Directory.Move(Next,Target);ComponentInstallJournal.Commit(root,"sing-box");
        ComponentInstallJournal.RecoverAll(root);
        Assert.Equal("new-exe",File.ReadAllText(Path.Combine(Target,"sing-box.exe")));
        Assert.Equal("new-dll",File.ReadAllText(Path.Combine(Target,"libcronet.dll")));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void CrashRecoveryPreservesUnexpectedChangedFilesAndJournal(bool changeBackup)
    {
        WritePackage(Target,"old");WritePackage(Next,"new");ComponentInstallJournal.Begin(root,"sing-box",Next);
        Directory.Move(Target,Target+".previous");Directory.Move(Next,Target);
        var changed=Path.Combine(changeBackup?Target+".previous":Target,"foreign.txt");File.WriteAllText(changed,"must-survive");
        Assert.Throws<IOException>(()=>ComponentInstallJournal.RecoverAll(root));
        Assert.Equal("must-survive",File.ReadAllText(changed));Assert.True(File.Exists(Journal));
        Assert.True(Directory.Exists(Target));Assert.True(Directory.Exists(Target+".previous"));
    }
    [Fact] public void FailedFirstInstallIsRemovedWithoutInventingBackup()
    {
        WritePackage(Next,"new");ComponentInstallJournal.Begin(root,"sing-box",Next);Directory.Move(Next,Target);
        ComponentInstallJournal.RecoverAll(root);Assert.False(Directory.Exists(Target));Assert.False(File.Exists(Journal));
    }
    [Fact] public void UnknownComponentJournalCannotNameArbitraryDirectory()
    {Assert.Throws<InvalidDataException>(()=>ComponentInstallJournal.Recover(root,"../outside"));}
    [Fact] public void PendingJournalPreventsDestructionOfItsBackup()
    {
        WritePackage(Target,"old");WritePackage(Next,"new");ComponentInstallJournal.Begin(root,"sing-box",Next);
        Directory.Move(Target,Target+".previous");
        Assert.Throws<IOException>(()=>ComponentInstallJournal.EnsureNoPending(root,"sing-box"));
        Assert.True(File.Exists(Path.Combine(Target+".previous","sing-box.exe")));Assert.True(File.Exists(Journal));
    }
    [Fact] public void OwnershipReceiptFailureRetainsRecoverableJournalAfterFilesRestored()
    {
        WritePackage(Target,"old");WritePackage(Next,"new");ComponentInstallJournal.Begin(root,"sing-box",Next);
        Directory.Move(Target,Target+".previous");Directory.Move(Next,Target);
        Assert.Throws<IOException>(()=>ComponentInstallJournal.RecoverAll(root,_=>throw new IOException("receipt unavailable")));
        Assert.True(File.Exists(Journal));bool receipt=false;
        ComponentInstallJournal.RecoverAll(root,_=>{Assert.Equal("old-exe",File.ReadAllText(Path.Combine(Target,"sing-box.exe")));receipt=true;});
        Assert.True(receipt);AssertOld();
    }
    [Fact] public async Task EarlyBuildUsabilityUpdateHealthFailureStopsNewFilesBeforeRollback()
    {
        var bin=Path.Combine(root,"modules");var live=Path.Combine(bin,"geosite");var staged=Path.Combine(bin,".prepared","geosite");
        Directory.CreateDirectory(live);Directory.CreateDirectory(staged);
        var old=new ModuleRelease("geosite","Loyalsoldier/v2ray-rules-dat","202609120001","geosite.dat","unused","");var next=old with {Version="202609130001"};
        File.WriteAllText(Path.Combine(live,"geosite.dat"),"old");File.WriteAllText(Path.Combine(live,"netcat-source.json"),JsonSerializer.Serialize(old,JsonSettings.Options));
        File.WriteAllText(Path.Combine(staged,"geosite.dat"),"new");File.WriteAllText(Path.Combine(staged,"netcat-source.json"),JsonSerializer.Serialize(next,JsonSettings.Options));
        var files=Directory.GetFiles(staged).ToDictionary(p=>Path.GetFileName(p)!,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        File.WriteAllText(Path.Combine(staged,"prepared.json"),JsonSerializer.Serialize(new{Release=next,Files=files},JsonSettings.Options));
        using var updater=new ModuleUpdater(bin);FileStream? held=null;bool stopped=false;
        try
        {
            await Assert.ThrowsAsync<IOException>(()=>updater.InstallPreparedAsync(next,new(),default,_=>
            {
                Assert.Equal(next.Version,updater.InstalledVersion("geosite"));
                held=new FileStream(Path.Combine(live,"geosite.dat"),FileMode.Open,FileAccess.Read,FileShare.Read);
                throw new IOException("controlled health failure");
            },()=>{held?.Dispose();held=null;stopped=true;return Task.CompletedTask;}));
        }
        finally{held?.Dispose();}
        Assert.True(stopped);Assert.Equal("old",File.ReadAllText(Path.Combine(live,"geosite.dat")));
        Assert.Equal(old.Version,updater.InstalledVersion("geosite"));
        var receipt=JsonSerializer.Deserialize<PackageComponent>(File.ReadAllText(Path.Combine(root,"metadata","components","geosite.json")),JsonSettings.Options)!;
        Assert.Equal(old.Version,receipt.Version);Assert.False(File.Exists(Path.Combine(bin,".transactions","geosite.json")));
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"metadata","components","geosite.json")));
        Assert.True(json.RootElement.GetProperty("InstalledAtUtc").GetDateTimeOffset()<=DateTimeOffset.UtcNow);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
