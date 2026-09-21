using System.Windows;
using System.Windows.Media;
namespace NetCat.UI;
public static class Theme
{
    public static void Apply(string baseHex, string accentHex)
    {
        var surface = (Color)ColorConverter.ConvertFromString(baseHex); var accent = (Color)ColorConverter.ConvertFromString(accentHex);
        bool dark = Luminance(surface) < .35;
        Color Mix(Color a, Color b, double amount) => Color.FromRgb((byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));
        void Set(string key, Color color) => Application.Current.Resources[key] = new SolidColorBrush(color);
        Set("BaseBrush", surface); Set("PanelBrush", Mix(surface, dark ? Colors.White : Colors.White, dark ? .05 : .7));
        Set("RaisedBrush", Mix(surface, dark ? Colors.White : Colors.Black, dark ? .09 : .035));
        Set("TextBrush", dark ? Color.FromRgb(234, 239, 244) : Color.FromRgb(28, 36, 45));
        Set("MutedBrush", dark ? Color.FromRgb(164, 176, 189) : Color.FromRgb(94, 108, 122));
        Set("LineBrush", Mix(surface, dark ? Colors.White : Colors.Black, dark ? .17 : .15));
        Set("AccentBrush", accent); Set("AccentSoftBrush", Mix(surface, accent, dark ? .25 : .13));
        Set("AccentTextBrush", Luminance(accent) < .38 ? Colors.White : Colors.Black);
        Set("DangerBrush", dark ? Color.FromRgb(239, 150, 150) : Color.FromRgb(167, 53, 53));
        Application.Current.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(accent);
        Application.Current.Resources[SystemColors.HighlightTextBrushKey] = Application.Current.Resources["AccentTextBrush"];
        Application.Current.Resources[SystemColors.WindowBrushKey] = Application.Current.Resources["PanelBrush"];
        Application.Current.Resources[SystemColors.WindowTextBrushKey] = Application.Current.Resources["TextBrush"];
    }
    private static double Luminance(Color c) => (.2126 * c.R + .7152 * c.G + .0722 * c.B) / 255;
}
