using System;
using System.IO;
using System.Text.Json;
using NetCat.Core.Enums;
using NetCat.Core.Models;
using NetCat.Engine.Parsers;
using NetCat.Engine.SingBox;
using NetCat.Engine.Zapret;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests
{
    public class EngineAndParserTests
    {
        [Fact]
        public void TestParseVlessReality()
        {
            string rawUri = "vless://11111111-2222-3333-4444-555555555555@example.com:443?security=reality&sni=yahoo.com&fp=chrome&pbk=1234567890abcdef&sid=abcd1234&type=tcp&flow=xtls-rprx-vision#My-VLESS-Node";
            var profile = ProtocolParser.Parse(rawUri);

            Assert.NotNull(profile);
            Assert.Equal("vless", profile.Protocol);
            Assert.Equal("example.com", profile.ServerAddress);
            Assert.Equal(443, profile.ServerPort);
            Assert.Equal("11111111-2222-3333-4444-555555555555", profile.Uuid);
            Assert.Equal("reality", profile.Security);
            Assert.Equal("yahoo.com", profile.Sni);
            Assert.Equal("1234567890abcdef", profile.PublicKey);
            Assert.Equal("abcd1234", profile.ShortId);
            Assert.Equal("xtls-rprx-vision", profile.Flow);
            Assert.Equal("My-VLESS-Node", profile.Name);
        }

        [Fact]
        public void TestParseHysteria2()
        {
            string rawUri = "hysteria2://supersecret@hy2.example.org:8443?sni=bing.com#Fast-Hy2";
            var profile = ProtocolParser.Parse(rawUri);

            Assert.NotNull(profile);
            Assert.Equal("hysteria2", profile.Protocol);
            Assert.Equal("hy2.example.org", profile.ServerAddress);
            Assert.Equal(8443, profile.ServerPort);
            Assert.Equal("supersecret", profile.Password);
            Assert.Equal("bing.com", profile.Sni);
            Assert.Equal("Fast-Hy2", profile.Name);
        }

        [Fact]
        public void TestSingBoxConfigBuilder_GeneratesValidJson()
        {
            var primary = new ProxyProfile
            {
                Name = "Primary Node",
                Protocol = "vless",
                ServerAddress = "nl.node.com",
                ServerPort = 443,
                Uuid = "00000000-0000-0000-0000-000000000000",
                Security = "reality",
                Sni = "microsoft.com",
                PublicKey = "pubkey",
                ShortId = "sid"
            };

            var backup = new ProxyProfile
            {
                Name = "Backup Node",
                Protocol = "hysteria2",
                ServerAddress = "de.node.com",
                ServerPort = 8443,
                Password = "pass",
                Sni = "bing.com"
            };

            var rules = new[]
            {
                new RoutingRule { Target = "discord.exe", Type = RuleType.Process, Action = RuleAction.Proxy },
                new RoutingRule { Target = ".ru,vk.com", Type = RuleType.Domain, Action = RuleAction.Direct }
            };

            string json = SingBoxConfigBuilder.BuildConfig(primary, backup, RoutingMode.RuleBased, rules);
            Assert.False(string.IsNullOrWhiteSpace(json));

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("inbounds", out var inbounds));
            Assert.True(root.TryGetProperty("outbounds", out var outbounds));
            Assert.True(root.TryGetProperty("dns", out var dns));
            Assert.True(root.TryGetProperty("route", out var route));
        }

        [Fact]
        public void TestZapretArgsBuilder_Strategies()
        {
            string alt13 = ZapretArgsBuilder.BuildArgs("ALT13");
            Assert.Contains("--wf-tcp=80,443", alt13);
            Assert.Contains("--dpi-desync=fake,split2", alt13);

            string discord = ZapretArgsBuilder.BuildArgs("DISCORD");
            Assert.Contains("--dpi-desync=split", discord);

            string youtube = ZapretArgsBuilder.BuildArgs("YOUTUBE");
            Assert.Contains("--dpi-desync=fake", youtube);
        }

        [Fact]
        public void TestChecksumValidator()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "NetCat Test Content");
                string hash = ChecksumValidator.ComputeSha256(tempFile);
                Assert.NotEmpty(hash);
                Assert.Equal(64, hash.Length);
                Assert.True(ChecksumValidator.ValidateSha256(tempFile, hash));
            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }
}
