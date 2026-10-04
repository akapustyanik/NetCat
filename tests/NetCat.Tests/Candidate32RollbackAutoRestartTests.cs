using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32RollbackAutoRestartTests
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
    public void RollbackUsesSameRuntimeCoordinatorAsModuleUpdate()
    {
        var root =
            FindRoot();

        var source =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "MainViewModel.cs"));

        var start =
            source.IndexOf(
                "public async Task RollbackModuleAsync(",
                StringComparison.Ordinal);

        var end =
            source.IndexOf(
                "public async Task ApplyRoutesAsync(",
                start,
                StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);

        var block =
            source[start..end];

        Assert.Contains(
            "RuntimeCoordinator.RunModuleUpdateAsync",
            block);

        Assert.Contains(
            "Updater.Rollback(",
            block);

        Assert.Contains(
            "await restore(",
            block);

        Assert.Contains(
            "await stop();",
            block);

        Assert.Contains(
            "UPDATE_ROLLBACK_RESTART",
            block);
    }


    [Fact]
    public void FailedRollbackCanSwapOriginalVersionBack()
    {
        var root =
            FindRoot();

        var source =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "MainViewModel.cs"));

        var start =
            source.IndexOf(
                "public async Task RollbackModuleAsync(",
                StringComparison.Ordinal);

        var end =
            source.IndexOf(
                "public async Task ApplyRoutesAsync(",
                start,
                StringComparison.Ordinal);

        var block =
            source[start..end];

        Assert.True(
            block.Split(
                "Updater.Rollback(",
                StringSplitOptions.None).Length >= 3);

        Assert.Contains(
            "result=reverted-to-original",
            block);

        Assert.Contains(
            "UPDATE_ROLLBACK_RECOVERY",
            block);
    }


    [Fact]
    public void RollbackUiDoesNotRequireManualStop()
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
                "private async void RollbackModule_Click(",
                StringComparison.Ordinal);

        var end =
            source.IndexOf(
                "private async void PinModule_Click(",
                start,
                StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);

        var block =
            source[start..end];

        Assert.Contains(
            "VM.RollbackModuleAsync(",
            block);

        Assert.DoesNotContain(
            "RequireStopped();",
            block);

        Assert.Contains(
            "перезапущен автоматически",
            block);
    }


    [Fact]
    public void TelegramRollbackPreservesRunningIntent()
    {
        var root =
            FindRoot();

        var source =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "MainViewModel.cs"));

        Assert.Contains(
            "telegramWasRunning",
            source);

        Assert.Contains(
            "Telegram.StopAsync()",
            source);

        Assert.Contains(
            "Telegram.StartAsync(",
            source);
    }
}