namespace NetCat.Network;

// Pending authentication, TCP and UDP have separate, atomic budgets. A quiet
// UDP association retains its control socket until the native router expires
// it; those sockets must never consume TCP's admission budget. UDP capacity
// covers 310 retained sources plus overlapping 256-source bursts without
// evicting active flows. All three budgets remain bounded.
internal sealed class LocalSocksAdmission
{
    private readonly SemaphoreSlim pending = new(512);
    private readonly SemaphoreSlim tcp = new(512);
    private readonly SemaphoreSlim udp = new(1024);

    public Lease? TryAccept() => pending.Wait(0) ? new Lease(this) : null;

    internal sealed class Lease(LocalSocksAdmission owner) : IDisposable
    {
        private SemaphoreSlim? slot = owner.pending;
        public bool Promote(bool datagram)
        {
            if (slot != owner.pending) throw new InvalidOperationException("Admission already promoted or released.");
            var next = datagram ? owner.udp : owner.tcp;
            if (!next.Wait(0)) return false;
            slot = next; owner.pending.Release(); return true;
        }
        public void Dispose() => Interlocked.Exchange(ref slot, null)?.Release();
    }
}
