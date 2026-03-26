using System.IO;
using System.Linq;
using ServiceLib.Common;
using ServiceLib.Models;

namespace ServiceLib.Handler;

public static class QuickRuleHandler
{
    private const string FileName = "quick-rules.json";
    private const string ProxyDomainsPresetFileName = "domains.lst";

    private static readonly string[] YoutubeDomains =
    [
        "geosite:youtube",
        "domain:youtube.com",
        "domain:youtu.be",
        "domain:googlevideo.com",
        "domain:ytimg.com",
        "domain:ggpht.com",
        "domain:youtubei.googleapis.com"
    ];

    private static readonly string[] TelegramDomains =
    [
        "geosite:telegram",
        "domain:t.me",
        "domain:telegram.me",
        "domain:telegram.org",
        "domain:telegram.dog",
        "domain:telegram.space",
        "domain:telegramdownload.com",
        "domain:telegra.ph",
        "domain:cdn-telegram.org",
        "domain:tg.dev"
    ];

    private static readonly string[] TelegramProcesses =
    [
        "telegram.exe",
        "telegramdesktop.exe"
    ];

    private static readonly string[] DiscordDomains =
    [
        "geosite:discord",
        "domain:discord.com",
        "domain:discord.gg",
        "domain:discord.media",
        "domain:discordapp.com",
        "domain:discordapp.net",
        "domain:discordcdn.com"
    ];

    private static readonly string[] DiscordProcesses =
    [
        "discord.exe",
        "discordcanary.exe",
        "discordptb.exe"
    ];

    public static QuickRuleConfig Load()
    {
        try
        {
            var path = Utils.GetConfigPath(FileName);
            if (!File.Exists(path))
            {
                return new QuickRuleConfig();
            }

            var text = FileUtils.NonExclusiveReadAllText(path);
            var cfg = JsonUtils.Deserialize<QuickRuleConfig>(text);
            return cfg ?? new QuickRuleConfig();
        }
        catch
        {
            return new QuickRuleConfig();
        }
    }

    public static async Task Save(QuickRuleConfig config)
    {
        var path = Utils.GetConfigPath(FileName);
        var content = JsonUtils.Serialize(config, true, true);
        await FileUtils.WriteAllTextWithRetryAsync(path, content ?? "{}");
    }

