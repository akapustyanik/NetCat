using NetCat.Core;

namespace NetCat.Network;

public interface IOpenVpnRetryController
{
    OpenVpnRetryState State { get; }
    OpenVpnFailureClass LastFailureClass { get; }
    string? LastError { get; }
    DateTimeOffset? NextAttemptAt { get; }
    int AttemptCount { get; }
    OpenVpnRetryKey CurrentKey { get; }
    event Action<OpenVpnRetryState>? StateChanged;

    bool CanAttempt(OpenVpnRetryKey key, DateTimeOffset now, ReconcileReason reason);
    void RecordAttemptStarted(OpenVpnRetryKey key, DateTimeOffset now);
    void RecordSuccess(OpenVpnRetryKey key);
    void RecordFailure(OpenVpnRetryKey key, OpenVpnFailureClass failureClass, string errorMessage, DateTimeOffset now);
    void Reset(OpenVpnRetryKey key);
    void GrantManualRetry(OpenVpnRetryKey key) => Reset(key);
    void Cancel();
    void SetKey(OpenVpnRetryKey key);
}

public sealed class OpenVpnRetryController : IOpenVpnRetryController
{
    private readonly object syncRoot = new();
    private readonly TimeSpan[] backoffSchedule;
    private readonly int maxRetries;
    private bool manualAttempt;

    public OpenVpnRetryState State { get; private set; } = OpenVpnRetryState.Idle;
    public OpenVpnFailureClass LastFailureClass { get; private set; } = OpenVpnFailureClass.None;
    public string? LastError { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public int AttemptCount { get; private set; }
    public OpenVpnRetryKey CurrentKey { get; private set; } = OpenVpnRetryKey.Empty;
    public event Action<OpenVpnRetryState>? StateChanged;

    public static readonly TimeSpan[] DefaultBackoffSchedule =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60)
    ];

    public OpenVpnRetryController(TimeSpan[]? backoffSchedule = null, int maxRetries = 4)
    {
        this.backoffSchedule = (backoffSchedule ?? DefaultBackoffSchedule).ToArray();
        if (maxRetries < 1 || this.backoffSchedule.Length == 0 || this.backoffSchedule.Any(x => x < TimeSpan.Zero)) throw new ArgumentOutOfRangeException(nameof(maxRetries));
        this.maxRetries = maxRetries;
    }

    public bool CanAttempt(OpenVpnRetryKey key, DateTimeOffset now, ReconcileReason reason)
    {
        lock (syncRoot)
        {
            if (CurrentKey != OpenVpnRetryKey.Empty && !Equals(CurrentKey, key))
            {
                return true;
            }

            if (State is OpenVpnRetryState.Starting or OpenVpnRetryState.Connecting)
            {
                return false;
            }

            if (State is OpenVpnRetryState.SuspendedFatal or OpenVpnRetryState.SuspendedAuth or OpenVpnRetryState.WaitingForRelevantChange)
            {
                return false;
            }

            if (NextAttemptAt.HasValue && now < NextAttemptAt.Value)
            {
                return false;
            }

            return true;
        }
    }

    public void RecordAttemptStarted(OpenVpnRetryKey key, DateTimeOffset now)
    {
        Action<OpenVpnRetryState>? notify = null;
        lock (syncRoot)
        {
            if (!Equals(CurrentKey, key))
            {
                CurrentKey = key;
                AttemptCount = 0;
                manualAttempt = false;
            }

            AttemptCount++;
            State = OpenVpnRetryState.Starting;
            NextAttemptAt = null;
            notify = StateChanged;
        }
        notify?.Invoke(OpenVpnRetryState.Starting);
    }

    public void RecordSuccess(OpenVpnRetryKey key)
    {
        Action<OpenVpnRetryState>? notify = null;
        lock (syncRoot)
        {
            if (!Equals(CurrentKey, key) || State is not (OpenVpnRetryState.Starting or OpenVpnRetryState.Connecting)) return;
            State = OpenVpnRetryState.Connected;
            AttemptCount = 0;
            NextAttemptAt = null;
            LastError = null;
            LastFailureClass = OpenVpnFailureClass.None;
            notify = StateChanged;
        }
        notify?.Invoke(OpenVpnRetryState.Connected);
    }

    public void RecordFailure(OpenVpnRetryKey key, OpenVpnFailureClass failureClass, string errorMessage, DateTimeOffset now)
    {
        Action<OpenVpnRetryState>? notify = null;
        OpenVpnRetryState nextState;
        lock (syncRoot)
        {
            if (!Equals(CurrentKey, key) || State is not (OpenVpnRetryState.Starting or OpenVpnRetryState.Connecting)) return;
            LastFailureClass = failureClass;
            LastError = errorMessage;

            switch (failureClass)
            {
                case OpenVpnFailureClass.DeterministicLocalFatal:
                    State = OpenVpnRetryState.SuspendedFatal;
                    NextAttemptAt = null;
                    break;

                case OpenVpnFailureClass.AuthenticationFatal:
                    State = OpenVpnRetryState.SuspendedAuth;
                    NextAttemptAt = null;
                    break;

                case OpenVpnFailureClass.PhysicalNetworkUnavailable:
                    State = OpenVpnRetryState.WaitingForRelevantChange;
                    NextAttemptAt = null;
                    break;

                case OpenVpnFailureClass.TransientNetwork:
                case OpenVpnFailureClass.RemoteTemporary:
                case OpenVpnFailureClass.Unknown:
                default:
                    if (manualAttempt || AttemptCount >= maxRetries)
                    {
                        State = OpenVpnRetryState.WaitingForRelevantChange;
                        NextAttemptAt = null;
                    }
                    else
                    {
                        State = OpenVpnRetryState.RetryScheduled;
                        int idx = Math.Min(Math.Max(0, AttemptCount - 1), backoffSchedule.Length - 1);
                        var delay = backoffSchedule[idx];
                        NextAttemptAt = now + delay;
                    }
                    break;
            }

            nextState = State;
            notify = StateChanged;
        }
        notify?.Invoke(nextState);
    }

    public void Cancel()
    {
        Action<OpenVpnRetryState>? notify = null;
        lock (syncRoot)
        {
            State = OpenVpnRetryState.Idle;
            NextAttemptAt = null;
            AttemptCount = 0;
            manualAttempt = false;
            LastError = null;
            LastFailureClass = OpenVpnFailureClass.None;
            notify = StateChanged;
        }
        notify?.Invoke(OpenVpnRetryState.Idle);
    }

    public void GrantManualRetry(OpenVpnRetryKey key)
    {
        lock (syncRoot)
        {
            CurrentKey = key; State = OpenVpnRetryState.Idle; NextAttemptAt = null;
            AttemptCount = 0; LastError = null; LastFailureClass = OpenVpnFailureClass.None; manualAttempt = true;
        }
        StateChanged?.Invoke(OpenVpnRetryState.Idle);
    }

    public void Reset(OpenVpnRetryKey key)
    {
        Action<OpenVpnRetryState>? notify = null;
        lock (syncRoot)
        {
            CurrentKey = key;
            State = OpenVpnRetryState.Idle;
            NextAttemptAt = null;
            AttemptCount = 0;
            manualAttempt = false;
            LastError = null;
            LastFailureClass = OpenVpnFailureClass.None;
            notify = StateChanged;
        }
        notify?.Invoke(OpenVpnRetryState.Idle);
    }

    public void SetKey(OpenVpnRetryKey key)
    {
        Action<OpenVpnRetryState>? notify = null;
        lock (syncRoot)
        {
            if (!Equals(CurrentKey, key))
            {
                CurrentKey = key;
                State = OpenVpnRetryState.Idle;
                NextAttemptAt = null;
                AttemptCount = 0; manualAttempt = false; LastError = null; LastFailureClass = OpenVpnFailureClass.None;
                notify = StateChanged;
            }
        }
        notify?.Invoke(OpenVpnRetryState.Idle);
    }
}

