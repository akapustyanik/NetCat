using System.Text;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
public sealed class Candidate32UnifiedSubscriptionTests
{
    private const string Link = "vless://11111111-1111-1111-1111-111111111111@vpn.example:443?security=tls&type=ws&path=%2Fsocket#Remote";
    private static ImportResult Manifest(string id = "native:1", string uri = Link, bool complete = true) => ProfileImporter.Parse(new JsonObject
    { ["format"] = "netcat-subscription", ["version"] = 1, ["complete"] = complete, ["profiles"] = new JsonArray(new JsonObject { ["id"] = id, ["uri"] = uri }) }.ToJsonString());
    private static (AppSettings, Subscription) Settings()
    {
        var sub = new Subscription { Name = "Server A", Url = "https://subscription.example/private-token", UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        return (new AppSettings { Subscriptions = [sub] }, sub);
    }
    [Fact] public void ManifestPreservesStableIdentityAcrossConnectionChanges()
    {
        var (s, sub) = Settings(); s = SubscriptionMerge.Apply(s, sub.Id, Manifest()).Settings;
        var old = Assert.Single(s.Profiles); old.Name = "My name"; old.Candidate = false; old.LearnedRoutes = ["10.2.0.0/16"];
        var outcome = SubscriptionMerge.Apply(s, sub.Id, Manifest(uri:Link.Replace(":443", ":8443")));
        var changed = Assert.Single(outcome.Settings.Profiles);
        Assert.Equal(old.Id, changed.Id); Assert.Equal(8443, changed.Port); Assert.Equal("My name", changed.Name); Assert.False(changed.Candidate);
        Assert.Equal(old.LearnedRoutes, changed.LearnedRoutes); Assert.Equal(1, outcome.Updated); Assert.Equal("Server A", changed.SubscriptionSource);
        Assert.Equal(443, old.Port);
    }
    [Fact] public void CompleteManifestDeletesOnlyUnselectedOwnedProfiles()
    {
        var (s, sub) = Settings(); s = SubscriptionMerge.Apply(s, sub.Id, Manifest()).Settings;
        var old = Assert.Single(s.Profiles); var local = new Profile { Name = "Local" }; s.Profiles.Add(local);
        var empty = new ImportResult([], []) { CompleteSnapshot = true };
        var removed = SubscriptionMerge.Apply(s, sub.Id, empty);
        Assert.Equal(1, removed.Removed); Assert.Equal(local.Id, Assert.Single(removed.Settings.Profiles).Id);
        s.MainProfileId = old.Id;
        var retained = SubscriptionMerge.Apply(s, sub.Id, empty);
        Assert.Equal(1, retained.Retained); Assert.True(retained.Settings.Profiles.Single(p => p.Id == old.Id).SubscriptionRemoved); Assert.Equal(old.Id, retained.Settings.MainProfileId);
    }
    [Fact] public void PartialManifestNeverDeletesLastWorkingProfiles()
    {
        var (s, sub) = Settings(); s = SubscriptionMerge.Apply(s, sub.Id, Manifest()).Settings;
        var partial = new ImportResult(Manifest("new", Link.Replace("vpn.example", "other.example")).Profiles, ["Профиль 2: некорректные параметры."]) { CompleteSnapshot = true };
        var merged = SubscriptionMerge.Apply(s, sub.Id, partial);
        Assert.Equal(2, merged.Settings.Profiles.Count); Assert.Equal(0, merged.Removed); Assert.Equal(1, merged.Rejected);
    }
    [Fact] public void TextSubscriptionCannotAuthorizeDeletion()
    {
        var (s, sub) = Settings(); s = SubscriptionMerge.Apply(s, sub.Id, Manifest()).Settings;
        var text = ProfileImporter.Parse(Link.Replace("vpn.example", "other.example"));
        Assert.False(text.CompleteSnapshot); Assert.Equal(2, SubscriptionMerge.Apply(s, sub.Id, text).Settings.Profiles.Count);
    }
    [Fact] public void ReimportSameUrlReusesSubscriptionAndProfileIds()
    {
        var (s, sub) = Settings(); s = SubscriptionMerge.Import(s, sub, Manifest().Profiles);
        var id = Assert.Single(s.Profiles).Id;
        var again = SubscriptionMerge.Import(s, new Subscription { Url = sub.Url, Name = "Reimport" }, Manifest().Profiles);
        Assert.Single(again.Subscriptions); Assert.Equal(id, Assert.Single(again.Profiles).Id);
    }
    [Fact] public void RepeatedManifestRefreshIsIdempotent()
    {
        var (s, sub) = Settings(); s = SubscriptionMerge.Apply(s, sub.Id, Manifest()).Settings;
        var again = SubscriptionMerge.Apply(s, sub.Id, Manifest());
        Assert.Single(again.Settings.Profiles); Assert.Equal(0, again.Added); Assert.Equal(0, again.Updated);
    }
    [Fact] public void EmptyUntrustedSubscriptionPreservesOriginalSnapshot()
    {
        var (s, sub) = Settings(); s = SubscriptionMerge.Apply(s, sub.Id, Manifest()).Settings;
        Assert.Throws<InvalidDataException>(() => SubscriptionMerge.Apply(s, sub.Id, new([], [])));
        Assert.Single(s.Profiles);
    }
    [Fact] public void DuplicateManifestIdDisablesDeletionAuthority()
    {
        var root = JsonNode.Parse("{\"format\":\"netcat-subscription\",\"version\":1,\"complete\":true,\"profiles\":[]}")!;
        var list = (JsonArray)root["profiles"]!;
        for (int i = 0; i < 2; i++) list.Add(new JsonObject { ["id"] = "same", ["uri"] = Link });
        var result = ProfileImporter.Parse(root.ToJsonString()); Assert.Single(result.Errors); Assert.False(result.CompleteSnapshot); Assert.Equal(2, result.ReceivedCount);
    }
    [Fact] public void ManifestVersionFailureDoesNotDowngradeToGenericJson()
    {
        var bad = ProfileImporter.Parse("{\"format\":\"netcat-subscription\",\"version\":2,\"profiles\":[]}");
        Assert.Empty(bad.Profiles); Assert.Single(bad.Errors); Assert.False(bad.CompleteSnapshot);
    }
    [Fact] public void Base64ManifestIsRecognizedAfterDecoding()
    {
        var text = "{\"format\":\"netcat-subscription\",\"version\":1,\"complete\":true,\"profiles\":[{\"id\":\"first\",\"uri\":\"" + Link + "\"}]}";
        var result = ProfileImporter.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
        Assert.Single(result.Profiles); Assert.Empty(result.Errors); Assert.True(result.CompleteSnapshot);
    }
    [Fact] public void BadJsonEntryDoesNotHideLaterValidEntryOrLeakItsValues()
    {
        var root = new JsonArray(new JsonObject { ["type"] = "credential-should-not-be-logged" }, new JsonObject { ["type"] = "vless", ["server"] = "vpn.example", ["server_port"] = 443, ["uuid"] = "fixture" });
        var result = ProfileImporter.Parse(root.ToJsonString()); Assert.Single(result.Profiles); Assert.Single(result.Errors);
        Assert.DoesNotContain("credential", string.Join("", result.Errors));
    }
    [Fact] public void ClashYamlPreservesTlsWsAndShadowsocks2022Credentials()
    {
        var yaml = "proxies:\n  - name: Web\n    type: vless\n    server: vpn.example\n    port: 443\n    uuid: fixture\n    tls: true\n    network: ws\n    ws-opts:\n      path: /socket\n      headers:\n        Host: vpn.example\n  - name: SS\n    type: ss\n    server: vpn.example\n    port: 40001\n    cipher: 2022-blake3-aes-128-gcm\n    password: serverkey:clientkey\n";
        var result = ProfileImporter.Parse(yaml); Assert.Empty(result.Errors); Assert.Equal(2, result.Profiles.Count);
        var ws = JsonNode.Parse(result.Profiles[0].OutboundJson)!; Assert.True(ws["tls"]!["enabled"]!.GetValue<bool>()); Assert.Equal("/socket", ws["transport"]!["path"]!.ToString());
        Assert.Equal("serverkey:clientkey", JsonNode.Parse(result.Profiles[1].OutboundJson)!["password"]!.ToString());
    }
    [Fact] public void YamlGeckoPreservesObfuscationAndRejectsUnknownExtensions()
    {
        var yaml = "proxies:\n - name: Gecko\n   type: hysteria2\n   server: vpn.example\n   port: 40005\n   password: auth\n   obfs: gecko\n   obfs-password: obfuscation\n";
        var good = ProfileImporter.Parse(yaml); Assert.Empty(good.Errors); Assert.Equal("gecko", JsonNode.Parse(Assert.Single(good.Profiles).OutboundJson)!["obfs"]!["type"]!.ToString());
        var bad = ProfileImporter.Parse(yaml + "   unsupported-extension: secret\n"); Assert.Empty(bad.Profiles); Assert.Single(bad.Errors); Assert.DoesNotContain("secret", bad.Errors[0]);
    }
    [Fact] public void YamlAliasesAndMultipleDocumentsAreRejected()
    {
        foreach (var yaml in new[] { "proxies: &list []\ncopy: *list\n", "proxies: []\n---\nproxies: []\n" })
        { var result = ProfileImporter.Parse(yaml); Assert.Empty(result.Profiles); Assert.Single(result.Errors); }
    }
    [Fact] public async Task ApplicationSchedulerRefreshesWithoutWindowAndBacksOffFailure()
    {
        var (_, sub) = Settings(); int calls = 0; var messages = new List<string>(); var now = DateTimeOffset.UtcNow;
        var scheduler = new SubscriptionScheduler(() => new[] { sub }, (_, _) => { calls++; throw new IOException(sub.Url); }, () => true, (_, msg) => messages.Add(msg)) { Now = () => now };
        await scheduler.TickAsync(default); await scheduler.TickAsync(default); Assert.Equal(1, calls); Assert.DoesNotContain("private-token", Assert.Single(messages));
        now += TimeSpan.FromMinutes(5); await scheduler.TickAsync(default); Assert.Equal(2, calls);
    }
    [Fact] public async Task SchedulerSkipsManualOnlyAndRecentSubscriptions()
    {
        var (_, sub) = Settings(); int calls = 0;
        var scheduler = new SubscriptionScheduler(() => new[] { sub }, (_, _) => { calls++; return Task.CompletedTask; }, () => true, (_, _) => { });
        sub.UpdateHours = 0; await scheduler.TickAsync(default); sub.UpdateHours = 24; sub.UpdatedAt = DateTimeOffset.UtcNow; await scheduler.TickAsync(default); Assert.Equal(0, calls);
    }
    [Fact] public async Task SchedulerSerializesConcurrentTicks()
    {
        var (_, sub) = Settings(); int calls = 0; var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var scheduler = new SubscriptionScheduler(() => new[] { sub }, async (_, _) => { calls++; entered.SetResult(); await release.Task; }, () => true, (_, _) => { });
        var first = scheduler.TickAsync(default); await entered.Task; await scheduler.TickAsync(default); release.SetResult(); await first; Assert.Equal(1, calls);
    }
    [Fact] public async Task SubscriptionIdentityAndSecretsSurviveProtectedSettingsRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NetCat-subscription-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (s, sub) = Settings(); s = SubscriptionMerge.Apply(s, sub.Id, Manifest()).Settings;
            await new SettingsStore(directory).SaveAsync(s);
            var bytes = File.ReadAllBytes(Path.Combine(directory, "settings.dpapi")); Assert.DoesNotContain("private-token", Encoding.UTF8.GetString(bytes));
            var loaded = new SettingsStore(directory).Load(); Assert.Equal(sub.Url, Assert.Single(loaded.Subscriptions).Url);
            var profile = Assert.Single(loaded.Profiles); Assert.Equal(s.Profiles[0].Id, profile.Id); Assert.Equal("manifest:native:1", profile.SubscriptionItemId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact] public void StableIdentityStillRequiresApprovalForTlsDowngrade()
    {
        var (s, sub) = Settings(); s = SubscriptionMerge.Apply(s, sub.Id, Manifest()).Settings;
        Assert.Throws<SubscriptionSecurityApprovalRequiredException>(() => SubscriptionMerge.Apply(s, sub.Id, Manifest(uri:Link.Replace("#Remote", "&insecure=true#Remote"))));
    }
    [Fact] public void GenericSingBoxYamlRetainsScalarTypes()
    {
        var parsed = ProfileImporter.Parse("outbounds:\n - type: vless\n   server: vpn.example\n   server_port: 443\n   uuid: 'fixture'\n   tls:\n     enabled: true\n     server_name: vpn.example\n");
        Assert.Empty(parsed.Errors); var profile = Assert.Single(parsed.Profiles); Assert.Equal(443, profile.Port); Assert.True(JsonNode.Parse(profile.OutboundJson)!["tls"]!["enabled"]!.GetValue<bool>());
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request)); }
    [Fact] public async Task SubscriptionRequestNegotiatesManifestWithoutLosingStandardFallback()
    {
        var client = new SubscriptionClient { Resolve = (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("8.8.8.8") }), CreateHandler = (_, _) => new Handler(request =>
        {
            Assert.Contains(request.Headers.Accept, a => a.MediaType == SubscriptionDocument.MediaType);
            Assert.Contains(request.Headers.Accept, a => a.MediaType == "text/plain");
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(Link) };
        }) };
        Assert.Equal(Link, await client.ReadAsync("https://subscription.example/private-token", false, default));
    }
    [Fact] public async Task TransportFailureNeverExposesSubscriptionUrl()
    {
        const string url = "https://subscription.example/private-token";
        var client = new SubscriptionClient { Resolve = (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("8.8.8.8") }), CreateHandler = (_, _) => new Handler(_ => throw new HttpRequestException(url)) };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync(url, false, default));
        Assert.DoesNotContain("private-token", error.ToString()); Assert.DoesNotContain("subscription.example", error.ToString());
    }
    [Fact] public async Task SchedulerShutdownCancelsInFlightDownload()
    {
        var (_, sub) = Settings(); using var stop = new CancellationTokenSource(); var entered = new TaskCompletionSource(); int failed = 0;
        var scheduler = new SubscriptionScheduler(() => new[] { sub }, async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }, () => true, (_, _) => failed++);
        var tick = scheduler.TickAsync(stop.Token); await entered.Task; stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick); Assert.Equal(0, failed);
    }
}
