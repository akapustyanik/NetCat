using NetCat.Engine;
using NetCat.Core;

namespace NetCat.Network;

// The trusted binary bundle and the generated private configuration stay locked
// until the child exits. Nothing is written to the parent/user/machine environment.
public sealed class OpenVpnCryptoScope : IDisposable
{
    private readonly IDisposable modules;
    private readonly FileStream config;
    public IReadOnlyDictionary<string, string> Environment { get; }
    public const string Configuration = "openssl_conf = openssl_init\n[openssl_init]\nproviders = provider_sect\n[provider_sect]\ndefault = default_sect\nlegacy = legacy_sect\n[default_sect]\nactivate = 1\n[legacy_sect]\nactivate = 1\n";
    private OpenVpnCryptoScope(IDisposable modules, FileStream config, string path, string directory)
    {
        this.modules = modules; this.config = config;
        Environment = new Dictionary<string, string> { ["OPENSSL_CONF"] = path, ["OPENSSL_MODULES"] = directory };
    }
    public static OpenVpnCryptoScope Create(string executable, string runtime)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var modules = ModuleIntegrity.Acquire("openvpn", root);
        try
        {
            var directory = OpenVpnService.FindLegacyProviderDirectory(executable)
                ?? throw new InvalidDataException("OpenVPN legacy provider missing from trusted bundle.");
            var path = Path.Combine(runtime, "openssl-netcat.cnf");
            ModuleIntegrity.CheckPath(path); PrivateFiles.ProtectDirectory(runtime);
            File.WriteAllText(path, Configuration, new System.Text.UTF8Encoding(false));
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                using var reader = new StreamReader(file, leaveOpen: true);
                if (reader.ReadToEnd() != Configuration) throw new InvalidDataException("OpenVPN private OpenSSL configuration changed.");
                return new(modules, file, path, directory);
            }
            catch { file.Dispose(); throw; }
        }
        catch { modules.Dispose(); throw; }
    }
    public void Dispose() { config.Dispose(); modules.Dispose(); }
}
