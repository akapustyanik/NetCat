using NetCat.Core;

namespace NetCat.Network;

// Secondary probes never own primary health or runtime convergence.
public sealed class TunDiagnosticPolicy
{
    private long revision = -1;
    public int WeakFailures { get; private set; }
    public int ConfirmationCount { get; init; } = TunHealthPolicy.WeakDiagnosticConfirmationCount;

    public void Observe(DelayResult? result, bool tunActive, long sessionRevision)
    {
        if (revision != sessionRevision) { revision = sessionRevision; WeakFailures = 0; }
        if (!tunActive || result is { Success: true }) WeakFailures = 0;
        else if (WeakFailures < int.MaxValue) WeakFailures++;
    }

    public string Detail(TunStructuralStatus status) => status switch
    {
        TunStructuralStatus.StructuralFailure => "TUN недоступен · восстанавливаю…",
        TunStructuralStatus.TransientDegraded => "Проверяю состояние TUN…",
        TunStructuralStatus.Healthy when WeakFailures >= ConfirmationCount => "Дополнительная проверка TUN временно недоступна",
        _ => ""
    };
}
