using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;
public sealed class ZapretStrategyTests
{
    private static List<string[]> Groups(string[] args)
    {
        var result = new List<string[]>(); var group = new List<string>();
        foreach (var arg in args)
        {
            if (arg == "--new") { result.Add(group.ToArray()); group.Clear(); }
            else group.Add(arg);
        }
        result.Add(group.ToArray()); return result;
    }
    private static bool ContainsHost(string[] group, string host) => group.Where(x => x.StartsWith("--hostlist-domains="))
        .SelectMany(x => x.Split('=')[1].Split(','))
        .Any(x => x.StartsWith('^') ? x[1..] == host : x == host || host.EndsWith("." + x));

    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void PackagedProfilesKeepGoogleDiscordAndIpFiltersSeparate(bool youtube, bool discord)
    {
        var root = Path.Combine(RoutingTests.FindRoot(), "bin", "zapret");
        foreach (var batch in Directory.GetFiles(root, "general*.bat"))
        {
            var args = ZapretArguments.Build(batch, root, "scenario-hosts.txt", youtube, discord, 24);
            Assert.Contains("--wf-iface=24", args);
            var groups = Groups(args);
            foreach (var group in groups.Where(g => g.Contains("--ip-id=zero")))
            {
                Assert.DoesNotContain(ServiceDomains.Discord, d => ContainsHost(group, d));
                Assert.Equal(youtube, ContainsHost(group, "www.youtube.com"));
                Assert.Equal(youtube, ContainsHost(group, "r1.googlevideo.com"));
                // Flowseal intentionally puts this one Discord download host in list-google.
                Assert.Equal(discord, ContainsHost(group, "stable.dl2.discordapp.net"));
            }
            if (discord)
            {
                var tcp = groups.FirstOrDefault(g => g.Any(a => a.StartsWith("--filter-tcp=") && a.Split('=')[1].Split(',').Contains("443")) && (ContainsHost(g, "gateway.discord.gg") || g.Contains("--hostlist=scenario-hosts.txt")));
                Assert.True(tcp != null, Path.GetFileName(batch));
                Assert.DoesNotContain("--ip-id=zero", tcp);
                var voice = Assert.Single(groups, g => g.Any(a => a.StartsWith("--filter-l7=discord,stun")));
                Assert.DoesNotContain(voice, a => a.StartsWith("--hostlist")); // STUN has no HTTP Host/TLS SNI.
                if (File.ReadAllText(batch).Contains("--hostlist-domains=discord.media"))
                {
                    var media = Assert.Single(groups, g => g.Contains("--filter-tcp=2053,2083,2087,2096,8443"));
                    Assert.True(ContainsHost(media, "cdn.discord.media"));
                    Assert.False(ContainsHost(media, "www.youtube.com"));
                }
            }
            foreach (var group in groups.Where(g => g.Any(a => a.StartsWith("--ipset="))))
                Assert.Contains("--hostlist=scenario-hosts.txt", group);
            foreach (var group in groups)
            {
                if (!discord) Assert.DoesNotContain(ServiceDomains.Discord, d => ContainsHost(group, d));
                if (!youtube) Assert.DoesNotContain(ServiceDomains.YouTube, d => ContainsHost(group, d));
            }
            Assert.True(string.Join(' ', args).Length < 30000, "Windows command line limit: " + batch);
        }
    }

    [Fact]
    public void ScenarioIntersectionPreservesExactHostsAndDropsEmptyProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "netcat-strategy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var batch = Path.Combine(root, "general.bat");
        try
        {
            File.WriteAllText(batch, "\"%BIN%winws.exe\" --filter-tcp=443 --hostlist-domains=unrelated.example --dpi-desync=fake --new " +
                "--filter-tcp=443 --hostlist-domains=^youtube.com,googleapis.com --dpi-desync=multisplit");
            var groups = Groups(ZapretArguments.Build(batch, root, "scenario.txt", true, false));
            var profile = Assert.Single(groups);
            Assert.DoesNotContain("--dpi-desync=fake", profile);
            Assert.True(ContainsHost(profile, "youtube.com")); Assert.False(ContainsHost(profile, "www.youtube.com"));
            Assert.True(ContainsHost(profile, "youtubei.googleapis.com")); Assert.False(ContainsHost(profile, "other.googleapis.com"));
        }
        finally { File.Delete(batch); Directory.Delete(root); }
    }
}
