using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Updater;

public sealed partial class ModuleUpdater
{
    public static bool SupportsVersionSelection(string key) =>
        key is
            "zapret" or
            "tg-ws-proxy" or
            "sing-box" or
            "xray";

    public static bool IsTrustedCoreUpstreamRelease(
        ModuleRelease release)
    {
        if(release.Key is not ("sing-box" or "xray"))
            return false;

        var source =
            Sources[release.Key];

        return
            release.Repository == source.Repo &&

            release.Url.StartsWith(
                $"https://github.com/{source.Repo}/releases/download/",
                StringComparison.Ordinal) &&

            System.Text.RegularExpressions.Regex.IsMatch(
                release.Asset,
                source.Pattern) &&

            System.Text.RegularExpressions.Regex.IsMatch(
                release.Sha256 ?? "",
                "^[a-fA-F0-9]{64}$");
    }

    public static bool RequiresForceUnreviewed(
        ModuleRelease release) =>
        release.Key is "sing-box" or "xray" &&
        IsTrustedCoreUpstreamRelease(release) &&
        !HasComponentAuthorization(release);

    private bool PreparedIsSelectedVersion(
        ModuleRelease release)
    {
        try
        {
            var path=
                Path.Combine(
                    PreparedFolder(release.Key),
                    "prepared.json");

            var prepared=
                System.Text.Json.JsonSerializer
                    .Deserialize<PreparedModule>(
                        File.ReadAllText(path),
                        JsonSettings.Options);

            return
                prepared != null &&
                prepared.Release == release &&
                prepared.SelectedVersion;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListVersionsAsync(
        string key,
        int limit,
        CancellationToken ct)
    {
        using var clientLease = clients.Acquire();
        var client = clientLease.Client;
        if(!SupportsVersionSelection(key))
            throw new InvalidOperationException(
                "Для этого компонента выбор версии пока не поддерживается.");

        limit=Math.Clamp(limit,1,500);

        var source=Sources[key];

        var result=
            new List<string>();

        var seen=
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        for(
            var page=1;
            page<=10 && result.Count<limit;
            page++)
        {
            using var request=
                new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://api.github.com/repos/{source.Repo}/releases?per_page=100&page={page}");

            request.Headers.UserAgent.ParseAdd(
                "NetCat/1.0.0");

            using var response=
                await client.SendAsync(
                    request,
                    ct);

            response.EnsureSuccessStatusCode();

            var array=
                JsonNode.Parse(
                    await response.Content
                        .ReadAsStringAsync(ct))!
                .AsArray();

            if(array.Count==0)
                break;

            foreach(var node in array)
            {
                if(node == null ||
                   node["draft"]?.GetValue<bool>() == true)
                    continue;

                var tag=
                    node["tag_name"]?.ToString()
                    ?? "";

                if(tag.Length==0 ||
                   ParseVersion(tag)==null ||
                   !seen.Add(tag))
                    continue;

                result.Add(tag);

                if(result.Count>=limit)
                    break;
            }

            if(array.Count<100)
                break;
        }

        return result;
    }

    private async Task<JsonNode> ExactReleaseNodeAsync(
        string repository,
        string requestedVersion,
        CancellationToken ct)
    {
        using var clientLease = clients.Acquire();
        var client = clientLease.Client;
        if(string.IsNullOrWhiteSpace(requestedVersion) ||
           requestedVersion.Length>100 ||
           requestedVersion.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException(
                "Некорректная версия компонента.");
        }

        var candidates=
            requestedVersion.StartsWith(
                "v",
                StringComparison.OrdinalIgnoreCase)
            ? new[]{requestedVersion}
            : new[]{requestedVersion,"v"+requestedVersion};

        foreach(var tag in candidates)
        {
            using var request=
                new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://api.github.com/repos/{repository}/releases/tags/{Uri.EscapeDataString(tag)}");

            request.Headers.UserAgent.ParseAdd(
                "NetCat/1.0.0");

            using var response=
                await client.SendAsync(
                    request,
                    ct);

            if(response.StatusCode ==
               System.Net.HttpStatusCode.NotFound)
                continue;

            response.EnsureSuccessStatusCode();

            var node=
                JsonNode.Parse(
                    await response.Content
                        .ReadAsStringAsync(ct))
                ?? throw new InvalidDataException(
                    "GitHub вернул пустой release.");

            if(node["draft"]?.GetValue<bool>() == true)
                throw new InvalidDataException(
                    "Черновой release нельзя устанавливать.");

            return node;
        }

        throw new InvalidOperationException(
            $"Версия {requestedVersion} не найдена в официальном upstream.");
    }

    public async Task<ModuleRelease> CheckVersionAsync(
        string key,
        string version,
        CancellationToken ct)
    {
        using var clientLease = clients.Acquire();
        var client = clientLease.Client;
        if(!SupportsVersionSelection(key))
            throw new InvalidOperationException(
                "Для этого компонента выбор версии пока не поддерживается.");

        var source=Sources[key];

        var root=
            await ExactReleaseNodeAsync(
                source.Repo,
                version,
                ct);

        var tag=
            root["tag_name"]?.ToString()
            ?? throw new InvalidDataException(
                "У release отсутствует tag.");

        if(ParseVersion(tag)==null)
            throw new InvalidDataException(
                "Некорректная версия upstream release.");

        if(key=="tg-ws-proxy")
        {
            using var treeRequest=
                new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://api.github.com/repos/{source.Repo}/git/trees/{Uri.EscapeDataString(tag)}?recursive=1");

            treeRequest.Headers.UserAgent.ParseAdd(
                "NetCat/1.0.0");

            using var treeResponse=
                await client.SendAsync(
                    treeRequest,
                    ct);

            treeResponse.EnsureSuccessStatusCode();

            var tree=
                JsonNode.Parse(
                    await treeResponse.Content
                        .ReadAsStringAsync(ct))
                ?? throw new InvalidDataException(
                    "GitHub не вернул Git tree.");

            var treeSha=
                tree["sha"]?.ToString()
                ?? "";

            if(!Regex.IsMatch(
                    treeSha,
                    "^[a-fA-F0-9]{40}$"))
            {
                throw new InvalidDataException(
                    "Некорректный Git tree SHA.");
            }

            var release=
                new ModuleRelease(
                    key,
                    source.Repo,
                    tag,
                    "headless source",
                    $"https://github.com/{source.Repo}/tree/{tag}",
                    "",
                    treeSha.ToLowerInvariant());

            if(!IsTrustedDirectUpstreamRelease(release))
                throw new InvalidDataException(
                    "Версия Telegram не прошла проверку upstream.");

            return release;
        }

        var matches=
            root["assets"]!
                .AsArray()
                .Where(
                    asset =>
                        asset != null &&
                        Regex.IsMatch(
                            asset["name"]!.ToString(),
                            source.Pattern))
                .ToArray();

        if(matches.Length!=1)
            throw new InvalidDataException(
                "Для выбранной версии не найден однозначный ZIP asset.");

        var asset=matches[0]!;

        var digest=
            asset["digest"]?.ToString()
            ?? "";

        if(!Regex.IsMatch(
                digest,
                "^sha256:[a-fA-F0-9]{64}$"))
        {
            throw new InvalidDataException(
                "Эта историческая версия не имеет проверяемого SHA-256 в GitHub release metadata.");
        }

        var result=
            new ModuleRelease(
                key,
                source.Repo,
                tag,
                asset["name"]!.ToString(),
                asset["browser_download_url"]!.ToString(),
                digest[7..].ToLowerInvariant());

        if(key is "sing-box" or "xray")
        {
            if(!IsTrustedCoreUpstreamRelease(result))
                throw new InvalidDataException(
                    "VPN-core не прошёл проверку официального upstream.");

            return await AuthorizeComponentAsync(
                result,
                ct);
        }

        if(!IsTrustedDirectUpstreamRelease(result))
            throw new InvalidDataException(
                "Версия Zapret не прошла проверку официального upstream.");

        return result;
    }

    public async Task<ModuleRelease> PrepareSpecificVersionAsync(
        string key,
        string version,
        AppSettings settings,
        CancellationToken ct,
        bool forceUnreviewed = false)
    {
        if(!SupportsVersionSelection(key))
            throw new InvalidOperationException(
                "Для этого компонента выбор версии не поддерживается.");

        var release =
            await CheckVersionAsync(
                key,
                version,
                ct);

        return await PrepareSpecificReleaseAsync(
            release,
            settings,
            ct,
            forceUnreviewed);
    }

    public async Task<ModuleRelease> PrepareSpecificReleaseAsync(
        ModuleRelease release,
        AppSettings settings,
        CancellationToken ct,
        bool forceUnreviewed = false)
    {
        if(!SupportsVersionSelection(release.Key))
            throw new InvalidOperationException(
                "Для этого компонента выбор версии не поддерживается.");

        var core =
            release.Key is "sing-box" or "xray";

        var needsForce =
            core &&
            !HasComponentAuthorization(release);

        if(!core &&
           forceUnreviewed)
        {
            throw new InvalidOperationException(
                "Принудительный режим предназначен только для sing-box/Xray.");
        }

        if(needsForce &&
           !forceUnreviewed)
        {
            throw new InvalidOperationException(
                "Эта версия VPN-ядра ещё не подтверждена NetCat. Для неё требуется явная принудительная установка.");
        }

        await InstallAsync(
            release,
            settings,
            ct,
            prepareOnly:true,
            selectedVersion:true,
            forceUnreviewed:
                needsForce &&
                forceUnreviewed);

        if(!PreparedIsSelectedVersion(release))
            throw new InvalidDataException(
                "Подготовленное обновление потеряло признак явного выбора версии.");

        return release;
    }
    public bool CanRollback(string key)
    {
        if(!Sources.ContainsKey(key) ||
           key=="openvpn")
            return false;

        return Directory.Exists(
            Path.Combine(bin,key)+".previous");
    }

    public string PreviousVersion(string key)
    {
        if(!CanRollback(key))
            return "";

        var folder=
            Path.Combine(bin,key)+".previous";

        try
        {
            if(key is "sing-box" or "xray")
            {
                var component=
                    ComponentTrust.Read(
                        folder,
                        key);

                if(component != null)
                    return component.Version;
            }

            var source=
                Path.Combine(
                    folder,
                    "netcat-source.json");

            if(File.Exists(source))
            {
                var node=
                    JsonNode.Parse(
                        File.ReadAllText(source));

                var value=
                    node?["Version"]?.ToString();

                if(!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch
        {
        }

        return "встроенная";
    }
}
