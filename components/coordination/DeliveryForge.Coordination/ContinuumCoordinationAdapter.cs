namespace DeliveryForge.Coordination;

public sealed class ContinuumCoordinationAdapter
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan RenewalTarget = TimeSpan.FromSeconds(100);
    private static readonly TimeSpan DefaultRollbackTimeout = TimeSpan.FromSeconds(10);

    private readonly ICoordinationTransport _transport;
    private readonly IMonotonicClock _clock;
    private readonly IOwnedRunQuiescenceSource _quiescence;
    private readonly TimeSpan _rollbackTimeout;
    private readonly string _adapterId = Guid.NewGuid().ToString("D");
    private readonly object _ledgerGate = new();
    private readonly Dictionary<DispatchAuthority, Lifecycle> _authorities = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AcquireContinuation, Lifecycle> _acquireContinuations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ReleaseContinuation, Lifecycle> _releaseContinuations = new(ReferenceEqualityComparer.Instance);

    private ContinuumCoordinationAdapter(
        ICoordinationTransport transport,
        CoordinationCapabilities capabilities,
        IMonotonicClock clock,
        IOwnedRunQuiescenceSource quiescence,
        TimeSpan rollbackTimeout)
    {
        _transport = transport;
        Capabilities = capabilities;
        _clock = clock;
        _quiescence = quiescence;
        _rollbackTimeout = rollbackTimeout;
    }

    public CoordinationCapabilities Capabilities { get; }

    public static async Task<ContinuumCoordinationAdapter> CreateProductionAsync(
        ICoordinationTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        CoordinationCapabilities capabilities;
        try { capabilities = await transport.DetectCapabilitiesAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new CoordinationUnavailableException("Coordination capability detection failed; production profile is INCOMPLETE.", exception);
        }

        var reason = ValidateCapabilities(capabilities, requireFixture: false);
        if (reason is not null) throw new CoordinationUnavailableException(reason);
        throw new CoordinationUnavailableException(
            "Production coordination is INCOMPLETE until nucleoid/continuum#7 is implemented, deployed, configured, and host-attested.");
    }

    internal static async Task<ContinuumCoordinationAdapter> CreateForContractTestsAsync(
        ICoordinationTransport transport,
        IOwnedRunQuiescenceSource quiescence,
        CancellationToken cancellationToken = default,
        IMonotonicClock? clock = null,
        TimeSpan? rollbackTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(quiescence);
        clock ??= new StopwatchMonotonicClock();
        ValidateQuiescenceBinding(quiescence.Binding, clock);
        var capabilities = await transport.DetectCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var reason = ValidateCapabilities(capabilities, requireFixture: true);
        if (reason is not null) throw new CoordinationUnavailableException(reason);
        var boundedTimeout = rollbackTimeout ?? DefaultRollbackTimeout;
        if (boundedTimeout <= TimeSpan.Zero || boundedTimeout >= DefaultTtl)
            throw new ArgumentOutOfRangeException(nameof(rollbackTimeout), "Rollback must have a known positive bound below the lease TTL.");
        return new ContinuumCoordinationAdapter(transport, capabilities, clock, quiescence, boundedTimeout);
    }

    public bool MayDispatch(DispatchAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        lock (_ledgerGate)
        {
            if (!_authorities.TryGetValue(authority, out var lifecycle)) return false;
            if (!ReferenceEquals(lifecycle.Authority, authority) || lifecycle.Busy ||
                authority.AdapterId != _adapterId || authority.ClockDomainId != _clock.DomainId ||
                authority.LifecycleId != lifecycle.Id || lifecycle.Ownership != LeaseOwnership.Held ||
                lifecycle.Entries.Count != lifecycle.Resources.Length ||
                lifecycle.Entries.Values.Any(entry => entry.ReleaseState != LeaseReleaseState.Pending))
                return false;

            if (lifecycle.Entries.Values.Any(entry => _clock.Elapsed >= entry.Evidence.SafeDeadline))
            {
                lifecycle.Authority = null;
                lifecycle.CleanupAuthority = authority;
                lifecycle.Ownership = LeaseOwnership.Uncertain;
                lifecycle.Reason = "Dispatch authority expired at a conservative lease deadline.";
                EnsureReleaseContinuation(lifecycle);
                return false;
            }

            return true;
        }
    }

    public Task<AcquisitionResult> AcquireAllAsync(
        IEnumerable<CoordinationResource> resources,
        LeaseOwner owner,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(owner);
        if (ttl != DefaultTtl) throw new ArgumentOutOfRangeException(nameof(ttl), "V1 ownership uses the 300-second TTL exactly.");
        var ordered = resources.Distinct().Order(Utf8LexicalComparer.Instance).ToArray();
        if (ordered.Length == 0) throw new ArgumentException("At least one coordination resource is required.", nameof(resources));
        var lifecycle = new Lifecycle(owner, ordered);
        return ContinueAcquisitionAsync(lifecycle, cancellationToken);
    }

    public Task<AcquisitionResult> ReconcileAcquireAsync(
        AcquireContinuation continuation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        Lifecycle lifecycle;
        lock (_ledgerGate)
        {
            if (!_acquireContinuations.TryGetValue(continuation, out lifecycle!) ||
                !ReferenceEquals(lifecycle.AcquireContinuation, continuation) || lifecycle.PendingAcquire is null || lifecycle.Busy)
                throw new CoordinationUncertainException("Acquire continuation is foreign, stale, or no longer reconcilable.");
            lifecycle.Busy = true;
        }
        return ContinueAcquisitionAsync(lifecycle, cancellationToken, alreadyBusy: true);
    }

    public async Task<RenewalResult> RenewAllAsync(
        DispatchAuthority authority,
        CancellationToken cancellationToken = default)
    {
        var lifecycle = BeginAuthorityTransition(authority, "renewal");
        foreach (var entry in HeldEntries(lifecycle))
        {
            RenewTransportResult result;
            var sentAt = _clock.Elapsed;
            try
            {
                result = await _transport.RenewAsync(new RenewRequest(entry.Evidence, entry.RenewRequestId), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return FinishFailedRenewal(lifecycle, $"Renew transport failed: {exception.GetType().Name}.");
            }
            var receivedAt = _clock.Elapsed;
            try
            {
                if (result.Outcome != TransportOutcome.Renewed || result.ServerTime is null || result.ExpiresAt is null)
                    throw new CoordinationProtocolException($"Renewal was not authoritative: {result.Reason}");
                entry.Evidence = CreateEvidence(entry.Evidence.Resource, lifecycle.Owner, entry.Evidence.LeaseId,
                    entry.Evidence.FencingToken, result.ServerTime.Value, result.ExpiresAt.Value,
                    sentAt, receivedAt, entry.Evidence.Generation);
            }
            catch (CoordinationException exception)
            {
                return FinishFailedRenewal(lifecycle, exception.Message);
            }
        }

        lock (_ledgerGate)
        {
            lifecycle.Busy = false;
            lifecycle.Ownership = LeaseOwnership.Held;
            lifecycle.Reason = "All leases renewed authoritatively.";
            lifecycle.Authority = NewAuthority(lifecycle);
            _authorities.Add(lifecycle.Authority, lifecycle);
            return new RenewalResult(Snapshot(lifecycle), lifecycle.Authority, null);
        }
    }

    public async Task<CoordinationInspection> InspectAsync(
        CoordinationResource resource,
        LeaseOwner owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(owner);
        var sentAt = _clock.Elapsed;
        InspectTransportResult result;
        try
        {
            result = await _transport.InspectAsync(new InspectRequest(resource, owner, FreshRequestId()), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new CoordinationUncertainException("Inspect transport failed; ownership is uncertain.", exception);
        }
        var receivedAt = _clock.Elapsed;
        try
        {
            if (result.Outcome != TransportOutcome.Inspected)
                throw new CoordinationProtocolException("Inspect response was not authoritative.");
            if (!result.Held)
            {
                if (result.CallerOwnsLease || result.LeaseId is not null || result.FencingToken is not null ||
                    result.ServerTime is not null || result.ExpiresAt is not null)
                    throw new CoordinationProtocolException("Available inspect response contained lease details.");
                return new CoordinationInspection(InspectionState.Available, null, result.Reason);
            }
            if (!result.CallerOwnsLease)
                return new CoordinationInspection(InspectionState.HeldByOther, null, "resource is held by another owner");
            if (result.ServerTime is null || result.ExpiresAt is null)
                throw new CoordinationProtocolException("Owned inspect response omitted authoritative times.");
            var evidence = CreateEvidence(resource, owner, result.LeaseId ?? "", result.FencingToken ?? "",
                result.ServerTime.Value, result.ExpiresAt.Value, sentAt, receivedAt, FreshRequestId());
            return new CoordinationInspection(InspectionState.HeldByCaller, evidence, "caller owns the inspected lease");
        }
        catch (CoordinationException exception)
        {
            throw new CoordinationUncertainException("Inspect response was malformed; ownership is uncertain.", exception);
        }
    }

    public Task<ReleaseResult> ReleaseAllAsync(
        DispatchAuthority authority,
        CancellationToken cancellationToken = default)
    {
        var lifecycle = BeginAuthorityTransition(authority, "release");
        EnsureReleaseContinuation(lifecycle);
        return ContinueReleaseCoreAsync(lifecycle, cancellationToken);
    }

    public Task<ReleaseResult> ContinueReleaseAsync(
        ReleaseContinuation continuation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        Lifecycle lifecycle;
        lock (_ledgerGate)
        {
            if (!_releaseContinuations.TryGetValue(continuation, out lifecycle!) ||
                !ReferenceEquals(lifecycle.ReleaseContinuation, continuation) || lifecycle.Busy)
                throw new CoordinationUncertainException("Release continuation is foreign, stale, or already complete.");
            lifecycle.Busy = true;
        }
        return ContinueReleaseCoreAsync(lifecycle, cancellationToken);
    }

    private async Task<AcquisitionResult> ContinueAcquisitionAsync(
        Lifecycle lifecycle,
        CancellationToken cancellationToken,
        bool alreadyBusy = false)
    {
        if (!alreadyBusy)
        {
            lock (_ledgerGate) lifecycle.Busy = true;
        }

        var targets = new List<CoordinationResource>();
        if (lifecycle.PendingAcquire is not null) targets.Add(lifecycle.PendingAcquire.Resource);
        targets.AddRange(lifecycle.Resources.Where(resource =>
            (lifecycle.PendingAcquire is null || resource != lifecycle.PendingAcquire.Resource) &&
            (!lifecycle.Entries.TryGetValue(resource, out var entry) || entry.ReleaseState == LeaseReleaseState.Released)));

        foreach (var resource in targets)
        {
            PendingAcquire pending;
            lock (_ledgerGate)
            {
                pending = lifecycle.PendingAcquire is not null && lifecycle.PendingAcquire.Resource == resource
                    ? lifecycle.PendingAcquire
                    : new PendingAcquire(resource, FreshRequestId());
                lifecycle.PendingAcquire = pending; // durable logical ledger transition precedes invocation
            }

            AcquireTransportResult response;
            var sentAt = _clock.Elapsed;
            try
            {
                response = await _transport.AcquireAsync(
                    new AcquireRequest(resource, lifecycle.Owner, DefaultTtl, pending.RequestId), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await RollBackHeldPrefixAsync(lifecycle).ConfigureAwait(false);
                return FinishUncertainAcquire(lifecycle, pending, $"Acquire transport failed: {exception.GetType().Name}.");
            }
            var receivedAt = _clock.Elapsed;

            if (response.Outcome == TransportOutcome.Contended)
            {
                lock (_ledgerGate) lifecycle.PendingAcquire = null;
                await RollBackHeldPrefixAsync(lifecycle).ConfigureAwait(false);
                lock (_ledgerGate)
                {
                    lifecycle.Busy = false;
                    var clean = lifecycle.Entries.Values.All(entry => entry.ReleaseState == LeaseReleaseState.Released);
                    lifecycle.Ownership = clean ? LeaseOwnership.Contended : LeaseOwnership.Uncertain;
                    lifecycle.Reason = clean ? response.Reason : "Contention occurred and prefix cleanup remains uncertain.";
                    if (!clean) EnsureReleaseContinuation(lifecycle);
                    return Acquisition(lifecycle, null);
                }
            }

            if (response.Outcome != TransportOutcome.Acquired)
            {
                await RollBackHeldPrefixAsync(lifecycle).ConfigureAwait(false);
                return FinishUncertainAcquire(lifecycle, pending, $"Acquire was not authoritative: {response.Reason}");
            }

            try
            {
                if (response.ServerTime is null || response.ExpiresAt is null)
                    throw new CoordinationProtocolException("Acquire response omitted authoritative times.");
                var evidence = CreateEvidence(resource, lifecycle.Owner, response.LeaseId ?? "", response.FencingToken ?? "",
                    response.ServerTime.Value, response.ExpiresAt.Value, sentAt, receivedAt, FreshRequestId());
                lock (_ledgerGate)
                {
                    lifecycle.Entries[resource] = new LeaseEntry(evidence);
                    lifecycle.PendingAcquire = null;
                }
            }
            catch (CoordinationException exception)
            {
                await RollBackHeldPrefixAsync(lifecycle).ConfigureAwait(false);
                return FinishUncertainAcquire(lifecycle, pending, $"Acquire response was malformed: {exception.Message}");
            }
        }

        lock (_ledgerGate)
        {
            lifecycle.Busy = false;
            lifecycle.Ownership = LeaseOwnership.Held;
            lifecycle.Reason = "All resources are authoritatively held.";
            lifecycle.AcquireContinuation = null;
            lifecycle.Authority = NewAuthority(lifecycle);
            _authorities.Add(lifecycle.Authority, lifecycle);
            return Acquisition(lifecycle, null);
        }
    }

    private async Task RollBackHeldPrefixAsync(Lifecycle lifecycle)
    {
        foreach (var entry in HeldEntries(lifecycle).Reverse())
        {
            var result = await InvokeBoundedReleaseAsync(lifecycle, entry).ConfigureAwait(false);
            lock (_ledgerGate)
            {
                if (result?.Outcome == TransportOutcome.Released)
                {
                    entry.ReleaseState = LeaseReleaseState.Released;
                    entry.ReleaseReason = result.Reason;
                }
                else
                {
                    entry.ReleaseState = LeaseReleaseState.Unknown;
                    entry.ReleaseReason = result?.Reason ?? "bounded rollback elapsed without an authoritative response";
                }
            }
        }
    }

    private async Task<ReleaseTransportResult?> InvokeBoundedReleaseAsync(Lifecycle lifecycle, LeaseEntry entry)
    {
        Task<ReleaseTransportResult> transportTask;
        using var timeout = new CancellationTokenSource(_rollbackTimeout);
        try
        {
            transportTask = _transport.ReleaseAsync(
                new ReleaseRequest(entry.Evidence.LeaseId, lifecycle.Owner, entry.Evidence.FencingToken, entry.ReleaseRequestId),
                timeout.Token);
        }
        catch (Exception) { return null; }

        try
        {
            var completed = await Task.WhenAny(transportTask, Task.Delay(_rollbackTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, transportTask))
            {
                _ = transportTask.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return null;
            }
            return await transportTask.ConfigureAwait(false);
        }
        catch (Exception) { return null; }
    }

    private AcquisitionResult FinishUncertainAcquire(Lifecycle lifecycle, PendingAcquire pending, string reason)
    {
        lock (_ledgerGate)
        {
            lifecycle.Busy = false;
            lifecycle.Ownership = LeaseOwnership.Uncertain;
            lifecycle.Reason = reason;
            lifecycle.AcquireContinuation ??= new AcquireContinuation();
            _acquireContinuations[lifecycle.AcquireContinuation] = lifecycle;
            if (lifecycle.Entries.Values.Any(entry => entry.ReleaseState != LeaseReleaseState.Released))
                EnsureReleaseContinuation(lifecycle);
            return Acquisition(lifecycle, new AcquireUncertainty(pending.Resource, pending.RequestId, lifecycle.Owner, reason));
        }
    }

    private RenewalResult FinishFailedRenewal(Lifecycle lifecycle, string reason)
    {
        lock (_ledgerGate)
        {
            lifecycle.Busy = false;
            lifecycle.Ownership = LeaseOwnership.Uncertain;
            lifecycle.Reason = reason;
            EnsureReleaseContinuation(lifecycle);
            return new RenewalResult(Snapshot(lifecycle), null, lifecycle.ReleaseContinuation);
        }
    }

    private async Task<ReleaseResult> ContinueReleaseCoreAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        var observations = new List<QuiescenceEvidence>();
        foreach (var entry in HeldEntries(lifecycle).Reverse())
        {
            QuiescenceEvidence evidence;
            var observationStarted = _clock.Elapsed;
            try { evidence = await _quiescence.ObserveAsync(lifecycle.Owner, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception)
            {
                entry.ReleaseState = LeaseReleaseState.Unknown;
                entry.ReleaseReason = $"Quiescence observation failed: {exception.GetType().Name}.";
                break;
            }
            var observationFinished = _clock.Elapsed;
            observations.Add(evidence);
            if (!IsValidQuiescence(evidence, lifecycle.Owner, observationStarted, observationFinished))
            {
                entry.ReleaseState = LeaseReleaseState.Unknown;
                entry.ReleaseReason = "Bound fresh quiescence was not established or owned activity resumed.";
                break;
            }

            try
            {
                var result = await _transport.ReleaseAsync(
                    new ReleaseRequest(entry.Evidence.LeaseId, lifecycle.Owner, entry.Evidence.FencingToken, entry.ReleaseRequestId),
                    cancellationToken).ConfigureAwait(false);
                if (result.Outcome == TransportOutcome.Released)
                {
                    entry.ReleaseState = LeaseReleaseState.Released;
                    entry.ReleaseReason = result.Reason;
                }
                else
                {
                    entry.ReleaseState = LeaseReleaseState.Unknown;
                    entry.ReleaseReason = result.Reason; // LeaseLost never proves release.
                }
            }
            catch (Exception exception)
            {
                entry.ReleaseState = LeaseReleaseState.Unknown;
                entry.ReleaseReason = $"Release transport failed: {exception.GetType().Name}.";
            }
        }

        lock (_ledgerGate)
        {
            lifecycle.Busy = false;
            var released = lifecycle.Entries.Values.All(entry => entry.ReleaseState == LeaseReleaseState.Released);
            lifecycle.Ownership = released ? LeaseOwnership.Released : LeaseOwnership.Uncertain;
            lifecycle.Reason = released ? "Every lease was authoritatively released." : "Release remains partially uncertain.";
            if (released)
            {
                if (lifecycle.ReleaseContinuation is not null) _releaseContinuations.Remove(lifecycle.ReleaseContinuation);
                lifecycle.ReleaseContinuation = null;
            }
            else EnsureReleaseContinuation(lifecycle);
            return Release(lifecycle, observations);
        }
    }

    private Lifecycle BeginAuthorityTransition(DispatchAuthority authority, string operation)
    {
        ArgumentNullException.ThrowIfNull(authority);
        lock (_ledgerGate)
        {
            if (!_authorities.TryGetValue(authority, out var lifecycle) || lifecycle.Busy ||
                authority.AdapterId != _adapterId ||
                authority.ClockDomainId != _clock.DomainId || authority.LifecycleId != lifecycle.Id)
                throw new CoordinationUncertainException($"{operation} requires the adapter's current live dispatch authority.");
            var currentHeld = ReferenceEquals(lifecycle.Authority, authority) && lifecycle.Ownership == LeaseOwnership.Held;
            var expiredCleanup = operation == "release" && ReferenceEquals(lifecycle.CleanupAuthority, authority) &&
                lifecycle.Authority is null && lifecycle.Ownership == LeaseOwnership.Uncertain;
            if (!currentHeld && !expiredCleanup)
                throw new CoordinationUncertainException($"{operation} requires the adapter's current live dispatch authority.");
            lifecycle.Authority = null; // revoke the exact original reference before any external observation or invoke
            lifecycle.CleanupAuthority = null;
            lifecycle.Busy = true;
            return lifecycle;
        }
    }

    private LeaseEvidence CreateEvidence(
        CoordinationResource resource, LeaseOwner owner, string leaseId, string fencingToken,
        DateTimeOffset serverTime, DateTimeOffset expiresAt, TimeSpan sentAt, TimeSpan receivedAt, string generation)
    {
        if (expiresAt - serverTime != DefaultTtl)
            throw new CoordinationProtocolException("Lease response did not grant the exact 300-second TTL.");
        var safeDeadline = LeaseDeadline.ComputeSafeDeadline(serverTime, expiresAt, receivedAt - sentAt, receivedAt, TimeSpan.Zero);
        return new LeaseEvidence(resource, owner, leaseId, fencingToken, serverTime, expiresAt,
            safeDeadline, receivedAt + RenewalTarget, generation);
    }

    private bool IsValidQuiescence(
        QuiescenceEvidence evidence, LeaseOwner owner, TimeSpan started, TimeSpan finished)
    {
        var binding = _quiescence.Binding;
        return evidence is not null && evidence.Quiescent && evidence.Owner == owner &&
            evidence.IssuerId == binding.IssuerId && evidence.SourceId == binding.SourceId &&
            evidence.ClockDomainId == binding.ClockDomainId && binding.ClockDomainId == _clock.DomainId &&
            !string.IsNullOrWhiteSpace(evidence.EvidenceId) && !evidence.EvidenceId.Any(char.IsControl) &&
            finished >= started && finished - started <= binding.MaximumObservationLatency &&
            evidence.ObservedAt >= started && evidence.ObservedAt <= finished &&
            finished - evidence.ObservedAt <= binding.FreshnessLimit;
    }

    private DispatchAuthority NewAuthority(Lifecycle lifecycle) =>
        new(_adapterId, _clock.DomainId, lifecycle.Id, FreshRequestId());

    private void EnsureReleaseContinuation(Lifecycle lifecycle)
    {
        if (lifecycle.ReleaseContinuation is not null) return;
        lifecycle.ReleaseContinuation = new ReleaseContinuation();
        _releaseContinuations.Add(lifecycle.ReleaseContinuation, lifecycle);
    }

    private static LeaseEntry[] HeldEntries(Lifecycle lifecycle) =>
        lifecycle.Entries.Values.Where(entry => entry.ReleaseState != LeaseReleaseState.Released)
            .OrderBy(entry => entry.Evidence.Resource, Utf8LexicalComparer.Instance).ToArray();

    private static LeaseSnapshot Snapshot(Lifecycle lifecycle) =>
        new(lifecycle.Owner,
            lifecycle.Entries.Values.OrderBy(entry => entry.Evidence.Resource, Utf8LexicalComparer.Instance).Select(entry => entry.Evidence),
            lifecycle.Ownership, lifecycle.Reason);

    private static IReadOnlyList<LeaseReleaseProgress> Progress(Lifecycle lifecycle) =>
        lifecycle.Entries.Values.OrderBy(entry => entry.Evidence.Resource, Utf8LexicalComparer.Instance)
            .Select(entry => new LeaseReleaseProgress(entry.Evidence.Resource, entry.Evidence.LeaseId,
                entry.Evidence.Generation, entry.ReleaseRequestId, entry.ReleaseState, entry.ReleaseReason)).ToArray();

    private static AcquisitionResult Acquisition(Lifecycle lifecycle, AcquireUncertainty? uncertainty) =>
        new(Snapshot(lifecycle), lifecycle.Authority, lifecycle.AcquireContinuation,
            lifecycle.ReleaseContinuation, uncertainty, Progress(lifecycle));

    private static ReleaseResult Release(Lifecycle lifecycle, IReadOnlyList<QuiescenceEvidence> evidence) =>
        new(Snapshot(lifecycle), Progress(lifecycle), lifecycle.ReleaseContinuation, evidence);

    private static void ValidateQuiescenceBinding(QuiescenceSourceBinding? binding, IMonotonicClock clock)
    {
        if (binding is null || !binding.BoundsKnown || binding.MaximumObservationLatency <= TimeSpan.Zero ||
            binding.FreshnessLimit <= TimeSpan.Zero || binding.ClockDomainId != clock.DomainId ||
            string.IsNullOrWhiteSpace(binding.IssuerId) || string.IsNullOrWhiteSpace(binding.SourceId))
            throw new CoordinationUnavailableException("Quiescence source requires known production bounds and exact issuer/source/clock binding.");
    }

    private static string? ValidateCapabilities(CoordinationCapabilities? capabilities, bool requireFixture)
    {
        if (capabilities is null) return "Coordination capability response is missing.";
        if (requireFixture != capabilities.Fixture)
            return requireFixture ? "Contract-test construction requires an explicitly marked fixture capability."
                : "A fixture capability cannot construct a production coordination adapter.";
        if (capabilities.Availability != CoordinationAvailability.Available) return "Coordination capability is unavailable or incomplete.";
        if (capabilities.ContractVersion != CoordinationCapabilities.V1ContractVersion) return "Coordination contract version is unsupported.";
        if (capabilities.Operations is null || !Enum.GetValues<CoordinationOperation>().All(capabilities.Operations.Contains))
            return "Required coordination operations are missing.";
        if (capabilities.MinimumTtl > DefaultTtl || capabilities.MaximumTtl < DefaultTtl) return "The required 300-second TTL is unsupported.";
        if (capabilities.IdempotencyHorizon < TimeSpan.FromHours(24)) return "The required idempotency horizon is unsupported.";
        if (!capabilities.DecimalStringFencing || !capabilities.HolderIdentityMasking || !capabilities.AuthenticatedExactScope)
            return "Fencing, masking, or authenticated exact-scope capability is incomplete.";
        return null;
    }

    private static string FreshRequestId() => Guid.NewGuid().ToString("D");

    private sealed class Lifecycle(LeaseOwner owner, CoordinationResource[] resources)
    {
        public string Id { get; } = Guid.NewGuid().ToString("D");
        public LeaseOwner Owner { get; } = owner;
        public CoordinationResource[] Resources { get; } = resources;
        public Dictionary<CoordinationResource, LeaseEntry> Entries { get; } = [];
        public PendingAcquire? PendingAcquire { get; set; }
        public DispatchAuthority? Authority { get; set; }
        public DispatchAuthority? CleanupAuthority { get; set; }
        public AcquireContinuation? AcquireContinuation { get; set; }
        public ReleaseContinuation? ReleaseContinuation { get; set; }
        public LeaseOwnership Ownership { get; set; } = LeaseOwnership.Uncertain;
        public string Reason { get; set; } = "Acquisition has not completed.";
        public bool Busy { get; set; }
    }

    private sealed class LeaseEntry(LeaseEvidence evidence)
    {
        public LeaseEvidence Evidence { get; set; } = evidence;
        public string RenewRequestId { get; } = FreshRequestId();
        public string ReleaseRequestId { get; } = FreshRequestId();
        public LeaseReleaseState ReleaseState { get; set; } = LeaseReleaseState.Pending;
        public string ReleaseReason { get; set; } = "release not attempted";
    }

    private sealed record PendingAcquire(CoordinationResource Resource, string RequestId);
}
