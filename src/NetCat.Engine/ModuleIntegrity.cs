using System.Security.Cryptography;
using System.Text.Json;

namespace NetCat.Engine;

// Trust comes from the application image, not an adjacent writable lock file.
// Reviewed package pins are the fallback. A detached publisher signature can
// authorize a complete compatible sing-box/Xray package without replacing NetCat.
public static class ModuleIntegrity
{
    private static readonly IReadOnlyDictionary<string,string> Trusted = Load();
    private static IReadOnlyDictionary<string,string> Load()
    {
        using var stream = typeof(ModuleIntegrity).Assembly.GetManifestResourceStream("NetCat.TrustedModules")!;
        return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;
    }
    public static bool IsPinned(string module) => module is "openvpn" or "sing-box" or "xray";
    public static int GetModuleRevision(string module)
    {
        var entries = Trusted.Where(p => p.Key.StartsWith(module + "/", StringComparison.Ordinal))
            .OrderBy(p => p.Key, StringComparer.Ordinal);
        using var sha = SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join(";", entries.Select(e => e.Key + "=" + e.Value)));
        var hashBytes = sha.ComputeHash(bytes);
        return BitConverter.ToInt32(hashBytes, 0);
    }
    public static void CheckPath(string path)
    {
        for (var node=Path.GetFullPath(path);node!=null;node=Path.GetDirectoryName(node))
            if ((File.Exists(node)||Directory.Exists(node)) && (File.GetAttributes(node)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Ссылка в пути исполняемого модуля запрещена.");
    }
    public static IDisposable? AcquireForExecutable(string executable)
    {
        if(!Path.IsPathFullyQualified(executable) || !Path.GetExtension(executable).Equals(".exe",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Требуется абсолютный путь к доверенному EXE.");
        var module=Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        var folder=Path.GetDirectoryName(Path.GetFullPath(executable))!;
        if(module=="winws")return ReviewedRuntimeTrust.Acquire("zapret",Path.GetDirectoryName(folder)!);
        if(module is "netcat.telegram" or "python" or "pythonw")return ReviewedRuntimeTrust.AcquireTelegram(folder);
        if(module=="openssl" && Path.GetFileName(folder).Equals("openvpn",StringComparison.OrdinalIgnoreCase))return Acquire("openvpn",folder);
        if (!IsPinned(module)) return NetCat.Core.WindowsExecutableTrust.Acquire(executable);
        return Acquire(module,Path.GetDirectoryName(Path.GetFullPath(executable))!);
    }
    public static IDisposable Acquire(string module,string folder)
    {
        if (!IsPinned(module)) throw new ArgumentException("Unknown pinned module",nameof(module));
        CheckPath(folder);

        if(UserApprovedCoreTrust.Exists(
                folder,
                module))
        {
            return UserApprovedCoreTrust.Acquire(
                module,
                folder);
        }

        var handles=new List<FileStream>();
        try
        {
            // A detached publisher signature authorizes an entire component
            // version. Adjacent unsigned hashes never become a trust root.
            var installed=ComponentTrust.Read(folder,module);
            if(installed!=null)ComponentTrust.VerifyFiles(folder,installed);
            var expected=installed==null ? Trusted.Where(p=>p.Key.StartsWith(module+"/",StringComparison.Ordinal)).ToArray()
                : installed.Files.Where(p=>Path.GetExtension(p.Key).ToLowerInvariant() is ".exe" or ".dll" or ".sys")
                    .Select(p=>new KeyValuePair<string,string>(module+"/"+p.Key,p.Value)).ToArray();
            var actual=SafeFiles(folder).Where(p=>Path.GetExtension(p).ToLowerInvariant() is ".exe" or ".dll" or ".sys")
                .Select(p=>module+"/"+Path.GetRelativePath(folder,p).Replace('\\','/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!actual.SetEquals(expected.Select(p=>p.Key))) throw new InvalidDataException("Состав исполняемого модуля изменён: "+module);
            foreach(var item in expected)
            {
                var file=Path.Combine(folder,item.Key[(module.Length+1)..]);CheckPath(file);
                var handle=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);handles.Add(handle);
                if (!Convert.ToHexString(SHA256.HashData(handle)).Equals(item.Value,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Не совпадает доверенный SHA-256 модуля: "+item.Key+". Установите проверенный пакет NetCat.");
            }
            return new Lease(handles);
        }
        catch {foreach(var handle in handles)handle.Dispose();throw;}
    }
    public static IEnumerable<string> SafeFiles(string folder)
    {
        CheckPath(folder);
        foreach(var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            CheckPath(entry);
            if(Directory.Exists(entry)){foreach(var file in SafeFiles(entry))yield return file;}
            else yield return entry;
        }
    }
    private sealed class Lease(List<FileStream> handles):IDisposable
    {public void Dispose(){foreach(var handle in handles)handle.Dispose();}}
}
