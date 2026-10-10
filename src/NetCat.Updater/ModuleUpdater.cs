using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Updater;
public enum UpdateStatusKind
{
    Checking,
    Current,
    UpdateAvailable,
    AutoUpdateUnsupported,
    TemporarilyUnavailable,
    NetworkError,
    ProviderError,
    IntegrityError
}

public enum VersionStatus
{
    Current,
    UpdateAvailable,
    Unknown,
    CheckFailed
}

public enum InstallabilityStatus
{
    Supported,
    PublisherManifestRequired,
    Unsupported,
    BlockedByPolicy,
    AwaitingTrustedPackage
}

public sealed record ModuleRelease(string Key, string Repository, string Version, string Asset, string Url, string Sha256, string SourceTree = "", string UpstreamVersion = "")
{
    public string? ComponentManifestBase64 { get; init; }
    public string? ComponentSignatureBase64 { get; init; }
}

public sealed record ModuleCheck(
    string Key,
    string Installed,
    ModuleRelease? Release,
    string Error = "",
    UpdateStatusKind StatusKind = UpdateStatusKind.Current,
    VersionStatus VersionStatus = VersionStatus.Current,
    InstallabilityStatus Installability = InstallabilityStatus.Supported
)
{
    public bool Available => Release != null && ModuleUpdater.IsNewer(Release.Version, Installed);
    // Available describes upstream discovery. Only this predicate describes a
    // checked update that the normal installer can actually accept.
    public bool InstallableUpdate => Available && Error.Length == 0 &&
        Release!.Key == Key && Installability == InstallabilityStatus.Supported &&
        VersionStatus is not (VersionStatus.Unknown or VersionStatus.CheckFailed) &&
        StatusKind is UpdateStatusKind.Current or UpdateStatusKind.UpdateAvailable &&
        AutoUpdateSupported;
    public string Latest => Release?.Version ?? "—";
    public string UpstreamLatest => !string.IsNullOrEmpty(Release?.UpstreamVersion) ? Release.UpstreamVersion : Latest;
    public bool AutoUpdateSupported
    {
        get
        {
            if (Release == null ||
                string.IsNullOrEmpty(Release.Url) ||
                string.IsNullOrEmpty(Release.Asset))
                return false;

            // OpenVPN is deliberately excluded from the normal module updater.
            if (Key == "openvpn")
                return false;

            if (ModuleUpdater.IsDirectUpstreamModule(Key))
                return ModuleUpdater.IsTrustedDirectUpstreamRelease(Release);

            // sing-box/Xray retain the publisher-approved default path.
            return
                (!ModuleIntegrity.IsPinned(Key) ||
                    ModuleUpdater.HasComponentAuthorization(Release)) &&
                (!ReviewedRuntimeTrust.RequiresReviewedPackage(Key) ||
                    ModuleUpdater.HasComponentAuthorization(Release));
        }
    }
    public string Status => GetStatusText();
    public string UpdateBlockedReason => Key is "sing-box" or "xray" or "zapret" or "tg-ws-proxy" && Release != null && !ModuleUpdater.HasComponentAuthorization(Release)
        ? "издатель ещё не выпустил подписанный совместимый пакет EXE и DLL" : "требуется отдельная сборка NetCat";

    private string GetStatusText()
    {
        if (StatusKind == UpdateStatusKind.NetworkError)
            return "Не удалось связаться с источником обновлений — Повторить";
        if (StatusKind == UpdateStatusKind.AutoUpdateUnsupported && Installability != InstallabilityStatus.PublisherManifestRequired && Installability != InstallabilityStatus.AwaitingTrustedPackage)
            return $"Доступна версия {Latest}, автообновление пока недоступно";
        if (StatusKind == UpdateStatusKind.TemporarilyUnavailable)
            return "Источник временно недоступен — Повторить";
        if (StatusKind == UpdateStatusKind.ProviderError)
            return Error.Length > 0 ? Error : "Ошибка источника обновлений";
        if (Error.Length > 0)
            return Error;
        if (Installability is InstallabilityStatus.PublisherManifestRequired or InstallabilityStatus.AwaitingTrustedPackage)
            return "Обновление найдено, но ещё не одобрено издателем NetCat";
        if (VersionStatus == VersionStatus.UpdateAvailable || Available)
        {
            if (Key == "openvpn")
                return $"Доступна версия {Latest} (требует отдельной сборки)";
            if (!AutoUpdateSupported)
                return $"Доступна версия {Latest}, автообновление пока недоступно";
            return $"Доступно обновление: {Latest}";
        }
        if (Release != null && !string.IsNullOrEmpty(Release.UpstreamVersion) && ModuleUpdater.IsNewer(Release.UpstreamVersion, Installed) && !Available)
        {
            return $"Установлена актуальная поддерживаемая NetCat бинарная версия. Upstream {Release.UpstreamVersion} доступна, автоматическое обновление пока недоступно.";
        }
        return "Установлена актуальная версия";
    }
}
public sealed partial class ModuleUpdater(string bin, Func<int>? proxyPort = null) : IDisposable
{
    private readonly ProxyHttpClientPool clients = new(() => proxyPort?.Invoke() ?? 0, CreateClient);
    public static HttpClient CreateClient(int port) => new(new SocketsHttpHandler { UseProxy=port>0, Proxy=port>0 ? new System.Net.WebProxy($"socks5://127.0.0.1:{port}") : null }) { Timeout=TimeSpan.FromMinutes(10) };
    public void Dispose() => clients.Dispose();
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

    // Components that follow their official upstream independently from
    // the NetCat application release cycle.
    public static bool IsDirectUpstreamModule(string key) =>
        key is "zapret" or "tg-ws-proxy" or
               "geoip" or "geosite" or "wintun";

