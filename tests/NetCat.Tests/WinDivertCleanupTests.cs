using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;
public sealed class WinDivertCleanupTests
{
    private sealed class Platform : IWinDivertCleanupPlatform
    {
        public WinDivertRegistration? Registration { get; set; }
        public int[] Clients { get; set; } = [];
        public bool StopResult { get; set; } = true;
        public int Probes, Stops;
        public WinDivertRegistration? ReadRegistration() => Registration;
        public IReadOnlyList<int> ActiveClients(string library) { Probes++; return Clients; }
        public bool StopAndDelete(string expectedDriver) { Stops++; return StopResult; }
    }
    private static string Modules => Path.Combine(Path.GetTempPath(), "NetCat-Cleanup", "modules");
    private static string Driver => Path.Combine(Modules, "zapret", "bin", "WinDivert64.sys");
    [Fact]
    public void MissingServiceDoesNotLoadLibraryOrChangeSystem()
    {
        var platform = new Platform();
        Assert.Equal(WinDivertCleanupResult.NotInstalled, WinDivertDriverCleanup.Cleanup(Modules, platform));
        Assert.Equal(0, platform.Probes); Assert.Equal(0, platform.Stops);
    }
    [Theory]
    [InlineData("C:\\Other\\WinDivert64.sys", 1)]
    [InlineData("WinDivert64.sys", 1)]
    [InlineData("own", 16)]
    public void ForeignOrNonDriverServiceIsNeverStopped(string path, uint type)
    {
        var platform = new Platform { Registration = new(path == "own" ? Driver : path, type) };
        Assert.Equal(WinDivertCleanupResult.ForeignInstallation, WinDivertDriverCleanup.Cleanup(Modules, platform));
        Assert.Equal(0, platform.Probes); Assert.Equal(0, platform.Stops);
    }
    [Fact]
    public void ClientFromAnyApplicationPreventsDriverUnload()
    {
        var platform = new Platform { Registration = new(Driver, 1), Clients = [123] };
        Assert.Equal(WinDivertCleanupResult.InUse, WinDivertDriverCleanup.Cleanup(Modules, platform));
        Assert.Equal(1, platform.Probes); Assert.Equal(0, platform.Stops);
    }
    [Theory]
    [InlineData("")][InlineData("\\??\\")][InlineData("\\\\?\\")]
    public void OnlyUnusedDriverFromThisInstallationIsRemoved(string prefix)
    {
        var platform = new Platform { Registration = new(prefix + Driver, 1) };
        Assert.Equal(WinDivertCleanupResult.Removed, WinDivertDriverCleanup.Cleanup(Modules, platform));
        Assert.Equal(1, platform.Probes); Assert.Equal(1, platform.Stops);
    }
    [Fact]
    public void FailedOrRacedStopDoesNotClaimSuccess()
    {
        var platform = new Platform { Registration = new(Driver, 1), StopResult = false };
        Assert.Equal(WinDivertCleanupResult.Unavailable, WinDivertDriverCleanup.Cleanup(Modules, platform));
    }
}
