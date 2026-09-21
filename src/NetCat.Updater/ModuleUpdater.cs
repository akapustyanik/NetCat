using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Updater;
public sealed record ModuleRelease(string Key, string Repository, string Version, string Asset, string Url, string Sha256, string SourceTree = "");
public sealed record ModuleCheck(string Key, string Installed, ModuleRelease? Release, string Error = "")
{
    public bool Available => Release != null && ModuleUpdater.IsNewer(Release.Version, Installed);
    public string Latest => Release?.Version ?? "—";
    public string Status => Error.Length > 0 ? Error : Available ? "Доступно обновление" : "Актуальная версия";
}
public sealed class ModuleUpdater(string bin, Func<int>? proxyPort = null) : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int,HttpClient> clients = new();
    private HttpClient client => clients.GetOrAdd(proxyPort?.Invoke() ?? 0, CreateClient);
    public static HttpClient CreateClient(int port) => new(new SocketsHttpHandler { UseProxy=port>0, Proxy=port>0 ? new System.Net.WebProxy($"socks5://127.0.0.1:{port}") : null }) { Timeout=TimeSpan.FromMinutes(10) };
    public void Dispose() { foreach(var client in clients.Values) client.Dispose(); }
    private static readonly Dictionary<string,(string Repo,string Pattern,string Binary)> Sources = new()
    {
        ["netcat"] = ("akapustyanik/NetCat", @"^NetCat-v[0-9.]+\.zip$", "NetCat.exe"),
        ["sing-box"] = ("SagerNet/sing-box", @"^sing-box-[0-9.]+-windows-amd64\.zip$", "sing-box.exe"),
        ["xray"] = ("XTLS/Xray-core", @"^Xray-windows-64\.zip$", "xray.exe"),
        ["zapret"] = ("Flowseal/zapret-discord-youtube", @"\.zip$", "winws.exe"),
        ["tg-ws-proxy"] = ("Flowseal/tg-ws-proxy", @"^TgWsProxy_windows\.exe$", "TgWsProxy_windows.exe"),
        ["openvpn"] = ("OpenVPN/openvpn", "", "openvpn.exe"),
        ["geoip"] = ("Loyalsoldier/v2ray-rules-dat", @"^geoip\.dat$", "geoip.dat"),
        ["geosite"] = ("Loyalsoldier/v2ray-rules-dat", @"^geosite\.dat$", "geosite.dat"),
        ["wintun"] = ("WireGuard/wintun", "", "wintun.dll")
    };
    public static string[] Keys => Sources.Keys.ToArray();
    public static Version? ParseVersion(string value)
    {
        var text = value.TrimStart('v','V').Split('-','+')[0];
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{12}$") && DateTime.TryParseExact(text, "yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var stamp))
            return new Version(stamp.Year, stamp.Month, stamp.Day, stamp.Hour * 60 + stamp.Minute);
        return Version.TryParse(text, out var version) ? version : null;
    }
    public static bool IsNewer(string remote, string local)
    {
        static Version? Parse(string s) => ParseVersion(s);
        var a = Parse(remote); var b = Parse(local); if(a==null)return false;if(b==null)return true;
        int compare=new Version(a.Major,a.Minor,Math.Max(0,a.Build),Math.Max(0,a.Revision)).CompareTo(new Version(b.Major,b.Minor,Math.Max(0,b.Build),Math.Max(0,b.Revision)));
        if(compare!=0)return compare>0;
        var ra=remote.Split('+')[0].Split('-',2);var rb=local.Split('+')[0].Split('-',2);
        if(ra.Length!=rb.Length)return ra.Length==1;
        if(ra.Length==1)return false;
        var ap=ra[1].Split('.');var bp=rb[1].Split('.');
        for(int i=0;i<Math.Min(ap.Length,bp.Length);i++) { if(ap[i]==bp[i])continue;bool an=int.TryParse(ap[i],out int av),bn=int.TryParse(bp[i],out int bv);return an&&bn?av>bv:an!=bn?!an:string.CompareOrdinal(ap[i],bp[i])>0; }
        return ap.Length>bp.Length;
    }
    public string InstalledVersion(string key)
    {
        try
        {
            string? packaged=null;var installed=Path.Combine(Path.GetDirectoryName(bin)!,"metadata","installed.json");
            if(File.Exists(installed)) packaged=JsonNode.Parse(File.ReadAllText(installed))?[key]?.ToString();
            if(key=="netcat") return packaged ?? typeof(ModuleUpdater).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().First().InformationalVersion.Split('+')[0];
            if (key is "geoip" or "geosite" && !File.Exists(Path.Combine(bin,key,key+".dat"))) return "Не установлен";
            var source = Path.Combine(bin,key,"netcat-source.json");
            if (File.Exists(source)) { var version=JsonNode.Parse(File.ReadAllText(source))?["Version"]?.ToString() ?? "Неизвестна"; return version; }
            if(packaged!=null)return packaged;
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(bin,"modules.lock.json")))!.AsArray();
            return manifest.FirstOrDefault(m => m?["key"]?.ToString() == key)?["version"]?.ToString() ?? "Не установлен";
        }
        catch { return "Не установлен"; }
    }
    public async Task<ModuleCheck[]> CheckAllAsync(CancellationToken ct)
        => await Task.WhenAll(Keys.Select(async key =>
        {
            try { return new ModuleCheck(key, InstalledVersion(key), await CheckAsync(key,ct)); }
            catch (Exception e) when (e is not OperationCanceledException) { return new ModuleCheck(key,InstalledVersion(key),PreparedRelease(key),"Проверка не удалась"); }
        }));
    public async Task<ModuleRelease> CheckAsync(string key, CancellationToken ct)
    {
        var source = Sources[key];
        if (key == "wintun")
        {
            var page=await client.GetStringAsync("https://www.wintun.net/",ct);
            var match=System.Text.RegularExpressions.Regex.Match(page,@"(?:https://www\.wintun\.net)?/?builds/wintun-([0-9.]+)\.zip");
            if(!match.Success) throw new InvalidDataException("Не найден официальный выпуск Wintun.");
            return new(key,source.Repo,match.Groups[1].Value,$"wintun-{match.Groups[1].Value}.zip","https://www.wintun.net/builds/wintun-"+match.Groups[1].Value+".zip","");
        }
        if (key == "openvpn")
        {
            using var tagsRequest=new HttpRequestMessage(HttpMethod.Get,"https://api.github.com/repos/OpenVPN/openvpn/tags?per_page=60"); tagsRequest.Headers.UserAgent.ParseAdd("NetCat/0.5.0");
            using var tagsResponse=await client.SendAsync(tagsRequest,ct); tagsResponse.EnsureSuccessStatusCode();
            var tags=JsonNode.Parse(await tagsResponse.Content.ReadAsStringAsync(ct))!.AsArray();
            var tag=tags.Select(t=>t!["name"]!.ToString()).Where(t=>System.Text.RegularExpressions.Regex.IsMatch(t,@"^v2\.6\.\d+$")).OrderByDescending(t=>Version.Parse(t[1..])).First();
            var installer=$"OpenVPN-{tag[1..]}-I001-amd64.msi";
            var url="https://swupdate.openvpn.org/community/releases/"+installer;
            using var head=await client.SendAsync(new HttpRequestMessage(HttpMethod.Head,url),ct); head.EnsureSuccessStatusCode();
            return new(key,source.Repo,tag,installer,url,"");
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{source.Repo}/releases/latest"); request.Headers.UserAgent.ParseAdd("NetCat/0.5.0");
        using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        if(key=="netcat")
        {
            var tag=root["tag_name"]!.ToString();var name="NetCat-"+tag+".zip";
            var netcatAsset=root["assets"]!.AsArray().SingleOrDefault(a=>a?["name"]?.ToString()==name) ?? throw new InvalidDataException("Нет полного архива NetCat.");
            var netcatDigest=netcatAsset["digest"]?.ToString() ?? "";string hash=netcatDigest.StartsWith("sha256:")?netcatDigest[7..]:"";
            if(hash.Length!=64 && IsNewer(tag,InstalledVersion(key)))
            {
                var checksum=root["assets"]!.AsArray().SingleOrDefault(a=>a?["name"]?.ToString()==name+".sha256") ?? throw new InvalidDataException("Нет SHA-256 NetCat.");
                var address=checksum["browser_download_url"]!.ToString();if(!address.StartsWith("https://github.com/akapustyanik/NetCat/releases/download/"))throw new InvalidDataException("Неизвестный источник SHA-256.");
                var content=await client.GetStringAsync(address,ct);var match=System.Text.RegularExpressions.Regex.Match(content,@"^([a-fA-F0-9]{64})\s+\*?"+System.Text.RegularExpressions.Regex.Escape(name)+@"\s*$");
                if(!match.Success)throw new InvalidDataException("Некорректная SHA-256 NetCat.");hash=match.Groups[1].Value;
            }
            return new(key,source.Repo,tag,name,netcatAsset["browser_download_url"]!.ToString(),hash);
        }
        if (key == "tg-ws-proxy")
        {
            var tag = root["tag_name"]!.ToString();
            using var treeRequest = new HttpRequestMessage(HttpMethod.Get,$"https://api.github.com/repos/{source.Repo}/git/trees/{Uri.EscapeDataString(tag)}?recursive=1"); treeRequest.Headers.UserAgent.ParseAdd("NetCat/0.5.0");
            using var treeResponse = await client.SendAsync(treeRequest,ct); treeResponse.EnsureSuccessStatusCode();
            var tree = JsonNode.Parse(await treeResponse.Content.ReadAsStringAsync(ct))!;
            return new(key,source.Repo,tag,"headless source",$"https://github.com/{source.Repo}/tree/{tag}","",tree["sha"]!.ToString());
        }
        var matches = root["assets"]!.AsArray().Where(a => System.Text.RegularExpressions.Regex.IsMatch(a!["name"]!.ToString(), source.Pattern)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Не найден однозначный x64 asset.");
        var asset = matches[0]!; var digest = asset["digest"]?.ToString() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[a-f0-9]{64}$")) throw new InvalidDataException("У релиза нет проверяемой SHA-256 суммы.");
        return new(key, source.Repo, root["tag_name"]!.ToString(), asset["name"]!.ToString(), asset["browser_download_url"]!.ToString(), digest[7..]);
    }
    public async Task InstallAsync(ModuleRelease release, AppSettings settings, CancellationToken ct, bool prepareOnly = false)
    {
        if (settings.PinnedModules.Contains(release.Key)) throw new InvalidOperationException("Версия модуля закреплена.");
        var source = Sources[release.Key];
        if (release.Key == "tg-ws-proxy") { await InstallTelegramAsync(release,ct,prepareOnly); return; }
        if (release.Key is "openvpn" or "wintun") { await InstallSignedWindowsModuleAsync(release,ct,prepareOnly); return; }
        if (release.Repository != source.Repo || !release.Url.StartsWith($"https://github.com/{source.Repo}/releases/download/", StringComparison.Ordinal)) throw new InvalidDataException("Недопустимый источник модуля.");
        var staging = Path.Combine(bin, ".update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        var archive = Path.Combine(staging, "package"); var target = Path.Combine(bin, release.Key); var backup = target + ".previous"; bool moved = false;
        try
        {
            using var response = await client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            await using (var stream = File.Create(archive)) await response.Content.CopyToAsync(stream, ct);
            await using (var file = File.OpenRead(archive)) if (Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant() != release.Sha256) throw new InvalidDataException("SHA-256 не совпадает.");
            var extracted = Path.Combine(staging, "extracted"); Directory.CreateDirectory(extracted); string payload;
            if (release.Asset.EndsWith(".zip"))
            {
                ZipFile.ExtractToDirectory(archive, extracted); // Framework rejects entries escaping destination.
                var found = Directory.GetFiles(extracted, source.Binary, SearchOption.AllDirectories);
                if (found.Length != 1) throw new InvalidDataException("Некорректный состав архива.");
                payload = release.Key == "zapret" ? Directory.GetParent(Path.GetDirectoryName(found[0])!)!.FullName : Path.GetDirectoryName(found[0])!;
            }
            else { File.Copy(archive, Path.Combine(extracted, source.Binary)); payload = extracted; }
            if (release.Key == "sing-box" && File.Exists(Path.Combine(target, "wintun.dll"))) File.Copy(Path.Combine(target, "wintun.dll"), Path.Combine(payload, "wintun.dll"), true);
            if (release.Key == "sing-box")
            {
                var probe=Path.Combine(staging,"schema-check.json");
                var profile=ProfileImporter.ParseLink("vless://00000000-0000-4000-8000-000000000001@vpn.example.com:443?security=tls");
                var config=SingBoxConfig.Build(new AppSettings { TelegramSocks=true,OpenVpnDomains="office.example" },new NetworkSnapshot("Ethernet",1,"192.168.1.2","192.168.1.1",[]),profile,new OpenVpnLink("NetCat-OpenVPN",2,"10.42.0.2","10.42.0.1","10.42.0.53"),true);
                await File.WriteAllTextAsync(probe,config.ToJsonString(JsonSettings.Options),ct);
                var validation=await ProcessHost.RunAsync(Path.Combine(payload,"sing-box.exe"),["check","-c",probe],ct);
                if(validation.Code!=0)throw new InvalidDataException("Новая версия sing-box несовместима с конфигурацией NetCat. Предыдущая версия сохранена.");
            }
            if (release.Key is "geoip" or "geosite")
            {
                var database = new Geodata(Path.Combine(payload, source.Binary), release.Key == "geoip");
                if (File.Exists(Path.Combine(target, "LICENSE"))) File.Copy(Path.Combine(target, "LICENSE"), Path.Combine(payload, "LICENSE"));
                await File.WriteAllTextAsync(Path.Combine(payload, "SOURCE.txt"), "Geodata source and notices: https://github.com/Loyalsoldier/v2ray-rules-dat\nRelease: " + release.Version + "\nAsset: " + release.Url, ct);
                foreach (var category in settings.Rules.Where(r => r.Enabled && (release.Key == "geoip" ? r.Kind == RuleKind.GeoIp : r.Kind == RuleKind.GeoSite)).Select(r => r.Value).Distinct()) _ = database.Match(category);
            }
            if(release.Key=="xray")
            {
                var version=await ProcessHost.RunAsync(Path.Combine(payload,"xray.exe"),["version"],ct);
                if(version.Code!=0)throw new InvalidDataException("Новая версия Xray не запускается.");
            }
            if(release.Key=="zapret") foreach(var batch in Directory.GetFiles(payload,"general*.bat")) _=ZapretArguments.Build(batch,payload,"scenario-hosts.txt",true,true);
            await File.WriteAllTextAsync(Path.Combine(payload, "netcat-source.json"), System.Text.Json.JsonSerializer.Serialize(release, JsonSettings.Options), ct);
            if (prepareOnly) { await StagePayloadAsync(release,payload,ct); return; }
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(target)) { Directory.Move(target, backup); moved = true; }
            Directory.Move(payload, target);
        }
        catch { if (moved && !Directory.Exists(target)) Directory.Move(backup, target); throw; }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private async Task InstallSignedWindowsModuleAsync(ModuleRelease release,CancellationToken ct,bool prepareOnly)
    {
        bool ovpn=release.Key=="openvpn";
        var pattern=ovpn?@"^https://swupdate\.openvpn\.org/community/releases/OpenVPN-2\.6\.\d+-I\d+-amd64\.msi$":@"^https://www\.wintun\.net/builds/wintun-[0-9.]+\.zip$";
        if(!System.Text.RegularExpressions.Regex.IsMatch(release.Url,pattern)) throw new InvalidDataException("Недопустимый официальный источник.");
        var staging=Path.Combine(bin,".signed-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        var target=Path.Combine(bin,release.Key); var backup=target+".previous";
        static string Quote(string s)=>"'"+s.Replace("'","''")+"'";
        async Task Verify(string path,string signer)
        {
            var script="$s=Get-AuthenticodeSignature -LiteralPath "+Quote(path)+"; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch "+Quote(signer)+") {exit 1}";
            var result=await ProcessHost.RunAsync(ProcessHost.PowerShellPath,["-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))],ct);
            if(result.Code!=0) throw new InvalidDataException("Подпись компонента не прошла проверку.");
        }
        try
        {
            var archive=Path.Combine(staging,release.Asset); using(var response=await client.GetAsync(release.Url,HttpCompletionOption.ResponseHeadersRead,ct)) { response.EnsureSuccessStatusCode(); await using var file=File.Create(archive); await response.Content.CopyToAsync(file,ct); }
            var payload=Path.Combine(staging,"payload"); Directory.CreateDirectory(payload);
            if(ovpn)
            {
                await Verify(archive,"OpenVPN"); var extracted=Path.Combine(staging,"msi");
                var unpack=await ProcessHost.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"msiexec.exe"),["/a",archive,"/qn","TARGETDIR="+Path.GetFullPath(extracted)],ct);
                if(unpack.Code!=0) throw new IOException("Ошибка извлечения OpenVPN MSI.");
                var exe=Directory.GetFiles(extracted,"openvpn.exe",SearchOption.AllDirectories).Single();
                foreach(var f in Directory.GetFiles(Path.GetDirectoryName(exe)!)) File.Copy(f,Path.Combine(payload,Path.GetFileName(f)));
                var wintun=Path.Combine(bin,"wintun","wintun.dll"); if(!File.Exists(wintun)) wintun=Path.Combine(bin,"sing-box","wintun.dll"); File.Copy(wintun,Path.Combine(payload,"wintun.dll"),true);
                var help=await ProcessHost.RunAsync(Path.Combine(payload,"openvpn.exe"),["--help"],ct); if(!help.Output.Contains("wintun")) throw new InvalidDataException("Эта версия OpenVPN несовместима с Wintun.");
            }
            else
            {
                var extracted=Path.Combine(staging,"zip"); ZipFile.ExtractToDirectory(archive,extracted);
                var dll=Directory.GetFiles(extracted,"wintun.dll",SearchOption.AllDirectories).Single(f=>Path.GetFileName(Path.GetDirectoryName(f))=="amd64"); await Verify(dll,"WireGuard"); File.Copy(dll,Path.Combine(payload,"wintun.dll"));
            }
            await File.WriteAllTextAsync(Path.Combine(payload,"netcat-source.json"),System.Text.Json.JsonSerializer.Serialize(release,JsonSettings.Options),ct);
            if (prepareOnly) { await StagePayloadAsync(release,payload,ct); return; }
            ct.ThrowIfCancellationRequested(); if(Directory.Exists(backup))Directory.Delete(backup,true); if(Directory.Exists(target))Directory.Move(target,backup);
            try { Directory.Move(payload,target); if(!ovpn) CopyWintun(target); }
            catch { if(Directory.Exists(target))Directory.Delete(target,true); if(Directory.Exists(backup)) { Directory.Move(backup,target); if(!ovpn)CopyWintun(target); } throw; }
        }
        finally { if(Directory.Exists(staging))Directory.Delete(staging,true); }
    }
    private void CopyWintun(string source)
    {
        foreach(var key in new[]{"sing-box","openvpn"}) { var folder=Path.Combine(bin,key); if(Directory.Exists(folder)) File.Copy(Path.Combine(source,"wintun.dll"),Path.Combine(folder,"wintun.dll"),true); }
    }
    private async Task InstallTelegramAsync(ModuleRelease release, CancellationToken ct,bool prepareOnly)
    {
        if (release.Repository != "Flowseal/tg-ws-proxy" || !System.Text.RegularExpressions.Regex.IsMatch(release.SourceTree,"^[a-f0-9]{40}$")) throw new InvalidDataException("Непроверенный источник Telegram.");
        var staging = Path.Combine(bin,".telegram-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        var target = Path.Combine(bin,"tg-ws-proxy"); var backup = target+".previous";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,$"https://api.github.com/repos/Flowseal/tg-ws-proxy/git/trees/{release.SourceTree}?recursive=1"); request.Headers.UserAgent.ParseAdd("NetCat/0.5.0");
            using var response = await client.SendAsync(request,ct); response.EnsureSuccessStatusCode(); var tree=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            foreach (var item in tree["tree"]!.AsArray())
            {
                var path=item!["path"]!.ToString();
                if (item["type"]!.ToString() != "blob" || !(System.Text.RegularExpressions.Regex.IsMatch(path,@"^proxy/[a-zA-Z0-9_/]+\.py$") || path == "LICENSE")) continue;
                var bytes=await client.GetByteArrayAsync($"https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/{release.SourceTree}/{path}",ct);
                var prefix=System.Text.Encoding.UTF8.GetBytes($"blob {bytes.Length}\0");
                var blob=new byte[prefix.Length+bytes.Length]; prefix.CopyTo(blob,0); bytes.CopyTo(blob,prefix.Length);
                if (Convert.ToHexString(SHA1.HashData(blob)).ToLowerInvariant()!=item["sha"]!.ToString()) throw new InvalidDataException("Хеш исходника Telegram не совпал.");
                var file=Path.Combine(staging,path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllBytesAsync(file,bytes,ct);
            }
            if (!File.Exists(Path.Combine(staging,"proxy","tg_ws_proxy.py"))) throw new InvalidDataException("Нет CLI-модуля Telegram.");
            var validation=await ProcessHost.RunAsync(Path.Combine(bin,"tg-runtime","NetCat.Telegram.exe"),["-c","import sys; sys.path.insert(0,sys.argv[1]); import proxy.tg_ws_proxy",staging],ct);
            if(validation.Code!=0) throw new InvalidDataException("Новая версия Telegram несовместима со встроенной средой. Обновление не применено.");
            await File.WriteAllTextAsync(Path.Combine(staging,"netcat-source.json"),System.Text.Json.JsonSerializer.Serialize(release,JsonSettings.Options),ct);
            if (prepareOnly) { await StagePayloadAsync(release,staging,ct); return; }
            ct.ThrowIfCancellationRequested(); if(Directory.Exists(backup)) Directory.Delete(backup,true); if(Directory.Exists(target)) Directory.Move(target,backup);
            try { Directory.Move(staging,target); } catch { if(!Directory.Exists(target)&&Directory.Exists(backup)) Directory.Move(backup,target); throw; }
        }
        finally { if(Directory.Exists(staging)) Directory.Delete(staging,true); }
    }
    private sealed record PreparedModule(ModuleRelease Release, Dictionary<string,string> Files);
    private string PreparedFolder(string key)
    {
        if (!Sources.ContainsKey(key) || key=="netcat") throw new ArgumentException("Неизвестный модуль.");
        return Path.Combine(bin,".prepared",key);
    }
    public ModuleRelease? PreparedRelease(string key)
    {
        if (key=="netcat") return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<PreparedModule>(File.ReadAllText(Path.Combine(PreparedFolder(key),"prepared.json")),JsonSettings.Options)?.Release; }
        catch { return null; }
    }
    public bool HasPrepared(ModuleRelease release) => PreparedRelease(release.Key) == release;
    private async Task StagePayloadAsync(ModuleRelease release,string payload,CancellationToken ct)
    {
        var files = new Dictionary<string,string>();
        foreach(var path in Directory.GetFiles(payload,"*",SearchOption.AllDirectories))
        {
            await using var stream=File.OpenRead(path);
            files[Path.GetRelativePath(payload,path).Replace('\\','/')] = Convert.ToHexString(await SHA256.HashDataAsync(stream,ct));
        }
        await File.WriteAllTextAsync(Path.Combine(payload,"prepared.json"),System.Text.Json.JsonSerializer.Serialize(new PreparedModule(release,files),JsonSettings.Options),ct);
        PrivateFiles.ProtectDirectory(payload,true);
        var destination=PreparedFolder(release.Key); PrivateFiles.ProtectDirectory(Path.GetDirectoryName(destination)!,true);
        ct.ThrowIfCancellationRequested(); if(Directory.Exists(destination)) Directory.Delete(destination,true); Directory.Move(payload,destination);
    }
    public async Task InstallPreparedAsync(ModuleRelease release,AppSettings settings,CancellationToken ct)
    {
        if(settings.PinnedModules.Contains(release.Key)) throw new InvalidOperationException("Версия модуля закреплена.");
        var folder=PreparedFolder(release.Key);
        var prepared=System.Text.Json.JsonSerializer.Deserialize<PreparedModule>(await File.ReadAllTextAsync(Path.Combine(folder,"prepared.json"),ct),JsonSettings.Options) ?? throw new InvalidDataException("Нет подготовленного обновления.");
        if(prepared.Release != release || prepared.Files.Count==0 || prepared.Files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=prepared.Files.Count) throw new InvalidDataException("Подготовлена другая версия модуля или повторяются пути.");
        var actual=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).Select(p=>Path.GetRelativePath(folder,p).Replace('\\','/')).Where(p=>p!="prepared.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!actual.SetEquals(prepared.Files.Keys)) throw new InvalidDataException("Состав подготовленного модуля изменён. Скачайте его заново.");
        if(!IsNewer(release.Version,InstalledVersion(release.Key))) throw new InvalidOperationException("Установлена такая же или более новая версия.");
        var target=Path.Combine(bin,release.Key); var backup=target+".previous";
        var clean=Path.Combine(Path.GetDirectoryName(folder)!,".install-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(clean);
        bool moved=false, installed=false;
        try
        {
            // Copy only listed files and hash the copies before replacing anything.
            // The control manifest and files added after enumeration never enter the live module.
            foreach(var file in prepared.Files)
            {
                if(file.Key.Equals("prepared.json",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Манифест не является файлом модуля.");
                var copy=PortableUpdate.SafePath(clean,file.Key); Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                await using(var input=File.OpenRead(PortableUpdate.SafePath(folder,file.Key))) await using(var output=File.Create(copy)) await input.CopyToAsync(output,ct);
                await using var stream=File.OpenRead(copy);
                if(!Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)).Equals(file.Value,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Подготовленный модуль повреждён. Скачайте его заново.");
            }
            ct.ThrowIfCancellationRequested(); if(Directory.Exists(backup)) Directory.Delete(backup,true);
            if(Directory.Exists(target)) { Directory.Move(target,backup); moved=true; }
            Directory.Move(clean,target); installed=true;
            if(release.Key=="wintun") CopyWintun(target);
            RecordOwnership(release.Key);
        }
        catch
        {
            if(installed) Directory.Move(target,clean);
            if(moved) { Directory.Move(backup,target); if(release.Key=="wintun") CopyWintun(target); }
            throw;
        }
        finally { if(Directory.Exists(clean)) Directory.Delete(clean,true); }
        Directory.Delete(folder,true);
    }
    private void RecordOwnership(string key)
    {
        var files=new List<PackageFile>();
        foreach(var directory in key=="tg-ws-proxy" ? new[]{"tg-ws-proxy","tg-runtime"} : new[]{key})
        {
            var folder=Path.Combine(bin,directory); if(!Directory.Exists(folder)) continue;
            foreach(var path in Directory.GetFiles(folder,"*",SearchOption.AllDirectories))
            {
                var relative="modules/"+Path.GetRelativePath(bin,path).Replace('\\','/'); if(!PortableUpdate.Owns(key,relative)) continue;
                using var stream=File.OpenRead(path); files.Add(new(relative,Convert.ToHexString(SHA256.HashData(stream))));
            }
        }
        if(key=="wintun") foreach(var secondary in new[]{"sing-box","openvpn"})
        { var copy=Path.Combine(bin,secondary,"wintun.dll"); if(!File.Exists(copy)) continue; using var stream=File.OpenRead(copy); files.Add(new("modules/"+secondary+"/wintun.dll",Convert.ToHexString(SHA256.HashData(stream)))); }
        var root=Path.GetDirectoryName(Path.GetFullPath(bin))!;
        var metadata=PortableUpdate.SafePath(root,"metadata/components/"+key+".json"); Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        var temporary=metadata+".new";
        File.WriteAllText(temporary,System.Text.Json.JsonSerializer.Serialize(new PackageComponent(key,InstalledVersion(key),files),JsonSettings.Options)); File.Move(temporary,metadata,true);
    }
    public void Rollback(string key)
    {
        if (!Sources.ContainsKey(key)) throw new ArgumentException("Неизвестный модуль.");
        var target = Path.Combine(bin, key); var backup = target + ".previous";
        if (!Directory.Exists(backup)) throw new InvalidOperationException("Резервной версии нет.");
        var swap = target + ".swap"; Directory.Move(target, swap); try { Directory.Move(backup, target); Directory.Move(swap, backup); } catch { if (!Directory.Exists(target)) Directory.Move(swap, target); throw; }
        if(key=="wintun") CopyWintun(target);
        RecordOwnership(key);
    }
}
