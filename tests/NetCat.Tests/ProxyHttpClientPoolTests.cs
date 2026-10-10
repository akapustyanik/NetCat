using System.Net;
using System.Reflection;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class ProxyHttpClientPoolTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public bool Released { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Released, this);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
        protected override void Dispose(bool disposing) { Released = true; base.Dispose(disposing); }
    }
    private sealed class Pool : IDisposable
    {
        private readonly object instance;
        private readonly MethodInfo acquire;
        public int Port;
        public readonly List<Handler> Handlers = [];
        public Pool()
        {
            var type = typeof(ModuleUpdater).Assembly.GetType("NetCat.Updater.ProxyHttpClientPool", throwOnError: true)!;
            instance = Activator.CreateInstance(type, new Func<int>(() => Port), new Func<int, HttpClient>(_ =>
            {
                var handler = new Handler(); Handlers.Add(handler); return new(handler);
            }))!;
            acquire = type.GetMethod("Acquire")!;
        }
        public IDisposable Acquire(out HttpClient client)
        {
            var lease = acquire.Invoke(instance, null)!;
            client = (HttpClient)lease.GetType().GetProperty("Client")!.GetValue(lease)!;
            return (IDisposable)lease;
        }
        public void Dispose() => ((IDisposable)instance).Dispose();
    }

    [Fact]
    public void InactiveProxyPoolsAreReleasedAcrossRepeatedProtocolChanges()
    {
        using var pool = new Pool();
        for (var port = 1; port <= 100; port++)
        {
            pool.Port = port;
            using var lease = pool.Acquire(out _);
            Assert.Equal(1, pool.Handlers.Count(handler => !handler.Released));
        }
        pool.Dispose();
        Assert.All(pool.Handlers, handler => Assert.True(handler.Released));
    }

    [Fact]
    public async Task PortChangeDoesNotDisposeAnInflightDownloadClient()
    {
        using var pool = new Pool();
        var oldLease = pool.Acquire(out var oldClient);
        pool.Port = 12345;
        using var newLease = pool.Acquire(out var newClient);
        Assert.NotSame(oldClient, newClient);
        Assert.False(pool.Handlers[0].Released);
        using var response = await oldClient.GetAsync("https://example.invalid/");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        oldLease.Dispose(); oldLease.Dispose();
        Assert.True(pool.Handlers[0].Released);
        Assert.False(pool.Handlers[1].Released);
    }

    [Fact]
    public async Task ConcurrentChecksReuseOneClientInsteadOfLosingFactoryResults()
    {
        using var pool = new Pool();
        var clients = new System.Collections.Concurrent.ConcurrentBag<HttpClient>();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            using var lease = pool.Acquire(out var client); clients.Add(client);
            using var response = await client.GetAsync("https://example.invalid/");
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        })));
        Assert.Single(pool.Handlers);
        Assert.All(clients, client => Assert.Same(clients.First(), client));
    }

    [Fact]
    public void DisposedUpdaterPoolCannotResurrectClients()
    {
        using var pool = new Pool();
        using var lease = pool.Acquire(out _);
        pool.Dispose(); pool.Dispose();
        Assert.True(pool.Handlers[0].Released);
        var error = Assert.Throws<TargetInvocationException>(() => pool.Acquire(out _));
        Assert.IsType<ObjectDisposedException>(error.InnerException);
    }
}
