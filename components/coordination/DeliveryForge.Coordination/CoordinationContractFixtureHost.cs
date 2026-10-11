using System.Collections.Frozen;

namespace DeliveryForge.Coordination;

// Explicit test-profile boundary. No production constructor, reducer or opaque handle is exposed.
// All ownership decisions remain in the existing fixture adapter and its opaque-reference ledger.
public sealed class CoordinationContractFixtureHost
{
    private readonly ContinuumCoordinationAdapter _adapter;
    private readonly IReadOnlyList<CoordinationResource> _resources;
    private readonly IOwnedRunQuiescenceSource _quiescence;
    private readonly QuiescenceSourceBinding _binding;
    private readonly IMonotonicClock _clock;
    private readonly string _clockDomain;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly HashSet<DispatchAuthority> _authorities = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ReleaseContinuation> _continuations = new(ReferenceEqualityComparer.Instance);
    private TimeSpan _lastElapsed;

    private CoordinationContractFixtureHost(ContinuumCoordinationAdapter adapter, LeaseOwner receiver,
        IReadOnlyList<CoordinationResource> resources, IOwnedRunQuiescenceSource quiescence,
        QuiescenceSourceBinding binding, IMonotonicClock clock, TimeSpan elapsed)
    {
        _adapter = adapter; Receiver = receiver; _resources = resources; _quiescence = quiescence;
        _binding = binding; _clock = clock; _clockDomain = binding.ClockDomainId; _lastElapsed = elapsed;
    }
    public bool Fixture => true;
    public bool ProductionCapable => false;
    public LeaseOwner Receiver { get; }

