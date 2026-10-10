namespace NetCat.Updater;

// Keep the current proxy's pool, plus only older pools still used by a request.
// A protocol switch cannot cancel a download already using its previous port.
internal sealed class ProxyHttpClientPool(Func<int> port, Func<int, HttpClient> create) : IDisposable
{
    private sealed class Entry(HttpClient client)
    {
        public HttpClient Client { get; } = client;
        public int Users;
    }
    private readonly object gate = new();
    private readonly Dictionary<int, Entry> entries = [];
    private int currentPort;
    private bool disposed;

    public sealed class Lease(HttpClient client, Action release) : IDisposable
    {
        public HttpClient Client { get; } = client;
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }

    public Lease Acquire()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            currentPort = port();
            foreach (var pair in entries.ToArray())
                if (pair.Key != currentPort && pair.Value.Users == 0)
                {
                    entries.Remove(pair.Key);
                    pair.Value.Client.Dispose();
                }
            if (!entries.TryGetValue(currentPort, out var entry))
                entries.Add(currentPort, entry = new(create(currentPort)));
            entry.Users++;
            var selectedPort = currentPort;
            return new(entry.Client, () => Release(selectedPort, entry));
        }
    }

    private void Release(int selectedPort, Entry entry)
    {
        lock (gate)
        {
            if (--entry.Users == 0 && (disposed || selectedPort != currentPort))
            {
                if (entries.TryGetValue(selectedPort, out var owned) && ReferenceEquals(owned, entry))
                    entries.Remove(selectedPort);
                entry.Client.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in entries.Values) entry.Client.Dispose();
            entries.Clear();
        }
    }
}
