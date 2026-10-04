namespace NetCat.Engine;

// A code-level dependency boundary for isolated native fixtures. No settings,
// profile, CLI option or installed manifest can replace the production policy.
public interface IExecutableTrustPolicy
{
    IDisposable? AcquireExecutable(string path);
    IDisposable AcquirePackage(string key,string folder);
}
