using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace NetCat.Engine;

public sealed record ComponentManifest(int Schema, string Key, string Version, string Provider,
    string Asset, string ArchiveSha256, int RuntimeApi, string Architecture, Dictionary<string,string> Files);

public static class ComponentTrust
{
    public const string ManifestName = "netcat-component.json";
    public const string SignatureName = "netcat-component.sig";
    public static byte[] PublicKey
    {
        get { using var s=typeof(ComponentTrust).Assembly.GetManifestResourceStream("NetCat.ComponentPublicKey")!;using var r=new StreamReader(s);return Convert.FromHexString(r.ReadToEnd().Trim()); }
    }
    public static ComponentManifest Verify(byte[] bytes, byte[] signature, string key, byte[]? publicKey = null)
    {
        var pk=publicKey??PublicKey;
        if(bytes.Length>2*1024*1024||signature.Length!=64||pk.Length!=32)throw new InvalidDataException("Некорректная подпись пакета компонента.");
        var verifier=new Ed25519Signer();verifier.Init(false,new Ed25519PublicKeyParameters(pk,0));verifier.BlockUpdate(bytes,0,bytes.Length);
        if(!verifier.VerifySignature(signature))throw new InvalidDataException("Подпись пакета компонента не прошла проверку.");
        var m=JsonSerializer.Deserialize<ComponentManifest>(bytes)??throw new InvalidDataException("Нет манифеста компонента.");
        var provider=key switch {"sing-box"=>"SagerNet/sing-box","xray"=>"XTLS/Xray-core","zapret"=>"Flowseal/zapret-discord-youtube","tg-ws-proxy"=>"Flowseal/tg-ws-proxy",_=>throw new InvalidDataException("Неподдерживаемый подписанный компонент.")};
        if(m.Schema!=1||m.Key!=key||m.Provider!=provider||m.RuntimeApi!=1||m.Architecture!="win-x64"||
           !Regex.IsMatch(m.Version??"",@"^v?\d+(?:\.\d+){1,3}(?:-[A-Za-z0-9.]+)?$")||
           m.Asset!=$"{key}-{m.Version}-win-x64.zip"||!Hash(m.ArchiveSha256)||m.Files==null||m.Files.Count==0||m.Files.Count>1024)
            throw new InvalidDataException("Несовместимый манифест компонента.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in m.Files)
            if(!SafeRelative(file.Key)||!names.Add(file.Key)||!Hash(file.Value)||IsMetadata(file.Key))
                throw new InvalidDataException("Недопустимый состав компонента.");
        var mainExe = key switch { "zapret" => "bin/winws.exe", "tg-ws-proxy" => "TgWsProxy_windows.exe", _ => key + ".exe" };
        if(!names.Contains(mainExe) && !names.Contains(key+".exe"))throw new InvalidDataException("В пакете нет исполняемого файла компонента.");
        return m;
    }
    private static bool Hash(string? value)=>Regex.IsMatch(value??"","^[a-fA-F0-9]{64}$");
    private static bool SafeRelative(string path)=>path.Length>0&&!path.Contains('\\')&&!path.Contains(':')&&!path.StartsWith('/')&&
        path.Split('/').All(p=>p.Length>0&&p is not "." and not ".."&&!p.EndsWith('.')&&!p.EndsWith(' ')&&
            !p.Any(c=>c<32||"<>\"|?*".Contains(c))&&!Regex.IsMatch(p,@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)",RegexOptions.IgnoreCase));
    private static bool IsMetadata(string path)=>new[]{ManifestName,SignatureName,"netcat-source.json","prepared.json"}.Contains(path,StringComparer.OrdinalIgnoreCase);
    public static ComponentManifest? Read(string folder,string key)
    {
        var manifest=Path.Combine(folder,ManifestName);var signature=Path.Combine(folder,SignatureName);
        if(!File.Exists(manifest)&&!File.Exists(signature))return null;
        ModuleIntegrity.CheckPath(manifest);ModuleIntegrity.CheckPath(signature);
        return Verify(File.ReadAllBytes(manifest),File.ReadAllBytes(signature),key);
    }
    public static void VerifyFiles(string folder,ComponentManifest manifest)
    {
        ModuleIntegrity.CheckPath(folder);
        var actual=ModuleIntegrity.SafeFiles(folder)
            .Select(p=>Path.GetRelativePath(folder,p).Replace('\\','/'))
            .Where(p=>!IsMetadata(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!actual.SetEquals(manifest.Files.Keys))throw new InvalidDataException("Пакет компонента неполон или содержит лишние файлы.");
        foreach(var item in manifest.Files)
        {
            var path=Path.Combine(folder,item.Key);ModuleIntegrity.CheckPath(path);using var file=File.OpenRead(path);
            if(!Convert.ToHexString(SHA256.HashData(file)).Equals(item.Value,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Нарушена целостность пакета компонента.");
        }
    }
}
