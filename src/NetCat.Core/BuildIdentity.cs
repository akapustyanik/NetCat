using System.Reflection;

namespace NetCat.Core;

/// <summary>
/// Build identity is emitted once by Directory.Build.props and read from the
/// assembly metadata. Runtime logs and update diagnostics use this source.
/// </summary>
public static class BuildIdentity
{
    private static readonly Assembly Assembly = typeof(BuildIdentity).Assembly;

    public static string Version =>
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "unknown";

    public static string Candidate =>
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key.Equals("NetCatCandidate", StringComparison.Ordinal))?.Value
        ?? "unknown";
}
