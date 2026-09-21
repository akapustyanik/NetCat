using System.Text.RegularExpressions;
using NetCat.Core;

namespace NetCat.Engine;
public static class ZapretArguments
{
    public static string[] Build(string batch, string root, string scenarioHosts, bool youtube, bool discord, int? physicalInterface = null)
    {
        if (!youtube && !discord) throw new InvalidOperationException("Нет сервисов, направленных через Zapret.");
        var text = File.ReadAllText(batch);
        var marker = Regex.Match(text, @"(?i)""%BIN%winws\.exe""");
        if (!marker.Success) throw new InvalidDataException("В стратегии не найден запуск winws.exe.");
        text = text[(marker.Index + marker.Length)..].Replace("^\r\n", " ").Replace("^\n", " ").Split('\n')[0];
        text = text.Replace("%BIN%", Path.Combine(root, "bin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase).Replace("%LISTS%", Path.Combine(root, "lists") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var args = new List<string> { "--wf-tcp=80,443,2053,2083,2087,2096,8443", "--wf-udp=" + (discord ? "443,19294-19344,50000-50100" : "443") };
        // Intercept the physical leg only. Desynchronizing ClientHello on the TUN leg
        // can prevent sing-box from sniffing/routing it, including traffic destined for VPN.
        if (physicalInterface.HasValue)
        {
            if (physicalInterface.Value <= 0) throw new ArgumentOutOfRangeException(nameof(physicalInterface));
            args.Add("--wf-iface=" + physicalInterface.Value);
        }
        var selected = (youtube ? ServiceDomains.YouTube : []).Concat(discord ? ServiceDomains.Discord : []).ToArray();
        var groups = Regex.Split(text, @"\s+--new\s+"); int count = 0;
        foreach (var group in groups)
        {
            if (group.Contains("--filter-tcp=%GameFilter") || group.Contains("--filter-udp=%GameFilter")) continue;
            bool voice = group.Contains("--filter-l7=discord,stun");
            if (voice && !discord) continue;
            // Batch escapes ! as ^!; native argv must receive ! (the built-in TLS payload), not a filename ^!.
            var tokens = Regex.Matches(group, "(?:[^\\s\"]+|\"[^\"]*\")+").Select(m => m.Value.Replace("\"", "").Replace("^!","!",StringComparison.Ordinal)).Where(t => !t.StartsWith("--wf-")).ToList();
            if (tokens.Count == 0) continue;
            if (tokens.Any(t => t.Contains('%'))) throw new InvalidDataException("Неизвестная переменная в стратегии; автоматическое выполнение BAT запрещено.");
            // Positive host lists in winws are ORed, not ANDed. Replacing all of them
            // with the scenario list makes the earlier Google profile steal Discord.
            // Intersect each original profile separately, keeping profile order and IP filters.
            var includes = tokens.Where(t => t.StartsWith("--hostlist=") || t.StartsWith("--hostlist-domains=")).ToArray();
            if (includes.Length > 0)
            {
                var original = new List<string>();
                foreach (var include in includes)
                {
                    var value = include[(include.IndexOf('=') + 1)..];
                    if (include.StartsWith("--hostlist-domains=")) original.AddRange(value.Split(','));
                    else if (File.Exists(value)) original.AddRange(File.ReadLines(value));
                    else if (!value.EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase))
                        throw new FileNotFoundException("Не найден список доменов стратегии Zapret.", value);
                }
                var hosts = IntersectHosts(original, selected);
                if (hosts.Length == 0) continue; // An empty winws hostlist would match everything.
                tokens.RemoveAll(t => t.StartsWith("--hostlist=") || t.StartsWith("--hostlist-domains="));
                tokens.Add("--hostlist-domains=" + string.Join(',', hosts));
            }
            else if (!voice) tokens.Add("--hostlist=" + scenarioHosts);
            // Upstream service.bat creates optional user lists on first launch. NetCat never runs that batch.
            tokens.RemoveAll(t => (t.StartsWith("--hostlist-exclude=") || t.StartsWith("--ipset-exclude=")) && t.EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase) && !File.Exists(t[(t.IndexOf('=') + 1)..]));
            if (count++ > 0) args.Add("--new"); args.AddRange(tokens);
        }
        if (count == 0) throw new InvalidDataException("В стратегии нет фильтров для выбранного сценария.");
        return args.ToArray();
    }

    private static string[] IntersectHosts(IEnumerable<string> original, IEnumerable<string> selected)
    {
        static bool Within(string host, string suffix) => host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in original)
        {
            var entry = line.Trim();
            if (entry.Length == 0 || entry.StartsWith('#')) continue;
            var exact = entry.StartsWith('^'); var host = entry.TrimStart('^');
            foreach (var domain in selected)
            {
                if (Within(host, domain)) result.Add(entry);
                else if (!exact && Within(domain, host)) result.Add(domain);
            }
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