    public static async Task Apply(Config config, QuickRuleConfig quick)
    {
        var rules = new List<RulesItem>();
        var useZapretForBlockedServices = config.GuiItem.ZapretEnabled;
        var telegramOutboundTag = TelegramWsProxyHandler.IsLocalSocksMode(quick.TelegramTrafficMode)
            ? Global.DirectTag
            : Global.ProxyTag;
        var telegramRemarksSuffix = TelegramWsProxyHandler.IsLocalSocksMode(quick.TelegramTrafficMode)
            ? "local SOCKS"
            : "VPN";
        var proxyDomainList = NormalizeList(quick.ProxyDomains);
        if (quick.UseProxyDomainsPreset)
        {
            proxyDomainList.AddRange(LoadProxyDomainsPreset());
            proxyDomainList = NormalizeList(proxyDomainList);
        }

        var blockDomainList = NormalizeList(quick.BlockDomains);
        if (blockDomainList.Count > 0)
        {
            rules.Add(new RulesItem
            {
                Type = "field",
                OutboundTag = Global.BlockTag,
                Domain = blockDomainList,
                Remarks = "Blocked domains"
            });
        }

        rules.Add(new RulesItem
        {
            Type = "field",
            OutboundTag = telegramOutboundTag,
            Process = NormalizeList(TelegramProcesses),
            Remarks = $"Telegram apps ({telegramRemarksSuffix})"
        });
        rules.Add(new RulesItem
        {
            Type = "field",
            OutboundTag = telegramOutboundTag,
            Domain = NormalizeList(TelegramDomains),
            Remarks = $"Telegram domains ({telegramRemarksSuffix})"
        });
        rules.Add(new RulesItem
        {
            Type = "field",
            OutboundTag = telegramOutboundTag,
            Ip = ["geoip:telegram"],
            Remarks = $"Telegram IPs ({telegramRemarksSuffix})"
        });

        var youtubeOutboundTag = useZapretForBlockedServices ? Global.DirectTag : Global.ProxyTag;
        var youtubeRouteMode = useZapretForBlockedServices ? "Direct/Zapret" : "VPN";
        rules.Add(new RulesItem
        {
            Type = "field",
            OutboundTag = youtubeOutboundTag,
            Domain = NormalizeList(YoutubeDomains),
            Remarks = $"YouTube ({youtubeRouteMode})"
        });

        var discordOutboundTag = useZapretForBlockedServices ? Global.DirectTag : Global.ProxyTag;
        var discordRouteMode = useZapretForBlockedServices ? "Direct/Zapret" : "VPN";
        rules.Add(new RulesItem
        {
            Type = "field",
            OutboundTag = discordOutboundTag,
            Process = NormalizeList(DiscordProcesses),
            Remarks = $"Discord apps ({discordRouteMode})"
        });
        rules.Add(new RulesItem
        {
            Type = "field",
            OutboundTag = discordOutboundTag,
            Domain = NormalizeList(DiscordDomains),
            Remarks = $"Discord domains ({discordRouteMode})"
        });

        var processList = NormalizeList(quick.DirectProcesses);
        if (processList.Count > 0)
        {
            rules.Add(new RulesItem
            {
                Type = "field",
                OutboundTag = Global.DirectTag,
                Process = processList,
                Remarks = "Direct apps"
            });
        }

        var domainList = NormalizeList(quick.DirectDomains);
        domainList = NormalizeList(domainList);
        if (domainList.Count > 0)
        {
            rules.Add(new RulesItem
            {
                Type = "field",
                OutboundTag = Global.DirectTag,
                Domain = domainList,
                Remarks = "Direct domains"
            });
        }

        rules.Add(new RulesItem
        {
            Type = "field",
            OutboundTag = Global.DirectTag,
            Protocol = ["bittorrent"],
            Remarks = "Direct bittorrent"
        });

        if (quick.BypassPrivate)
        {
            rules.Add(new RulesItem
            {
                Type = "field",
                OutboundTag = Global.DirectTag,
                Ip = ["geoip:private"],
                Remarks = "Local network"
            });
            rules.Add(new RulesItem
            {
                Type = "field",
                OutboundTag = Global.DirectTag,
                Domain = ["geosite:private"],
                Remarks = "Local domains"
            });
        }

        if (proxyDomainList.Count > 0)
        {
            rules.Add(new RulesItem
            {
                Type = "field",
                OutboundTag = Global.ProxyTag,
                Domain = proxyDomainList,
                Remarks = "Proxy listed domains"
            });
        }

        var proxyProcessList = NormalizeList(quick.ProxyProcesses);
        if (proxyProcessList.Count > 0)
        {
            rules.Add(new RulesItem
            {
                Type = "field",
                OutboundTag = Global.ProxyTag,
                Process = proxyProcessList,
                Remarks = "Proxy apps"
            });
        }

        rules.Add(new RulesItem
        {
            Type = "field",
            OutboundTag = quick.ProxyOnlyMode ? Global.DirectTag : Global.ProxyTag,
            Port = "0-65535",
            Remarks = quick.ProxyOnlyMode ? "Direct default" : "Proxy default"
        });

        var routingItem = new RoutingItem
        {
            Id = quick.RoutingId.IsNullOrEmpty() ? Utils.GetGuid(false) : quick.RoutingId!,
            Remarks = "Quick Rules",
            Enabled = true,
            Sort = 1
        };

        var ruleSetJson = JsonUtils.Serialize(rules, false);
        await ConfigHandler.AddBatchRoutingRules(routingItem, ruleSetJson ?? "[]");
        await ConfigHandler.SetDefaultRouting(config, routingItem);

        quick.RoutingId = routingItem.Id;
        await Save(quick);
    }

    private static List<string> NormalizeList(IEnumerable<string> list)
    {
        return list
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> LoadProxyDomainsPreset()
    {
        try
        {
            var path = FindProxyDomainsPresetPath();
            if (path.IsNullOrEmpty() || !File.Exists(path))
            {
                return [];
            }

            return File.ReadLines(path)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Where(t => !t.StartsWith('#'))
                .Select(NormalizeDomainRule)
                .Where(t => !t.IsNullOrEmpty())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? FindProxyDomainsPresetPath()
    {
        var candidates = new List<string>();
        if (!Environment.CurrentDirectory.IsNullOrEmpty())
        {
            candidates.Add(Path.Combine(Environment.CurrentDirectory, ProxyDomainsPresetFileName));
        }

        var current = AppContext.BaseDirectory;
        while (!current.IsNullOrEmpty())
        {
            candidates.Add(Path.Combine(current, ProxyDomainsPresetFileName));
            current = Directory.GetParent(current)?.FullName;
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static string NormalizeDomainRule(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.IsNullOrEmpty())
        {
            return trimmed;
        }

        if (trimmed.Contains(':'))
        {
            return trimmed;
        }

        var value = trimmed.TrimStart('*').TrimStart('.');
        if (value.IsNullOrEmpty())
        {
            return trimmed;
        }

        return $"domain:{value}";
    }
}
