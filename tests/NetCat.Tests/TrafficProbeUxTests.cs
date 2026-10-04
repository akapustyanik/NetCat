using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NetCat.Core;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed class TrafficProbeEndpointTests
{
    [Fact]
    public void DefaultWebChecksUseTwoIndependentConnectivityEndpoints()
    {
        var web = Assert.IsAssignableFrom<IReadOnlyList<Uri>>(TrafficProbeTargets.Default.Web);
        Assert.Equal(2, web.Count);
        Assert.Equal(new[] { "www.gstatic.com", "cp.cloudflare.com" }, web.Select(w => w.Host));
        Assert.All(web, w => { Assert.Equal("https", w.Scheme); Assert.Equal("/generate_204", w.AbsolutePath); });
        Assert.DoesNotContain(web, w => w.Host.Contains("2ip", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(204, TrafficProbeTargets.Default.WebExpectedStatusCode);
    }
}

public sealed partial class Candidate32TrafficAndDisplayTests
{
    private static TrafficProbeTargets StrictWebTargets(TrafficFixture fixture, int status) => fixture.Targets with
    {
        Web = [new(fixture.Targets.Download, "/web-status/204"), new(fixture.Targets.Download, $"/web-status/{status}?control=2")],
        WebExpectedStatusCode = 204
    };

    [Theory]
    [InlineData(204, true)]
    [InlineData(200, false)]
    [InlineData(302, false)]
    [InlineData(503, false)]
    public async Task ConnectivityChecksRequireExpectedResponseThroughSameSocks(int status, bool passes)
    {
        await using var fixture = new TrafficFixture();
        var result = await ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, CancellationToken.None, StrictWebTargets(fixture, status));
        Assert.True(result.Download.Success);
        Assert.True(result.Upload.Success);
        Assert.Equal(ProfileTrafficProbe.DownloadBytes, result.Download.ReceivedBytes);
        Assert.Equal(ProfileTrafficProbe.UploadBytes, fixture.UploadReceived);
        Assert.Equal(8, result.UdpReceived);
        Assert.Equal(passes, result.Success);
        Assert.Equal(2, result.WebChecks!.Count);
        Assert.True(result.WebChecks[0].Success);
        Assert.Equal(passes, result.WebChecks[1].Success);
        Assert.Equal(status, result.WebChecks[1].StatusCode);
        Assert.Equal(4, fixture.ConnectCount); // Redirect must not add another SOCKS connection.
        Assert.Contains(passes ? "сайты 2/2" : "сайты 1/2", result.Summary);
        if(status == 200) Assert.Contains("ожидался 204", result.WebChecks[1].Error);
    }

    [Fact]
    public async Task HealthyWebsitesDoNotMaskFailedRealUpload()
    {
        await using var fixture = new TrafficFixture { CorruptEcho = true };
        var result = await ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, CancellationToken.None, StrictWebTargets(fixture, 204));
        Assert.All(result.WebChecks!, w => Assert.True(w.Success));
        Assert.True(result.Download.Success);
        Assert.Equal(8, result.UdpReceived);
        Assert.False(result.Upload.Success);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task WebChecksNeverFallBackToDirectWhenSocksIsUnavailable()
    {
        await using var fixture = new TrafficFixture();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var result = await ProfileTrafficProbe.MeasureAsync(port, CancellationToken.None, StrictWebTargets(fixture, 204));
        Assert.False(result.Success);
        Assert.Equal(2, result.WebChecks!.Count);
        Assert.All(result.WebChecks, w => { Assert.False(w.Success); Assert.Equal(0, w.StatusCode); });
        Assert.Equal(0, fixture.ConnectCount);
        Assert.Equal(0, fixture.HttpReceived);
    }
}

public sealed partial class Candidate32UiAuditTests
{
    private static IEnumerable<T> ProbeUxChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for(var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if(child is T match) yield return match;
            foreach(var nested in ProbeUxChildren<T>(child)) yield return nested;
        }
    }

    private static FrameworkElement ArrangeProbeUx(MainWindow window, double width)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, 620));
        content.Arrange(new Rect(0, 0, width, 620));
        content.UpdateLayout();
        return content;
    }

    [Fact]
    public Task ConnectionPageOmitsTrafficAndRouteApplyButtonsButProfilesKeepChecks() => Sta.Run(() =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
        var window = new MainWindow(vm);
        try
        {
            var pages = (TabControl)window.FindName("Pages");
            pages.SelectedIndex = 0;
            var content = ArrangeProbeUx(window, 820);
            var buttons = ProbeUxChildren<Button>(content).Select(b => b.Content as string).ToArray();
            Assert.DoesNotContain("Проверить трафик текущего TUN", buttons);
            Assert.DoesNotContain("Применить маршруты", buttons);
            Assert.Contains(ProbeUxChildren<TextBlock>(content), t => t.Text.StartsWith("HTTP / SOCKS5"));
            pages.SelectedIndex = 1;
            ArrangeProbeUx(window, 820);
            buttons = ProbeUxChildren<Button>(content).Select(b => b.Content as string).ToArray();
            Assert.Contains("Трафик выбранного", buttons);
            Assert.Contains("Трафик всех", buttons);
            pages.SelectedIndex = 2;
            ArrangeProbeUx(window, 820);
            Assert.Contains(ProbeUxChildren<Button>(content), b => (string?)b.Content == "Применить правила");
            Assert.Equal(0, vm.Router.StartCount);
            Assert.False(vm.Router.VpnRunning);
            return Task.CompletedTask;
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task DisconnectedConnectionPageDoesNotShowStaleTunDiagnostic() => Sta.Run(() =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
        var window = new MainWindow(vm);
        try
        {
            var content = ArrangeProbeUx(window, 820);
            // The user removed this diagnostic line entirely (also when ON).
            Assert.DoesNotContain(ProbeUxChildren<TextBlock>(content), t => t.Text.StartsWith("Текущий TUN:"));
            Assert.Equal(0, vm.Router.StartCount);
            return Task.CompletedTask;
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task AutoCheckAndSwitchSaveButtonHasExplicitScope() => Sta.Run(() =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
        var window = new MainWindow(vm);
        try
        {
            ((TabControl)window.FindName("Pages")).SelectedIndex = 1;
            var content = ArrangeProbeUx(window, 820);
            var buttons = ProbeUxChildren<Button>(content).Select(b => b.Content as string).ToArray();
            Assert.Contains("Сохранить настройки автопроверки и автосмены", buttons);
            Assert.DoesNotContain("Сохранить настройки теста", buttons);
            Assert.Equal(0, vm.Router.StartCount);
            return Task.CompletedTask;
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(820)]
    [InlineData(1340)]
    public Task PageScrollbarsSitAtWindowEdgeWhileContentKeepsItsInset(double width) => Sta.Run(() =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
        var window = new MainWindow(vm);
        try
        {
            var pages = (TabControl)window.FindName("Pages");
            foreach(var index in new[] { 0, 2, 4, 5 })
            {
                pages.SelectedIndex = index;
                var content = ArrangeProbeUx(window, width);
                var scroll = Assert.IsType<ScrollViewer>(((TabItem)pages.Items[index]).Content);
                var bar = Assert.IsType<ScrollBar>(scroll.Template.FindName("PART_VerticalScrollBar", scroll));
                Assert.Same(scroll, bar.TemplatedParent);
                Assert.Equal(Visibility.Visible, bar.Visibility);
                var right = bar.TranslatePoint(new Point(bar.ActualWidth, 0), content).X;
                Assert.InRange(width - right, 2, 6);
                var inner = (FrameworkElement)scroll.Content;
                Assert.True(inner.TranslatePoint(new Point(inner.ActualWidth, 0), content).X <= width - 24);
            }
            Assert.Equal(0, vm.Router.StartCount);
            return Task.CompletedTask;
        }
        finally { window.Close(); }
    });
}
