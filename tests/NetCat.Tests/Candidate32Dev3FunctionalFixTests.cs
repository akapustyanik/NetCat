using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32Dev3FunctionalFixTests
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
    public void StagingTrustAcceptsExactNativeTreeAndRejectsMutation()
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "NetCat-C32-Stage-" +
                Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            var executable =
                Path.Combine(
                    root,
                    "sing-box.exe");

            var library =
                Path.Combine(
                    root,
                    "libcronet.dll");

            File.WriteAllText(
                executable,
                "candidate executable");

            File.WriteAllText(
                library,
                "candidate library");

            var trust =
                StagedExecutableTrustPolicy.Capture(
                    root,
                    executable);

            using(
                var lease =
                    trust.AcquireExecutable(
                        executable))
            {
                Assert.NotNull(lease);
            }

            File.AppendAllText(
                library,
                "-tampered");

            Assert.Throws<InvalidDataException>(
                () =>
                    trust.AcquireExecutable(
                        executable));
        }
        finally
        {
            if(Directory.Exists(root))
                Directory.Delete(root,true);
        }
    }

    [Fact]
    public void StagingTrustRejectsExtraNativeFile()
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "NetCat-C32-StageExtra-" +
                Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            var executable =
                Path.Combine(
                    root,
                    "xray.exe");

            File.WriteAllText(
                executable,
                "candidate executable");

            var trust =
                StagedExecutableTrustPolicy.Capture(
                    root,
                    executable);

            File.WriteAllText(
                Path.Combine(
                    root,
                    "unexpected.dll"),
                "unexpected");

            Assert.Throws<InvalidDataException>(
                () =>
                    trust.AcquireExecutable(
                        executable));
        }
        finally
        {
            if(Directory.Exists(root))
                Directory.Delete(root,true);
        }
    }

    [Fact]
    public void CoreCompatibilityProbesUseEphemeralStagingTrust()
    {
        var root =
            FindRoot();

        var source =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "NetCat.Updater",
                    "ModuleUpdater.cs"));

        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex
                .Matches(
                    source,
                    "trustPolicy: stagingTrust")
                .Count);

        Assert.Contains(
            "UPDATE_VERIFY module=sing-box result=staging-runtime-compatible",
            source);

        Assert.Contains(
            "UPDATE_VERIFY module=xray result=staging-runtime-compatible",
            source);
    }

    [Fact]
    public void VersionPickerUsesExistingNetCatDialog()
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
                "private ModuleVersionChoice? ShowVersionPicker",
                StringComparison.Ordinal);

        var end =
            source.IndexOf(
                "private async void SelectModuleVersion_Click",
                start,
                StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);

        var picker =
            source[start..end];

        Assert.Contains(
            "new EditorDialog(",
            picker);

        Assert.Contains(
            "dialog.Choice(",
            picker);

        Assert.DoesNotContain(
            "new Window",
            picker);

        Assert.DoesNotContain(
            "IsEditable",
            picker);

        Assert.Contains(
            "ConfirmForcedCoreInstall",
            source);

        Assert.Contains(
            "PrepareSpecificReleaseAsync(",
            source);
    }

    [Fact]
    public void WrappedButtonsHaveVerticalGapAndTelegramUsesGrid()
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

        Assert.True(
            System.Text.RegularExpressions.Regex
                .Matches(
                    xaml,
                    "Margin=\"0,0,8,8\"")
                .Count >= 12);

        var telegram =
            xaml.IndexOf(
                "ApplyTelegramPort_Click",
                StringComparison.Ordinal);

        Assert.True(telegram >= 0);

        var around =
            xaml.Substring(
                Math.Max(0,telegram-1000),
                Math.Min(
                    xaml.Length-Math.Max(0,telegram-1000),
                    2000));

        Assert.Contains(
            "<Grid",
            around);

        Assert.Contains(
            "VerticalAlignment=\"Bottom\"",
            around);
    }

    [Fact]
    public void VersionPreparationCanReuseAlreadyCheckedRelease()
    {
        var root =
            FindRoot();

        var source =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "NetCat.Updater",
                    "VersionedModuleUpdates.cs"));

        Assert.Contains(
            "PrepareSpecificReleaseAsync(",
            source);

        var ui =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "NetCat.UI",
                    "MainWindow.xaml.cs"));

        Assert.Contains(
            "PrepareSpecificReleaseAsync(",
            ui);
    }
}