    // This validates the immutable identity of the expected upstream source.
    // Actual downloaded contents are still verified later by the installer.
    public static bool IsTrustedDirectUpstreamRelease(ModuleRelease release)
    {
        static bool Sha256(string value) =>
            System.Text.RegularExpressions.Regex.IsMatch(
                value ?? "",
                "^[a-fA-F0-9]{64}$");

        static bool GitTree(string value) =>
            System.Text.RegularExpressions.Regex.IsMatch(
                value ?? "",
                "^[a-fA-F0-9]{40}$");

        return release.Key switch
        {
            "zapret" =>
                release.Repository ==
                    "Flowseal/zapret-discord-youtube" &&
                release.Asset.EndsWith(
                    ".zip",
                    StringComparison.OrdinalIgnoreCase) &&
                release.Url.StartsWith(
                    "https://github.com/Flowseal/zapret-discord-youtube/releases/download/",
                    StringComparison.Ordinal) &&
                Sha256(release.Sha256),

            "tg-ws-proxy" =>
                release.Repository ==
                    "Flowseal/tg-ws-proxy" &&
                release.Asset ==
                    "headless source" &&
                release.Url.StartsWith(
                    "https://github.com/Flowseal/tg-ws-proxy/tree/",
                    StringComparison.Ordinal) &&
                GitTree(release.SourceTree),

            "geoip" =>
                release.Repository ==
                    "Loyalsoldier/v2ray-rules-dat" &&
                release.Asset.Equals(
                    "geoip.dat",
                    StringComparison.OrdinalIgnoreCase) &&
                release.Url.StartsWith(
                    "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/download/",
                    StringComparison.Ordinal) &&
                Sha256(release.Sha256),

            "geosite" =>
                release.Repository ==
                    "Loyalsoldier/v2ray-rules-dat" &&
                release.Asset.Equals(
                    "geosite.dat",
                    StringComparison.OrdinalIgnoreCase) &&
                release.Url.StartsWith(
                    "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/download/",
                    StringComparison.Ordinal) &&
                Sha256(release.Sha256),

            "wintun" =>
                release.Repository ==
                    "WireGuard/wintun" &&
                System.Text.RegularExpressions.Regex.IsMatch(
                    release.Asset,
                    @"^wintun-[0-9.]+\.zip$") &&
                System.Text.RegularExpressions.Regex.IsMatch(
                    release.Url,
                    @"^https://www\.wintun\.net/builds/wintun-[0-9.]+\.zip$"),

            _ => false
        };
    }
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
            if (key is "sing-box" or "xray" && ComponentTrust.Read(Path.Combine(bin,key),key) is {} component) return component.Version;
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
    public event Action<string>? Log;

    public async Task<ModuleCheck[]> CheckAllAsync(CancellationToken ct)
        => await Task.WhenAll(Keys.Select(async key => await CheckWithClassificationAsync(key, ct)));

    public async Task<ModuleCheck> CheckWithClassificationAsync(string key, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var installed = InstalledVersion(key);
        var repo = Sources.TryGetValue(key, out var src) ? src.Repo : "custom";
        try
        {
            var release = await CheckAsync(key, ct);
            sw.Stop();
            var available = release != null && IsNewer(release.Version, installed);
            var initialCheck = new ModuleCheck(key, installed, release);
            var versionStatus = available ? VersionStatus.UpdateAvailable : VersionStatus.Current;
            var installability =
                !available
                    ? InstallabilityStatus.Supported
                    : key == "openvpn"
                        ? InstallabilityStatus.Unsupported
                        : IsDirectUpstreamModule(key)
                            ? initialCheck.AutoUpdateSupported
                                ? InstallabilityStatus.Supported
                                : InstallabilityStatus.BlockedByPolicy
                            : key is "sing-box" or "xray" &&
                              !HasComponentAuthorization(release!)
                                ? InstallabilityStatus.PublisherManifestRequired
                                : initialCheck.AutoUpdateSupported
                                    ? InstallabilityStatus.Supported
                                    : InstallabilityStatus.Unsupported;

            var statusKind = available ? (initialCheck.AutoUpdateSupported ? UpdateStatusKind.UpdateAvailable : UpdateStatusKind.AutoUpdateUnsupported) : UpdateStatusKind.Current;
            var result = available ? (initialCheck.AutoUpdateSupported ? "update-available" : "auto-update-unsupported") : "current";
            Log?.Invoke($"UPDATE_CHECK module={key} provider={repo} versionStatus={versionStatus} installability={installability} result={result} httpStatus=200 errorClass=none duration={sw.ElapsedMilliseconds}ms");
            return initialCheck with { StatusKind = statusKind, VersionStatus = versionStatus, Installability = installability };
        }
        catch (HttpRequestException he)
        {
            sw.Stop();
            var status = he.StatusCode.HasValue ? ((int)he.StatusCode.Value).ToString() : "network";
            Log?.Invoke($"UPDATE_CHECK module={key} provider={repo} versionStatus=CheckFailed installability=Unsupported result=failed httpStatus={status} errorClass=NetworkError duration={sw.ElapsedMilliseconds}ms");
            return new ModuleCheck(key, installed, PreparedRelease(key), "Не удалось связаться с источником обновлений — Повторить", UpdateStatusKind.NetworkError, VersionStatus.CheckFailed, InstallabilityStatus.Unsupported);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            sw.Stop();
            Log?.Invoke($"UPDATE_CHECK module={key} provider={repo} versionStatus=CheckFailed installability=Unsupported result=failed errorClass={e.GetType().Name} duration={sw.ElapsedMilliseconds}ms");
            var kind = e is InvalidDataException ? UpdateStatusKind.ProviderError : UpdateStatusKind.TemporarilyUnavailable;
            return new ModuleCheck(key, installed, PreparedRelease(key), "Не удалось связаться с источником обновлений — Повторить", kind, VersionStatus.CheckFailed, InstallabilityStatus.Unsupported);
        }
    }

