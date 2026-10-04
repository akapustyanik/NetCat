using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

public sealed class ProfileCountryCorrectionTests
{
    [Theory]
    [InlineData("server-a · VLESS WebSocket", "Germany · VLESS WebSocket")]
    [InlineData("SERVER A - Trojan", "Germany - Trojan")]
    [InlineData("server-b · TUIC v5", "Finland · TUIC v5")]
    [InlineData("Server B", "Finland")]
    public void LegacyServerNamesMatchCorrectCountries(string original, string expected) =>
        Assert.Equal(expected, ProfileDisplayNames.Normalize(original));

    [Theory]
    [InlineData("Finland · VLESS WebSocket", "Germany · VLESS WebSocket")]
    [InlineData("German · VLESS WebSocket", "Finland · VLESS WebSocket")]
    [InlineData("Germany · VLESS WebSocket", "Finland · VLESS WebSocket")]
    public void RefreshCorrectsGeneratedCountryAndRemainsIdempotent(string oldName, string correctedName)
    {
        var sub = new Subscription { Name = "Country fixture" };
        var saved = new Profile
        {
            Name = oldName, Host = "fixture.invalid", Port = 443, Password = "fixture-password",
            Protocol = "vless", OutboundJson = "{}", SubscriptionId = sub.Id,
            SubscriptionItemId = "manifest:vless-ws", Candidate = true
        };
        var settings = new AppSettings { Subscriptions = [sub], Profiles = [saved], MainProfileId = saved.Id };
        var incoming = JsonSettings.Clone(saved); incoming.Name = correctedName;
        var document = new ImportResult([incoming], []) { CompleteSnapshot = true };
        for (int repeat = 0; repeat < 3; repeat++)
        {
            var result = SubscriptionMerge.Apply(settings, sub.Id, document);
            var profile = Assert.Single(result.Settings.Profiles);
            Assert.Equal(correctedName, profile.Name);
            Assert.Equal(saved.Id, result.Settings.MainProfileId);
            var expected = JsonSettings.Clone(saved); expected.Name = correctedName; expected.SubscriptionSource = sub.Name;
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(profile));
            Assert.Equal(0, result.Added); Assert.Equal(0, result.Removed);
            Assert.Equal(repeat == 0 ? 1 : 0, result.Updated);
            settings = result.Settings;
        }
    }

    [Theory]
    [InlineData("My private profile")]
    [InlineData("Finland · My custom name")]
    [InlineData("Germany home")]
    public void RefreshDoesNotOverwriteCustomNames(string customName)
    {
        var sub = new Subscription();
        var saved = new Profile { Name = customName, SubscriptionId = sub.Id, SubscriptionItemId = "manifest:vless-ws" };
        var settings = new AppSettings { Subscriptions = [sub], Profiles = [saved] };
        var incoming = new Profile { Name = "Germany · VLESS WebSocket", SubscriptionItemId = saved.SubscriptionItemId };
        var result = SubscriptionMerge.Apply(settings, sub.Id, new([incoming], []));
        Assert.Equal(customName, Assert.Single(result.Settings.Profiles).Name);
    }
}
