using System.Text.Json;

namespace NetCat.Engine;

// Exact packages already reviewed in an earlier NetCat release. Keeping a
// pinned component during an application update must preserve its trust too.
// These hashes live in the signed application image, never in a writable lock.
internal static class ReviewedPackageHistory
{
    private sealed record History(Dictionary<string, Dictionary<string, string>[]> Modules,
        Dictionary<string, Dictionary<string, string>[]> Runtimes);
    private static readonly History Packages = Load();
    private static History Load()
    {
        using var stream = typeof(ReviewedPackageHistory).Assembly.GetManifestResourceStream("NetCat.ReviewedPackageHistory")!;
        return JsonSerializer.Deserialize<History>(stream)!;
    }
    public static IEnumerable<IReadOnlyDictionary<string, string>> Inventories(string key,
        IReadOnlyDictionary<string, string> current, bool runtime)
    {
        yield return current;
        var packages = runtime ? Packages.Runtimes : Packages.Modules;
        if (packages.TryGetValue(key, out var previous)) foreach (var inventory in previous) yield return inventory;
    }
    public static bool Matches(IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> expected) =>
        actual.Count == expected.Count && expected.All(p => actual.TryGetValue(p.Key, out var hash) &&
            hash.Equals(p.Value, StringComparison.OrdinalIgnoreCase));
}
