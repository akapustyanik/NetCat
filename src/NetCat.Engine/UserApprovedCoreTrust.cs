using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using NetCat.Core;

namespace NetCat.Engine;

public static class UserApprovedCoreTrust
{
    public const string ReceiptName =
        ".netcat-user-approved-core.json";

    private sealed record Receipt(
        int Schema,
        string Module,
        string Repository,
        string Version,
        string ArchiveSha256,
        Dictionary<string,string> Files);

    private static readonly IReadOnlyDictionary<string,string>
        OfficialRepositories =
            new Dictionary<string,string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["sing-box"] =
                    "SagerNet/sing-box",

                ["xray"] =
                    "XTLS/Xray-core"
            };

    public static bool Supports(string module) =>
        OfficialRepositories.ContainsKey(module);

    public static bool Exists(
        string folder,
        string module)
    {
        if(!Supports(module))
            return false;

        return File.Exists(
            Path.Combine(
                folder,
                ReceiptName));
    }

    private static bool ControlFile(
        string relative)
    {
        return
            relative.Equals(
                ReceiptName,
                StringComparison.OrdinalIgnoreCase) ||

            relative.Equals(
                "netcat-source.json",
                StringComparison.OrdinalIgnoreCase) ||

            relative.Equals(
                "prepared.json",
                StringComparison.OrdinalIgnoreCase) ||

            relative.Equals(
                ComponentTrust.ManifestName,
                StringComparison.OrdinalIgnoreCase) ||

            relative.Equals(
                ComponentTrust.SignatureName,
                StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CoveredFiles(
        string folder)
    {
        foreach(var path in
                ModuleIntegrity.SafeFiles(folder))
        {
            var relative =
                Path.GetRelativePath(
                        folder,
                        path)
                    .Replace('\\','/');

            if(!ControlFile(relative))
                yield return path;
        }
    }

    private static void ValidateIdentity(
        string module,
        string repository,
        string version,
        string archiveSha256)
    {
        if(!OfficialRepositories.TryGetValue(
                module,
                out var expectedRepository) ||
           !string.Equals(
                repository,
                expectedRepository,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "VPN-core не относится к разрешённому официальному upstream.");
        }

        if(!Regex.IsMatch(
                version ?? "",
                @"^v?\d+(?:\.\d+){1,3}(?:-[A-Za-z0-9._-]+)?$"))
        {
            throw new InvalidDataException(
                "Некорректная версия VPN-core.");
        }

        if(!Regex.IsMatch(
                archiveSha256 ?? "",
                "^[a-fA-F0-9]{64}$"))
        {
            throw new InvalidDataException(
                "Для принудительной установки VPN-core требуется SHA-256 официального release asset.");
        }
    }

    private static string RequiredExecutable(
        string module) =>
        module switch
        {
            "sing-box" => "sing-box.exe",
            "xray" => "xray.exe",

            _ => throw new InvalidDataException(
                "Неизвестный VPN-core.")
        };

    public static void Write(
        string folder,
        string module,
        string repository,
        string version,
        string archiveSha256)
    {
        ValidateIdentity(
            module,
            repository,
            version,
            archiveSha256);

        ModuleIntegrity.CheckPath(folder);

        var executable =
            Path.Combine(
                folder,
                RequiredExecutable(module));

        if(!File.Exists(executable))
        {
            throw new InvalidDataException(
                "В VPN-core отсутствует основной исполняемый файл.");
        }

        var files =
            new Dictionary<string,string>(
                StringComparer.OrdinalIgnoreCase);

        foreach(var file in
                CoveredFiles(folder))
        {
            var relative =
                Path.GetRelativePath(
                        folder,
                        file)
                    .Replace('\\','/');

            using var stream =
                File.OpenRead(file);

            files[relative] =
                Convert.ToHexString(
                    SHA256.HashData(stream));
        }

        if(files.Count==0)
        {
            throw new InvalidDataException(
                "VPN-core не содержит контролируемых файлов.");
        }

        var executableRelative =
            RequiredExecutable(module)
                .Replace('\\','/');

        if(!files.ContainsKey(
                executableRelative))
        {
            throw new InvalidDataException(
                "Основной EXE не вошёл в trust inventory.");
        }

        var receipt =
            new Receipt(
                1,
                module,
                repository,
                version,
                archiveSha256.ToUpperInvariant(),
                files);

        var receiptPath =
            Path.Combine(
                folder,
                ReceiptName);

        RuntimeTrustReceipt.Write(
            receiptPath,
            JsonSerializer.Serialize(
                receipt,
                JsonSettings.Options));
    }

    private static Receipt ReadRequired(
        string folder,
        string module)
    {
        if(!Supports(module))
        {
            throw new InvalidDataException(
                "Этот модуль не поддерживает user-approved trust.");
        }

        ModuleIntegrity.CheckPath(folder);

        var receiptPath =
            Path.Combine(
                folder,
                ReceiptName);

        var receipt =
            JsonSerializer.Deserialize<Receipt>(
                RuntimeTrustReceipt.Read(receiptPath),
                JsonSettings.Options)
            ?? throw new InvalidDataException(
                "Повреждён user-approved trust receipt.");

        if(receipt.Schema!=1 ||
           !string.Equals(
                receipt.Module,
                module,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Trust receipt относится к другому VPN-core.");
        }

        ValidateIdentity(
            receipt.Module,
            receipt.Repository,
            receipt.Version,
            receipt.ArchiveSha256);

        if(receipt.Files.Count==0)
        {
            throw new InvalidDataException(
                "Пустой user-approved trust inventory.");
        }

        return receipt;
    }

    public static IDisposable Acquire(
        string module,
        string folder)
    {
        var receipt =
            ReadRequired(
                folder,
                module);

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

                if(handle.IsInvalid)
                {
                    handle.Dispose();

                    throw new
                        System.ComponentModel.Win32Exception();
                }

                handles.Add(handle);

                if(elevated)
                {
                    PrivateFiles.ProtectDirectory(
                        directory,
                        administratorsOnly:true);
                }

                foreach(var child in
                        Directory.EnumerateDirectories(
                            directory))
                {
                    LockDirectory(child);
                }
            }

            LockDirectory(folder);

            var actual =
                CoveredFiles(folder)
                    .Select(
                        file =>
                            Path.GetRelativePath(
                                    folder,
                                    file)
                                .Replace('\\','/'))
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            if(!actual.SetEquals(
                    receipt.Files.Keys))
            {
                throw new InvalidDataException(
                    "Состав user-approved VPN-core изменён: " +
                    module);
            }

            foreach(var item in
                    receipt.Files)
            {
                var filePath =
                    Path.Combine(
                        folder,
                        item.Key.Replace(
                            '/',
                            Path.DirectorySeparatorChar));

                ModuleIntegrity.CheckPath(filePath);

                var stream =
                    new FileStream(
                        filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);

                handles.Add(stream);

                var actualHash =
                    Convert.ToHexString(
                        SHA256.HashData(stream));

                if(!actualHash.Equals(
                        item.Value,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Нарушена целостность user-approved VPN-core: " +
                        item.Key);
                }
            }

            return new Lease(handles);
        }
        catch
        {
            foreach(var handle in handles)
                handle.Dispose();

            throw;
        }
    }

    public static void RefreshAfterVerifiedDependencyUpdate(
        string module,
        string folder)
    {
        if(!Exists(folder,module))
            return;

        var receipt =
            ReadRequired(
                folder,
                module);

        Write(
            folder,
            receipt.Module,
            receipt.Repository,
            receipt.Version,
            receipt.ArchiveSha256);
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
            foreach(var handle in handles)
                handle.Dispose();
        }
    }
}
