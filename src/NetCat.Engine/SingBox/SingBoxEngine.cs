using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetCat.Core.Enums;
using NetCat.Core.Models;
using NetCat.Engine.Management;

namespace NetCat.Engine.SingBox
{
    public class SingBoxConfigBuilder
    {
        public static string BuildConfig(
            ProxyProfile primary,
            ProxyProfile? backup,
            RoutingMode routingMode,
            IReadOnlyList<RoutingRule> rules,
            string tunInterface = "wintun-netcat",
            string tunAddress = "172.19.0.1/30",
            int socksPort = 10808)
        {
            var config = new JsonObject
            {
                ["log"] = new JsonObject
                {
                    ["level"] = "warn",
                    ["timestamp"] = true
                },
                ["dns"] = BuildDnsSection(routingMode),
                ["inbounds"] = BuildInboundsSection(tunInterface, tunAddress, socksPort),
                ["outbounds"] = BuildOutboundsSection(primary, backup),
                ["route"] = BuildRouteSection(routingMode, rules)
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
            };
            return config.ToJsonString(options);
        }

        private static JsonObject BuildDnsSection(RoutingMode mode)
        {
            var servers = new JsonArray
            {
                new JsonObject { ["tag"] = "dns-remote", ["address"] = "tcp://1.1.1.1", ["detour"] = "proxy-out" },
                new JsonObject { ["tag"] = "dns-direct", ["address"] = "local", ["detour"] = "direct" },
                new JsonObject { ["tag"] = "dns-block", ["address"] = "rcode://success" }
            };

            var rules = new JsonArray
            {
                new JsonObject { ["outbound"] = "any", ["server"] = "dns-direct" }
            };

            if (mode == RoutingMode.Global)
            {
                rules.Add(new JsonObject { ["clash_mode"] = "Global", ["server"] = "dns-remote" });
            }
            else
            {
                rules.Add(new JsonObject { ["clash_mode"] = "Direct", ["server"] = "dns-direct" });
            }

            return new JsonObject
            {
                ["servers"] = servers,
                ["rules"] = rules,
                ["strategy"] = "prefer_ipv4"
            };
        }

