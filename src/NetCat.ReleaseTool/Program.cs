using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Updater;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

// This tool is never packaged with the application. Secret input is a DPAPI file
// outside the repository, or NETCAT_RELEASE_KEY_BASE64 supplied by CI.
var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var keyFile = Environment.GetEnvironmentVariable("NETCAT_RELEASE_KEY_FILE") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netcat-release", "release-key.dpapi");
if (Path.GetFullPath(keyFile).StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Private release key must be outside the repository.");
if (args.Length == 1 && args[0] == "init")
{
    if (File.Exists(keyFile)) throw new IOException("Release key already exists; never silently rotate it.");
    PrivateFiles.ProtectDirectory(Path.GetDirectoryName(keyFile)!);
    var seed = RandomNumberGenerator.GetBytes(32);
    try
    {
        var key = new Ed25519PrivateKeyParameters(seed, 0);
        File.WriteAllBytes(keyFile, ProtectedData.Protect(seed, null, DataProtectionScope.CurrentUser));
        File.WriteAllText(Path.Combine(repo, "src/NetCat.Updater/ReleasePublicKey.hex"), Convert.ToHexString(key.GeneratePublicKey().GetEncoded()) + "\n");
        Console.WriteLine("Public key saved; private key stored outside repository with CurrentUser DPAPI.");
    }
    finally { CryptographicOperations.ZeroMemory(seed); }
    return;
}
if(args.Length==5&&args[0]=="component")
{
    var component=args[1];var version=args[2];var input=Path.GetFullPath(args[3]);var output=Path.GetFullPath(args[4]);
    _=ModuleUpdater.ComponentReleaseBase(component,version);
    if(output.StartsWith(input+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Output must be outside component input.");
    var provider=component=="sing-box"?"SagerNet/sing-box":"XTLS/Xray-core";
    var files=ModuleIntegrity.SafeFiles(input).ToDictionary(p=>Path.GetRelativePath(input,p).Replace('\\','/'),p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    var componentSecret=Environment.GetEnvironmentVariable("NETCAT_RELEASE_KEY_BASE64");
    var seed=componentSecret!=null?Convert.FromBase64String(componentSecret):ProtectedData.Unprotect(File.ReadAllBytes(keyFile),null,DataProtectionScope.CurrentUser);
    try
    {
        if(seed.Length!=32)throw new InvalidDataException("Ed25519 seed must be 32 bytes.");
        var key=new Ed25519PrivateKeyParameters(seed,0);
        if(!CryptographicOperations.FixedTimeEquals(key.GeneratePublicKey().GetEncoded(),ComponentTrust.PublicKey))throw new InvalidDataException("Component signing key does not match application.");
        byte[] Sign(byte[] data){var signer=new Ed25519Signer();signer.Init(true,key);signer.BlockUpdate(data,0,data.Length);return signer.GenerateSignature();}
        var name=$"{component}-{version}-win-x64.zip";
        var draft=new ComponentManifest(1,component,version,provider,name,new string('0',64),1,"win-x64",files);
        var draftBytes=JsonSerializer.SerializeToUtf8Bytes(draft);
        _=ComponentTrust.Verify(draftBytes,Sign(draftBytes),component);
        // Never overwrite a published or previously reviewed package.
        if(Directory.Exists(output))throw new IOException("Component output must be a new directory.");
        Directory.CreateDirectory(output);var package=Path.Combine(output,name);
        ZipFile.CreateFromDirectory(input,package,CompressionLevel.Optimal,false);
        ComponentTrust.VerifyFiles(input,draft);
        var signedComponent=draft with {ArchiveSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package)))};
        var data=JsonSerializer.SerializeToUtf8Bytes(signedComponent);
        await File.WriteAllBytesAsync(Path.Combine(output,ComponentTrust.ManifestName),data);
        await File.WriteAllBytesAsync(Path.Combine(output,ComponentTrust.SignatureName),Sign(data));
        Console.WriteLine("Signed component package created. Run compatibility/VM acceptance before publishing. Nothing published.");
    }
    finally{CryptographicOperations.ZeroMemory(seed);}
    return;
}
if (args.Length != 2) throw new ArgumentException("Usage: init | <clean package folder> <zip path> | component <key> <version> <clean component folder> <new output directory>");
var folder = Path.GetFullPath(args[0]); var archive = Path.GetFullPath(args[1]);
var manifest = await PortableUpdate.VerifyAsync(folder, CancellationToken.None);
ReleaseTrust.ValidateBuildVersion(manifest.Version, System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(folder, "NetCat.exe")).ProductVersion);
if (Path.GetFileName(archive) != $"NetCat-v{manifest.Version}-win-x64.zip") throw new InvalidDataException("Incorrect release filename.");
var owned = manifest.Components.SelectMany(c => c.Files).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
var extras = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
    .Select(p => (Full: p, Relative: Path.GetRelativePath(folder, p).Replace('\\', '/')))
    .Where(p => !owned.Contains(p.Relative)).OrderBy(p => p.Relative, StringComparer.Ordinal)
    .Select(p => { using var file = File.OpenRead(p.Full); return new PackageFile(p.Relative, Convert.ToHexString(SHA256.HashData(file))); }).ToList();
using var zip = File.OpenRead(archive);
var signed = manifest with { Channel = manifest.Version.Contains('-') ? "beta" : "stable", Package = new(Path.GetFileName(archive), Convert.ToHexString(SHA256.HashData(zip))), ExtraFiles = extras };
var bytes = JsonSerializer.SerializeToUtf8Bytes(signed, JsonSettings.Options);
var secret = Environment.GetEnvironmentVariable("NETCAT_RELEASE_KEY_BASE64");
var raw = secret != null ? Convert.FromBase64String(secret) : ProtectedData.Unprotect(File.ReadAllBytes(keyFile), null, DataProtectionScope.CurrentUser);
try
{
    if (raw.Length != 32) throw new InvalidDataException("Ed25519 seed must be 32 bytes.");
    var key = new Ed25519PrivateKeyParameters(raw, 0);
    if (!CryptographicOperations.FixedTimeEquals(key.GeneratePublicKey().GetEncoded(), ReleaseTrust.PublicKey)) throw new InvalidDataException("Private key does not match embedded public key.");
    var signer = new Ed25519Signer(); signer.Init(true, key); signer.BlockUpdate(bytes, 0, bytes.Length);
    var output = Path.GetDirectoryName(archive)!;
    // No overwrites: an existing signed manifest may describe another published release.
    using var m = new FileStream(Path.Combine(output, "release-manifest.json"), FileMode.CreateNew); m.Write(bytes); m.Flush(true);
    using var sig = new FileStream(Path.Combine(output, "release-manifest.sig"), FileMode.CreateNew); sig.Write(signer.GenerateSignature()); sig.Flush(true);
    Console.WriteLine("Detached Ed25519 signature created. Nothing published.");
}
finally { CryptographicOperations.ZeroMemory(raw); }
