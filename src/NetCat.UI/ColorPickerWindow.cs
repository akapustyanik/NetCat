using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NetCat.Core;

namespace NetCat.UI;
public sealed class ColorPickerWindow : Window
{
    private readonly Grid plane = new() { Height = 235, Focusable = true, ClipToBounds = true, Cursor = Cursors.Cross };
    private readonly Ellipse marker = new() { Width = 15, Height = 15, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent, IsHitTestVisible = false };
    private readonly Canvas markerLayer = new() { IsHitTestVisible = false };
    private readonly Slider hue = new() { Minimum = 0, Maximum = 359.99, Height = 30, Margin = new(0, 12, 0, 12) };
    private readonly Border preview = new() { Height = 40, CornerRadius = new(8), Margin = new(8, 0, 0, 0) };
    private double saturation, value;
    public string SelectedHex { get; private set; } = "";
    public ColorPickerWindow(Window owner, string title, string initial, bool primary)
    {
        Owner = owner; Title = title; Width = 520; Height = 600; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(24) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true }; var accept = new Button { Content = "Выбрать цвет", IsDefault = true }; accept.SetResourceReference(StyleProperty, "PrimaryButton");
        accept.Click += (_, _) => DialogResult = true; actions.Children.Add(cancel); actions.Children.Add(accept); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var body = new StackPanel(); root.Children.Add(body);
        var label = new TextBlock { Text = "Выберите оттенок, затем яркость и насыщенность", Margin = new(0, 0, 0, 16) }; label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); body.Children.Add(label);
        plane.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.White, Colors.Transparent, 0), IsHitTestVisible = false });
        plane.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90), IsHitTestVisible = false });
        markerLayer.Children.Add(marker); plane.Children.Add(markerLayer); body.Children.Add(plane);
        hue.Background = new LinearGradientBrush(new GradientStopCollection { new(Colors.Red,0), new(Colors.Yellow,1d/6), new(Colors.Lime,2d/6), new(Colors.Cyan,.5), new(Colors.Blue,4d/6), new(Colors.Magenta,5d/6), new(Colors.Red,1) }, 0);
        System.Windows.Automation.AutomationProperties.SetName(hue, "Оттенок цвета");
        System.Windows.Automation.AutomationProperties.SetName(plane, "Насыщенность и яркость. Стрелки изменяют цвет.");
        body.Children.Add(hue);
        hue.SetResourceReference(StyleProperty,"GradientSlider");
        // Capture the whole strip so a press on the track becomes the start of a drag.
        void PickHue(MouseEventArgs e)
        {
            var track = hue.Template.FindName("PART_Track", hue) as Track;
            var inset = (track?.Thumb?.ActualWidth ?? 18) / 2;
            hue.Value = hue.Maximum * Math.Clamp((e.GetPosition(hue).X - inset) / Math.Max(1, hue.ActualWidth - inset * 2), 0, 1);
        }
        hue.PreviewMouseLeftButtonDown += (_, e) => { hue.Focus(); hue.CaptureMouse(); PickHue(e); e.Handled = true; };
        hue.PreviewMouseMove += (_, e) => { if (hue.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) { PickHue(e); e.Handled = true; } };
        hue.PreviewMouseLeftButtonUp += (_, e) => { if (hue.IsMouseCaptured) { PickHue(e); hue.ReleaseMouseCapture(); e.Handled = true; } };
        var swatches = new UniformGrid { Columns = 2, Margin = new(0, 0, 0, 16) };
        swatches.Children.Add(new Border { Height = 40, CornerRadius = new(8), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(initial)), ToolTip = "Текущий цвет" });
        preview.ToolTip = "Выбранный цвет"; swatches.Children.Add(preview); body.Children.Add(swatches);
        var presets = new WrapPanel(); body.Children.Add(presets);
        var colors = primary ? new[] { "#F4F6F8", "#151A22", "#F5F0E8", "#241E2B", "#EAF0F4", "#172321" } : new[] { "#3975D6", "#8D55D6", "#D05A32", "#258366", "#C64A7E", "#D3A239" };
        var names = primary ? new[] { "Светлый", "Графит", "Песочный", "Сливовый", "Серый", "Хвойный" } : new[] { "Синий", "Фиолетовый", "Терракота", "Зелёный", "Розовый", "Золотой" };
        for (int i = 0; i < colors.Length; i++)
        {
            var hex = colors[i]; var b = new Button { Width = 62, Height = 35, Padding = new(0), Margin = new(0, 0, 8, 0), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), ToolTip = names[i] };
            System.Windows.Automation.AutomationProperties.SetName(b, names[i]); b.Click += (_, _) => Select(hex); presets.Children.Add(b);
        }
        plane.MouseLeftButtonDown += (_, e) => { plane.Focus(); plane.CaptureMouse(); Pick(e.GetPosition(plane)); };
        plane.MouseMove += (_, e) => { if (plane.IsMouseCaptured) Pick(e.GetPosition(plane)); };
        plane.MouseLeftButtonUp += (_, _) => plane.ReleaseMouseCapture();
        plane.KeyDown += (_, e) => { double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .1 : .01; if (e.Key == Key.Left) saturation -= step; else if (e.Key == Key.Right) saturation += step; else if (e.Key == Key.Up) value += step; else if (e.Key == Key.Down) value -= step; else return; saturation = Math.Clamp(saturation,0,1); value = Math.Clamp(value,0,1); Update(); e.Handled = true; };
        hue.ValueChanged += (_, _) => Update(); plane.SizeChanged += (_, _) => Update(); Content = root; Select(initial); WindowFrame.Apply(this);
    }
    private void Select(string hex) { var c = (Color)ColorConverter.ConvertFromString(hex); var hsv = ColorMath.ToHsv(c.R,c.G,c.B); saturation = hsv.S; value = hsv.V; hue.Value = hsv.H; Update(); }
    private void Pick(Point p) { saturation = Math.Clamp(p.X / plane.ActualWidth, 0, 1); value = 1 - Math.Clamp(p.Y / plane.ActualHeight, 0, 1); Update(); }
    private void Update()
    {
        var pure = ColorMath.FromHsv(hue.Value,1,1); plane.Background = new SolidColorBrush(Color.FromRgb(pure.R,pure.G,pure.B));
        var c = ColorMath.FromHsv(hue.Value,saturation,value); var color = Color.FromRgb(c.R,c.G,c.B); preview.Background = new SolidColorBrush(color); SelectedHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        Canvas.SetLeft(marker, saturation * plane.ActualWidth - 7.5); Canvas.SetTop(marker, (1 - value) * plane.ActualHeight - 7.5);
    }
}
