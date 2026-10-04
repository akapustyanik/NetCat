using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32ReleaseUiTests
{
    private static string FindRoot()
    {
        System.IO.DirectoryInfo? current =
            new(AppContext.BaseDirectory);

        while(current != null)
        {
            if(System.IO.File.Exists(
                System.IO.Path.Combine(
                    current.FullName,
                    "NetCat.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new System.IO.DirectoryNotFoundException(
            "Repository root not found.");
    }


    [Fact]
    public void NetCatUpdateRowExposesGitHubRepositoryLink()
    {
        var root = FindRoot();

        var xaml =
            System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "MainWindow.xaml"));

        var code =
            System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "MainWindow.xaml.cs"));

        Assert.Contains(
            "OpenModuleGitHub_Click",
            xaml);

        Assert.Contains(
            "Value=\"netcat\"",
            xaml);

        Assert.Contains(
            "GitHub →",
            xaml);

        Assert.Contains(
            "https://github.com/akapustyanik/NetCat",
            code);

        Assert.Contains(
            "ExplorerDesktopShell.Open(url)",
            code);

        Assert.Contains(
            "Ссылка на GitHub скопирована",
            code);
    }


    [Fact]
    public void HiddenAutostartRequestsReconcileBeforePresentation()
    {
        var root = FindRoot();

        var source =
            System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "App.xaml.cs"));

        var restore =
            source.IndexOf(
                "if (!IsSmoke && settings.RestoreConnectionsOnStartup)",
                System.StringComparison.Ordinal);

        var reconcile =
            source.IndexOf(
                "vm.RuntimeCoordinator.RequestReconcile(ReconcileReason.Startup);",
                System.StringComparison.Ordinal);

        var autostart =
            source.IndexOf(
                "bool isAutostart = e.Args.Contains(\"--autostart\");",
                System.StringComparison.Ordinal);

        var hidden =
            source.IndexOf(
                "trayAvailable && isAutostart && presentationState == WindowPresentationState.HiddenToTray",
                System.StringComparison.Ordinal);

        Assert.True(restore >= 0);
        Assert.True(reconcile > restore);
        Assert.True(autostart > reconcile);
        Assert.True(hidden > autostart);
    }
}