    public async Task<ModuleRelease> CheckAsync(string key, CancellationToken ct)
    {
        using var clientLease = clients.Acquire();
        var client = clientLease.Client;
        var source = Sources[key];
        if (key == "wintun")
        {
            var page = await client.GetStringAsync("https://www.wintun.net/", ct);
            var match = System.Text.RegularExpressions.Regex.Match(page, @"(?:https://www\.wintun\.net)?/?builds/wintun-([0-9.]+)\.zip");
            if (!match.Success) throw new InvalidDataException("Не найден официальный выпуск Wintun.");
            return new(key, source.Repo, match.Groups[1].Value, $"wintun-{match.Groups[1].Value}.zip", "https://www.wintun.net/builds/wintun-" + match.Groups[1].Value + ".zip", "");
        }
        if (key == "openvpn")
        {
            using var tagsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/OpenVPN/openvpn/tags?per_page=60");
            tagsRequest.Headers.UserAgent.ParseAdd("NetCat/1.0.0");
            using var tagsResponse = await client.SendAsync(tagsRequest, ct);
            tagsResponse.EnsureSuccessStatusCode();
            var tags = JsonNode.Parse(await tagsResponse.Content.ReadAsStringAsync(ct))!.AsArray();
            var candidateTags = tags
                .Select(t => t!["name"]!.ToString())
                .Where(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^v2\.6\.\d+$"))
                .OrderByDescending(t => Version.Parse(t[1..]))
                .ToArray();

            string? validTag = null;
            string? validInstaller = null;
            string? validUrl = null;

            foreach (var t in candidateTags)
            {
                var inst = $"OpenVPN-{t[1..]}-I001-amd64.msi";
                var u = "https://swupdate.openvpn.org/community/releases/" + inst;
                try
                {
                    using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, u), ct);
                    if (head.IsSuccessStatusCode)
                    {
                        validTag = t;
                        validInstaller = inst;
                        validUrl = u;
                        break;
                    }
                }
                catch when (!ct.IsCancellationRequested) { }
            }

            var latestUpstream = candidateTags.FirstOrDefault() ?? "v2.6.23";
            if (validTag != null)
            {
                return new(key, source.Repo, validTag, validInstaller!, validUrl!, "", "", latestUpstream);
            }
            return new(key, source.Repo, latestUpstream, "", "", "", "", latestUpstream);
        }
        var endpoint = key == "netcat" && ReleaseTrust.Channel == "beta" ? "releases?per_page=100" : "releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{source.Repo}/{endpoint}"); request.Headers.UserAgent.ParseAdd($"NetCat/{ReleaseTrust.CurrentVersion}");
        using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        if(key=="netcat")
        {
            if (root is JsonArray releases) root = SelectNetCatRelease(releases, ReleaseTrust.Channel);
            var tag=root["tag_name"]!.ToString(); var name=$"NetCat-{tag}-win-x64.zip";
            var netcatAsset=root["assets"]!.AsArray().SingleOrDefault(a=>a?["name"]?.ToString()==name);
            // The initial beta predates detached signatures and is never a self-update target.
            if (!IsNewer(tag, InstalledVersion(key))) return new(key, source.Repo, tag, name, "", "");
            if (netcatAsset == null) throw new InvalidDataException("Нет подписанного полного архива NetCat.");
            var url = netcatAsset["browser_download_url"]!.ToString();
            var signed = await ReleaseTrust.FetchAsync(client, url, tag, InstalledVersion(key), ct);
            return new(key,source.Repo,tag,name,url,signed.Manifest.Package!.Sha256);
        }
        if (key == "tg-ws-proxy")
        {
            var tag = root["tag_name"]!.ToString();
            using var treeRequest = new HttpRequestMessage(HttpMethod.Get,$"https://api.github.com/repos/{source.Repo}/git/trees/{Uri.EscapeDataString(tag)}?recursive=1"); treeRequest.Headers.UserAgent.ParseAdd($"NetCat/{ReleaseTrust.CurrentVersion}");
            using var treeResponse = await client.SendAsync(treeRequest,ct); treeResponse.EnsureSuccessStatusCode();
            var tree = JsonNode.Parse(await treeResponse.Content.ReadAsStringAsync(ct))!;
            return new(key,source.Repo,tag,"headless source",$"https://github.com/{source.Repo}/tree/{tag}","",tree["sha"]!.ToString());
        }
        var matches = root["assets"]!.AsArray().Where(a => System.Text.RegularExpressions.Regex.IsMatch(a!["name"]!.ToString(), source.Pattern)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Не найден однозначный x64 asset.");
        var asset = matches[0]!; var digest = asset["digest"]?.ToString() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[a-f0-9]{64}$")) throw new InvalidDataException("У релиза нет проверяемой SHA-256 суммы.");
        var release=new ModuleRelease(key, source.Repo, root["tag_name"]!.ToString(), asset["name"]!.ToString(), asset["browser_download_url"]!.ToString(), digest[7..]);
        return key is "sing-box" or "xray"
            ? await AuthorizeComponentAsync(release,ct)
            : release;
    }
    public static JsonNode SelectNetCatRelease(JsonArray releases, string channel)
    {
        var eligible = releases.Where(r => r != null && r["draft"]?.GetValue<bool>() != true &&
            (channel == "beta" || r["prerelease"]?.GetValue<bool>() != true)).ToArray();
        JsonNode? newest = null;
        foreach (var release in eligible)
            if (ParseVersion(release!["tag_name"]!.ToString()) != null && (newest == null || IsNewer(release["tag_name"]!.ToString(), newest["tag_name"]!.ToString()))) newest = release;
        return newest ?? throw new InvalidDataException("Нет выпусков выбранного канала.");
    }
    public async Task InstallAsync(
        ModuleRelease release,
        AppSettings settings,
        CancellationToken ct,
        bool prepareOnly = false,
        bool selectedVersion = false,
        bool forceUnreviewed = false)
    {
        using var clientLease = clients.Acquire();
        var client = clientLease.Client;
        ct.ThrowIfCancellationRequested();

        var coreSelection =
            release.Key is "sing-box" or "xray";

        if(selectedVersion)
        {
            if(!prepareOnly)
                throw new InvalidOperationException(
                    "Выбранная вручную версия сначала должна быть подготовлена.");

            if(release.Key is not (
                "zapret" or
                "tg-ws-proxy" or
                "sing-box" or
                "xray"))
            {
                throw new InvalidOperationException(
                    "Для этого компонента явный выбор версии не поддерживается.");
            }

            if(release.Key is "zapret" or "tg-ws-proxy")
            {
                if(!IsTrustedDirectUpstreamRelease(release))
                    throw new InvalidDataException(
                        "Выбранная версия не прошла проверку официального upstream.");
            }
            else
            {
                var publisherApproved =
                    HasComponentAuthorization(release);

                if(!publisherApproved &&
                   !forceUnreviewed)
                {
                    throw new InvalidOperationException(
                        "Эта версия VPN-ядра не подтверждена NetCat. Требуется явная принудительная установка.");
                }

                if(!publisherApproved &&
                   !IsTrustedCoreUpstreamRelease(release))
                {
                    throw new InvalidDataException(
                        "VPN-ядро не прошло проверку официального upstream.");
                }
            }
        }

        if(forceUnreviewed &&
           (!selectedVersion ||
            !prepareOnly ||
            !coreSelection))
        {
            throw new InvalidOperationException(
                "Принудительная установка разрешена только для явно выбранной версии sing-box/Xray на этапе подготовки.");
        }

        if (settings.PinnedModules.Contains(release.Key) &&
            !selectedVersion) throw new InvalidOperationException("Версия модуля закреплена.");
        if (ReviewedRuntimeTrust.RequiresReviewedPackage(release.Key) &&
            !HasComponentAuthorization(release) &&
            !IsTrustedDirectUpstreamRelease(release))
        {
            throw new InvalidOperationException(
                "Runtime не прошёл проверку официального upstream.");
        }
        if ((release.Key == "sing-box" ||
             release.Key == "xray") &&
            !HasComponentAuthorization(release) &&
            !forceUnreviewed)
        {
            throw new InvalidOperationException(
                "Эта версия VPN-ядра не подтверждена NetCat.");
        }

        if ((release.Key == "sing-box" ||
             release.Key == "xray") &&
            HasComponentAuthorization(release))
        {
            await PrepareSignedComponentAsync(release,settings,ct,selectedVersion);

            if(!prepareOnly)
                await InstallPreparedAsync(
                    release,
                    settings,
                    ct);

            return;
        }
        var source = Sources[release.Key];
        if (release.Key == "tg-ws-proxy")
        {
            await InstallTelegramAsync(
                release,
                ct,
                prepareOnly,
                selectedVersion);

            return;
        }
        if (release.Key is "openvpn" or "wintun")
        {
            await InstallSignedWindowsModuleAsync(
                release,
                ct,
                prepareOnly,
                selectedVersion);

            return;
        }
        if (release.Repository != source.Repo || !release.Url.StartsWith($"https://github.com/{source.Repo}/releases/download/", StringComparison.Ordinal)) throw new InvalidDataException("Недопустимый источник модуля.");
        var staging = Path.Combine(bin, ".update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        var archive = Path.Combine(staging, "package"); var target = Path.Combine(bin, release.Key); var backup = target + ".previous"; bool moved = false;
        try
        {
            using var response = await client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            await PackageLimits.DownloadAsync(response, archive, ct);
            await using (var file = File.OpenRead(archive)) if (Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant() != release.Sha256) throw new InvalidDataException("SHA-256 не совпадает.");
            var extracted = Path.Combine(staging, "extracted"); Directory.CreateDirectory(extracted); string payload;
            if (release.Asset.EndsWith(".zip"))
            {
                await PackageLimits.ExtractAsync(archive, extracted, ct);
                var found = Directory.GetFiles(extracted, source.Binary, SearchOption.AllDirectories);
                if (found.Length != 1) throw new InvalidDataException("Некорректный состав архива.");
                payload = release.Key == "zapret" ? Directory.GetParent(Path.GetDirectoryName(found[0])!)!.FullName : Path.GetDirectoryName(found[0])!;
            }
            else { File.Copy(archive, Path.Combine(extracted, source.Binary)); payload = extracted; }
            if (release.Key == "sing-box")
            {
                var wintun =
                    Path.Combine(
                        bin,
                        "wintun",
                        "wintun.dll");

                if(!File.Exists(wintun))
                {
                    wintun =
                        Path.Combine(
                            target,
                            "wintun.dll");
                }

                if(!File.Exists(wintun) ||
                   !PublisherTrust.IsTrusted(wintun))
                {
                    throw new InvalidDataException(
                        "Для этой версии sing-box требуется проверенный Wintun.");
                }

                File.Copy(
                    wintun,
                    Path.Combine(
                        payload,
                        "wintun.dll"),
                    true);
            }
            if (release.Key == "sing-box")
            {
                var probe=Path.Combine(staging,"schema-check.json");
                var profile=ProfileImporter.ParseLink("vless://00000000-0000-4000-8000-000000000001@vpn.example.com:443?security=tls");
                var config=SingBoxConfig.Build(new AppSettings { TelegramSocks=true,OpenVpnDomains="office.example" },new NetworkSnapshot("Ethernet",1,"192.168.1.2","192.168.1.1",[]),profile,new OpenVpnLink("NetCat-OpenVPN",2,"10.42.0.2","10.42.0.1","10.42.0.53"),true);
                await File.WriteAllTextAsync(probe,config.ToJsonString(JsonSettings.Options),ct);
                var executable =
                    Path.Combine(
                        payload,
                        "sing-box.exe");

                var stagingTrust =
                    StagedExecutableTrustPolicy.Capture(
                        payload,
                        executable);

                var validation =
                    await ProcessHost.RunAsync(
                        executable,
                        ["check","-c",probe],
                        ct,
                        trustPolicy: stagingTrust);

                Log?.Invoke(
                    "UPDATE_VERIFY module=sing-box result=staging-runtime-compatible");
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
                var executable =
                    Path.Combine(
                        payload,
                        "xray.exe");

                var stagingTrust =
                    StagedExecutableTrustPolicy.Capture(
                        payload,
                        executable);

                var version =
                    await ProcessHost.RunAsync(
                        executable,
                        ["version"],
                        ct,
                        trustPolicy: stagingTrust);

                Log?.Invoke(
                    "UPDATE_VERIFY module=xray result=staging-runtime-compatible");
                if(version.Code!=0)throw new InvalidDataException("Новая версия Xray не запускается.");
            }
            if ((release.Key is "sing-box" or "xray") &&
                forceUnreviewed)
            {
                if(!IsTrustedCoreUpstreamRelease(release))
                    throw new InvalidDataException(
                        "VPN-core не прошёл проверку официального upstream.");

                UserApprovedCoreTrust.Write(
                    payload,
                    release.Key,
                    release.Repository,
                    release.Version,
                    release.Sha256);
            }

            if(release.Key=="zapret")
            {
                if(!IsTrustedDirectUpstreamRelease(release))
                {
                    throw new InvalidDataException(
                        "Zapret не прошёл проверку официального upstream.");
                }

                var batches =
                    Directory.GetFiles(
                        payload,
                        "general*.bat");

                if(batches.Length==0)
                {
                    throw new InvalidDataException(
                        "В пакете Zapret нет стратегий general*.bat.");
                }

                foreach(var batch in batches)
                {
                    _=ZapretArguments.Build(
                        batch,
                        payload,
                        "scenario-hosts.txt",
                        true,
                        true);
                }

                UpstreamRuntimeTrust.Write(
                    payload,
                    "zapret",
                    release.Repository,
                    release.Version,
                    release.Sha256);
            }
            await File.WriteAllTextAsync(Path.Combine(payload, "netcat-source.json"), System.Text.Json.JsonSerializer.Serialize(release, JsonSettings.Options), ct);
            if (prepareOnly) { await StagePayloadAsync(release,payload,ct,selectedVersion); return; }
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(target)) { Directory.Move(target, backup); moved = true; }
            Directory.Move(payload, target);
        }
        catch { if (moved && !Directory.Exists(target)) Directory.Move(backup, target); throw; }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private async Task InstallSignedWindowsModuleAsync(
        ModuleRelease release,
        CancellationToken ct,
        bool prepareOnly,
        bool selectedVersion = false)
    {
        using var clientLease = clients.Acquire();
        var client = clientLease.Client;
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
            var archive=Path.Combine(staging,release.Asset); using(var response=await client.GetAsync(release.Url,HttpCompletionOption.ResponseHeadersRead,ct)) await PackageLimits.DownloadAsync(response,archive,ct);
            var payload=Path.Combine(staging,"payload"); Directory.CreateDirectory(payload);
            if(ovpn)
            {
                await Verify(archive,"OpenVPN"); var extracted=Path.Combine(staging,"msi");
                var unpack=await ProcessHost.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"msiexec.exe"),["/a",archive,"/qn","TARGETDIR="+Path.GetFullPath(extracted)],ct);
                var exe=Directory.GetFiles(extracted,"openvpn.exe",SearchOption.AllDirectories).Single();
                foreach(var f in Directory.GetFiles(Path.GetDirectoryName(exe)!)) File.Copy(f,Path.Combine(payload,Path.GetFileName(f)));
                var legacy=Directory.GetFiles(extracted,"legacy.dll",SearchOption.AllDirectories).FirstOrDefault();
                if(legacy!=null){var modDir=Path.Combine(payload,"ssl","modules");Directory.CreateDirectory(modDir);File.Copy(legacy,Path.Combine(modDir,"legacy.dll"),true);}
                var wintun=Path.Combine(bin,"wintun","wintun.dll"); if(!File.Exists(wintun)) wintun=Path.Combine(bin,"sing-box","wintun.dll"); File.Copy(wintun,Path.Combine(payload,"wintun.dll"),true);
                var help=await ProcessHost.RunAsync(Path.Combine(payload,"openvpn.exe"),["--help"],ct); if(!help.Output.Contains("wintun")) throw new InvalidDataException("Эта версия OpenVPN несовместима с Wintun.");
            }
            else
            {
                var extracted=Path.Combine(staging,"zip"); await PackageLimits.ExtractAsync(archive,extracted,ct);
                var dll=Directory.GetFiles(extracted,"wintun.dll",SearchOption.AllDirectories).Single(f=>Path.GetFileName(Path.GetDirectoryName(f))=="amd64"); await Verify(dll,"WireGuard"); File.Copy(dll,Path.Combine(payload,"wintun.dll"));
            }
            await File.WriteAllTextAsync(Path.Combine(payload,"netcat-source.json"),System.Text.Json.JsonSerializer.Serialize(release,JsonSettings.Options),ct);
            if (prepareOnly) { await StagePayloadAsync(release,payload,ct,selectedVersion); return; }
            ct.ThrowIfCancellationRequested(); if(Directory.Exists(backup))Directory.Delete(backup,true); if(Directory.Exists(target))Directory.Move(target,backup);
            try { Directory.Move(payload,target); if(!ovpn) CopyWintun(target); }
            catch { if(Directory.Exists(target))Directory.Delete(target,true); if(Directory.Exists(backup)) { Directory.Move(backup,target); if(!ovpn)CopyWintun(target); } throw; }
        }
        finally { if(Directory.Exists(staging))Directory.Delete(staging,true); }
    }
    private void CopyWintun(string source)
    {
        var sourceDll =
            Path.Combine(
                source,
                "wintun.dll");

        foreach(var key in
                new[]{"sing-box","openvpn"})
        {
            var folder =
                Path.Combine(
                    bin,
                    key);

            if(!Directory.Exists(folder))
                continue;

            var destination =
                Path.Combine(
                    folder,
                    "wintun.dll");

            var rollback =
                destination +
                ".netcat-before-wintun-update";

            var hadPrevious =
                File.Exists(destination);

            if(hadPrevious)
            {
                File.Copy(
                    destination,
                    rollback,
                    true);
            }

            try
            {
                File.Copy(
                    sourceDll,
                    destination,
                    true);

                if(key=="sing-box" &&
                   UserApprovedCoreTrust.Exists(
                       folder,
                       "sing-box"))
                {
                    UserApprovedCoreTrust
                        .RefreshAfterVerifiedDependencyUpdate(
                            "sing-box",
                            folder);
                }

                using var lease =
                    ModuleIntegrity.Acquire(
                        key,
                        folder);

                if(File.Exists(rollback))
                    File.Delete(rollback);

                Log?.Invoke(
                    $"WINTUN_COPY module={key} result=updated");
            }
            catch(Exception e)
            {
                try
                {
                    if(hadPrevious &&
                       File.Exists(rollback))
                    {
                        File.Copy(
                            rollback,
                            destination,
                            true);
                    }
                    else if(!hadPrevious &&
                            File.Exists(destination))
                    {
                        File.Delete(destination);
                    }

                    if(key=="sing-box" &&
                       UserApprovedCoreTrust.Exists(
                           folder,
                           "sing-box"))
                    {
                        UserApprovedCoreTrust
                            .RefreshAfterVerifiedDependencyUpdate(
                                "sing-box",
                                folder);
                    }
                }
                finally
                {
                    if(File.Exists(rollback))
                        File.Delete(rollback);
                }

                Log?.Invoke(
                    $"WINTUN_COPY module={key} result=kept-previous reason={e.GetType().Name}");
            }
        }
    }
    private async Task InstallTelegramAsync(
        ModuleRelease release,
        CancellationToken ct,
        bool prepareOnly,
        bool selectedVersion = false)
    {
        using var clientLease = clients.Acquire();
        var client = clientLease.Client;
        if (release.Repository != "Flowseal/tg-ws-proxy" || !System.Text.RegularExpressions.Regex.IsMatch(release.SourceTree,"^[a-f0-9]{40}$")) throw new InvalidDataException("Непроверенный источник Telegram.");
        var staging = Path.Combine(bin,".telegram-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        var target = Path.Combine(bin,"tg-ws-proxy"); var backup = target+".previous";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,$"https://api.github.com/repos/Flowseal/tg-ws-proxy/git/trees/{release.SourceTree}?recursive=1"); request.Headers.UserAgent.ParseAdd($"NetCat/{ReleaseTrust.CurrentVersion}");
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
            await ValidateTelegramAsync(staging,ct);
            if(!IsTrustedDirectUpstreamRelease(release))
            {
                throw new InvalidDataException(
                    "Telegram proxy не прошёл проверку официального upstream.");
            }

            UpstreamRuntimeTrust.Write(
                staging,
                "tg-ws-proxy",
                release.Repository,
                release.Version,
                release.SourceTree);

            await File.WriteAllTextAsync(
                Path.Combine(
                    staging,
                    "netcat-source.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    release,
                    JsonSettings.Options),
                ct);
            if (prepareOnly) { await StagePayloadAsync(release,staging,ct,selectedVersion); return; }
            ct.ThrowIfCancellationRequested(); if(Directory.Exists(backup)) Directory.Delete(backup,true); if(Directory.Exists(target)) Directory.Move(target,backup);
            try { Directory.Move(staging,target); } catch { if(!Directory.Exists(target)&&Directory.Exists(backup)) Directory.Move(backup,target); throw; }
        }
        finally { if(Directory.Exists(staging)) Directory.Delete(staging,true); }
    }
    private async Task ValidateTelegramAsync(string staging,CancellationToken ct)
    {
        // Match the production interpreter flags. Validation must not mutate
        // the reviewed runtime or populate the candidate with bytecode.
        const string script = "import sys\nsys.path.insert(0,sys.argv[1])\ntry:\n import proxy.tg_ws_proxy\nexcept ModuleNotFoundError as error:\n print('NETCAT_MISSING_MODULE:' + str(error.name))\n sys.exit(78)\n";
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var validation=await ProcessHost.RunAsync(
            Path.Combine(bin,"tg-runtime","NetCat.Telegram.exe"),
            ["-I","-B","-c",script,staging],deadline.Token);
        if(validation.Code==0)return;

        // Only expose a validated dependency name, never arbitrary process output.
        var missing=System.Text.RegularExpressions.Regex.Match(
            validation.Output,@"(?m)^NETCAT_MISSING_MODULE:([A-Za-z_][A-Za-z0-9_.]{0,99})\r?$");
        if(validation.Code==78 && missing.Success)
            throw new InvalidDataException(
                $"Новая версия Telegram требует библиотеку {missing.Groups[1].Value}, которой нет во встроенной среде. Обновите NetCat. Работающий модуль не заменён.");
        throw new InvalidDataException(
            "Новая версия Telegram несовместима со встроенной средой. Обновление не применено.");
    }
    private sealed record PreparedModule(
        ModuleRelease Release,
        Dictionary<string,string> Files,
        bool SelectedVersion = false);
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
    private async Task StagePayloadAsync(
        ModuleRelease release,
        string payload,
        CancellationToken ct,
        bool selectedVersion = false)
    {
        var files = new Dictionary<string,string>();
        foreach(var path in Directory.GetFiles(payload,"*",SearchOption.AllDirectories))
        {
            await using var stream=File.OpenRead(path);
            files[Path.GetRelativePath(payload,path).Replace('\\','/')] = Convert.ToHexString(await SHA256.HashDataAsync(stream,ct));
        }
        await File.WriteAllTextAsync(Path.Combine(payload,"prepared.json"),System.Text.Json.JsonSerializer.Serialize(new PreparedModule(release,files,selectedVersion),JsonSettings.Options),ct);
        PrivateFiles.ProtectDirectory(payload,true);
        var destination=PreparedFolder(release.Key); PrivateFiles.ProtectDirectory(Path.GetDirectoryName(destination)!,true);
        ct.ThrowIfCancellationRequested(); if(Directory.Exists(destination)) Directory.Delete(destination,true); Directory.Move(payload,destination);
    }
    public async Task InstallPreparedAsync(ModuleRelease release,AppSettings settings,CancellationToken ct,Func<CancellationToken,Task>? postInstallHealth=null,Func<Task>? beforeRollback=null)
    {
        ct.ThrowIfCancellationRequested();

        if(settings.PinnedModules.Contains(release.Key) &&
           !PreparedIsSelectedVersion(release))
        {
            throw new InvalidOperationException(
                "Версия модуля закреплена.");
        }

        var folder=PreparedFolder(release.Key);

        var upstreamPrepared =
            (release.Key == "zapret" ||
             release.Key == "tg-ws-proxy") &&
            IsTrustedDirectUpstreamRelease(release) &&
            UpstreamRuntimeTrust.Exists(
                folder,
                release.Key);

        if(ReviewedRuntimeTrust.RequiresReviewedPackage(release.Key) &&
           !upstreamPrepared)
        {
            throw new InvalidOperationException(
                "Подготовленный runtime не имеет проверенного upstream trust receipt.");
        }
        var prepared=System.Text.Json.JsonSerializer.Deserialize<PreparedModule>(await File.ReadAllTextAsync(Path.Combine(folder,"prepared.json"),ct),JsonSettings.Options) ?? throw new InvalidDataException("Нет подготовленного обновления.");
        if(prepared.Release != release || prepared.Files.Count==0 || prepared.Files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=prepared.Files.Count) throw new InvalidDataException("Подготовлена другая версия модуля или повторяются пути.");
        var actual=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).Select(p=>Path.GetRelativePath(folder,p).Replace('\\','/')).Where(p=>p!="prepared.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!actual.SetEquals(prepared.Files.Keys)) throw new InvalidDataException("Состав подготовленного модуля изменён. Скачайте его заново.");
        if(!prepared.SelectedVersion &&
           !IsNewer(
               release.Version,
               InstalledVersion(release.Key)))
        {
            throw new InvalidOperationException(
                "Установлена такая же или более новая версия.");
        }
        var target=Path.Combine(bin,release.Key); var backup=target+".previous";
        var clean=Path.Combine(Path.GetDirectoryName(folder)!,".install-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(clean);
        bool moved=false, installed=false, journalStarted=false;
        try
        {
            // Copy only listed files and hash the copies before replacing anything.
            // The control manifest and files added after enumeration never enter the live module.
            // Verify local authorization at its source, before an elevated copy
            // can give an unprivileged receipt a new administrative owner/ACL.
            using (var sourceApproval = UserApprovedCoreTrust.Exists(folder, release.Key)
                ? UserApprovedCoreTrust.Acquire(release.Key, folder)
                : UpstreamRuntimeTrust.Exists(folder, release.Key)
                    ? UpstreamRuntimeTrust.Acquire(release.Key, folder) : null)
            {
            foreach(var file in prepared.Files)
            {
                if(file.Key.Equals("prepared.json",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Манифест не является файлом модуля.");
                var copy=PortableUpdate.SafePath(clean,file.Key); Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                await using(var input=File.OpenRead(PortableUpdate.SafePath(folder,file.Key))) await using(var output=File.Create(copy)) await input.CopyToAsync(output,ct);
                await using var stream=File.OpenRead(copy);
                if(!Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)).Equals(file.Value,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Подготовленный модуль повреждён. Скачайте его заново.");
                if (WindowsExecutableTrust.IsElevated && file.Key is UserApprovedCoreTrust.ReceiptName or UpstreamRuntimeTrust.ReceiptName)
                {
                    // File.Copy/CopyTo inherits the destination ACL. Preserve
                    // the verified source approval's restricted descriptor,
                    // rather than rejecting a legitimate installed receipt or
                    // converting an unprivileged one into privileged approval.
                    using var approval = File.OpenRead(PortableUpdate.SafePath(folder, file.Key));
                    var security = approval.GetAccessControl();
                    RuntimeTrustReceipt.RequireAdministratorControl(security);
                    var copiedSecurity = new System.Security.AccessControl.FileSecurity();
                    copiedSecurity.SetSecurityDescriptorBinaryForm(security.GetSecurityDescriptorBinaryForm());
                    new FileInfo(copy).SetAccessControl(copiedSecurity);
                }
            }
            }
            var userApprovedCore =
                (release.Key is "sing-box" or "xray") &&
                prepared.SelectedVersion &&
                UserApprovedCoreTrust.Exists(
                    clean,
                    release.Key);

            if (ModuleIntegrity.IsPinned(release.Key) &&
                !HasComponentAuthorization(release) &&
                !userApprovedCore)
            {
                throw new InvalidOperationException(
                    "Нужен подписанный пакет NetCat либо явно подтверждённая пользователем версия официального VPN-core.");
            }

            if(release.Key is "sing-box" or "xray")
            {
                if(HasComponentAuthorization(release))
                {
                    var authorized =
                        VerifyComponentAuthorization(release);

                    ComponentTrust.VerifyFiles(
                        clean,
                        authorized);

                    using var lease =
                        ModuleIntegrity.Acquire(
                            release.Key,
                            clean);
                }
                else if(userApprovedCore)
                {
                    using var lease =
                        ModuleIntegrity.Acquire(
                            release.Key,
                            clean);
                }
                else
                {
                    throw new InvalidDataException(
                        "VPN-core не имеет допустимого trust path.");
                }
            }
            if(release.Key is "zapret" or "tg-ws-proxy")
            {
                if(!upstreamPrepared)
                {
                    throw new InvalidDataException(
                        "Нет проверенного upstream trust receipt.");
                }

                using(
                    var lease=
                        ReviewedRuntimeTrust.Acquire(
                            release.Key,
                            clean))
                {
                }
            }

            if(release.Key=="wintun")
            {
                var dll=
                    Path.Combine(
                        clean,
                        "wintun.dll");

                if(!File.Exists(dll) ||
                   !PublisherTrust.IsTrusted(dll))
                {
                    throw new InvalidDataException(
                        "Подготовленный Wintun не прошёл повторную проверку Authenticode.");
                }
            }
            ComponentInstallJournal.EnsureNoPending(bin,release.Key);
            ct.ThrowIfCancellationRequested(); if(Directory.Exists(backup)) Directory.Delete(backup,true);
            // Elevated installs exclude the unelevated user; user-mode portable
            // installs restrict journal writes to their actual owner and SYSTEM.
            var elevated=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            PrivateFiles.ProtectDirectory(Path.Combine(bin,".transactions"),elevated);
            ComponentInstallJournal.Begin(bin,release.Key,clean);
            journalStarted=true;
            if(Directory.Exists(target)) { Directory.Move(target,backup); moved=true; }
            Directory.Move(clean,target); installed=true;
            Log?.Invoke($"UPDATE_INSTALL module={release.Key} result=swapped");
            if(release.Key=="wintun") CopyWintun(target);
            if(release.Key is "sing-box" or "xray") {using var lease=ModuleIntegrity.Acquire(release.Key,target);}
            Log?.Invoke($"UPDATE_POST_VERIFY module={release.Key} result=ok");
            RecordOwnership(release.Key);
            if(postInstallHealth!=null)await postInstallHealth(ct);
            ComponentInstallJournal.Commit(bin,release.Key);
        }
        catch
        {
            Log?.Invoke($"UPDATE_ROLLBACK module={release.Key} reason=install-or-health-failed");
            if(installed&&beforeRollback!=null)await beforeRollback();
            if(installed) Directory.Move(target,clean);
            if(moved) { Directory.Move(backup,target); if(release.Key=="wintun") CopyWintun(target); RecordOwnership(release.Key); }
            if(journalStarted)ComponentInstallJournal.Recover(bin,release.Key);
            throw;
        }
        finally { if(Directory.Exists(clean)) Directory.Delete(clean,true); }
        Directory.Delete(folder,true);
    }
    public void RecoverInterruptedInstalls()=>ComponentInstallJournal.RecoverAll(bin,RecordOwnership);
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
        var receipt=System.Text.Json.JsonSerializer.SerializeToNode(new PackageComponent(key,InstalledVersion(key),files),JsonSettings.Options)!.AsObject();
        receipt["InstalledAtUtc"]=DateTimeOffset.UtcNow;
        receipt["TrustSource"] =
            key is "sing-box" or "xray" &&
            UserApprovedCoreTrust.Exists(
                Path.Combine(bin,key),
                key)
                ? "user-approved-official-upstream"
                : key is "sing-box" or "xray" &&
                  File.Exists(
                      Path.Combine(
                          bin,
                          key,
                          ComponentTrust.ManifestName))
                    ? "publisher-signed-component"
                    : "bundled-or-verified-upstream";
        File.WriteAllText(temporary,receipt.ToJsonString(JsonSettings.Options)); File.Move(temporary,metadata,true);
    }
    public void Rollback(string key)
    {
        if(!Sources.ContainsKey(key))
            throw new ArgumentException(
                "Неизвестный модуль.",
                nameof(key));

        if(key=="openvpn")
            throw new InvalidOperationException(
                "OpenVPN исключён из общего механизма rollback.");

        var target=
            Path.Combine(bin,key);

        var backup=
            target+".previous";

        if(!Directory.Exists(target))
            throw new InvalidOperationException(
                "Текущая версия модуля отсутствует.");

        if(!Directory.Exists(backup))
            throw new InvalidOperationException(
                "Резервной версии нет.");

        // Verify the candidate BEFORE changing any live directories.
        if(key is "sing-box" or "xray")
        {
            using var lease=
                ModuleIntegrity.Acquire(
                    key,
                    backup);
        }
        else if(key is "zapret" or "tg-ws-proxy")
        {
            using var lease=
                ReviewedRuntimeTrust.Acquire(
                    key,
                    backup);
        }
        else if(key=="wintun")
        {
            var dll=
                Path.Combine(
                    backup,
                    "wintun.dll");

            if(!File.Exists(dll) ||
               !PublisherTrust.IsTrusted(dll))
            {
                throw new InvalidDataException(
                    "Предыдущая версия Wintun не прошла проверку Authenticode.");
            }
        }

        var swap=
            target+".swap";

        if(Directory.Exists(swap))
            Directory.Delete(
                swap,
                true);

        var targetMoved=false;
        var backupActivated=false;

        try
        {
            Directory.Move(
                target,
                swap);

            targetMoved=true;

            Directory.Move(
                backup,
                target);

            backupActivated=true;

            Directory.Move(
                swap,
                backup);

            targetMoved=false;
        }
        catch
        {
            // Restore the pre-operation layout whenever possible.
            if(backupActivated &&
               Directory.Exists(target) &&
               !Directory.Exists(backup))
            {
                Directory.Move(
                    target,
                    backup);

                backupActivated=false;
            }

            if(targetMoved &&
               Directory.Exists(swap) &&
               !Directory.Exists(target))
            {
                Directory.Move(
                    swap,
                    target);

                targetMoved=false;
            }

            throw;
        }

        if(key=="wintun")
            CopyWintun(target);

        RecordOwnership(key);
    }
}
