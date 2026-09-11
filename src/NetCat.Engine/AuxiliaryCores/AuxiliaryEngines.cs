using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetCat.Core.Models;
using NetCat.Engine.Management;

namespace NetCat.Engine.Xray
{
    public class XrayConfigBuilder
    {
        public static string BuildConfig(ProxyProfile profile, int localSocksPort = 10850)
        {
            var config = new JsonObject
            {
                ["log"] = new JsonObject { ["loglevel"] = "warning" },
                ["inbounds"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["tag"] = "socks-in",
                        ["port"] = localSocksPort,
                        ["listen"] = "127.0.0.1",
                        ["protocol"] = "socks",
                        ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true }
                    }
                },
                ["outbounds"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["tag"] = "proxy",
                        ["protocol"] = "vless",
                        ["settings"] = new JsonObject
                        {
                            ["vnext"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["address"] = profile.ServerAddress,
                                    ["port"] = profile.ServerPort,
                                    ["users"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["id"] = profile.Uuid,
                                            ["encryption"] = "none",
                                            ["flow"] = profile.Flow
                                        }
                                    }
                                }
                            }
                        },
                        ["streamSettings"] = new JsonObject
                        {
                            ["network"] = string.IsNullOrEmpty(profile.Network) ? "tcp" : profile.Network,
                            ["security"] = profile.Security.Equals("reality", StringComparison.OrdinalIgnoreCase) ? "reality" : "tls",
                            ["realitySettings"] = profile.Security.Equals("reality", StringComparison.OrdinalIgnoreCase) ? new JsonObject
                            {
                                ["serverName"] = profile.Sni,
                                ["publicKey"] = profile.PublicKey,
                                ["shortId"] = profile.ShortId,
                                ["fingerprint"] = profile.Fingerprint
                            } : null
                        }
                    }
                }
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
            };
            return config.ToJsonString(options);
        }
    }

    public class XrayProcess
    {
        private readonly ProcessSupervisor _supervisor;
        private readonly string _binaryPath;
        private const string ProcessKey = "xray-core";

        public bool IsRunning => _supervisor.IsRunning(ProcessKey);

        public XrayProcess(ProcessSupervisor supervisor, string? binaryPath = null)
        {
            _supervisor = supervisor;
            _binaryPath = binaryPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "xray", "xray.exe");
        }

        public void Start(string configFilePath)
        {
            _supervisor.StartProcess(ProcessKey, _binaryPath, $"run -c \"{configFilePath}\"");
        }

        public void Stop()
        {
            _supervisor.StopProcess(ProcessKey);
        }
    }
}

namespace NetCat.Engine.OpenVpn
{
    public class OpenVpnProcess
    {
        private readonly ProcessSupervisor _supervisor;
        private readonly string _binaryPath;
        private const string ProcessKey = "openvpn-core";

        public bool IsRunning => _supervisor.IsRunning(ProcessKey);

        public OpenVpnProcess(ProcessSupervisor supervisor, string? binaryPath = null)
        {
            _supervisor = supervisor;
            _binaryPath = binaryPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "openvpn", "openvpn.exe");
        }

        public void Start(string ovpnConfigPath)
        {
            _supervisor.StartProcess(ProcessKey, _binaryPath, $"--config \"{ovpnConfigPath}\"");
        }

        public void Stop()
        {
            _supervisor.StopProcess(ProcessKey);
        }
    }
}
