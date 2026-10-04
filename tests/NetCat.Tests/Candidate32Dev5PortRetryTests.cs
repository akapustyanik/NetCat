using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32Dev5PortRetryTests
{
    [Fact]
    public async Task PortCollisionRetriesWithNextAttempt()
    {
        var attempts = new List<int>();

        var result = await PortStartup.RetryAsync(
            async attempt =>
            {
                attempts.Add(attempt);
                await Task.Yield();

                if (attempt < 2)
                    throw new PortCollisionException(
                        "Injected port collision");

                return 42;
            },
            CancellationToken.None,
            attempts: 3);

        Assert.Equal(42, result);
        Assert.Equal(new[] { 0, 1, 2 }, attempts);
    }

    [Fact]
    public async Task NonCollisionFailureIsNotRetried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<IOException>(
            () => PortStartup.RetryAsync(
                async attempt =>
                {
                    attempts++;
                    await Task.Yield();
                    throw new IOException(
                        "Injected non-collision error");
#pragma warning disable CS0162
                    return attempt;
#pragma warning restore CS0162
                },
                CancellationToken.None,
                attempts: 3));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void SidecarEnablesLocalRetryAndClearsPendingFlag()
    {
        var root = RoutingTests.FindRoot();

        var source = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "NetCat.Network",
                "OpenVpnSidecar.cs"));

        var begin = source.IndexOf(
            "public async Task ApplyAsync(",
            StringComparison.Ordinal);

        var end = source.IndexOf(
            "public async Task DeactivateAsync(",
            begin,
            StringComparison.Ordinal);

        Assert.True(begin >= 0);
        Assert.True(end > begin);

        var method = source.Substring(begin, end - begin);

        Assert.Contains(
            "}, ct, attempts: 3).ConfigureAwait(false);",
            method);

        Assert.Contains(
            "if (attempt > 0) backend = Gateway with",
            method);

        var publish = method.IndexOf(
            "Publish(revision, link, config, stillCurrent, ct);",
            StringComparison.Ordinal);

        var reset = method.IndexOf(
            "reallocateBackend = false;",
            publish,
            StringComparison.Ordinal);

        Assert.True(publish >= 0);
        Assert.True(reset > publish);
    }
}