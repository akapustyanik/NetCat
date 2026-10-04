using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32ShellRecoveryTests
{
    private static string FindRoot()
    {
        DirectoryInfo? current =
            new(AppContext.BaseDirectory);

        while(current != null)
        {
            if(File.Exists(
                Path.Combine(
                    current.FullName,
                    "NetCat.sln")))
            {
                return current.FullName;
            }

            current =
                current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Repository root not found.");
    }


    [Fact]
    public void ElevatedShellHasMediumExplorerTokenPathAndComFallback()
    {
        var root =
            FindRoot();

        var source =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "ExplorerDesktopShell.cs"));

        Assert.Contains(
            "CreateProcessWithTokenW",
            source);

        Assert.Contains(
            "DuplicateTokenEx",
            source);

        Assert.Contains(
            "FindExplorerProcess",
            source);

        Assert.Contains(
            "url.dll,FileProtocolHandler",
            source);

        Assert.Contains(
            "OpenViaDesktopCom",
            source);

        Assert.Contains(
            "IntegrityLevel(candidate.Id)",
            source);
    }


    [Fact]
    public void ModulesFolderOpeningRemainsRestrictedToExactBinFolder()
    {
        var root =
            FindRoot();

        var source =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "MainWindow.xaml.cs"));

        var start =
            source.IndexOf(
                "private bool OpenExternal(",
                StringComparison.Ordinal);

        var end =
            source.IndexOf(
                "private void BaseColor_Click(",
                start,
                StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);

        var block =
            source[start..end];

        Assert.Contains(
            "Path.GetFullPath(VM.Bin)",
            block);

        Assert.Contains(
            "ExplorerDesktopShell.Open(",
            block);

        Assert.Contains(
            "StringComparison.OrdinalIgnoreCase",
            block);

        Assert.DoesNotContain(
            "UseShellExecute=true",
            block);

        Assert.DoesNotContain(
            "WindowsExecutableTrust.IsElevated",
            block);
    }


    [Fact]
    public void UpdateButtonsHaveLabelsBeforeModuleSelection()
    {
        var root =
            FindRoot();

        var xaml =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "MainWindow.xaml"));

        Assert.Contains(
            "FallbackValue='Откат недоступен'",
            xaml);

        Assert.Contains(
            "FallbackValue='Закрепить текущую версию'",
            xaml);
    }


    [Fact]
    public void JournalAndTelegramStillUseRestrictedLauncher()
    {
        var root =
            FindRoot();

        var source =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "MainWindow.xaml.cs"));

        Assert.Contains(
            "journalShell.OpenFile(",
            source);

        Assert.Contains(
            "journalShell.OpenFolder(",
            source);

        Assert.Contains(
            "journalShell.OpenTelegramProxy(",
            source);
    }

    [Fact]
    public void UacDisabledUsesNormalCurrentShellWithoutRunAs()
    {
        var root =
            FindRoot();

        var source =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "ExplorerDesktopShell.cs"));

        Assert.Contains(
            "IsUacEnabled()",
            source);

        Assert.Contains(
            "OpenUsingCurrentShell(target)",
            source);

        Assert.Contains(
            "\"EnableLUA\"",
            source);

        Assert.Contains(
            "UseShellExecute = true",
            source);

        Assert.DoesNotContain(
            "Verb = \"runas\"",
            source);

        // UAC-enabled elevated machines must retain the hardened
        // unelevated handoff paths.
        Assert.Contains(
            "CreateProcessWithTokenW",
            source);

        Assert.Contains(
            "OpenViaDesktopCom",
            source);
    }
}