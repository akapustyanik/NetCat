namespace NetCat.Core;
public static class ColorMath
{
    public static (byte R, byte G, byte B) FromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360; saturation = Math.Clamp(saturation, 0, 1); value = Math.Clamp(value, 0, 1);
        double chroma = value * saturation, x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - chroma;
        var (r, g, b) = hue switch { < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x), < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x) };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
    public static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        double red = r / 255d, green = g / 255d, blue = b / 255d, max = Math.Max(red, Math.Max(green, blue)), min = Math.Min(red, Math.Min(green, blue)), delta = max - min;
        double hue = delta == 0 ? 0 : max == red ? 60 * ((green - blue) / delta % 6) : max == green ? 60 * ((blue - red) / delta + 2) : 60 * ((red - green) / delta + 4);
        return ((hue + 360) % 360, max == 0 ? 0 : delta / max, max);
    }
}
