using System.Globalization;
using NetCat.Core;

namespace NetCat.Network;

// Unified synchronous corporate domain ownership authority.
// Suffix matching follows sing-box's domain_suffix semantics:
// exact domain or dot-prefixed subdomain. Substrings do not match.
// All reads are lock-free on an immutable snapshot.
public sealed class CorporateDomainGuard
{
    private static readonly IdnMapping Idn = new();
    private readonly object gate = new();
    private readonly Dictionary<(Guid Profile, long Generation), string[]> candidates = [];
    private string[] known = [];
    private volatile string[] activeSnapshot = [];
    private long revision;

    public long Revision => Interlocked.Read(ref revision);

    public static string Canonicalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var trimmed = value.Trim().TrimEnd('.');
        if (trimmed.StartsWith("domain:", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[7..];
        if (trimmed.Contains("://") && Uri.TryCreate(trimmed, UriKind.Absolute, out var u)) trimmed = u.Host;
        trimmed = trimmed.TrimStart('*', '.').TrimEnd('.');
        if (string.IsNullOrWhiteSpace(trimmed)) return "";
        try { trimmed = Idn.GetAscii(trimmed); } catch { }
        return trimmed.ToLowerInvariant();
    }

    public void Prepare(AppSettings settings) => Prepare(settings.OpenVpnDomains);

    public void Prepare(string? rawDomains)
    {
        var domains = RuleValidation.Domains(rawDomains ?? "")
            .Select(Canonicalize)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        lock (gate)
        {
            known = domains;
            Rebuild();
        }
    }

    public void Candidate(Guid profile, long generation, IReadOnlyList<string> domains)
    {
        var canonical = domains
            .Select(Canonicalize)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        lock (gate)
        {
            candidates[(profile, generation)] = canonical;
            Rebuild();
        }
    }

    public void ClearCandidate(Guid profile, long generation)
    {
        lock (gate)
        {
            if (candidates.Remove((profile, generation)))
            {
                Rebuild();
            }
        }
    }

    public void RemoveProfile(Guid profile)
    {
        lock (gate)
        {
            var keys = candidates.Keys.Where(k => k.Profile == profile).ToArray();
            if (keys.Length > 0)
            {
                foreach (var k in keys) candidates.Remove(k);
                Rebuild();
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            known = [];
            candidates.Clear();
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var all = known.Concat(candidates.Values.SelectMany(x => x))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)
            .ToArray();
        activeSnapshot = all;
        Interlocked.Increment(ref revision);
    }

    public bool IsCorporate(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return false;
        var canonical = Canonicalize(hostname);
        if (canonical.Length == 0) return false;

        var snapshot = activeSnapshot;
        for (int i = 0; i < snapshot.Length; i++)
        {
            var pattern = snapshot[i];
            if (canonical.Equals(pattern, StringComparison.Ordinal) ||
                canonical.EndsWith("." + pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // Linearization point shared with Prepare/Candidate. No automatic request
    // may be submitted after ownership publication using an earlier snapshot.
    public Task DispatchAutomatic(string hostname, Func<Task> send)
    {
        lock(gate)
        {
            if(IsCorporate(hostname))throw new IOException("Corporate destination requires OpenVPN.");
            return send();
        }
    }
}
