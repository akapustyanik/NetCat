using System.Text.Json;
using NetCat.Core;
using Xunit;

namespace NetCat.Tests;
public sealed class InterfaceScaleTests
{
    [Theory]
    [InlineData(.7)][InlineData(.8)][InlineData(1)][InlineData(1.3)]
    public async Task InterfaceScaleSurvivesSettingsStoreRestart(double scale)
    {
        var root = Path.Combine(Path.GetTempPath(), "NetCat-Scale-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new SettingsStore(root).SaveAsync(new AppSettings { InterfaceScale = scale });
            Assert.Equal(scale, new SettingsStore(root).Load().InterfaceScale);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(.69)][InlineData(1.31)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public void InvalidScaleIsRejected(double scale) => Assert.Throws<FormatException>(() => SettingsValidation.Validate(new AppSettings { InterfaceScale = scale }));
    [Fact]
    public void ExistingSettingsDefaultToOneHundredPercent() =>
        Assert.Equal(1, JsonSerializer.Deserialize<AppSettings>("{}", JsonSettings.Options)!.InterfaceScale);
    [Fact]
    public async Task SessionEndingFlushCannotBeOverwrittenByQueuedOlderScale()
    {
        var root = Path.Combine(Path.GetTempPath(), "NetCat-Scale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var preference = new InterfaceScalePreference(root);
            var pending = Enumerable.Range(0, 25).Select(i => preference.SaveAsync(i % 2 == 0 ? .7 : 1.3)).ToArray();
            preference.Flush(.85);
            await Task.WhenAll(pending);
            Assert.Equal(.85, new InterfaceScalePreference(root).Load(1));
            Assert.Empty(Directory.GetFiles(root, "*.new"));
            preference.Flush(.85);
            Assert.Equal(.85, new InterfaceScalePreference(root).Load(1));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
