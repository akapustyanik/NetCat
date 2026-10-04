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
    [InlineData(430)]
    [InlineData(760)]
    public Task WrappedActionButtonsHaveAtLeastEightPixelsBetweenRows(double width) => Sta.Run(() =>
    {
        var panel = new WrapPanel { Width = width };
        foreach (var label in new[] { "+ Правило", "Добавить EXE", "Из запущенных", "Список доменов", "Добавить стандартный пресет", "Изменить", "Удалить", "↑", "↓" })
            panel.Children.Add(new Button { Content = label, Style = (Style)Application.Current!.FindResource(typeof(Button)) });
        panel.Measure(new Size(width, 500));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        panel.UpdateLayout();
        var rowGapFound = false;
        for (var i = 1; i < panel.Children.Count; i++)
        {
            var previous = (Button)panel.Children[i - 1];
            var current = (Button)panel.Children[i];
            var oldPosition = previous.TranslatePoint(new Point(), panel);
            var position = current.TranslatePoint(new Point(), panel);
            if (position.Y <= oldPosition.Y) continue;
            rowGapFound = true;
            Assert.True(position.Y - oldPosition.Y - previous.ActualHeight >= 8);
        }
        Assert.True(rowGapFound);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AppearanceControlsRenderAndSaveWithoutChangingDesiredNetworking() => Sta.Run(async () =>
    {
        var store = new SettingsStore(root);
        using var vm = new MainViewModel(store, new AppSettings());
        var before = JsonSettings.Clone(vm.DesiredState);
        var window = new MainWindow(vm);
        try
        {
            ((TabControl)window.FindName("Pages")).SelectedIndex = 5;
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(820, 760));
            content.Arrange(new Rect(0, 0, 820, 760));
            content.UpdateLayout();
            var slider = Assert.IsType<Slider>(window.FindName("PanelBrightnessSlider"));
            var toggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(window.FindName("HighContrastSwitch"));
            Assert.True(slider.ActualWidth > 150);
            Assert.True(toggle.ActualWidth > 0);
            slider.Value = -12;
            toggle.IsChecked = true;
            await vm.SaveAsync();
            Assert.Equal(-12, store.Load().PanelBrightness);
            Assert.True(store.Load().HighContrastText);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before, JsonSettings.Options),
                System.Text.Json.JsonSerializer.Serialize(vm.DesiredState, JsonSettings.Options));
            var output = Environment.GetEnvironmentVariable("NETCAT_APPEARANCE_RENDER_DIR");
            if (output != null)
            {
                Directory.CreateDirectory(output);
                foreach (var colour in new[] { "#535070", "#7B75B8", "#F4F6F8" })
                {
                    vm.State.BaseColor = colour;
                    vm.State.AccentColor = "#B58787";
                    Theme.Apply(vm.State);
                    content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(820, 760, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, "appearance-" + colour[1..] + ".png"));
                    encoder.Save(file);
                }
            }
        }
        finally { Theme.Apply(new AppSettings()); window.Close(); }
    });
}
