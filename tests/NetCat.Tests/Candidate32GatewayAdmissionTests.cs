using System.Reflection;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32GatewayAdmissionTests
{
    private static readonly Type Admission = typeof(RouterService).Assembly.GetType("NetCat.Network.LocalSocksAdmission", throwOnError: true)!;
    private static object Create() => Activator.CreateInstance(Admission, nonPublic: true)!;
    private static IDisposable? Accept(object owner) => (IDisposable?)Admission.GetMethod("TryAccept")!.Invoke(owner, null);
    private static bool Promote(IDisposable lease, bool udp) => (bool)lease.GetType().GetMethod("Promote")!.Invoke(lease, [udp])!;

    [Fact]
    public async Task UnauthenticatedGatewayAdmissionIsAtomicBoundedAndReleased()
    {
        var owner = Create();
        var leases = await Task.WhenAll(Enumerable.Range(0, 2048).Select(_ => Task.Run(() => Accept(owner))));
        try { Assert.Equal(512, leases.Count(lease => lease != null)); Assert.Null(Accept(owner)); }
        finally { foreach (var lease in leases) lease?.Dispose(); }
        using var next = Accept(owner); Assert.NotNull(next);
    }

    [Fact]
    public void FullUdpBudgetStillAdmitsTcpAndReleasesCompletedUdp()
    {
        var owner = Create(); var udp = new List<IDisposable>();
        try
        {
            for (int i = 0; i < 1024; i++) { var lease = Accept(owner)!; Assert.True(Promote(lease, true)); udp.Add(lease); }
            using (var rejected = Accept(owner)) { Assert.NotNull(rejected); Assert.False(Promote(rejected!, true)); }
            using var tcp = Accept(owner); Assert.NotNull(tcp); Assert.True(Promote(tcp!, false));
            udp[0].Dispose(); udp[0].Dispose();
            using var replacement = Accept(owner); Assert.NotNull(replacement); Assert.True(Promote(replacement!, true));
        }
        finally { foreach (var lease in udp) lease.Dispose(); }
    }

    [Fact]
    public void RejectedTcpPromotionDoesNotLeakHandshakeOrUdpCapacity()
    {
        var owner = Create(); var tcp = new List<IDisposable>();
        try
        {
            for (int i = 0; i < 512; i++) { var lease = Accept(owner)!; Assert.True(Promote(lease, false)); tcp.Add(lease); }
            for (int i = 0; i < 1024; i++) { using var rejected = Accept(owner); Assert.NotNull(rejected); Assert.False(Promote(rejected!, false)); }
            using var udp = Accept(owner); Assert.NotNull(udp); Assert.True(Promote(udp!, true));
        }
        finally { foreach (var lease in tcp) lease.Dispose(); }
    }
}
