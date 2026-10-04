using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetCat.Core;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed partial class Candidate32UiAuditTests
{
    [Theory]
    [InlineData(760, false)]
    [InlineData(760, true)]
    [InlineData(1000, false)]
    [InlineData(1000, true)]
    public Task OpenVpnHomeButtonsHaveEqualHeightAndVerticalAlignment(double width, bool light) => Sta.Run(() =>
    {
        var settings = new AppSettings { BaseColor = light ? "#F4F6F8" : "#171717", AccentColor = "#FF0000" };
        Theme.Apply(settings);
        using var vm = new MainViewModel(new SettingsStore(root), settings);
        var window = new MainWindow(vm);
        try
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, 940));
            content.Arrange(new Rect(0, 0, width, 940));
            content.UpdateLayout();
            var connect = Assert.IsType<Button>(window.FindName("HomeOpenVpnButton"));
            var domains = Assert.IsType<Button>(window.FindName("HomeOpenVpnDomainsButton"));
            var panel = Assert.IsType<StackPanel>(connect.Parent);
            var a = connect.TranslatePoint(new Point(), panel);
            var b = domains.TranslatePoint(new Point(), panel);
            Assert.Equal(a.Y, b.Y, 4);
            Assert.Equal(connect.ActualHeight, domains.ActualHeight, 4);
            Assert.Equal(48, connect.ActualHeight, 4);
            Assert.True(connect.ActualWidth >= 168);
            Assert.True(domains.ActualWidth >= 100);
            Assert.InRange(b.X - a.X - connect.ActualWidth, 8, 9);
            Assert.True(panel.TranslatePoint(new Point(panel.ActualWidth, 0), content).X <= width);
            foreach (var label in new[] { "Подключить OpenVPN", "Отключить OpenVPN", "Отменить подключение" })
            {
                connect.SetCurrentValue(ContentControl.ContentProperty, label);
                content.Measure(new Size(width, 940));
                content.Arrange(new Rect(0, 0, width, 940));
                content.UpdateLayout();
                Assert.Equal(48, connect.ActualHeight, 4);
                Assert.Equal(connect.ActualHeight, domains.ActualHeight, 4);
                Assert.Equal(connect.TranslatePoint(new Point(), panel).Y, domains.TranslatePoint(new Point(), panel).Y, 4);
                Assert.True(connect.ActualWidth >= connect.DesiredSize.Width - connect.Margin.Left - connect.Margin.Right);
                Assert.True(panel.TranslatePoint(new Point(panel.ActualWidth, 0), content).X <= width);
                var grid = Assert.IsType<Grid>(panel.Parent);
                var profile = Assert.IsType<StackPanel>(grid.Children[0]);
                Assert.True(profile.ActualWidth >= 280);
                Assert.True(profile.TranslatePoint(new Point(profile.ActualWidth, 0), grid).X + profile.Margin.Right <= panel.TranslatePoint(new Point(), grid).X);
            }
            var output = Environment.GetEnvironmentVariable("NETCAT_TRAY_RENDER_DIR");
            if (output != null)
            {
                Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap((int)width, 940, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"home-openvpn-{width}-{(light ? "light" : "dark")}.png"));
                encoder.Save(file);
            }
            return Task.CompletedTask;
        }
        finally { Theme.Apply(new AppSettings()); window.Close(); }
    });
}
