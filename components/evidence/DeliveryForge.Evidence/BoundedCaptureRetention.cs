using System.Collections.Concurrent;

namespace DeliveryForge.Evidence;

// Actual creator graph only. Unknown graphs are retained; no public reconciliation or PID adoption.
internal sealed class BoundedCaptureRetention
{
    private static readonly ConcurrentDictionary<BoundedCaptureRetention, byte> Pending = new();
    private readonly List<object> _originals = [];
    internal BoundedCaptureRetention() => Pending.TryAdd(this, 0);
    internal void Add(object original) => _originals.Add(original);
    internal void ReleaseKnown() => Pending.TryRemove(this, out _);
}