        private static JsonArray BuildInboundsSection(string tunInterface, string tunAddress, int socksPort)
        {
            return new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = tunInterface,
                    ["inet4_address"] = tunAddress,
                    ["auto_route"] = true,
                    ["strict_route"] = true,
                    ["stack"] = "mixed",
                    ["sniff"] = true
                },
                new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "socks-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = socksPort,
                    ["sniff"] = true
                }
            };
        }

        private static JsonArray BuildOutboundsSection(ProxyProfile primary, ProxyProfile? backup)
        {
            var outbounds = new JsonArray();

            var poolTags = new JsonArray { "out-primary" };
            if (backup != null)
            {
                poolTags.Add("out-backup");
            }

            outbounds.Add(new JsonObject
            {
                ["type"] = "urltest",
                ["tag"] = "proxy-out",
                ["outbounds"] = poolTags,
                ["url"] = "http://cp.cloudflare.com/generate_204",
                ["interval"] = "5s",
                ["tolerance"] = 50
            });

            outbounds.Add(BuildOutboundNode("out-primary", primary));

            if (backup != null)
            {
                outbounds.Add(BuildOutboundNode("out-backup", backup));
            }

            outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
            outbounds.Add(new JsonObject { ["type"] = "block", ["tag"] = "block" });

            return outbounds;
        }

        private static JsonObject BuildOutboundNode(string tag, ProxyProfile profile)
        {
            var node = new JsonObject
            {
                ["tag"] = tag,
                ["server"] = profile.ServerAddress,
                ["server_port"] = profile.ServerPort
            };

            switch (profile.Protocol.ToLowerInvariant())
            {
                case "vless":
                    node["type"] = "vless";
                    node["uuid"] = profile.Uuid;
                    if (!string.IsNullOrEmpty(profile.Flow)) node["flow"] = profile.Flow;
                    if (profile.Security.Equals("reality", StringComparison.OrdinalIgnoreCase))
                    {
                        node["tls"] = new JsonObject
                        {
                            ["enabled"] = true,
                            ["server_name"] = profile.Sni,
                            ["utls"] = new JsonObject
                            {
                                ["enabled"] = true,
                                ["fingerprint"] = string.IsNullOrEmpty(profile.Fingerprint) ? "chrome" : profile.Fingerprint
                            },
                            ["reality"] = new JsonObject
                            {
                                ["enabled"] = true,
                                ["public_key"] = profile.PublicKey,
                                ["short_id"] = profile.ShortId
                            }
                        };
                    }
                    else if (profile.Security.Equals("tls", StringComparison.OrdinalIgnoreCase))
                    {
                        node["tls"] = new JsonObject
                        {
                            ["enabled"] = true,
                            ["server_name"] = profile.Sni,
                            ["utls"] = new JsonObject
                            {
                                ["enabled"] = true,
                                ["fingerprint"] = string.IsNullOrEmpty(profile.Fingerprint) ? "chrome" : profile.Fingerprint
                            }
                        };
                    }
                    break;

                case "trojan":
                    node["type"] = "trojan";
                    node["password"] = profile.Password;
                    node["tls"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["server_name"] = profile.Sni
                    };
                    break;

                case "hysteria2":
                    node["type"] = "hysteria2";
                    node["password"] = profile.Password;
                    node["tls"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["server_name"] = profile.Sni
                    };
                    break;

                case "ss":
                    node["type"] = "shadowsocks";
                    node["method"] = string.IsNullOrEmpty(profile.Security) ? "aes-256-gcm" : profile.Security;
                    node["password"] = profile.Password;
                    break;

                default:
                    node["type"] = "socks";
                    break;
            }

            return node;
        }

        private static JsonObject BuildRouteSection(RoutingMode mode, IReadOnlyList<RoutingRule> rules)
        {
            var routeRules = new JsonArray
            {
                new JsonObject { ["protocol"] = "dns", ["outbound"] = "dns-direct" }
            };

            if (mode == RoutingMode.Global)
            {
                routeRules.Add(new JsonObject { ["outbound"] = "proxy-out" });
            }
            else
            {
                // Default direct bypasses
                routeRules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" });

                foreach (var rule in rules)
                {
                    if (!rule.Enabled) continue;
                    string outbound = rule.Action == RuleAction.Proxy ? "proxy-out" :
                                      rule.Action == RuleAction.Direct ? "direct" : "block";

                    var items = rule.Target.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    switch (rule.Type)
                    {
                        case RuleType.Process:
                            var procArr = new JsonArray();
                            foreach (var p in items) procArr.Add(p);
                            routeRules.Add(new JsonObject { ["process_name"] = procArr, ["outbound"] = outbound });
                            break;

                        case RuleType.Domain:
                            var domArr = new JsonArray();
                            foreach (var d in items) domArr.Add(d);
                            routeRules.Add(new JsonObject { ["domain_suffix"] = domArr, ["outbound"] = outbound });
                            break;

                        case RuleType.GeoData:
                            if (rule.Target.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase))
                            {
                                routeRules.Add(new JsonObject { ["geoip"] = rule.Target.Substring(6), ["outbound"] = outbound });
                            }
                            break;

                        case RuleType.IP:
                            if (rule.Target.Equals("ip_is_private", StringComparison.OrdinalIgnoreCase))
                            {
                                routeRules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = outbound });
                            }
                            break;
                    }
                }
            }

            return new JsonObject
            {
                ["rules"] = routeRules,
                ["auto_detect_interface"] = true
            };
        }
    }

    public class SingBoxProcess
    {
        private readonly ProcessSupervisor _supervisor;
        private readonly string _binaryPath;
        private const string ProcessKey = "sing-box";

        public bool IsRunning => _supervisor.IsRunning(ProcessKey);

        public SingBoxProcess(ProcessSupervisor supervisor, string? binaryPath = null)
        {
            _supervisor = supervisor;
            _binaryPath = binaryPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "sing-box", "sing-box.exe");
        }

        public void Start(string configFilePath)
        {
            if (!File.Exists(configFilePath))
            {
                throw new FileNotFoundException($"Sing-box configuration file not found: {configFilePath}");
            }

            string args = $"run -c \"{configFilePath}\"";
            _supervisor.StartProcess(ProcessKey, _binaryPath, args);
        }

        public void Stop()
        {
            _supervisor.StopProcess(ProcessKey);
        }
    }
}
