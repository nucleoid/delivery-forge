namespace DeliveryForge.Planning;

// Owner-issued diagnostic DATA only. It cannot grant Ready, execution or policy authority.
public sealed class PlanImportResourceObservation
{
    internal PlanImportResourceObservation(int discovered, int queued, int entries)
    { MaximumDiscoveredReferenceDirectories = discovered; MaximumQueuedReferenceDirectories = queued; MaximumEnumeratedReferenceEntries = entries; }
    public int MaximumDiscoveredReferenceDirectories { get; }
    public int MaximumQueuedReferenceDirectories { get; }
    public int MaximumEnumeratedReferenceEntries { get; }
}

internal sealed class PlanImportResourceCollector
{
    private readonly object _sync = new();
    private int _discovered, _queued, _entries;
    internal void Observe(int discovered, int queued, int entries)
    {
        lock (_sync)
        { _discovered = Math.Max(_discovered, discovered); _queued = Math.Max(_queued, queued); _entries = Math.Max(_entries, entries); }
    }
    internal PlanImportResourceObservation Snapshot()
    { lock (_sync) return new(_discovered, _queued, _entries); }
}
