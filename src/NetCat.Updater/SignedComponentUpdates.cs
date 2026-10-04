using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Updater;

public sealed partial class ModuleUpdater
{
    public static string ComponentReleaseBase(string key,string version)
    {
        if(key is not ("sing-box" or "xray" or "zapret" or "tg-ws-proxy") || !Regex.IsMatch(version,@"^v?\d+(?:\.\d+){1,3}(?:-[A-Za-z0-9.]+)?$"))throw new InvalidDataException("Недопустимый выпуск компонента.");
        return $"https://github.com/akapustyanik/NetCat/releases/download/modules-{key}-{version}/";
    }
    public static ComponentManifest VerifyComponentAuthorization(ModuleRelease release)
    {
        if(release.ComponentManifestBase64==null||release.ComponentSignatureBase64==null)throw new InvalidDataException("Издатель ещё не выпустил подписанный совместимый пакет компонента.");
        var m=ComponentTrust.Verify(Convert.FromBase64String(release.ComponentManifestBase64),Convert.FromBase64String(release.ComponentSignatureBase64),release.Key);
        if(m.Version!=release.Version||m.Asset!=release.Asset||m.Provider!=release.Repository||!m.ArchiveSha256.Equals(release.Sha256,StringComparison.OrdinalIgnoreCase)||release.Url!=ComponentReleaseBase(m.Key,m.Version)+m.Asset)
            throw new InvalidDataException("Пакет не соответствует подписи компонента.");
        return m;
    }
    public static bool HasComponentAuthorization(ModuleRelease release)
    {try{_ = VerifyComponentAuthorization(release);return true;}catch(Exception e)when(e is InvalidDataException or FormatException or JsonException){return false;}}
    private async Task<ModuleRelease> AuthorizeComponentAsync(ModuleRelease upstream,CancellationToken ct)
    {
        var source=ComponentReleaseBase(upstream.Key,upstream.Version);
        try
        {
            var bytes=await ReleaseTrust.DownloadAsync(client,source+ComponentTrust.ManifestName,2*1024*1024,ct);
            var sig=await ReleaseTrust.DownloadAsync(client,source+ComponentTrust.SignatureName,64,ct);
            var m=ComponentTrust.Verify(bytes,sig,upstream.Key);
            var authorized=upstream with {Asset=m.Asset,Url=source+m.Asset,Sha256=m.ArchiveSha256,ComponentManifestBase64=Convert.ToBase64String(bytes),ComponentSignatureBase64=Convert.ToBase64String(sig)};
            _=VerifyComponentAuthorization(authorized);return authorized;
        }
        catch(HttpRequestException e)when(e.StatusCode==System.Net.HttpStatusCode.NotFound)
        {Log?.Invoke($"UPDATE_CHECK module={upstream.Key} result=blocked reason=publisher-manifest-unavailable");return upstream;}
    }
    private async Task PrepareSignedComponentAsync(
        ModuleRelease release,
        AppSettings settings,
        CancellationToken ct,
        bool selectedVersion = false)
    {
        var manifest=VerifyComponentAuthorization(release);
        if(!selectedVersion &&
           !IsNewer(
               manifest.Version,
               InstalledVersion(release.Key)))
        {
            throw new InvalidDataException(
                "Повтор или понижение версии компонента запрещены.");
        }
        var staging=Path.Combine(bin,".signed-component-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
        try
        {
            Log?.Invoke($"UPDATE_DOWNLOAD module={release.Key} source=signed-component");
            var archive=Path.Combine(staging,"archive.zip");
            using(var response=await client.GetAsync(release.Url,HttpCompletionOption.ResponseHeadersRead,ct))
                await PackageLimits.DownloadAsync(response,archive,ct);
            await using(var file=File.OpenRead(archive))
                if(!Convert.ToHexString(await SHA256.HashDataAsync(file,ct)).Equals(manifest.ArchiveSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("SHA-256 архива компонента не совпадает.");
            var payload=Path.Combine(staging,"payload");Directory.CreateDirectory(payload);
            using(var zip=ZipFile.OpenRead(archive))
            {
                var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
                foreach(var entry in zip.Entries)
                {
                    if(entry.FullName.EndsWith('/'))continue;
                    if(!seen.Add(entry.FullName)||!manifest.Files.ContainsKey(entry.FullName)||(total+=entry.Length)>1024L*1024*1024)throw new InvalidDataException("Неожиданный состав архива компонента.");
                    _=PortableUpdate.SafePath(payload,entry.FullName);
                }
            }
            await PackageLimits.ExtractAsync(archive,payload,ct);
            ComponentTrust.VerifyFiles(payload,manifest);
            await File.WriteAllBytesAsync(Path.Combine(payload,ComponentTrust.ManifestName),Convert.FromBase64String(release.ComponentManifestBase64!),ct);
            await File.WriteAllBytesAsync(Path.Combine(payload,ComponentTrust.SignatureName),Convert.FromBase64String(release.ComponentSignatureBase64!),ct);
            Log?.Invoke($"UPDATE_VERIFY module={release.Key} result=signature-and-files-ok");
            var binaryName = release.Key switch
            {
                "zapret" => File.Exists(Path.Combine(payload, "bin", "winws.exe")) ? Path.Combine("bin", "winws.exe") : "winws.exe",
                "tg-ws-proxy" => "TgWsProxy_windows.exe",
                _ => release.Key + ".exe"
            };
            var checkArgs = release.Key switch
            {
                "zapret" => new[] { "--version" },
                "tg-ws-proxy" => new[] { "--help" },
                _ => new[] { "version" }
            };
            var binaryPath = Path.Combine(payload, binaryName);
            if (File.Exists(binaryPath))
            {
                var check = await ProcessHost.RunAsync(binaryPath, checkArgs, ct);
                if (check.Code != 0) throw new InvalidDataException("Компонент не прошёл проверку запуска.");
            }
            if(release.Key=="sing-box")
            {
                // Exercise the current schema before stopping a working router.
                // These reserved fixture addresses are never contacted by `check`.
                var profile=ProfileImporter.ParseLink("vless://00000000-0000-4000-8000-000000000001@vpn.example.com:443?security=tls");
                var config=SingBoxConfig.Build(new AppSettings {TelegramSocks=true,OpenVpnDomains="office.example"},
                    new NetworkSnapshot("Ethernet",1,"192.0.2.2","192.0.2.1",[]),profile,
                    new OpenVpnLink("NetCat-OpenVPN",2,"192.0.2.10","192.0.2.11","192.0.2.53"),true);
                var probe=Path.Combine(staging,"schema-check.json");
                await File.WriteAllTextAsync(probe,config.ToJsonString(JsonSettings.Options),ct);
                var validation=await ProcessHost.RunAsync(Path.Combine(payload,"sing-box.exe"),["check","-c",probe],ct);
                if(validation.Code!=0)throw new InvalidDataException("Пакет sing-box несовместим с конфигурацией NetCat.");
            }
            await File.WriteAllTextAsync(Path.Combine(payload,"netcat-source.json"),JsonSerializer.Serialize(release,JsonSettings.Options),ct);
            await StagePayloadAsync(release,payload,ct,selectedVersion);
            Log?.Invoke($"UPDATE_PREPARE module={release.Key} result=ready");
        }
        finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
    }
}
