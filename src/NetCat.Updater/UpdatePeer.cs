using System.Security.Cryptography;
namespace NetCat.Updater;

// Facts are collected from the connected OS pipe/process, never from the update payload.
public sealed record UpdatePeer(int Pid,long Started,string Executable,bool Elevated,byte[] Hash);
public static class UpdateAuthentication
{
    public static void PipeName(string name)
    {
        if(!name.StartsWith("NetCat.Update.",StringComparison.Ordinal) || !Guid.TryParseExact(name[14..],"N",out _)) throw new InvalidDataException("Обновление запускается только из NetCat через одноразовый канал.");
    }
    public static void Receiver(int expected,int actual)
    { if(expected<=0 || actual!=expected) throw new InvalidDataException("Подключился посторонний получатель задания."); }
    public static void HelperHash(byte[] expected,byte[] actual)
    { if(expected.Length!=32 || !CryptographicOperations.FixedTimeEquals(expected,actual)) throw new InvalidDataException("Помощник не соответствует запущенной программе."); }
    public static void Job(UpdateJob job,UpdatePeer parent,string stage,byte[] helperHash)
    {
        HelperHash(parent.Hash,helperHash);
        var root=Path.GetDirectoryName(Path.GetFullPath(parent.Executable))!;
        if(!parent.Elevated || job.ParentId!=parent.Pid || job.ParentStart!=parent.Started || job.Smoke ||
            !Path.GetFullPath(job.Root).TrimEnd(Path.DirectorySeparatorChar).Equals(root,StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(job.Stage).TrimEnd(Path.DirectorySeparatorChar).Equals(stage,StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(job.ArchiveHash,"^[a-fA-F0-9]{64}$") ||
            job.Pinned==null || job.Pinned.Any(p=>!ModuleUpdater.Keys.Contains(p))) throw new InvalidDataException("Задание не принадлежит отправителю.");
    }
}
