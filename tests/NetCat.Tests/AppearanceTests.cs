using NetCat.Core;
using Xunit;

namespace NetCat.Tests;

public sealed class AppearanceTests
{
    [Fact]
    public void ContrastUsesLinearSrgbRatherThanByteBrightness()
    {
        Assert.Equal(.21586050011389926, ThemeColor.Parse("#808080").Luminance, 10);
        Assert.Equal(21, ThemeColor.Parse("#FFFFFF").Contrast(ThemeColor.Parse("#000000")));
    }

    [Fact]
    public void ReadabilityHoldsAcrossCustomColourSpaceAndBothPanelDirections()
    {
        var random = new Random(92741);
        for (var sample = 0; sample < 400; sample++)
        foreach (var brightness in new[] { -20, 0, 20 })
        {
            var palette = ThemePalette.Create(new AppSettings
            { BaseColor = $"#{random.Next(0x1000000):X6}", AccentColor = $"#{random.Next(0x1000000):X6}", PanelBrightness = brightness });
            foreach (var surface in new[] { palette.Base, palette.Panel, palette.Raised, palette.AccentSoft })
            foreach (var text in new[] { palette.Text, palette.Muted, palette.AccentForeground })
                Assert.True(surface.Contrast(text) >= 4.5);
        }
    }
    [Fact]
    public void ColoursAcrossFormerThemeThresholdKeepContinuousPanels()
    {
        var before = ThemePalette.Create(new AppSettings { BaseColor = "#555555" });
        var after = ThemePalette.Create(new AppSettings { BaseColor = "#666666" });
        Assert.InRange(after.Panel.R - before.Panel.R, 0, 20);
        Assert.InRange(after.Panel.G - before.Panel.G, 0, 20);
        Assert.InRange(after.Panel.B - before.Panel.B, 0, 20);
    }

    [Theory]
    [InlineData("#535070", "#B58787")]
    [InlineData("#7B75B8", "#B58787")]
    [InlineData("#000000", "#000000")]
    [InlineData("#FFFFFF", "#FFFFFF")]
    [InlineData("#777777", "#FFFFFF")]
    [InlineData("#777777", "#000000")]
    [InlineData("#00FF00", "#FFFF00")]
    [InlineData("#0000FF", "#0000FF")]
    public void CustomColoursKeepReadableTextAndOriginalAccent(string background, string accent)
    {
        foreach (var brightness in new[] { -20, 0, 6, 20 })
        foreach (var enhanced in new[] { false, true })
        {
            var palette = ThemePalette.Create(new AppSettings
            { BaseColor = background, AccentColor = accent, PanelBrightness = brightness, HighContrastText = enhanced });
            Assert.Equal(ThemeColor.Parse(background), palette.Base);
            Assert.Equal(ThemeColor.Parse(accent), palette.Accent);
            foreach (var surface in new[] { palette.Base, palette.Panel, palette.Raised, palette.AccentSoft })
            foreach (var foreground in new[] { palette.Text, palette.Muted, palette.AccentForeground })
                Assert.True(surface.Contrast(foreground) >= 4.5, $"{background}, {brightness}, {enhanced}: {surface.Contrast(foreground)}");
            Assert.True(palette.Accent.Contrast(palette.AccentText) >= 4.5);
        }
    }

    [Fact]
    public void EnhancedContrastIncreasesCaptionReadabilityWithoutChangingChosenColours()
    {
        var settings = new AppSettings { BaseColor = "#151A22", AccentColor = "#B58787" };
        var normal = ThemePalette.Create(settings);
        settings.HighContrastText = true;
        var enhanced = ThemePalette.Create(settings);
        Assert.Equal(normal.Base, enhanced.Base);
        Assert.Equal(normal.Accent, enhanced.Accent);
        Assert.True(enhanced.Panel.Contrast(enhanced.Muted) >= 7);
        Assert.True(enhanced.Panel.Contrast(enhanced.Muted) > normal.Panel.Contrast(normal.Muted));
    }

    [Fact]
    public void AppearanceRoundTripAndOldSettingsDefaults()
    {
        var settings = JsonSettings.Clone(new AppSettings { PanelBrightness = -12, HighContrastText = true });
        Assert.Equal(-12, settings.PanelBrightness);
        Assert.True(settings.HighContrastText);
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"BaseColor\":\"#535070\",\"AccentColor\":\"#B58787\"}", JsonSettings.Options)!;
        Assert.Equal(6, old.PanelBrightness);
        Assert.False(old.HighContrastText);
        SettingsValidation.Validate(old);
    }
}
