using System.Windows;
using System.Windows.Media;
using NetCat.Core;
namespace NetCat.UI;
public static class Theme
{
    public static void Apply(string baseHex, string accentHex) =>
        Apply(new AppSettings { BaseColor = baseHex, AccentColor = accentHex });

    public static void Apply(AppSettings settings)
    {
        if (Application.Current == null) return;
        var scale = double.IsFinite(settings.InterfaceScale) ? Math.Clamp(settings.InterfaceScale, .7, 1.3) : 1;
        Application.Current.Resources["InterfaceScaleTransform"] = new ScaleTransform(scale, scale);
        WindowFrame.UpdateScale(scale);
        var palette = ThemePalette.Create(settings);
        void Set(string key, ThemeColor color) => Application.Current.Resources[key] =
            new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        Set("BaseBrush", palette.Base); Set("PanelBrush", palette.Panel);
        Set("RaisedBrush", palette.Raised); Set("TextBrush", palette.Text);
        Set("MutedBrush", palette.Muted); Set("LineBrush", palette.Line);
        Set("AccentBrush", palette.Accent); Set("AccentForegroundBrush", palette.AccentForeground);
        Set("AccentSoftBrush", palette.AccentSoft); Set("AccentTextBrush", palette.AccentText);
        Set("DangerBrush", palette.Danger);
        Application.Current.Resources[SystemColors.HighlightBrushKey] = Application.Current.Resources["AccentBrush"];
        Application.Current.Resources[SystemColors.HighlightTextBrushKey] = Application.Current.Resources["AccentTextBrush"];
        Application.Current.Resources[SystemColors.WindowBrushKey] = Application.Current.Resources["PanelBrush"];
        Application.Current.Resources[SystemColors.WindowTextBrushKey] = Application.Current.Resources["TextBrush"];
    }
}
