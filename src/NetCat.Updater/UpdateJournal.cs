using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Core;

namespace NetCat.Updater;

public enum UpdatePhase { Prepared, Applying, Committed, CleanupPending }
public sealed record UpdateOperation(string Target, bool Existed, string? BackupHash);
public sealed record UpdateJournal(int Schema, string Root, string UpdateId, string? Stage, UpdatePhase Phase,
    Dictionary<string,string> OldVersions, Dictionary<string,string> NewVersions, List<UpdateOperation> Operations);
// Only the fault-injection harness throws this: simulate process death, with no catch-time rollback.
public sealed class SimulatedUpdateCrash : Exception;

public static class DurableUpdate
{
    public const string JournalPath = "metadata/update-journal.json";
    private static string Canonical(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    private static string BackupPath(UpdateJournal journal) => "metadata/rollback/" + journal.UpdateId;
    private static string Hash(string path) { using var file=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void FlushFile(string path) { using var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read); file.Flush(true); }
    private static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var next=path+".netcat-new";
        using(var file=new FileStream(next,FileMode.Create,FileAccess.Write,FileShare.None))
        { JsonSerializer.Serialize(file,value,JsonSettings.Options); file.Flush(true); }
        File.Move(next,path,true);
    }
    private static void Save(UpdateJournal journal) => Write(PortableUpdate.SafePath(journal.Root,JournalPath),journal);
    private static bool Allowed(string path) => path=="metadata/installed.json" ||
        ModuleUpdater.Keys.Any(key=>PortableUpdate.Owns(key,path) || path=="metadata/components/"+key+".json");
    public static void Apply(string root,string payload,List<PackageComponent> plan,Dictionary<string,string> versions,
        Action<int,UpdatePhase>? afterMutation=null,string? stage=null)
    {
        root=Canonical(root);
        if(File.Exists(PortableUpdate.SafePath(root,JournalPath))) throw new IOException("Сначала необходимо восстановить предыдущее обновление.");
        var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var c in plan) foreach(var f in c.Files)
            if(!PortableUpdate.Owns(c.Key,f.Path) || !unique.Add(f.Path)) throw new InvalidDataException("Недопустимое владение файлом обновления.");
        var oldVersions=new Dictionary<string,string>(versions);
        var nextVersions=new Dictionary<string,string>(versions);
        foreach(var c in plan) nextVersions[c.Key]=c.Version;
        var journal=new UpdateJournal(1,root,Guid.NewGuid().ToString("N"),stage,UpdatePhase.Prepared,oldVersions,nextVersions,[]);
        var backup=PortableUpdate.SafePath(root,BackupPath(journal)); Directory.CreateDirectory(backup);
        Save(journal); int count=0;
        void Change(string relative,string? source)
        {
            var target=PortableUpdate.SafePath(root,relative);
            // Reject duplicate targets before touching either the backup or the installed file.
            if(journal.Operations.Any(o=>o.Target.Equals(relative,StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Повторная операция обновления.");
            var existed=File.Exists(target); var old=PortableUpdate.SafePath(backup,relative);
            if(existed) { Directory.CreateDirectory(Path.GetDirectoryName(old)!); File.Copy(target,old); FlushFile(old); }
            journal.Operations.Add(new(relative,existed,existed?Hash(old):null));
            Save(journal); // Durable undo information precedes ALL changes, including deletes and new files.
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if(source==null) File.Delete(target);
            else { var next=PortableUpdate.SafePath(root,relative+".netcat-new"); File.Copy(source,next,true); FlushFile(next); File.Move(next,target,true); }
            afterMutation?.Invoke(++count,UpdatePhase.Applying);
        }
        try
        {
            journal=journal with {Phase=UpdatePhase.Applying}; Save(journal);
            PackageManifest? previous=null; var previousPath=PortableUpdate.SafePath(root,PortableUpdate.ManifestPath);
            if(File.Exists(previousPath)) previous=JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(previousPath),JsonSettings.Options);
            foreach(var c in plan)
            {
                var relative="metadata/components/"+c.Key+".json"; var ownership=PortableUpdate.SafePath(root,relative);
                var old=File.Exists(ownership)?JsonSerializer.Deserialize<PackageComponent>(File.ReadAllText(ownership),JsonSettings.Options):previous?.Components.FirstOrDefault(p=>p.Key==c.Key);
                var retained=c.Files.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach(var f in old?.Files ?? [])
                {
                    // Older manifests assigned secondary Wintun copies to OpenVPN; never delete those.
                    if(c.Key=="openvpn" && f.Path=="modules/openvpn/wintun.dll") continue;
                    if(!PortableUpdate.Owns(c.Key,f.Path)) throw new InvalidDataException("Некорректное старое владение файлом.");
                    if(!retained.Contains(f.Path) && File.Exists(PortableUpdate.SafePath(root,f.Path))) Change(f.Path,null);
                }
                foreach(var f in c.Files) Change(f.Path,PortableUpdate.SafePath(payload,f.Path));
                var source=Path.Combine(backup,"ownership-"+c.Key+".json"); Write(source,c); Change(relative,source);
            }
            var versionFile=Path.Combine(backup,"versions.json"); Write(versionFile,nextVersions); Change("metadata/installed.json",versionFile);
            journal=journal with {Phase=UpdatePhase.Committed}; Save(journal);
            versions.Clear(); foreach(var pair in nextVersions) versions[pair.Key]=pair.Value;
            afterMutation?.Invoke(++count,UpdatePhase.Committed);
        }
        catch(Exception error) when(error is not SimulatedUpdateCrash)
        {
            Recover(root);
            versions.Clear(); foreach(var pair in oldVersions) versions[pair.Key]=pair.Value;
            throw;
        }
        Cleanup(journal);
    }
    public static UpdateJournal? Read(string root)
    {
        root=Canonical(root); var path=PortableUpdate.SafePath(root,JournalPath); if(!File.Exists(path)) return null;
        var journal=JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(path),JsonSettings.Options) ?? throw new InvalidDataException("Пустой журнал обновления.");
        if(journal.Schema!=1 || Canonical(journal.Root)!=root || !Guid.TryParseExact(journal.UpdateId,"N",out _) || !Enum.IsDefined(journal.Phase)) throw new InvalidDataException("Некорректный журнал обновления.");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var op in journal.Operations)
        { if(!Allowed(op.Target) || !paths.Add(op.Target)) throw new InvalidDataException("Недопустимый путь восстановления."); PortableUpdate.SafePath(root,op.Target); }
        return journal;
    }
    public static void Recover(string root)
    {
        var journal=Read(root); if(journal==null) return;
        if(journal.Phase is UpdatePhase.Prepared or UpdatePhase.Applying)
        {
            var backup=PortableUpdate.SafePath(root,BackupPath(journal));
            // Validate every backup BEFORE restoring anything. Partial/corrupt backups remain available for diagnosis.
            foreach(var op in journal.Operations.Where(o=>o.Existed))
                if(Hash(PortableUpdate.SafePath(backup,op.Target))!=op.BackupHash) throw new InvalidDataException("Повреждена резервная копия обновления.");
            foreach(var op in journal.Operations.AsEnumerable().Reverse())
            {
                var target=PortableUpdate.SafePath(root,op.Target);
                if(op.Existed)
                {
                    var next=PortableUpdate.SafePath(root,op.Target+".netcat-new");File.Copy(PortableUpdate.SafePath(backup,op.Target),next,true);FlushFile(next);
                    // A mapped Windows EXE may be renamed but cannot be overwritten in place.
                    // The operation's durable backup also covers a crash between these two moves.
                    if(op.Target=="NetCat.exe" && File.Exists(target) && Path.GetFullPath(Environment.ProcessPath!).Equals(target,StringComparison.OrdinalIgnoreCase))
                    {
                        var displaced=PortableUpdate.SafePath(root,"NetCat.exe.netcat-displaced");
                        if(File.Exists(displaced))File.Delete(displaced);
                        File.Move(target,displaced);
                    }
                    File.Move(next,target,true);
                }
                else File.Delete(target);
            }
            // CleanupPending also means a completed rollback; a subsequent startup must never replay it.
            journal=journal with {Phase=UpdatePhase.CleanupPending}; Save(journal);
        }
        Cleanup(journal);
    }
    private static void Cleanup(UpdateJournal journal)
    {
        journal=journal with {Phase=UpdatePhase.CleanupPending}; Save(journal);
        foreach(var op in journal.Operations) File.Delete(PortableUpdate.SafePath(journal.Root,op.Target+".netcat-new"));
        DeleteTree(journal.Root,BackupPath(journal));
        try {File.Delete(PortableUpdate.SafePath(journal.Root,"NetCat.exe.netcat-displaced"));}
        catch(IOException) {return;}
        catch(UnauthorizedAccessException) {return;}
        // A running helper may still lock its EXE. Keep the journal so the restarted app can retry.
        if(journal.Stage!=null && !UpdateCleanup.TryRemoveStage(journal.Stage)) return;
        File.Delete(PortableUpdate.SafePath(journal.Root,JournalPath+".netcat-new"));
        File.Delete(PortableUpdate.SafePath(journal.Root,JournalPath));
    }
    internal static void DeleteTree(string root,string relative)
    {
        var path=PortableUpdate.SafePath(root,relative); if(!Directory.Exists(path)) return;
        foreach(var child in Directory.EnumerateFileSystemEntries(path))
        {
            var name=Path.GetRelativePath(root,child).Replace('\\','/'); PortableUpdate.SafePath(root,name);
            if(Directory.Exists(child)) DeleteTree(root,name); else File.Delete(child);
        }
        Directory.Delete(path);
    }
}
