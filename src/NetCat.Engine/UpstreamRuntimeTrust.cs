using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NetCat.Core;

namespace NetCat.Engine;

/// <summary>
/// Trust receipt created only after NetCat has independently verified an
/// official upstream package/tree. The receipt records the exact files that
/// may execute. It is not publisher approval and does not make arbitrary
/// upstream content trusted.
/// </summary>
public static class UpstreamRuntimeTrust
{
    public const string ReceiptName =
        ".netcat-upstream-trust.json";

    private sealed record Receipt(
        int Schema,
        string Module,
        string Repository,
        string Version,
        string SourceDigest,
        Dictionary<string,string> Files);

    private static readonly IReadOnlyDictionary<string,string>
        OfficialRepositories =
            new Dictionary<string,string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["zapret"] =
                    "Flowseal/zapret-discord-youtube",

                ["tg-ws-proxy"] =
                    "Flowseal/tg-ws-proxy"
            };

    public static bool Supports(string module) =>
        OfficialRepositories.ContainsKey(module);

    public static bool Exists(
        string folder,
        string module)
    {
        if (!Supports(module))
            return false;

        return File.Exists(
            Path.Combine(folder,ReceiptName));
    }

    private static bool ReceiptFile(
        string relative) =>
        relative.Equals(
            ReceiptName,
            StringComparison.OrdinalIgnoreCase) ||
        relative.Equals(
            "netcat-source.json",
            StringComparison.OrdinalIgnoreCase) ||
        relative.Equals(
            "prepared.json",
            StringComparison.OrdinalIgnoreCase);

    private static bool Covered(
        string module,
        string relative)
    {
        if (ReceiptFile(relative))
            return false;

        if (module == "zapret")
        {
            return
                Path.GetExtension(relative)
                    .ToLowerInvariant()
                is ".exe" or
                   ".dll" or
                   ".sys" or
                   ".bat" or
                   ".cmd" or
                   ".ps1";
        }

        // tg-ws-proxy is Python source. Cover every downloaded file,
        // including LICENSE and package markers, rather than only one .py.
        return module == "tg-ws-proxy";
    }

    private static IEnumerable<string> CoveredFiles(
        string folder,
        string module)
    {
        foreach (var path in
                 ModuleIntegrity.SafeFiles(folder))
        {
            var relative =
                Path.GetRelativePath(folder,path)
                    .Replace('\\','/');

            if (Covered(module,relative))
                yield return path;
        }
    }

    private static void ValidateIdentity(
        string module,
        string repository,
        string version,
        string sourceDigest)
    {
        if (!OfficialRepositories.TryGetValue(
                module,
                out var expectedRepository) ||
            !string.Equals(
                repository,
                expectedRepository,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Runtime не относится к разрешённому " +
                "официальному upstream.");
        }

        if (string.IsNullOrWhiteSpace(version) ||
            version.Length > 100)
        {
            throw new InvalidDataException(
                "Некорректная версия upstream runtime.");
        }

        var digestOk =
            sourceDigest.Length is 40 or 64 &&
            sourceDigest.All(Uri.IsHexDigit);

        if (!digestOk)
        {
            throw new InvalidDataException(
                "Некорректный идентификатор upstream source.");
        }

        if (module == "zapret" &&
            sourceDigest.Length != 64)
        {
            throw new InvalidDataException(
                "Zapret требует SHA-256 release asset.");
        }

        if (module == "tg-ws-proxy" &&
            sourceDigest.Length != 40)
        {
            throw new InvalidDataException(
                "Telegram proxy требует Git tree SHA.");
        }
    }

    public static void Write(
        string folder,
        string module,
        string repository,
        string version,
        string sourceDigest)
    {
        ValidateIdentity(
            module,
            repository,
            version,
            sourceDigest);

        ModuleIntegrity.CheckPath(folder);

        var files =
            new Dictionary<string,string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var path in
                 CoveredFiles(folder,module))
        {
            var relative =
                Path.GetRelativePath(folder,path)
                    .Replace('\\','/');

            using var stream =
                File.OpenRead(path);

            files[relative] =
                Convert.ToHexString(
                    SHA256.HashData(stream));
        }

        if (files.Count == 0)
        {
            throw new InvalidDataException(
                "В upstream runtime нет контролируемых файлов.");
        }

        var receipt =
            new Receipt(
                1,
                module,
                repository,
                version,
                sourceDigest.ToUpperInvariant(),
                files);

        var receiptPath =
            Path.Combine(folder,ReceiptName);

        RuntimeTrustReceipt.Write(
            receiptPath,
            JsonSerializer.Serialize(
                receipt,
                JsonSettings.Options));
    }

    private static Receipt Read(
        string folder,
        string module)
    {
        ModuleIntegrity.CheckPath(folder);

        var path =
            Path.Combine(
                folder,
                ReceiptName);

        var receipt =
            JsonSerializer.Deserialize<Receipt>(
                RuntimeTrustReceipt.Read(path),
                JsonSettings.Options)
            ?? throw new InvalidDataException(
                "Повреждён upstream trust receipt.");

        if (receipt.Schema != 1 ||
            !string.Equals(
                receipt.Module,
                module,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Upstream trust receipt относится " +
                "к другому runtime.");
        }

        ValidateIdentity(
            receipt.Module,
            receipt.Repository,
            receipt.Version,
            receipt.SourceDigest);

        if (receipt.Files.Count == 0)
        {
            throw new InvalidDataException(
                "Пустой upstream trust receipt.");
        }

        return receipt;
    }

    public static IDisposable Acquire(
        string module,
        string folder)
    {
        if (!Supports(module))
        {
            throw new InvalidDataException(
                "Этот runtime не поддерживает " +
                "upstream trust.");
        }

        var receipt =
            Read(folder,module);

        var handles =
            new List<IDisposable>();

        try
        {
            using var identity =
                WindowsIdentity.GetCurrent();

            var elevated =
                new WindowsPrincipal(identity)
                    .IsInRole(
                        WindowsBuiltInRole.Administrator);

            void LockDirectory(
                string directory)
            {
                ModuleIntegrity.CheckPath(directory);

                var handle =
                    OpenDirectory(
                        directory,
                        0,
                        3,
                        0,
                        3,
                        0x02000000,
                        0);

                if (handle.IsInvalid)
                {
                    handle.Dispose();

                    throw new
                        System.ComponentModel.Win32Exception();
                }

                handles.Add(handle);

                // Live NetCat normally runs elevated. Protect the installed
                // runtime from ordinary-user replacement after verification.
                if (elevated)
                {
                    PrivateFiles.ProtectDirectory(
                        directory,
                        administratorsOnly:true);
                }

                foreach (var child in
                         Directory.EnumerateDirectories(
                             directory))
                {
                    LockDirectory(child);
                }
            }

            LockDirectory(folder);

            var actual =
                CoveredFiles(folder,module)
                    .Select(
                        path =>
                            Path.GetRelativePath(
                                    folder,
                                    path)
                                .Replace('\\','/'))
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            if (!actual.SetEquals(
                    receipt.Files.Keys))
            {
                throw new InvalidDataException(
                    "Состав upstream runtime изменён: " +
                    module);
            }

            foreach (var pair in
                     receipt.Files)
            {
                var path =
                    Path.Combine(
                        folder,
                        pair.Key.Replace(
                            '/',
                            Path.DirectorySeparatorChar));

                ModuleIntegrity.CheckPath(path);

                var file =
                    new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);

                handles.Add(file);

                var actualHash =
                    Convert.ToHexString(
                        SHA256.HashData(file));

                if (!actualHash.Equals(
                        pair.Value,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Нарушена целостность upstream runtime: " +
                        pair.Key);
                }
            }

            return new Lease(handles);
        }
        catch
        {
            foreach (var handle in handles)
                handle.Dispose();

            throw;
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint="CreateFileW",
        CharSet=CharSet.Unicode,
        SetLastError=true)]
    private static extern SafeFileHandle OpenDirectory(
        string path,
        uint access,
        uint share,
        nint security,
        uint disposition,
        uint flags,
        nint template);

    private sealed class Lease(
        List<IDisposable> handles)
        : IDisposable
    {
        public void Dispose()
        {
            foreach (var handle in handles)
                handle.Dispose();
        }
    }
}