    public static async Task<CoordinationContractFixtureHost> CreateAsync(
        ICoordinationTransport fixtureTransport, LeaseOwner receiver, IReadOnlyList<CoordinationResource> resources,
        IOwnedRunQuiescenceSource quiescence, IMonotonicClock clock, ICoordinationCheckpointStore checkpoints,
        TimeSpan operationTimeout, TimeSpan maximumClockUncertainty, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixtureTransport); ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(resources); ArgumentNullException.ThrowIfNull(quiescence);
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(checkpoints);
        if (resources.Count is < 1 or > 16) throw new CoordinationUnavailableException("Contract fixture resource count is unavailable.");
        var supplied = resources.ToArray();
        if (supplied.Length is < 1 or > 16 || supplied.Any(resource => resource is null) || supplied.Distinct().Count() != supplied.Length)
            throw new CoordinationUnavailableException("Contract fixture requires a bounded explicit unique resource set.");
        var captured = Array.AsReadOnly(supplied.Select(resource => new CoordinationResource(resource.Scope, resource.ResourceKey)).ToArray());
        var owner = new LeaseOwner(receiver.PrincipalId, receiver.RunId);
        var binding = quiescence.Binding;
        var clockDomain = clock.DomainId;
        var initialElapsed = clock.Elapsed;
        if (binding is null || !binding.BoundsKnown || string.IsNullOrWhiteSpace(clockDomain) ||
            binding.ClockDomainId != clockDomain || initialElapsed < TimeSpan.Zero ||
            binding.MaximumObservationLatency <= TimeSpan.Zero || binding.MaximumObservationLatency >= ContinuumCoordinationAdapter.DefaultTtl ||
            binding.FreshnessLimit <= TimeSpan.Zero || binding.FreshnessLimit >= ContinuumCoordinationAdapter.DefaultTtl)
            throw new CoordinationUnavailableException("Contract fixture requires stable known finite source/clock bounds.");
        binding = binding with { };
        var adapter = await ContinuumCoordinationAdapter.CreateForContractTestsAsync(new SnapshotTransport(fixtureTransport),
            quiescence, cancellationToken, clock, operationTimeout, checkpoints, maximumClockUncertainty).ConfigureAwait(false);
        var elapsed = clock.Elapsed;
        if (!adapter.Capabilities.Fixture || quiescence.Binding != binding || clock.DomainId != clockDomain || elapsed < initialElapsed)
            throw new CoordinationUnavailableException("Contract fixture binding changed during construction.");
        return new(adapter, owner, captured, quiescence, binding, clock, elapsed);
    }

    public Task<CoordinationFixtureObservation<AcquisitionResult>> AcquireAsync(CancellationToken cancellationToken = default) =>
        SerialAsync(async () =>
        {
            var result = await _adapter.AcquireAllAsync(_resources, Receiver, ContinuumCoordinationAdapter.DefaultTtl, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (result.Authority is not null) _authorities.Add(result.Authority);
                if (result.ReleaseContinuation is not null) _continuations.Add(result.ReleaseContinuation);
            }
            return new CoordinationFixtureObservation<AcquisitionResult>(result);
        }, cancellationToken);

    public bool MayDispatchFixture(DispatchAuthority authority)
    {
        lock (_gate)
        {
            if (authority is null || !_authorities.Contains(authority)) return false;
            try { EnsureBinding(); return _adapter.MayDispatch(authority); }
            catch (CoordinationException) { return false; }
        }
    }

    public Task<CoordinationFixtureObservation<RenewalResult>> RenewAsync(DispatchAuthority authority, CancellationToken cancellationToken = default) =>
        SerialAsync(async () =>
        {
            lock (_gate)
                if (authority is null || !_authorities.Contains(authority)) throw new CoordinationUncertainException("Fixture authority belongs to another receiver host.");
            var result = await _adapter.RenewAllAsync(authority, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (result.Authority is not null) _authorities.Add(result.Authority);
                if (result.ReleaseContinuation is not null) _continuations.Add(result.ReleaseContinuation);
            }
            return new CoordinationFixtureObservation<RenewalResult>(result);
        }, cancellationToken);

    public Task<CoordinationFixtureObservation<ReleaseResult>> ReleaseAsync(DispatchAuthority authority, CancellationToken cancellationToken = default) =>
        SerialAsync(async () =>
        {
            lock (_gate)
                if (authority is null || !_authorities.Contains(authority)) throw new CoordinationUncertainException("Fixture authority belongs to another receiver host.");
            var result = await _adapter.ReleaseAllAsync(authority, cancellationToken).ConfigureAwait(false);
            return ObserveRelease(result);
        }, cancellationToken);

    public Task<CoordinationFixtureObservation<ReleaseResult>> ReleaseAsync(ReleaseContinuation continuation, CancellationToken cancellationToken = default) =>
        SerialAsync(async () =>
        {
            lock (_gate)
                if (continuation is null || !_continuations.Contains(continuation)) throw new CoordinationUncertainException("Fixture continuation belongs to another receiver host.");
            var result = await _adapter.ContinueReleaseAsync(continuation, cancellationToken).ConfigureAwait(false);
            return ObserveRelease(result);
        }, cancellationToken);

    private CoordinationFixtureObservation<ReleaseResult> ObserveRelease(ReleaseResult result)
    {
        lock (_gate)
            if (result.Continuation is not null) _continuations.Add(result.Continuation);
        return new(result);
    }

    private async Task<T> SerialAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate) EnsureBinding();
            var result = await action().ConfigureAwait(false);
            lock (_gate) EnsureBinding();
            return result;
        }
        finally { _serial.Release(); }
    }

    private void EnsureBinding()
    {
        var elapsed = _clock.Elapsed;
        if (!_adapter.Capabilities.Fixture || _quiescence.Binding != _binding || _clock.DomainId != _clockDomain || elapsed < TimeSpan.Zero || elapsed < _lastElapsed)
            throw new CoordinationUncertainException("Fixture receiver source/clock binding changed; no further authority can be granted.");
        _lastElapsed = elapsed;
    }

    // Copy mutable capability collections without relabelling their fixture marker or inventing operations.
    private sealed class SnapshotTransport(ICoordinationTransport source) : ICoordinationTransport
    {
        public async Task<CoordinationCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            var capabilities = await source.DetectCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (capabilities is null) return null!;
            return capabilities with
            {
                Operations = capabilities.Operations is null ? null! : capabilities.Operations.ToFrozenSet(),
                Limitations = Array.AsReadOnly((capabilities.Limitations ?? []).ToArray())
            };
        }
        public Task<AcquireTransportResult> AcquireAsync(AcquireRequest request, CancellationToken cancellationToken = default) => source.AcquireAsync(request, cancellationToken);
        public Task<RenewTransportResult> RenewAsync(RenewRequest request, CancellationToken cancellationToken = default) => source.RenewAsync(request, cancellationToken);
        public Task<InspectTransportResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default) => source.InspectAsync(request, cancellationToken);
        public Task<ReleaseTransportResult> ReleaseAsync(ReleaseRequest request, CancellationToken cancellationToken = default) => source.ReleaseAsync(request, cancellationToken);
    }
}

public sealed class CoordinationFixtureObservation<T>
{
    internal CoordinationFixtureObservation(T value) => Value = value;
    public bool Fixture => true;
    public bool ProductionCapable => false;
    public T Value { get; }
}