public sealed class OpenVpnFailureException(OpenVpnFailureClass failureClass, string message) : IOException(message)
{ public OpenVpnFailureClass FailureClass { get; } = failureClass; }

public static class OpenVpnFailureClassifier
{
    public static OpenVpnFailureClass Classify(string? message, Exception? ex = null)
    {
        if (ex is OpenVpnFailureException typed) return typed.FailureClass;
        var text = $"{message} {ex?.Message} {ex?.GetType().Name}".Trim();

        // 1. Deterministic Local Fatal
        if (text.Contains("RC2-40-CBC", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error:0308010C", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("digital envelope", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unsupported:Algorithm", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("legacy provider", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("wintun.dll", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("missing module", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("tampered", StringComparison.OrdinalIgnoreCase) ||
            ex is InvalidDataException ||
            ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return OpenVpnFailureClass.DeterministicLocalFatal;
        }

        // 2. Authentication Fatal
        if (text.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("bad auth", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
        {
            return OpenVpnFailureClass.AuthenticationFatal;
        }

        // 3. Physical Network Unavailable
        if ((text.Contains("физическ", StringComparison.InvariantCultureIgnoreCase) && text.Contains("недоступ", StringComparison.InvariantCultureIgnoreCase)) ||
            text.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase))
        {
            return OpenVpnFailureClass.PhysicalNetworkUnavailable;
        }

        // 4. Transient Network
        if (text.Contains("TLS Error: TLS key negotiation failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("TLS handshake failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Connection reset", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("RESOLVE: Cannot resolve host address", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Завершился до готовности соединения", StringComparison.OrdinalIgnoreCase))
        {
            return OpenVpnFailureClass.TransientNetwork;
        }

        // 5. Remote Temporary
        if (text.Contains("server restart", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("restart pause", StringComparison.OrdinalIgnoreCase))
        {
            return OpenVpnFailureClass.RemoteTemporary;
        }

        return OpenVpnFailureClass.Unknown;
    }
}
