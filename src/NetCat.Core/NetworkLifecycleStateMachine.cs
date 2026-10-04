namespace NetCat.Core;

public sealed class NetworkLifecycleStateMachine
{
    private NetworkLifecycleState state = NetworkLifecycleState.Normal;
    private long generation;
    private string? temporaryStatus;

    public NetworkLifecycleState State => state;
    public bool InNetworkTransition => state == NetworkLifecycleState.NetworkTransition;
    public bool IsNetworkRebuilding => state == NetworkLifecycleState.Rebuilding;
    public bool IsNetworkUnavailable => state == NetworkLifecycleState.NetworkUnavailable;
    public long Generation => Interlocked.Read(ref generation);
    public string? TemporaryStatus => temporaryStatus;

    public event Action? Changed;

    public long BeginTransition()
    {
        if (state == NetworkLifecycleState.Normal)
        {
            state = NetworkLifecycleState.NetworkTransition;
            Changed?.Invoke();
        }
        return Interlocked.Increment(ref generation);
    }

    public void BeginRebuilding(long currentGeneration)
    {
        if (Interlocked.Read(ref generation) == currentGeneration)
        {
            state = NetworkLifecycleState.Rebuilding;
            Changed?.Invoke();
        }
    }

    public bool CompleteRebuild(long currentGeneration, bool success, string interfaceName, string ipAddress)
    {
        if (Interlocked.Read(ref generation) != currentGeneration) return false;

        if (success)
        {
            state = NetworkLifecycleState.Normal;
            temporaryStatus = $"Физическая сеть восстановлена: {interfaceName}, {ipAddress}";
            Changed?.Invoke();
            return true;
        }
        else
        {
            state = NetworkLifecycleState.NetworkUnavailable;
            Changed?.Invoke();
            return false;
        }
    }

    public void MarkUnavailable(long currentGeneration)
    {
        if (Interlocked.Read(ref generation) == currentGeneration)
        {
            state = NetworkLifecycleState.NetworkUnavailable;
            Changed?.Invoke();
        }
    }

    public void ClearTemporaryStatus()
    {
        temporaryStatus = null;
        Changed?.Invoke();
    }

    public void Reset()
    {
        state = NetworkLifecycleState.Normal;
        temporaryStatus = null;
        Interlocked.Increment(ref generation);
        Changed?.Invoke();
    }
}
