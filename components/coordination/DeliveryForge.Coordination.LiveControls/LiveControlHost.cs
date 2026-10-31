using System.Diagnostics;
using DeliveryForge.Coordination;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DeliveryForge.Coordination.Tests")]

namespace DeliveryForge.Coordination.LiveControls;

// Root-owned, non-publishing live control host. It has no process/model/GitHub port.
// Its only dispatchable operation is a tracked, synchronous synthetic sink action.
internal sealed class LiveControlHost(LeaseOwner owner) : IOwnedRunQuiescenceSource, IMonotonicClock
{
    private readonly object _gate = new();
    private readonly long _original = Stopwatch.GetTimestamp();
    private readonly List<Task> _actualActions = [];
    private ContinuumCoordinationAdapter? _adapter;
    internal int Dispatches { get; private set; }
    internal int Publications { get; private set; }
    public string DomainId { get; } = Guid.NewGuid().ToString("D");
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_original);
    public QuiescenceSourceBinding Binding => new("parent-live-control-host", DomainId, DomainId,
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), true);

    // Live controls use the same durable store as the real parent host.
    internal static FileCoordinationCheckpointStore CreateCheckpointStore(string root) => new(root);

    internal void Bind(ContinuumCoordinationAdapter adapter)
    {
        lock (_gate)
        {
            if (_adapter is not null) throw new InvalidOperationException("Live control host binds once.");
            _adapter = adapter;
        }
    }

    internal bool TryDispatch(DispatchAuthority authority) => Act(authority, publication: false);
    internal bool TryPublish(DispatchAuthority authority) => Act(authority, publication: true);

    private bool Act(DispatchAuthority authority, bool publication)
    {
        lock (_gate)
        {
            if (_adapter is null || !_adapter.MayDispatch(authority)) return false;
            if (_actualActions.Count >= 16) throw new InvalidOperationException("Finite synthetic action bound.");
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _actualActions.Add(operation.Task);
            try
            {
                if (publication) Publications++; else Dispatches++;
                operation.SetResult();
                return true;
            }
            catch (Exception error) { operation.SetException(error); throw; }
        }
    }

    public Task<QuiescenceEvidence> ObserveAsync(LeaseOwner requested, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (requested != owner) throw new CoordinationProtocolException("Foreign live-control owner.");
            var quiet = _actualActions.All(task => task.IsCompletedSuccessfully);
            return Task.FromResult(new QuiescenceEvidence(owner, Binding.IssuerId, Binding.SourceId, DomainId,
                Elapsed, Guid.NewGuid().ToString("D"), quiet));
        }
    }
}

