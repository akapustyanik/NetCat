namespace NetCat.Core;

public readonly record struct ThemeColor(byte R, byte G, byte B)
{
    public static ThemeColor Parse(string hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#' ||
            !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            throw new FormatException("Цвет должен иметь формат #RRGGBB.");
        return new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    public double Luminance
    {
        get
        {
            static double Linear(byte value)
            {
                var channel = value / 255d;
                return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
            }
            return .2126 * Linear(R) + .7152 * Linear(G) + .0722 * Linear(B);
        }
    }

    public double Contrast(ThemeColor other) =>
        (Math.Max(Luminance, other.Luminance) + .05) / (Math.Min(Luminance, other.Luminance) + .05);

    public ThemeColor Mix(ThemeColor other, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new((byte)Math.Round(R + (other.R - R) * amount),
            (byte)Math.Round(G + (other.G - G) * amount),
            (byte)Math.Round(B + (other.B - B) * amount));
    }
}

/// <summary>Surface colours follow the chosen colour continuously; no light/dark surface threshold.</summary>
public sealed record ThemePalette(ThemeColor Base, ThemeColor Panel, ThemeColor Raised,
    ThemeColor Text, ThemeColor Muted, ThemeColor Line, ThemeColor Accent,
    ThemeColor AccentForeground, ThemeColor AccentSoft, ThemeColor AccentText, ThemeColor Danger)
{
    public static ThemePalette Create(AppSettings settings)
    {
        var surface = ThemeColor.Parse(settings.BaseColor);
        var accent = ThemeColor.Parse(settings.AccentColor);
        var black = new ThemeColor(0, 0, 0);
        var white = new ThemeColor(255, 255, 255);
        // Polarity only affects text, never switches the surface to a different theme.
        var text = surface.Contrast(black) >= surface.Contrast(white) ? black : white;
        var required = Math.Min(settings.HighContrastText ? 7 : 4.5, surface.Contrast(text));
        var amount = Math.Clamp(settings.PanelBrightness, -20, 20) / 100;
        var panel = LimitSurface(surface.Mix(amount >= 0 ? white : black, Math.Abs(amount)));
        var raised = LimitSurface(panel.Mix(text, .035));
        var soft = LimitSurface(panel.Mix(accent, .12));
        ThemeColor[] backgrounds = [surface, panel, raised, soft];
        var muted = Readable(surface.Mix(text, .6), text, required, backgrounds);
        var accentForeground = Readable(accent, text, required, backgrounds);
        var line = Readable(surface.Mix(text, .2), text, 3, [surface, panel]);
        var accentText = accent.Contrast(black) >= accent.Contrast(white) ? black : white;
        var danger = Readable(new ThemeColor(190, 65, 65), text, required, backgrounds);
        return new(surface, panel, raised, text, muted, line, accent, accentForeground, soft, accentText, danger);

        ThemeColor LimitSurface(ThemeColor candidate)
        {
            if (candidate.Contrast(text) >= required) return candidate;
            // Keep arbitrary custom colours readable even around the text-polarity boundary.
            for (var step = 1; step <= 100; step++)
            {
                var adjusted = candidate.Mix(surface, step / 100d);
                if (adjusted.Contrast(text) >= required) return adjusted;
            }
            return surface;
        }
    }

    private static ThemeColor Readable(ThemeColor candidate, ThemeColor text, double ratio,
        IReadOnlyList<ThemeColor> backgrounds)
    {
        for (var step = 0; step <= 100; step++)
        {
            var adjusted = candidate.Mix(text, step / 100d);
            if (backgrounds.All(background => adjusted.Contrast(background) >= ratio)) return adjusted;
        }
        return text;
    }
}
