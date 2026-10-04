using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Updater;

// Paths are computed from a fixed component key, never taken from journal data.
// Hashes identify the directories owned by this transaction before rollback.
public sealed record ComponentInstallJournal(int Schema,string Key,Dictionary<string,string>? Previous,Dictionary<string,string> Next)
{
    private static readonly string[] Keys=["sing-box","xray","zapret","tg-ws-proxy","geoip","geosite"];
    private static string PathFor(string bin,string key)
    {
        if(!Keys.Contains(key))throw new InvalidDataException("Unknown component journal.");
        var path=Path.Combine(bin,".transactions",key+".json");ModuleIntegrity.CheckPath(path);return path;
    }
    public static Dictionary<string,string> Capture(string folder)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string directory)
        {
            ModuleIntegrity.CheckPath(directory);
            foreach(var item in Directory.EnumerateFileSystemEntries(directory))
            {
                ModuleIntegrity.CheckPath(item);
                if(Directory.Exists(item)){Walk(item);continue;}
                using var file=File.OpenRead(item);result.Add(Path.GetRelativePath(folder,item).Replace('\\','/'),Convert.ToHexString(SHA256.HashData(file)));
            }
        }
        Walk(folder);return result;
    }
    private static bool Matches(string folder,Dictionary<string,string> expected)
    {
        if(!Directory.Exists(folder))return false;
        var actual=Capture(folder);return actual.Count==expected.Count&&expected.All(p=>actual.TryGetValue(p.Key,out var hash)&&hash.Equals(p.Value,StringComparison.OrdinalIgnoreCase));
    }
    public static void Begin(string bin,string key,string next)
    {
        var path=PathFor(bin,key);if(File.Exists(path))throw new IOException("Незавершённая установка компонента требует восстановления.");
        var target=Path.Combine(bin,key);var backup=target+".previous";ModuleIntegrity.CheckPath(target);ModuleIntegrity.CheckPath(backup);
        if(Directory.Exists(backup))throw new IOException("Предыдущий backup должен быть обработан до начала транзакции.");
        // The installer secures the transaction directory before calling Begin.
        // Keeping ACL policy at that boundary also permits non-elevated crash tests.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var record=new ComponentInstallJournal(1,key,Directory.Exists(target)?Capture(target):null,Capture(next));
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream,record);stream.Flush(true);
    }
    public static void Commit(string bin,string key){File.Delete(PathFor(bin,key));}
    public static void EnsureNoPending(string bin,string key)
    {if(File.Exists(PathFor(bin,key)))throw new IOException("Незавершённая установка требует восстановления перед новым обновлением.");}
    public static void Recover(string bin,string key,Action<string>? refreshOwnership=null)
    {
        var path=PathFor(bin,key);if(!File.Exists(path))return;
        var record=JsonSerializer.Deserialize<ComponentInstallJournal>(File.ReadAllText(path))??throw new InvalidDataException("Empty component journal.");
        if(record.Schema!=1||record.Key!=key||record.Next==null)throw new InvalidDataException("Invalid component journal.");
        var target=Path.Combine(bin,key);var backup=target+".previous";ModuleIntegrity.CheckPath(target);ModuleIntegrity.CheckPath(backup);
        if(record.Previous!=null)
        {
            if(Directory.Exists(backup))
            {
                if(!Matches(backup,record.Previous))throw new IOException("Backup компонента изменён; автоматический откат остановлен.");
                if(Directory.Exists(target)){if(!Matches(target,record.Next))throw new IOException("Текущий компонент изменён; автоматический откат остановлен.");Directory.Delete(target,true);}
                Directory.Move(backup,target);
            }
            else if(!Matches(target,record.Previous))throw new IOException("Нет подтверждённого backup компонента.");
        }
        else if(Directory.Exists(target))
        {if(!Matches(target,record.Next))throw new IOException("Неизвестный компонент на месте новой установки.");Directory.Delete(target,true);}
        refreshOwnership?.Invoke(key);
        File.Delete(path);
    }
    public static void RecoverAll(string bin,Action<string>? refreshOwnership=null){foreach(var key in Keys)Recover(bin,key,refreshOwnership);}
}
