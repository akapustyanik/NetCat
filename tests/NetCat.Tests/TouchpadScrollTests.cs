using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetCat.Core;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;
public sealed partial class Candidate32UiAuditTests
{
    private static void Scroll(FrameworkElement element, double distance) =>
        Assert.Equal(true, typeof(SmoothScroll).GetMethod("ScrollBy", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [element, distance]));
    private static ScrollViewer Viewer(UIElement content, double height = 200) => new()
    { Content = content, Height = height, Width = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    [Fact]
    public Task PrecisionDeltasAccumulateWithoutDestinationJumpOrRestart() => Sta.Run(async () =>
    {
        var viewer = Viewer(new Border { Height = 2000 });
        SmoothScroll.SetEnabled(viewer, true);
        var window = new Window { Content = viewer, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show(); viewer.UpdateLayout();
            for (var i = 0; i < 24; i++) Scroll(viewer, 2);
            if (SystemParameters.ClientAreaAnimation) Assert.Equal(0, viewer.VerticalOffset);
            double previous = 0;
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(10); viewer.UpdateLayout();
                Assert.InRange(viewer.VerticalOffset, previous, 48.01);
                previous = viewer.VerticalOffset;
            }
            Assert.InRange(viewer.VerticalOffset, 47.8, 48.01);
            Scroll(viewer, -24);
            await Task.Delay(350); viewer.UpdateLayout();
            Assert.InRange(viewer.VerticalOffset, 23.8, 24.2);
        }
        finally { window.Close(); }
    });
    [Fact]
    public Task WheelTargetsInnerListThenParentAtBoundary() => Sta.Run(async () =>
    {
        var child = new Border { Height = 800 };
        var inner = Viewer(child, 120);
        var content = new StackPanel(); content.Children.Add(inner); content.Children.Add(new Border { Height = 800 });
        var outer = Viewer(content);
        SmoothScroll.SetEnabled(outer, true); SmoothScroll.SetEnabled(inner, true);
        var window = new Window { Content = outer, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show(); outer.UpdateLayout();
            void Wheel() => child.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Wheel(); await Task.Delay(350); outer.UpdateLayout();
            Assert.True(inner.VerticalOffset > 0); Assert.Equal(0, outer.VerticalOffset);
            inner.ScrollToBottom(); inner.UpdateLayout(); Wheel();
            await Task.Delay(350); outer.UpdateLayout();
            Assert.True(outer.VerticalOffset > 0);
        }
        finally { window.Close(); }
    });
    [Fact]
    public Task ScalePreviewResizesContentAndChromeAndPersistsWithoutNetworkChanges() => Sta.Run(async () =>
    {
        var store = new SettingsStore(root);
        using var vm = new MainViewModel(store, new AppSettings());
        var networking = JsonSettings.Clone(vm.DesiredState);
        var window = new MainWindow(vm);
        try
        {
            ((TabControl)window.FindName("Pages")).SelectedIndex = 5;
            window.Show(); window.UpdateLayout();
            var slider = Assert.IsType<Slider>(window.FindName("InterfaceScaleSlider"));
            slider.Value = .8; window.UpdateLayout();
            Assert.Equal(.8, vm.State.InterfaceScale);
            var frame = Assert.IsType<Grid>(((Border)window.Content).Child);
            Assert.Equal(.8, Assert.IsType<ScaleTransform>(frame.LayoutTransform).ScaleX);
            Assert.Equal(38 * .8, System.Windows.Shell.WindowChrome.GetWindowChrome(window).CaptionHeight);
            Assert.Equal(820 * .8, window.MinWidth);
            await vm.SaveAsync();
            Assert.Equal(.8, new SettingsStore(root).Load().InterfaceScale);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(networking, JsonSettings.Options), System.Text.Json.JsonSerializer.Serialize(vm.DesiredState, JsonSettings.Options));
            if (Environment.GetEnvironmentVariable("NETCAT_SCALE_RENDER_DIR") is { } output)
            {
                Directory.CreateDirectory(output);
                window.Width = 960; window.Height = 650; window.UpdateLayout();
                var content = (FrameworkElement)window.Content;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(content.ActualWidth * 1.5), (int)Math.Ceiling(content.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "scale-80-at-150-dpi.png"));
                encoder.Save(file);
            }
        }
        finally { Theme.Apply(new AppSettings()); window.Close(); }
    });
    [Theory]
    [InlineData(.7)]
    [InlineData(1.3)]
    public Task DropdownUsesExactlyOneInterfaceScale(double scale) => Sta.Run(async () =>
    {
        Theme.Apply(new AppSettings { InterfaceScale = scale });
        var combo = new ComboBox { Width = 240, ItemsSource = new[] { "Finland", "Germany" }, SelectedIndex = 0 };
        var window = new Window { Content = combo, Width = 400, Height = 200 };
        WindowFrame.Apply(window);
        try
        {
            window.Show(); window.UpdateLayout();
            combo.IsDropDownOpen = true;
            await Task.Delay(100); window.UpdateLayout();
            var item = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(0));
            var itemPixel = item.PointToScreen(new Point(10, 0)).X - item.PointToScreen(new Point()).X;
            var comboPixel = combo.PointToScreen(new Point(10, 0)).X - combo.PointToScreen(new Point()).X;
            Assert.InRange(itemPixel / comboPixel, .98, 1.02);
        }
        finally { combo.IsDropDownOpen = false; window.Close(); Theme.Apply(new AppSettings()); }
    });
    [Fact]
    public Task ProfileListPreservesPixelScrollingAndVirtualization() => Sta.Run(async () =>
    {
        var list = new ListBox { Style = (Style)Application.Current!.FindResource("CardList"),
            ItemsSource = Enumerable.Range(0, 500).Select(i => $"Profile {i}").ToArray() };
        var window = new Window { Content = list, Width = 500, Height = 300 };
        try
        {
            window.Show(); window.UpdateLayout();
            var viewer = Assert.IsType<ScrollViewer>(typeof(SmoothScroll).GetMethod("FindViewer", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [list]));
            Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
            Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
            for (var i = 0; i < 24; i++) Scroll(list, 2);
            await Task.Delay(450); window.UpdateLayout();
            Assert.InRange(viewer.VerticalOffset, 47.8, 48.2);
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(499));
        }
        finally { window.Close(); }
    });
    [Fact]
    public Task ScaleSliderChoosesClickedPositionAndCommandsChangeOnlyFivePercent() => Sta.Run(() =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
        var window = new MainWindow(vm);
        try
        {
            ((TabControl)window.FindName("Pages")).SelectedIndex = 5;
            window.Show(); window.UpdateLayout();
            var slider = Assert.IsType<Slider>(window.FindName("InterfaceScaleSlider"));
            Assert.True(slider.IsMoveToPointEnabled);
            slider.Value = .9;
            Slider.IncreaseLarge.Execute(null, slider); window.UpdateLayout();
            Assert.Equal(.95, slider.Value, 5);
            Slider.DecreaseLarge.Execute(null, slider); window.UpdateLayout();
            Assert.Equal(.9, slider.Value, 5);
            var track = Assert.IsType<System.Windows.Controls.Primitives.Track>(slider.Template.FindName("PART_Track", slider));
            var middle = track.ValueFromPoint(new Point(track.ActualWidth / 2, track.ActualHeight / 2));
            Assert.InRange(middle, .99, 1.01);
        }
        finally { window.Close(); Theme.Apply(new AppSettings()); }
        return Task.CompletedTask;
    });
    [Fact]
    public Task IncreasingScaleKeepsWindowInsideCurrentMonitorWorkArea() => Sta.Run(() =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings { InterfaceScale = .7 });
        Theme.Apply(vm.State);
        var window = new MainWindow(vm);
        try
        {
            window.Show(); window.UpdateLayout();
            Rect Work() => (Rect)typeof(WindowFrame).GetMethod("GetWorkArea", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window])!;
            var work = Work();
            window.Left = work.Right - window.Width;
            window.Top = work.Bottom - window.Height;
            // Native suites use smoke mode to protect the host network and to
            // allow intentionally off-screen screenshot windows. Only this
            // synchronous geometry update exercises the visible-window policy.
            var smoke = App.IsSmoke;
            try
            {
                typeof(App).GetProperty(nameof(App.IsSmoke))!.SetValue(null, false);
                Theme.Apply(new AppSettings { InterfaceScale = 1.3 }); window.UpdateLayout();
            }
            finally { typeof(App).GetProperty(nameof(App.IsSmoke))!.SetValue(null, smoke); }
            work = Work();
            Assert.InRange(window.Left, work.Left, work.Right - window.Width + .01);
            Assert.InRange(window.Top, work.Top, work.Bottom - window.Height + .01);
            Assert.True(window.Width <= work.Width && window.Height <= work.Height);
        }
        finally { window.Close(); Theme.Apply(new AppSettings()); }
        return Task.CompletedTask;
    });
    [Fact]
    public Task ScrollingDuringLogBatchesKeepsInputResponsiveAndLogBounded() => Sta.Run(async () =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
        vm.Logs.Prepend(Enumerable.Range(0, 2000).Select(i => $"Synthetic log {i}: " + new string('x', 200)));
        var list = new ListBox { ItemsSource = vm.Logs, FontSize = 12, FontFamily = new FontFamily("Consolas") };
        var window = new Window { Content = list, Width = 650, Height = 350 };
        try
        {
            window.Show(); window.UpdateLayout();
            var changes = 0;
            vm.Logs.CollectionChanged += (_, _) => changes++;
            var delays = new List<double>();
            for (var i = 0; i < 35; i++)
            {
                Scroll(list, 2);
                vm.WriteLog("Synthetic scroll/update event " + i);
                var started = System.Diagnostics.Stopwatch.StartNew();
                await window.Dispatcher.InvokeAsync(() => delays.Add(started.Elapsed.TotalMilliseconds), System.Windows.Threading.DispatcherPriority.Input);
                await Task.Delay(20);
            }
            Assert.True(changes > 0);
            Assert.InRange(vm.Logs.Count, 1, 2000);
            Assert.True(delays.Max() < 750, $"Input dispatch took {delays.Max():F1} ms while scrolling and receiving log batches.");
        }
        finally { window.Close(); }
    });
    [Fact]
    public Task PrecisionDeltaIsAppliedOnNextFrameWithoutAnExtraEasingTail() => Sta.Run(async () =>
    {
        var viewer = Viewer(new Border { Height = 2000 });
        SmoothScroll.SetEnabled(viewer, true);
        var window = new Window { Content = viewer, SizeToContent = SizeToContent.WidthAndHeight };
        EventHandler? rendered = null;
        try
        {
            window.Show(); viewer.UpdateLayout();
            var step = (SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight * .85 : SystemParameters.WheelScrollLines * 16) / 120d;
            var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -1) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            viewer.RaiseEvent(args);
            var nextFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rendered = (_, _) => { CompositionTarget.Rendering -= rendered; nextFrame.TrySetResult(); };
            CompositionTarget.Rendering += rendered;
            await nextFrame.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await viewer.Dispatcher.InvokeAsync(() => viewer.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.InRange(viewer.VerticalOffset, step - .01, step + .01);
        }
        finally { CompositionTarget.Rendering -= rendered; window.Close(); }
    });
    [Fact]
    public Task ContinuousPrecisionGestureSurvivesRecyclingAndPauseWithoutNewTouch() => Sta.Run(async () =>
    {
        var list = new ListBox { Style = (Style)Application.Current!.FindResource("CardList"),
            ItemsSource = Enumerable.Range(0, 2000).Select(i => $"Profile {i}").ToArray() };
        var window = new Window { Content = list, Width = 500, Height = 300 };
        try
        {
            window.Show(); window.UpdateLayout();
            var viewer = Assert.IsType<ScrollViewer>(typeof(SmoothScroll).GetMethod("FindViewer", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [list]));
            var step = (SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight * .85 : SystemParameters.WheelScrollLines * 16) / 120d;
            for (var batch = 0; batch < 12; batch++)
            {
                for (var i = 0; i < 50; i++)
                {
                    var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -1) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    viewer.RaiseEvent(args);
                    Assert.True(args.Handled);
                }
                await Task.Delay(batch == 5 ? 300 : 20); window.UpdateLayout();
            }
            await Task.Delay(120); window.UpdateLayout();
            Assert.InRange(viewer.VerticalOffset, 600 * step - 1, 600 * step + 1);
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(1999));
        }
        finally { window.Close(); }
    });
    [Fact]
    public Task ScaleAutomaticallyPersistsWithoutSavingUnrelatedDrafts() => Sta.Run(async () =>
    {
        var store = new SettingsStore(root);
        await store.SaveAsync(new AppSettings());
        using var vm = new MainViewModel(store, store.Load());
        var window = new MainWindow(vm);
        try
        {
            ((TabControl)window.FindName("Pages")).SelectedIndex = 5;
            window.Show(); window.UpdateLayout();
            vm.State.InterfaceScale = .8;
            await vm.PendingInterfaceScaleSave;
            vm.AssertCommittedInvariant();
            vm.State.TestIntervalSeconds = 2;
            var desired = System.Text.Json.JsonSerializer.Serialize(vm.DesiredState, JsonSettings.Options);
            var slider = Assert.IsType<Slider>(window.FindName("InterfaceScaleSlider"));
            slider.Value = .75; slider.Value = 1.25; slider.Value = .8;
            await vm.PendingInterfaceScaleSave;
            Assert.Equal(.8, new SettingsStore(root).Load().InterfaceScale);
            Assert.Equal(15, new SettingsStore(root).Load().TestIntervalSeconds);
            Assert.Equal(2, vm.State.TestIntervalSeconds);
            Assert.Equal(desired, System.Text.Json.JsonSerializer.Serialize(vm.DesiredState, JsonSettings.Options));
            await vm.UpdateSettingsAsync(next => next.HighContrastText = true);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(.8, vm.State.InterfaceScale);
            Assert.Equal(.8, new SettingsStore(root).Load().InterfaceScale);
            // A replacement EXE still uses this user's external preferences.
            using var reopened = new MainViewModel(new SettingsStore(root), new SettingsStore(root).Load());
            Assert.Equal(.8, reopened.State.InterfaceScale);
        }
        finally { window.Close(); Theme.Apply(new AppSettings()); }
    });
}
