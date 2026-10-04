using System.Security.Cryptography;
using NetCat.Engine;

namespace NetCat.Updater;

/// <summary>
/// One-shot trust policy for a freshly extracted updater staging directory.
///
/// Provenance still comes from the upstream archive digest already verified
/// by ModuleUpdater. This object only freezes and re-verifies the native
/// staging inventory while a compatibility probe is launched.
///
/// It must never be used as persistent installed-module authorization.
/// </summary>
public sealed class StagedExecutableTrustPolicy : IExecutableTrustPolicy
{
    private readonly string root;
    private readonly string executable;
    private readonly IReadOnlyDictionary<string,string> hashes;

    private StagedExecutableTrustPolicy(
        string root,
        string executable,
        IReadOnlyDictionary<string,string> hashes)
    {
        this.root = root;
        this.executable = executable;
        this.hashes = hashes;
    }

    public static StagedExecutableTrustPolicy Capture(
        string root,
        string executable)
    {
        root =
            Path.GetFullPath(root);

        executable =
            Path.GetFullPath(executable);

        ModuleIntegrity.CheckPath(root);
        ModuleIntegrity.CheckPath(executable);

        if(!Directory.Exists(root))
            throw new DirectoryNotFoundException(
                "Staging runtime directory not found.");

        if(!File.Exists(executable))
            throw new FileNotFoundException(
                "Staging executable not found.",
                executable);

        if(!IsInside(root, executable))
            throw new InvalidDataException(
                "Staging executable is outside the verified payload.");

        var hashes =
            ModuleIntegrity.SafeFiles(root)
                .Where(IsNativeCode)
                .ToDictionary(
                    path => Relative(root,path),
                    HashFile,
                    StringComparer.OrdinalIgnoreCase);

        var executableRelative =
            Relative(root,executable);

        if(!hashes.ContainsKey(executableRelative))
            throw new InvalidDataException(
                "Staging executable is not part of the native inventory.");

        return new StagedExecutableTrustPolicy(
            root,
            executable,
            hashes);
    }

    public IDisposable? AcquireExecutable(
        string path)
    {
        var full =
            Path.GetFullPath(path);

        if(!full.Equals(
            executable,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Staging trust is bound to one executable.");
        }

        return AcquireLease();
    }

    public IDisposable AcquirePackage(
        string key,
        string folder)
    {
        var full =
            Path.GetFullPath(folder);

        if(!full.Equals(
            root,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Staging trust is bound to one payload directory.");
        }

        return AcquireLease();
    }

    private IDisposable AcquireLease()
    {
        ModuleIntegrity.CheckPath(root);

        var actual =
            ModuleIntegrity.SafeFiles(root)
                .Where(IsNativeCode)
                .Select(path => Relative(root,path))
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);

        if(!actual.SetEquals(hashes.Keys))
        {
            throw new InvalidDataException(
                "Native staging inventory changed after verification.");
        }

        var handles =
            new List<FileStream>();

        try
        {
            foreach(var item in
                    hashes.OrderBy(
                        item => item.Key,
                        StringComparer.OrdinalIgnoreCase))
            {
                var file =
                    Path.Combine(
                        root,
                        item.Key.Replace(
                            '/',
                            Path.DirectorySeparatorChar));

                ModuleIntegrity.CheckPath(file);

                var handle =
                    new FileStream(
                        file,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);

                handles.Add(handle);

                var actualHash =
                    Convert.ToHexString(
                        SHA256.HashData(handle));

                if(!actualHash.Equals(
                    item.Value,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Native staging file changed after verification: " +
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

    private static bool IsNativeCode(
        string path) =>
        Path.GetExtension(path)
            .ToLowerInvariant()
            is ".exe" or ".dll" or ".sys";

    private static string Relative(
        string root,
        string path) =>
        Path.GetRelativePath(
                root,
                path)
            .Replace('\\','/');

    private static bool IsInside(
        string root,
        string path)
    {
        var relative =
            Path.GetRelativePath(
                root,
                path);

        return
            !Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith(
                ".." +
                Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
    }

    private static string HashFile(
        string path)
    {
        using var stream =
            File.OpenRead(path);

        return Convert.ToHexString(
            SHA256.HashData(stream));
    }

    private sealed class Lease(
        List<FileStream> handles)
        : IDisposable
    {
        public void Dispose()
        {
            foreach(var handle in handles)
                handle.Dispose();
        }
    }
}