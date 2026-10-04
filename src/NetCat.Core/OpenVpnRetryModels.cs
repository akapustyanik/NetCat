namespace NetCat.Core;

public enum OpenVpnFailureClass
{
    None = 0,
    DeterministicLocalFatal,
    AuthenticationFatal,
    TransientNetwork,
    PhysicalNetworkUnavailable,
    RemoteTemporary,
    Unknown
}

public enum OpenVpnRetryState
{
    Idle = 0,
    Starting,
    Connecting,
    Connected,
    RetryScheduled,
    WaitingForRelevantChange,
    SuspendedFatal,
    SuspendedAuth,
    Stopping
}

public sealed record OpenVpnRetryKey(
    Guid? ProfileId,
    int ProfileRevision,
    long PhysicalNetworkGeneration,
    int CredentialsRevision,
    int NativeModuleRevision
)
{
    public static readonly OpenVpnRetryKey Empty = new(null, 0, 0, 0, 0);
}
