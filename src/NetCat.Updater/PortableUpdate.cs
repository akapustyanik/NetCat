using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Core;

namespace NetCat.Updater;
public sealed record PackageFile(string Path,string Sha256);
public sealed record PackageComponent(string Key,string Version,List<PackageFile> Files);
public sealed record PackageManifest(int Schema,string Version,List<PackageComponent> Components);
public sealed record UpdateJob(string Root,string Stage,int ParentId,long ParentStart,string ArchiveHash,string[] Pinned,bool Smoke=false);

public static class PortableUpdate
{
    public const string ManifestPath = "metadata/release-manifest.json";
    private static readonly HashSet<string> Keys = ["netcat","sing-box","xray","zapret","tg-ws-proxy","openvpn","wintun","geoip","geosite"];
    public static string SafePath(string root,string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(s=>s is "" or "." or ".." || s.EndsWith('.') || s.EndsWith(' '))) throw new InvalidDataException("Недопустимый путь в пакете.");
        var full=Path.GetFullPath(Path.Combine(root,relative)); var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь выходит из папки NetCat.");
        for(var node=new FileInfo(full) as FileSystemInfo;node!=null;node=node is DirectoryInfo d?d.Parent:((FileInfo)node).Directory)
        { if(node.Exists && (node.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Ссылки файловой системы в обновлении запрещены."); if(node.FullName.TrimEnd('\\').Equals(Path.GetFullPath(root).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) break; }
        return full;
    }
    public static bool Owns(string key,string path) => key switch
    {
        "netcat" => path=="NetCat.exe",
        "tg-ws-proxy" => path.StartsWith("modules/tg-ws-proxy/",StringComparison.Ordinal) || path.StartsWith("modules/tg-runtime/",StringComparison.Ordinal),
        "wintun" => path.StartsWith("modules/wintun/",StringComparison.Ordinal) || path is "modules/sing-box/wintun.dll" or "modules/openvpn/wintun.dll",
        "sing-box" => path.StartsWith("modules/sing-box/",StringComparison.Ordinal) && path!="modules/sing-box/wintun.dll",
        "openvpn" => path.StartsWith("modules/openvpn/",StringComparison.Ordinal) && path!="modules/openvpn/wintun.dll",
        _ => Keys.Contains(key) && path.StartsWith("modules/"+key+"/",StringComparison.Ordinal)
    };
    public static async Task<PackageManifest> VerifyAsync(string root,CancellationToken ct)
    {
        var manifest=JsonSerializer.Deserialize<PackageManifest>(await File.ReadAllTextAsync(SafePath(root,ManifestPath),ct),JsonSettings.Options) ?? throw new InvalidDataException("Нет манифеста обновления.");
        if(manifest.Schema!=1 || manifest.Components.Count!=Keys.Count || !manifest.Components.Select(c=>c.Key).ToHashSet().SetEquals(Keys)) throw new InvalidDataException("Неполный пакет NetCat или неизвестный формат.");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var c in manifest.Components)
        {
            if(c.Files.Count==0 || ModuleUpdater.ParseVersion(c.Version)==null) throw new InvalidDataException("Нет файлов или версии компонента.");
            foreach(var f in c.Files)
            {
                if(!Owns(c.Key,f.Path)||!seen.Add(f.Path)) throw new InvalidDataException("Файл не принадлежит компоненту или повторяется.");
                var path=SafePath(root,f.Path); await using var file=File.OpenRead(path);
                if(!Convert.ToHexString(await SHA256.HashDataAsync(file,ct)).Equals(f.Sha256,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Повреждён файл: "+f.Path);
            }
        }
        if(manifest.Components.Single(c=>c.Key=="netcat").Version!=manifest.Version) throw new InvalidDataException("Версии программы и пакета различаются.");
        return manifest;
    }
    public static List<PackageComponent> Plan(PackageManifest manifest,Func<string,string> installed,ISet<string> pinned) => manifest.Components.Where(c=>!pinned.Contains(c.Key)&&ModuleUpdater.IsNewer(c.Version,installed(c.Key))).ToList();
    public static async Task<string> PrepareAsync(ModuleRelease release,string root,ISet<string> pinned,CancellationToken ct,int proxyPort=0)
    {
        if(release.Key!="netcat" || release.Repository!="akapustyanik/NetCat" || !release.Url.StartsWith("https://github.com/akapustyanik/NetCat/releases/download/",StringComparison.Ordinal) || !System.Text.RegularExpressions.Regex.IsMatch(release.Sha256,"^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Непроверенный источник NetCat.");
        PublisherTrust.RequireSamePublisher(Environment.ProcessPath!,Environment.ProcessPath!);
        var updateRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"NetCat");
        PrivateFiles.ProtectDirectory(updateRoot,true); updateRoot=Path.Combine(updateRoot,"updates"); PrivateFiles.ProtectDirectory(updateRoot,true);
        var stage=Path.Combine(updateRoot,Guid.NewGuid().ToString("N")); PrivateFiles.ProtectDirectory(stage,true);
        try
        {
        using var stageLease=new FileStream(Path.Combine(stage,"active.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        using var http=ModuleUpdater.CreateClient(proxyPort); http.Timeout=TimeSpan.FromMinutes(15);
        using var response=await http.GetAsync(release.Url,HttpCompletionOption.ResponseHeadersRead,ct); response.EnsureSuccessStatusCode();
        await using(var input=await response.Content.ReadAsStreamAsync(ct)) await using(var output=File.Create(Path.Combine(stage,"package.zip")))
        {
            var buffer=new byte[65536]; long total=0; int read;
            while((read=await input.ReadAsync(buffer,ct))>0) { total+=read; if(total>1024L*1024*1024)throw new InvalidDataException("Архив превышает 1 ГБ."); await output.WriteAsync(buffer.AsMemory(0,read),ct); }
        }
        await ExtractVerifiedAsync(stage,release.Sha256,ct);
        var manifest=await VerifyAsync(Path.Combine(stage,"payload"),ct);
        if(manifest.Version.TrimStart('v')!=release.Version.TrimStart('v')) throw new InvalidDataException("Манифест не соответствует GitHub-релизу.");
        PublisherTrust.RequireSamePublisher(Environment.ProcessPath!,Path.Combine(stage,"payload","NetCat.exe"));
        var updater=new ModuleUpdater(Path.Combine(root,"modules"));
        if(Plan(manifest,updater.InstalledVersion,pinned).Count==0) throw new InvalidOperationException("В пакете нет более новых незакреплённых компонентов.");
        var job=new UpdateJob(root,stage,Environment.ProcessId,Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,release.Sha256,pinned.ToArray());
        var jobPath=Path.Combine(stage,"job.json"); await File.WriteAllTextAsync(jobPath,JsonSerializer.Serialize(job,JsonSettings.Options),ct);
        File.Copy(Environment.ProcessPath!,Path.Combine(stage,"NetCat.Update.exe"));
        return jobPath;
        }
        catch { UpdateCleanup.TryRemoveStage(stage); throw; }
    }
    public static async Task ExtractVerifiedAsync(string stage,string hash,CancellationToken ct)
    {
        var zip=Path.Combine(stage,"package.zip"); await using(var file=File.OpenRead(zip))
            if(!Convert.ToHexString(await SHA256.HashDataAsync(file,ct)).Equals(hash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("SHA-256 архива не совпадает.");
        var payload=Path.Combine(stage,"payload"); Directory.CreateDirectory(payload);
        using var archive=ZipFile.OpenRead(zip);
        if(archive.Entries.Count>50000 || archive.Entries.Sum(e=>e.Length)>2L*1024*1024*1024) throw new InvalidDataException("Слишком большой распакованный архив.");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name=entry.FullName.TrimEnd('/'); if(name.Length==0)continue; var path=SafePath(payload,name);
            if(!paths.Add(name))throw new InvalidDataException("Повторяющееся имя файла.");
            if(entry.FullName.EndsWith('/')){Directory.CreateDirectory(path);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path,true);
        }
    }
    public static Task LaunchAsync(string jobPath) => UpdateChannel.LaunchAsync(jobPath);
    internal static async Task ApplyAuthenticatedJobAsync(UpdateJob job)
    {
        using var stageLease=new FileStream(Path.Combine(job.Stage,"active.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var meta=Path.Combine(job.Root,"metadata"); Directory.CreateDirectory(meta);
        using var updateLock=new FileStream(SafePath(job.Root,"metadata/update.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        try
        {
            try { using var parent=Process.GetProcessById(job.ParentId); if(parent.StartTime.ToUniversalTime().Ticks==job.ParentStart) await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); } catch(ArgumentException) { }
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await ExtractVerifiedAsync(job.Stage,job.ArchiveHash,deadline.Token);
            var payload=Path.Combine(job.Stage,"payload");var manifest=await VerifyAsync(payload,deadline.Token);
            PublisherTrust.RequireSamePublisher(Environment.ProcessPath!,Path.Combine(payload,"NetCat.exe"));
            var updater=new ModuleUpdater(Path.Combine(job.Root,"modules"));
            var plan=Plan(manifest,updater.InstalledVersion,job.Pinned.ToHashSet());
            ApplyFiles(job.Root,payload,plan,manifest.Components.ToDictionary(c=>c.Key,c=>updater.InstalledVersion(c.Key)),stage:job.Stage);
            await File.WriteAllTextAsync(Path.Combine(meta,"last-update.txt"),"Обновлено: "+string.Join(", ",plan.Select(c=>c.Key+" "+c.Version)));
        }
        catch(Exception e) { await File.WriteAllTextAsync(Path.Combine(meta,"last-update.txt"),"Обновление отменено: "+e.Message); throw; }
        finally { updateLock.Dispose(); }
        var restart=new ProcessStartInfo(Path.Combine(job.Root,"NetCat.exe")) { UseShellExecute=!job.Smoke,CreateNoWindow=job.Smoke,WindowStyle=job.Smoke?ProcessWindowStyle.Hidden:ProcessWindowStyle.Normal };
        if(job.Smoke)restart.ArgumentList.Add("--smoke");
        using var restarted=Process.Start(restart);
        if(job.Smoke && restarted!=null) { await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));if(restarted.ExitCode!=0)throw new IOException("Обновлённый EXE не прошёл smoke-проверку."); }
    }
    public static void ApplyFiles(string root,string payload,List<PackageComponent> plan,Dictionary<string,string> versions,
        Action<int,UpdatePhase>? afterMutation=null,string? stage=null) => DurableUpdate.Apply(root,payload,plan,versions,afterMutation,stage);
}
