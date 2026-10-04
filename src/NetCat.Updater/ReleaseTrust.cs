using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using NetCat.Core;

namespace NetCat.Updater;

public sealed record ReleasePackage(string Name, string Sha256);
public static class ReleaseTrust
{
    public static string CurrentVersion => typeof(ReleaseTrust).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public static string Channel => CurrentVersion.Contains('-') ? "beta" : "stable";
    public static void ValidateBuildVersion(string packageVersion, string? executableVersion)
    {
        if (packageVersion != CurrentVersion || executableVersion?.Split('+')[0] != packageVersion)
            throw new InvalidDataException("Версии инструмента подписи, манифеста и EXE должны совпадать.");
    }
    public static byte[] PublicKey
    {
        get
        {
            using var stream = typeof(ReleaseTrust).Assembly.GetManifestResourceStream("NetCat.ReleasePublicKey")!;
            using var reader = new StreamReader(stream); return Convert.FromHexString(reader.ReadToEnd().Trim());
        }
    }
    public static PackageManifest Verify(byte[] bytes, byte[] signature, byte[] key, string currentVersion, string channel, string expectedAsset, string expectedVersion)
    {
        if (bytes.Length > 8 * 1024 * 1024 || signature.Length != 64 || key.Length != 32) throw new InvalidDataException("Некорректная подпись манифеста.");
        var verifier = new Ed25519Signer(); verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
        verifier.BlockUpdate(bytes, 0, bytes.Length);
        if (!verifier.VerifySignature(signature)) throw new InvalidDataException("Подпись Ed25519 обновления не прошла проверку.");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(bytes, new JsonSerializerOptions(JsonSettings.Options) { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Нет манифеста.");
        if (manifest.Schema != 1 || manifest.Package == null || manifest.ExtraFiles == null || manifest.Components == null ||
            !Regex.IsMatch(manifest.Version, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?$") ||
            manifest.Version != expectedVersion.TrimStart('v') || manifest.Channel is not ("beta" or "stable") ||
            manifest.Channel != (manifest.Version.Contains('-') ? "beta" : "stable") ||
            channel == "stable" && manifest.Channel != "stable" || channel is not ("stable" or "beta") ||
            !ModuleUpdater.IsNewer(manifest.Version, currentVersion) ||
            manifest.Package.Name != expectedAsset || expectedAsset != $"NetCat-v{manifest.Version}-win-x64.zip" ||
            !Regex.IsMatch(manifest.Package.Sha256, "^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("Версия, канал или имя подписанного пакета недопустимы (повтор/понижение запрещены).");
        return manifest;
    }
    public static async Task<byte[]> DownloadAsync(HttpClient client, string url, int limit, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[16384];
        int count; while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + count > limit) throw new InvalidDataException("Слишком большой release asset."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    public static async Task<(PackageManifest Manifest, byte[] Bytes, byte[] Signature)> FetchAsync(HttpClient client, string packageUrl, string version, string current, CancellationToken ct)
    {
        const string prefix = "https://github.com/akapustyanik/NetCat/releases/download/";
        if (!packageUrl.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Неверный источник обновления.");
        var baseUrl = packageUrl[..(packageUrl.LastIndexOf('/') + 1)];
        var bytes = await DownloadAsync(client, baseUrl + "release-manifest.json", 8 * 1024 * 1024, ct);
        var sig = await DownloadAsync(client, baseUrl + "release-manifest.sig", 64, ct);
        return (Verify(bytes, sig, PublicKey, current, Channel, Uri.UnescapeDataString(new Uri(packageUrl).Segments.Last()), version), bytes, sig);
    }
    public static async Task VerifyPayloadAsync(PackageManifest signed, string payload, CancellationToken ct)
    {
        var internalManifest = await PortableUpdate.VerifyAsync(payload, ct);
        if (internalManifest.Version != signed.Version || JsonSerializer.Serialize(internalManifest.Components) != JsonSerializer.Serialize(signed.Components))
            throw new InvalidDataException("Внутренний манифест не соответствует подписанному.");
        var files = signed.Components.SelectMany(c => c.Files).Concat(signed.ExtraFiles ?? []).ToArray();
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files)
        {
            if (!expected.Add(item.Path)) throw new InvalidDataException("Повторяющийся файл в подписанном манифесте.");
            var path = PortableUpdate.SafePath(payload, item.Path);
            await using var file = File.OpenRead(path);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Повреждён подписанный файл: " + item.Path);
        }
        var actual = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(payload, p).Replace('\\', '/'));
        if (!expected.SetEquals(actual)) throw new InvalidDataException("В пакете присутствуют неожиданные файлы.");
    }
    public static async Task<PackageManifest> VerifyStageAsync(UpdateJob job, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(job.Stage, "release-manifest.json"), ct);
        var signature = await File.ReadAllBytesAsync(Path.Combine(job.Stage, "release-manifest.sig"), ct);
        var manifest = Verify(bytes, signature, PublicKey, CurrentVersion, Channel, job.Asset, job.Version);
        if (!manifest.Package!.Sha256.Equals(job.ArchiveHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Задание не соответствует подписанному архиву.");
        return manifest;
    }
    public static void RememberVersion(string root, PackageManifest manifest)
    {
        var directory = PortableUpdate.SafePath(root, "metadata/release-history"); Directory.CreateDirectory(directory);
        var path = PortableUpdate.SafePath(root, "metadata/release-history/" + manifest.Version + ".sha256");
        var hash = manifest.Package!.Sha256.ToUpperInvariant();
        if (File.Exists(path))
        {
            if (File.ReadAllText(path).Trim() != hash) throw new InvalidDataException("Эта версия уже была подписана с другим содержимым.");
            return;
        }
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = System.Text.Encoding.ASCII.GetBytes(hash); file.Write(bytes); file.Flush(true);
    }
}
