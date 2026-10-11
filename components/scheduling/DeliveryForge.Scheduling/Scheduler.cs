using System.Collections.Concurrent;
using DeliveryForge.Execution;

namespace DeliveryForge.Scheduling;

public enum ScheduledOutcome { Pass, Fail, Incomplete }
public sealed record FixtureSchedulingItem(string Id, ChangeFootprint Changes, RunDemand Demand, int RiskClass = 0);
public sealed record FairnessPolicy(string Identity, bool Approved, int MaximumBypasses);
public sealed record SchedulingRecoveryFacts(bool LiveChild = false, bool WriterPresent = false, bool LeasePresent = false, bool Uncertain = false);
public sealed record ReconstructedQueue(bool Fixture, bool Paused, bool Stopped, bool MayResumeData, IReadOnlyList<string> Pending, IReadOnlyList<string> Limitations);

public sealed class SchedulingTicket
{
    internal SchedulingTicket(string nodeId) => NodeId = nodeId;
    public string NodeId { get; }
    public bool Fixture => true;
}

// This fixture model has no execution-host port. It cannot dispatch a real worker or prove production ownership.
public sealed class Scheduler
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CreationLocks = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly string _runId;
    private readonly DependencyGraph _graph;
    private readonly ResourceBudget _budget;
    private readonly FairnessPolicy _fairness;
    private readonly RunStore _store;
    private readonly List<Node> _nodes;
    private readonly Dictionary<string, Node> _byId;
    private readonly Dictionary<SchedulingTicket, Node> _tickets = new(ReferenceEqualityComparer.Instance);
    private int _paused;
    private int _stopped;
    private bool _journalUncertain;

    private Scheduler(string runId, DependencyGraph graph, ResourceBudget budget, FairnessPolicy fairness, RunStore store, List<Node> nodes)
    {
        _runId = runId; _graph = graph; _budget = budget; _fairness = fairness; _store = store; _nodes = nodes;
        _byId = nodes.ToDictionary(node => node.Item.Id, StringComparer.Ordinal);
    }

    public static async Task<Scheduler> CreateContractFixtureAsync(string runId, DependencyGraph graph, IReadOnlyList<FixtureSchedulingItem> items,
        ResourceBudget budget, FairnessPolicy fairness, RunStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(fairness);
        ArgumentNullException.ThrowIfNull(store);
        if (!fairness.Approved || string.IsNullOrWhiteSpace(fairness.Identity) || fairness.Identity.Length > 256 || fairness.MaximumBypasses is < 1 or > 1024)
            throw new SchedulingException("An approved identified finite fairness policy is required.");
        if (items.Count is < 1 or > 4096 || items.Count != graph.TopologicalOrder.Count) throw new SchedulingException("Queue must contain exactly the bounded graph nodes.");
        var nodes = new List<Node>();
        foreach (var item in items)
        {
            if (item is null || item.RiskClass is < 0 or > 100 || item.Changes is null || item.Changes.Paths is null || item.Changes.SharedOwners is null ||
                item.Changes.Paths.Count > 4096 || item.Changes.SharedOwners.Count > 1024 || !budget.CanAllocate(item.Demand))
                throw new SchedulingException("Queue item has invalid or unavailable approved limits.");
            var captured = item with
            {
                Changes = item.Changes with { Paths = Array.AsReadOnly(item.Changes.Paths.ToArray()), SharedOwners = Array.AsReadOnly(item.Changes.SharedOwners.ToArray()) },
                Demand = item.Demand with { ExclusiveResources = Array.AsReadOnly(item.Demand.ExclusiveResources.ToArray()) }
            };
            nodes.Add(new Node(captured, nodes.Count));
        }
        if (nodes.Select(node => node.Item.Id).Distinct(StringComparer.Ordinal).Count() != nodes.Count ||
            !nodes.Select(node => node.Item.Id).Order(StringComparer.Ordinal).SequenceEqual(graph.TopologicalOrder.Order(StringComparer.Ordinal)))
            throw new SchedulingException("Queue identities must match the exact dependency graph.");
        EnsurePlainStore(store.RootPath);
        var creation = CreationLocks.GetOrAdd(Path.Combine(store.RootPath, runId), _ => new SemaphoreSlim(1, 1));
        await creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if ((await store.RecoverAsync(runId, cancellationToken).ConfigureAwait(false)).Records.Count != 0)
                throw new SchedulingException("An existing queue journal is recovery data; it cannot create fresh dispatch tickets.");
            var scheduler = new Scheduler(runId, graph, budget, fairness, store, nodes);
            await scheduler.PersistAsync(cancellationToken).ConfigureAwait(false);
            return scheduler;
        }
        finally { creation.Release(); }
    }

    public async Task<IReadOnlyList<SchedulingTicket>> AllocateFixtureWaveAsync(IReadOnlySet<string> integrated, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrated);
        if (integrated.Count > _nodes.Count || integrated.Any(id => !_byId.ContainsKey(id))) throw new SchedulingException("Integration observations contain foreign queue nodes.");
        var capturedIntegration = new HashSet<string>(integrated, StringComparer.Ordinal);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureJournal();
            if (Volatile.Read(ref _paused) != 0 || Volatile.Read(ref _stopped) != 0) return [];
            var ready = _nodes.Where(node => node.Phase == Phase.Pending && _graph.DependenciesOf(node.Item.Id)
                .All(id => _byId[id].Phase == Phase.Pass && capturedIntegration.Contains(id))).ToArray();
            var selected = new List<SchedulingTicket>();
            foreach (var node in ready.OrderByDescending(node => node.Bypasses >= _fairness.MaximumBypasses).ThenBy(node => node.Bypasses >= _fairness.MaximumBypasses ? 0 : node.Item.RiskClass).ThenBy(node => node.Sequence))
            {
                if (Volatile.Read(ref _paused) != 0 || Volatile.Read(ref _stopped) != 0) break;
                if (_nodes.Any(active => active.Phase is Phase.Reserved or Phase.Running or Phase.Cleaning && OverlapDetector.Conflicts(active.Item.Changes, node.Item.Changes)) ||
                    !_budget.CanAllocate(node.Item.Demand)) continue;
                node.Reservation = _budget.Allocate(node.Item.Demand);
                node.Phase = Phase.Reserved;
                var ticket = new SchedulingTicket(node.Item.Id);
                _tickets.Add(ticket, node);
                selected.Add(ticket);
            }
            if (selected.Count != 0)
            {
                foreach (var waiting in ready.Where(node => node.Phase == Phase.Pending)) waiting.Bypasses = Math.Min(_fairness.MaximumBypasses, waiting.Bypasses + 1);
                await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
            return selected.AsReadOnly();
        }
        finally { _serial.Release(); }
    }

    public Task StartFixtureAsync(SchedulingTicket ticket, CancellationToken cancellationToken = default) => SerialAsync(async () =>
    {
        EnsureJournal();
        if (Volatile.Read(ref _paused) != 0 || Volatile.Read(ref _stopped) != 0) throw new SchedulingException("Pause/stop prevents new fixture dispatch.");
        var node = Owned(ticket);
        if (node.Phase != Phase.Reserved) throw new SchedulingException("Ticket is not a current unstarted reservation.");
        node.Phase = Phase.Running;
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _paused) != 0 || Volatile.Read(ref _stopped) != 0) throw new SchedulingException("Pause/stop arrived during allocation; no new fixture start is authorized.");
    }, cancellationToken);

    public Task RecordFixtureOutcomeAsync(SchedulingTicket ticket, ScheduledOutcome outcome, bool quiescent, CancellationToken cancellationToken = default) => SerialAsync(async () =>
    {
        if (!Enum.IsDefined(outcome)) throw new SchedulingException("Undefined scheduling outcome.");
        var node = Owned(ticket);
        if (node.Phase is not (Phase.Running or Phase.Cleaning)) throw new SchedulingException("Outcome requires an owned started fixture ticket.");
        if (node.Outcome is not null && node.Outcome != outcome) throw new SchedulingException("A retained outcome cannot be rewritten during cleanup.");
        node.Outcome = outcome;
        if (!quiescent) node.Phase = Phase.Cleaning;
        else
        {
            node.Phase = outcome switch { ScheduledOutcome.Pass => Phase.Pass, ScheduledOutcome.Fail => Phase.Fail, _ => Phase.Incomplete };
            node.Reservation!.Dispose();
            node.Reservation = null;
            _tickets.Remove(ticket);
        }
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _paused, 1);
        return SerialAsync(async () => { CancelUnstarted(false); await PersistAsync(cancellationToken).ConfigureAwait(false); }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _stopped, 1);
        return SerialAsync(async () => { CancelUnstarted(true); await PersistAsync(cancellationToken).ConfigureAwait(false); }, cancellationToken);
    }

    public Task ResumeFixtureAsync(SchedulingRecoveryFacts facts, CancellationToken cancellationToken = default) => SerialAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(facts);
        EnsureJournal();
        if (Volatile.Read(ref _stopped) != 0 || Volatile.Read(ref _paused) == 0 || !Clear(facts) || _nodes.Any(node => Active(node.Phase)))
            throw new SchedulingException("Resume requires a paused queue and reconciled quiescence; stop and uncertain/live ownership prevent reuse.");
        Interlocked.Exchange(ref _paused, 0);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    public static async Task<ReconstructedQueue> ReconstructAsync(string runId, RunStore store, SchedulingRecoveryFacts facts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(facts);
        var recovered = await store.RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
        var latest = recovered.Records.LastOrDefault(record => record.Kind == "fixture-scheduler-checkpoint")
            ?? throw new SchedulingException("No queue checkpoint exists; missing data is not dispatch authority.");
        var checkpoint = store.ReadPayload<FixtureCheckpoint>(latest);
        if (checkpoint.Version != 1 || !checkpoint.Fixture || checkpoint.RunId != runId || checkpoint.Nodes is null || checkpoint.Nodes.Count is < 1 or > 4096 ||
            checkpoint.Nodes.Any(node => node is null || !Enum.IsDefined(node.Phase)) || checkpoint.Nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != checkpoint.Nodes.Count)
            throw new SchedulingException("Queue checkpoint is malformed or incompatible.");
        var canResume = !checkpoint.Stopped && !checkpoint.Uncertain && Clear(facts) && !checkpoint.Nodes.Any(node => Active(node.Phase));
        return new(true, checkpoint.Paused, checkpoint.Stopped, canResume,
            Array.AsReadOnly(checkpoint.Nodes.Where(node => node.Phase == Phase.Pending).Select(node => node.Id).ToArray()),
            canResume ? ["Reconstructed data only; new parent reconciliation is required and no dispatch ticket was restored."] : ["Live/uncertain child, writer, lease, checkpoint, or stopped state prevents reuse; no ticket was restored."]);
    }

    private async Task SerialAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _serial.Release(); }
    }

    private Node Owned(SchedulingTicket ticket) => ticket is not null && _tickets.TryGetValue(ticket, out var node) ? node : throw new SchedulingException("Ticket was not issued by this live parent or was revoked.");
    private void EnsureJournal() { if (_journalUncertain) throw new SchedulingException("Queue journal write is uncertain; dispatch remains disabled."); }
    private static bool Clear(SchedulingRecoveryFacts facts) => !facts.LiveChild && !facts.WriterPresent && !facts.LeasePresent && !facts.Uncertain;
    private static bool Active(Phase phase) => phase is Phase.Reserved or Phase.Running or Phase.Cleaning;

    private void CancelUnstarted(bool stop)
    {
        foreach (var (ticket, node) in _tickets.ToArray())
        {
            if (node.Phase != Phase.Reserved) continue;
            node.Reservation!.Dispose(); node.Reservation = null;
            node.Phase = stop ? Phase.Stopped : Phase.Pending;
            _tickets.Remove(ticket);
        }
        if (stop) foreach (var node in _nodes.Where(node => node.Phase == Phase.Pending)) node.Phase = Phase.Stopped;
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var checkpoint = new FixtureCheckpoint(1, true, _runId, _budget.PolicyIdentity, _fairness.Identity, Volatile.Read(ref _paused) != 0,
            Volatile.Read(ref _stopped) != 0, _journalUncertain, _nodes.Select(node => new NodeCheckpoint(node.Item.Id, node.Phase, node.Sequence, node.Bypasses, node.Outcome)).ToArray());
        try { await _store.AppendAsync(_runId, "fixture-scheduler-checkpoint", checkpoint, cancellationToken).ConfigureAwait(false); }
        catch { _journalUncertain = true; throw; }
    }

    private static void EnsurePlainStore(string root)
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new SchedulingException("Queue store cannot traverse a symlink/reparse ancestor.");
    }

    private enum Phase { Pending, Reserved, Running, Cleaning, Pass, Fail, Incomplete, Stopped }
    private sealed class Node(FixtureSchedulingItem item, int sequence)
    {
        public FixtureSchedulingItem Item { get; } = item;
        public int Sequence { get; } = sequence;
        public Phase Phase { get; set; }
        public int Bypasses { get; set; }
        public ScheduledOutcome? Outcome { get; set; }
        public ResourceReservation? Reservation { get; set; }
    }
    private sealed record NodeCheckpoint(string Id, Phase Phase, int Sequence, int Bypasses, ScheduledOutcome? Outcome);
    private sealed record FixtureCheckpoint(int Version, bool Fixture, string RunId, string BudgetPolicyIdentity, string FairnessPolicyIdentity,
        bool Paused, bool Stopped, bool Uncertain, IReadOnlyList<NodeCheckpoint> Nodes);
}
