namespace DeliveryForge.Coordination;

public sealed class ContinuumCoordinationAdapter
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan RenewalTarget = TimeSpan.FromSeconds(100);
    private static readonly TimeSpan DefaultRollbackTimeout = TimeSpan.FromSeconds(10);

    private readonly ICoordinationTransport _transport;
    private readonly IMonotonicClock _clock;
    private readonly IOwnedRunQuiescenceSource _quiescence;
    private readonly QuiescenceSourceBinding _quiescenceBinding;
    private readonly ICoordinationCheckpointStore _checkpoints;
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
        QuiescenceSourceBinding quiescenceBinding,
        ICoordinationCheckpointStore checkpoints,
        TimeSpan rollbackTimeout)
    {
        _transport = transport;
        Capabilities = capabilities;
        _clock = clock;
        _quiescence = quiescence;
        _quiescenceBinding = quiescenceBinding;
        _checkpoints = checkpoints;
        _rollbackTimeout = rollbackTimeout;
    }

    public CoordinationCapabilities Capabilities { get; }

    public static Task<ContinuumCoordinationAdapter> CreateProductionAsync(
        ICoordinationTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return Task.FromException<ContinuumCoordinationAdapter>(new CoordinationUnavailableException(
            "Production coordination is INCOMPLETE until nucleoid/continuum#7 is implemented, deployed, configured, authenticated, versioned, and host-attested."));
    }

    internal static async Task<ContinuumCoordinationAdapter> CreateForContractTestsAsync(
        ICoordinationTransport transport,
        IOwnedRunQuiescenceSource quiescence,
        CancellationToken cancellationToken = default,
        IMonotonicClock? clock = null,
        TimeSpan? rollbackTimeout = null,
        ICoordinationCheckpointStore? checkpoints = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(quiescence);
        clock ??= new StopwatchMonotonicClock();
        var binding = quiescence.Binding;
        ValidateQuiescenceBinding(binding, clock);
        CoordinationCapabilities? capabilities;
        try { capabilities = await transport.DetectCapabilitiesAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception) { throw new CoordinationUnavailableException("Coordination capability detection failed."); }
        var reason = ValidateCapabilities(capabilities, requireFixture: true);
        if (reason is not null) throw new CoordinationUnavailableException(reason);
        var boundedTimeout = rollbackTimeout ?? DefaultRollbackTimeout;
        if (boundedTimeout <= TimeSpan.Zero || boundedTimeout >= DefaultTtl)
            throw new ArgumentOutOfRangeException(nameof(rollbackTimeout), "Rollback must have a known positive bound below the lease TTL.");
        return new ContinuumCoordinationAdapter(transport, capabilities!, clock, quiescence, binding,
            checkpoints ?? new VolatileCheckpointStore(), boundedTimeout);
    }

    public bool MayDispatch(DispatchAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        lock (_ledgerGate)
        {
            if (!_authorities.TryGetValue(authority, out var lifecycle) ||
                !ReferenceEquals(lifecycle.Authority, authority) || lifecycle.Busy ||
                authority.AdapterId != _adapterId || authority.ClockDomainId != _clock.DomainId ||
                authority.LifecycleId != lifecycle.Id || lifecycle.Phase != CoordinationLifecyclePhase.Held ||
                !CanBeHeld(lifecycle)) return false;

            if (lifecycle.Entries.Values.Any(entry => _clock.Elapsed >= entry.Evidence.SafeDeadline))
            {
                lifecycle.CleanupAuthority = authority;
                lifecycle.Authority = null;
                lifecycle.Phase = CoordinationLifecyclePhase.Cleaning;
                lifecycle.Reason = "authority-expired";
                EnsureReleaseContinuationUnderGate(lifecycle);
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
        lock (_ledgerGate) lifecycle.Busy = true;
        return AcquireCoreAsync(lifecycle, cancellationToken);
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
                !ReferenceEquals(lifecycle.AcquireContinuation, continuation) || lifecycle.Busy ||
                lifecycle.Phase != CoordinationLifecyclePhase.Cleaning ||
                lifecycle.PendingIntent?.Prepared.Kind != CoordinationIntentKind.Acquire)
                throw new CoordinationUncertainException("Acquire continuation is foreign, stale, or no longer reconcilable.");
            lifecycle.Busy = true;
        }
        return ReconcileAcquireCoreAsync(lifecycle, cancellationToken);
    }

    public async Task<RenewalResult> RenewAllAsync(
        DispatchAuthority authority,
        CancellationToken cancellationToken = default)
    {
        var lifecycle = BeginAuthorityTransition(authority, CoordinationLifecyclePhase.Renewing, "renewal");
        try
        {
            foreach (var entry in ActiveEntries(lifecycle))
            {
                var intent = ReserveIntent(lifecycle, CoordinationIntentKind.Renew, entry.Evidence.Resource,
                    entry.Identity, FreshRequestId());
                if (!await PrepareAsync(intent, cancellationToken).ConfigureAwait(false))
                    return FinishFailedRenewal(lifecycle, "checkpoint-prepare-failed");

                RenewTransportResult? result;
                try
                {
                    var task = _transport.RenewAsync(new RenewRequest(entry.Evidence, intent.Prepared.RequestId), cancellationToken);
                    result = task is null ? null : await task.ConfigureAwait(false);
                }
                catch (Exception) { return FinishFailedRenewal(lifecycle, "renew-transport-uncertain"); }
                var receivedAt = _clock.Elapsed;

                LeaseEvidence renewed;
                try
                {
                    if (result is null || result.Outcome != TransportOutcome.Renewed ||
                        result.ServerTime is null || result.ExpiresAt is null)
                        throw new CoordinationProtocolException("renew-response-not-authoritative");
                    renewed = CreateGrantEvidence(entry.Evidence.Resource, lifecycle.Owner, entry.Evidence.LeaseId,
                        entry.Evidence.FencingToken, result.ServerTime.Value, result.ExpiresAt.Value,
                        intent.Prepared.FirstSendMonotonic, receivedAt, entry.Evidence.Generation);
                    if (renewed.ExpiresAt <= intent.Prepared.PreviousLease!.ExpiresAt ||
                        renewed.ServerTime <= intent.Prepared.PreviousLease.ServerTime ||
                        renewed.SafeDeadline <= entry.Evidence.SafeDeadline)
                        throw new CoordinationProtocolException("renewal-did-not-progress");
                }
                catch (CoordinationException) { return FinishFailedRenewal(lifecycle, "renew-response-invalid"); }

                lock (_ledgerGate)
                {
                    entry.Evidence = renewed;
                    entry.Identity = Identity(renewed);
                }
                if (!await ProgressAsync(intent, "renewed", entry.Identity, cancellationToken).ConfigureAwait(false))
                    return FinishFailedRenewal(lifecycle, "checkpoint-progress-failed");
                ClearIntent(lifecycle, intent);
            }

            lock (_ledgerGate)
            {
                if (!CanBeHeld(lifecycle)) return FinishFailedRenewalUnderGate(lifecycle, "renewal-incomplete");
                lifecycle.Busy = false;
                lifecycle.Phase = CoordinationLifecyclePhase.Held;
                lifecycle.Ownership = LeaseOwnership.Held;
                lifecycle.Reason = "held";
                lifecycle.Authority = NewAuthority(lifecycle);
                _authorities.Add(lifecycle.Authority, lifecycle);
                return new RenewalResult(Snapshot(lifecycle), lifecycle.Authority, null);
            }
        }
        catch (Exception)
        {
            return FinishFailedRenewal(lifecycle, "renew-unexpected-failure");
        }
        finally { SettleBusyIfNeeded(lifecycle); }
    }

    public async Task<RenewalResult> ReconcileRenewalAsync(
        ReleaseContinuation continuation,
        CancellationToken cancellationToken = default)
    {
        var lifecycle = ReserveReleaseContinuation(continuation, requirePendingKind: CoordinationIntentKind.Renew);
        try
        {
            await ResolvePendingRenewAsync(lifecycle, cancellationToken).ConfigureAwait(false);
            lock (_ledgerGate)
            {
                lifecycle.Busy = false;
                lifecycle.Phase = CoordinationLifecyclePhase.Cleaning;
                lifecycle.Reason = "cleanup-required";
                return new RenewalResult(Snapshot(lifecycle), null, lifecycle.ReleaseContinuation);
            }
        }
        finally { SettleBusyIfNeeded(lifecycle); }
    }

    public async Task<CoordinationInspection> InspectAsync(
        CoordinationResource resource,
        LeaseOwner owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(owner);
        var lifecycle = new Lifecycle(owner, [resource]) { Busy = true, Phase = CoordinationLifecyclePhase.Cleaning };
        var intent = ReserveIntent(lifecycle, CoordinationIntentKind.Inspect, resource, null, FreshRequestId());
        if (!await PrepareAsync(intent, cancellationToken).ConfigureAwait(false))
            throw new CoordinationUncertainException("inspect-checkpoint-prepare-failed");
        InspectTransportResult? result;
        try
        {
            var task = _transport.InspectAsync(new InspectRequest(resource, owner, intent.Prepared.RequestId), cancellationToken);
            result = task is null ? null : await task.ConfigureAwait(false);
        }
        catch (Exception) { throw new CoordinationUncertainException("inspect-transport-uncertain"); }
        var receivedAt = _clock.Elapsed;
        CoordinationInspection inspection;
        try { inspection = ValidateInspection(resource, owner, result, intent.Prepared.FirstSendMonotonic, receivedAt); }
        catch (CoordinationException) { throw new CoordinationUncertainException("inspect-response-invalid"); }
        if (!await ProgressAsync(intent, "inspected", inspection.OwnedLease is null ? null : Identity(inspection.OwnedLease), cancellationToken)
                .ConfigureAwait(false))
            throw new CoordinationUncertainException("inspect-checkpoint-progress-failed");
        return inspection;
    }

    public Task<ReleaseResult> ReleaseAllAsync(
        DispatchAuthority authority,
        CancellationToken cancellationToken = default)
    {
        var lifecycle = BeginAuthorityTransition(authority, CoordinationLifecyclePhase.Cleaning, "release");
        lock (_ledgerGate) EnsureReleaseContinuationUnderGate(lifecycle);
        return ContinueReleaseCoreAsync(lifecycle, cancellationToken);
    }

    public Task<ReleaseResult> ContinueReleaseAsync(
        ReleaseContinuation continuation,
        CancellationToken cancellationToken = default) =>
        ContinueReleaseCoreAsync(ReserveReleaseContinuation(continuation), cancellationToken);

    internal async Task<ReleaseResult> RecoverForCleanupAsync(
        string lifecycleId,
        LeaseOwner owner,
        IEnumerable<CoordinationResource> resources,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(lifecycleId, "D", out _))
            throw new CoordinationProtocolException("recovery-lifecycle-invalid");
        var canonical = resources.Distinct().Order(Utf8LexicalComparer.Instance).ToArray();
        if (canonical.Length == 0) throw new CoordinationProtocolException("recovery-resources-empty");
        IReadOnlyList<CoordinationCheckpointRecord> records;
        try { records = await _checkpoints.ReadLifecycleAsync(lifecycleId, cancellationToken).ConfigureAwait(false); }
        catch (Exception) { throw new CoordinationUncertainException("checkpoint-read-failed"); }
        var lifecycle = new Lifecycle(owner, canonical, lifecycleId) { Phase = CoordinationLifecyclePhase.Cleaning };
        var prepared = new Dictionary<long, PreparedCoordinationIntent>();
        var progress = new Dictionary<long, CoordinationIntentProgress>();
        foreach (var record in records)
        {
            if ((record.Prepared is null) == (record.Progress is null))
                throw new CoordinationProtocolException("checkpoint-record-invalid");
            if (record.Prepared is not null)
            {
                ValidateRecovered(record.Prepared, lifecycle);
                if (!prepared.TryAdd(record.Prepared.OperationSequence, record.Prepared))
                    throw new CoordinationProtocolException("checkpoint-prepare-duplicate");
            }
            else
            {
                var item = record.Progress!;
                if (item.LifecycleId != lifecycleId || !progress.TryAdd(item.OperationSequence, item))
                    throw new CoordinationProtocolException("checkpoint-progress-invalid");
            }
        }
        foreach (var pair in progress)
        {
            if (!prepared.TryGetValue(pair.Key, out var source))
                throw new CoordinationProtocolException("checkpoint-progress-without-prepare");
            if (pair.Value.Lease is not null)
            {
                ValidateRecoveredIdentity(pair.Value.Lease, source, lifecycle);
                var entry = RecoveredEntry(pair.Value.Lease);
                if (pair.Value.AdapterCode == "released") entry.ReleaseState = LeaseReleaseState.Released;
                else if (pair.Value.AdapterCode == "original-not-owned") entry.ReleaseState = LeaseReleaseState.NotOwned;
                lifecycle.Entries[source.Resource] = entry;
            }
        }
        var unresolved = prepared.Values.Where(item => !progress.ContainsKey(item.OperationSequence)).OrderBy(item => item.OperationSequence).ToArray();
        if (unresolved.Length > 1) throw new CoordinationProtocolException("checkpoint-has-overlapping-intents");
        if (unresolved.Length == 1)
        {
            var item = unresolved[0];
            lifecycle.PendingIntent = new OperationIntent(item);
            if (item.PreviousLease is not null)
                lifecycle.Entries[item.Resource] = RecoveredEntry(item.PreviousLease);
        }
        lifecycle.AcquisitionResolved = prepared.Values.Any(item => item.Kind == CoordinationIntentKind.Acquire) &&
            prepared.Values.Where(item => item.Kind == CoordinationIntentKind.Acquire)
                .All(item => progress.ContainsKey(item.OperationSequence));
        lifecycle.NextSequence = prepared.Keys.DefaultIfEmpty().Max() + 1;
        lock (_ledgerGate)
        {
            EnsureReleaseContinuationUnderGate(lifecycle);
            lifecycle.Reason = "recovered-cleanup-only";
            return Release(lifecycle, []);
        }
    }

    private async Task<AcquisitionResult> AcquireCoreAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var resource in lifecycle.Resources)
            {
                var intent = ReserveIntent(lifecycle, CoordinationIntentKind.Acquire, resource, null, FreshRequestId());
                if (!await PrepareAsync(intent, cancellationToken).ConfigureAwait(false))
                    return await FailAcquireAsync(lifecycle, intent, "checkpoint-prepare-failed").ConfigureAwait(false);
                var response = await InvokeAcquireAsync(lifecycle, intent, cancellationToken).ConfigureAwait(false);
                if (response is null)
                    return await FailAcquireAsync(lifecycle, intent, "acquire-transport-uncertain").ConfigureAwait(false);
                if (response.Outcome == TransportOutcome.Contended)
                {
                    if (!await ProgressAsync(intent, "contended", null, cancellationToken).ConfigureAwait(false))
                        return await FailAcquireAsync(lifecycle, intent, "checkpoint-progress-failed").ConfigureAwait(false);
                    ClearIntent(lifecycle, intent);
                    await RollBackKnownAsync(lifecycle).ConfigureAwait(false);
                    lock (_ledgerGate)
                    {
                        lifecycle.Busy = false;
                        lifecycle.AcquisitionResolved = true;
                        lifecycle.Phase = IsClosed(lifecycle) ? CoordinationLifecyclePhase.Closed : CoordinationLifecyclePhase.Cleaning;
                        lifecycle.Reason = lifecycle.Phase == CoordinationLifecyclePhase.Closed ? "contended" : "cleanup-required";
                        if (lifecycle.Phase != CoordinationLifecyclePhase.Closed) EnsureReleaseContinuationUnderGate(lifecycle);
                        return Acquisition(lifecycle, null);
                    }
                }
                LeaseEvidence evidence;
                try
                {
                    if (response.Outcome != TransportOutcome.Acquired || response.ServerTime is null || response.ExpiresAt is null)
                        throw new CoordinationProtocolException("acquire-response-not-authoritative");
                    evidence = CreateGrantEvidence(resource, lifecycle.Owner, response.LeaseId ?? "", response.FencingToken ?? "",
                        response.ServerTime.Value, response.ExpiresAt.Value, intent.Prepared.FirstSendMonotonic,
                        _clock.Elapsed, FreshRequestId());
                }
                catch (CoordinationException)
                {
                    RetainStructuralIdentity(lifecycle, intent, response);
                    return await FailAcquireAsync(lifecycle, intent, "acquire-response-invalid").ConfigureAwait(false);
                }
                var entry = new LeaseEntry(evidence);
                lock (_ledgerGate) lifecycle.Entries[resource] = entry;
                if (!await ProgressAsync(intent, "acquired", entry.Identity, cancellationToken).ConfigureAwait(false))
                    return await FailAcquireAsync(lifecycle, intent, "checkpoint-progress-failed").ConfigureAwait(false);
                ClearIntent(lifecycle, intent);
            }
            lock (_ledgerGate)
            {
                if (!CanBeHeld(lifecycle))
                    return FinishUncertainAcquireUnderGate(lifecycle, lifecycle.PendingIntent, "acquire-incomplete");
                lifecycle.Busy = false;
                lifecycle.AcquisitionResolved = true;
                lifecycle.Phase = CoordinationLifecyclePhase.Held;
                lifecycle.Ownership = LeaseOwnership.Held;
                lifecycle.Reason = "held";
                lifecycle.Authority = NewAuthority(lifecycle);
                _authorities.Add(lifecycle.Authority, lifecycle);
                return Acquisition(lifecycle, null);
            }
        }
        catch (Exception)
        {
            return await FailAcquireAsync(lifecycle, lifecycle.PendingIntent, "acquire-unexpected-failure").ConfigureAwait(false);
        }
        finally { SettleBusyIfNeeded(lifecycle); }
    }

    private async Task<AcquisitionResult> ReconcileAcquireCoreAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        try
        {
            var intent = lifecycle.PendingIntent!;
            var response = await InvokeAcquireAsync(lifecycle, intent, cancellationToken).ConfigureAwait(false);
            if (response is null) return FinishUncertainAcquire(lifecycle, intent, "acquire-reconcile-uncertain");
            if (response.Outcome == TransportOutcome.Contended)
            {
                if (await ProgressAsync(intent, "contended", null, cancellationToken).ConfigureAwait(false)) ClearIntent(lifecycle, intent);
            }
            else if (response.Outcome == TransportOutcome.Acquired && response.ServerTime is not null && response.ExpiresAt is not null)
            {
                try
                {
                    var evidence = CreateCleanupEvidence(intent.Prepared.Resource, lifecycle.Owner, response.LeaseId ?? "",
                        response.FencingToken ?? "", response.ServerTime.Value, response.ExpiresAt.Value, FreshRequestId());
                    var entry = new LeaseEntry(evidence);
                    lock (_ledgerGate) lifecycle.Entries[intent.Prepared.Resource] = entry;
                    if (await ProgressAsync(intent, "acquired", entry.Identity, cancellationToken).ConfigureAwait(false))
                        ClearIntent(lifecycle, intent);
                }
                catch (CoordinationException) { RetainStructuralIdentity(lifecycle, intent, response); }
            }
            lock (_ledgerGate)
            {
                lifecycle.Busy = false;
                lifecycle.Phase = CoordinationLifecyclePhase.Cleaning;
                lifecycle.Reason = "cleanup-required";
                EnsureReleaseContinuationUnderGate(lifecycle);
                if (lifecycle.PendingIntent is null)
                {
                    lifecycle.AcquisitionResolved = true;
                    if (lifecycle.AcquireContinuation is not null) _acquireContinuations.Remove(lifecycle.AcquireContinuation);
                    lifecycle.AcquireContinuation = null;
                }
                return Acquisition(lifecycle, lifecycle.PendingIntent is null ? null : Uncertainty(lifecycle.PendingIntent, "acquire-reconcile-uncertain"));
            }
        }
        finally { SettleBusyIfNeeded(lifecycle); }
    }

    private async Task<AcquireTransportResult?> InvokeAcquireAsync(
        Lifecycle lifecycle, OperationIntent intent, CancellationToken cancellationToken)
    {
        try
        {
            var request = new AcquireRequest(intent.Prepared.Resource, lifecycle.Owner, intent.Prepared.Ttl, intent.Prepared.RequestId);
            var task = _transport.AcquireAsync(request, cancellationToken);
            return task is null ? null : await task.ConfigureAwait(false);
        }
        catch (Exception) { return null; }
    }

    private async Task<AcquisitionResult> FailAcquireAsync(Lifecycle lifecycle, OperationIntent? intent, string code)
    {
        await RollBackKnownAsync(lifecycle).ConfigureAwait(false);
        return FinishUncertainAcquire(lifecycle, intent, code);
    }

    private AcquisitionResult FinishUncertainAcquire(Lifecycle lifecycle, OperationIntent? intent, string code)
    {
        lock (_ledgerGate) return FinishUncertainAcquireUnderGate(lifecycle, intent, code);
    }

    private AcquisitionResult FinishUncertainAcquireUnderGate(Lifecycle lifecycle, OperationIntent? intent, string code)
    {
        lifecycle.Busy = false;
        lifecycle.Phase = CoordinationLifecyclePhase.Cleaning;
        lifecycle.Reason = code;
        if (intent is not null && intent.Prepared.Kind == CoordinationIntentKind.Acquire)
        {
            lifecycle.AcquireContinuation ??= new AcquireContinuation();
            _acquireContinuations[lifecycle.AcquireContinuation] = lifecycle;
        }
        EnsureReleaseContinuationUnderGate(lifecycle);
        return Acquisition(lifecycle, intent is null ? null : Uncertainty(intent, code));
    }

    private async Task RollBackKnownAsync(Lifecycle lifecycle)
    {
        foreach (var entry in ActiveEntries(lifecycle).Reverse())
        {
            if (!await ObserveQuiescenceAsync(lifecycle, CancellationToken.None).ConfigureAwait(false))
            {
                lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.Unknown; entry.ReleaseReason = "quiescence-unproved"; }
                continue;
            }
            await ReleaseEntryAsync(lifecycle, entry, CancellationToken.None, bounded: true).ConfigureAwait(false);
        }
    }

    private async Task<ReleaseResult> ContinueReleaseCoreAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        var observations = new List<QuiescenceEvidence>();
        try
        {
            if (lifecycle.PendingIntent?.Prepared.Kind == CoordinationIntentKind.Acquire)
                await ResolvePendingAcquireForCleanupAsync(lifecycle, cancellationToken).ConfigureAwait(false);
            else if (lifecycle.PendingIntent?.Prepared.Kind == CoordinationIntentKind.Renew)
                await ResolvePendingRenewAsync(lifecycle, cancellationToken).ConfigureAwait(false);

            foreach (var entry in ActiveEntries(lifecycle).Reverse())
            {
                var observation = await GetQuiescenceAsync(lifecycle, cancellationToken).ConfigureAwait(false);
                if (observation is not null) observations.Add(observation);
                if (observation is null || !IsValidQuiescence(observation, lifecycle.Owner, observation.ObservedAt, _clock.Elapsed))
                {
                    lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.Unknown; entry.ReleaseReason = "quiescence-unproved"; }
                    break;
                }
                var outcome = await ReleaseEntryAsync(lifecycle, entry, cancellationToken, bounded: false).ConfigureAwait(false);
                if (outcome == TransportOutcome.LeaseLost)
                    await ProveNotOwnedAsync(lifecycle, entry, cancellationToken).ConfigureAwait(false);
            }
            lock (_ledgerGate)
            {
                lifecycle.Busy = false;
                lifecycle.Phase = IsClosed(lifecycle) ? CoordinationLifecyclePhase.Closed : CoordinationLifecyclePhase.Cleaning;
                lifecycle.Ownership = lifecycle.Phase == CoordinationLifecyclePhase.Closed ? LeaseOwnership.Released : LeaseOwnership.Uncertain;
                lifecycle.Reason = lifecycle.Phase == CoordinationLifecyclePhase.Closed ? "cleanup-complete" : "cleanup-uncertain";
                if (lifecycle.Phase == CoordinationLifecyclePhase.Closed) CloseUnderGate(lifecycle);
                else EnsureReleaseContinuationUnderGate(lifecycle);
                return Release(lifecycle, observations);
            }
        }
        catch (Exception)
        {
            lock (_ledgerGate)
            {
                lifecycle.Busy = false;
                lifecycle.Phase = CoordinationLifecyclePhase.Cleaning;
                lifecycle.Ownership = LeaseOwnership.Uncertain;
                lifecycle.Reason = "cleanup-unexpected-failure";
                EnsureReleaseContinuationUnderGate(lifecycle);
                return Release(lifecycle, observations);
            }
        }
        finally { SettleBusyIfNeeded(lifecycle); }
    }

    private async Task ResolvePendingAcquireForCleanupAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        var intent = lifecycle.PendingIntent!;
        var response = await InvokeAcquireAsync(lifecycle, intent, cancellationToken).ConfigureAwait(false);
        if (response is null) return;
        if (response.Outcome == TransportOutcome.Contended)
        {
            if (await ProgressAsync(intent, "contended", null, cancellationToken).ConfigureAwait(false))
            {
                ClearIntent(lifecycle, intent);
                lock (_ledgerGate) lifecycle.AcquisitionResolved = true;
            }
            return;
        }
        if (response.Outcome != TransportOutcome.Acquired || response.ServerTime is null || response.ExpiresAt is null) return;
        try
        {
            var evidence = CreateCleanupEvidence(intent.Prepared.Resource, lifecycle.Owner, response.LeaseId ?? "",
                response.FencingToken ?? "", response.ServerTime.Value, response.ExpiresAt.Value, FreshRequestId());
            var entry = new LeaseEntry(evidence);
            lock (_ledgerGate) lifecycle.Entries[evidence.Resource] = entry;
            if (await ProgressAsync(intent, "acquired", entry.Identity, cancellationToken).ConfigureAwait(false))
            {
                ClearIntent(lifecycle, intent);
                lock (_ledgerGate) lifecycle.AcquisitionResolved = true;
            }
        }
        catch (CoordinationException) { RetainStructuralIdentity(lifecycle, intent, response); }
    }

    private async Task ResolvePendingRenewAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        var intent = lifecycle.PendingIntent!;
        if (intent.Prepared.Kind != CoordinationIntentKind.Renew ||
            !lifecycle.Entries.TryGetValue(intent.Prepared.Resource, out var entry)) return;
        RenewTransportResult? result;
        try
        {
            var request = new RenewRequest(Evidence(intent.Prepared.PreviousLease!), intent.Prepared.RequestId);
            var task = _transport.RenewAsync(request, cancellationToken);
            result = task is null ? null : await task.ConfigureAwait(false);
        }
        catch (Exception) { return; }
        if (result is null || result.Outcome != TransportOutcome.Renewed || result.ServerTime is null || result.ExpiresAt is null) return;
        try
        {
            var renewed = CreateGrantEvidence(entry.Evidence.Resource, lifecycle.Owner, entry.Evidence.LeaseId,
                entry.Evidence.FencingToken, result.ServerTime.Value, result.ExpiresAt.Value,
                intent.Prepared.FirstSendMonotonic, _clock.Elapsed, entry.Evidence.Generation);
            lock (_ledgerGate)
            {
                entry.Evidence = renewed;
                entry.Identity = Identity(renewed);
            }
            if (await ProgressAsync(intent, "renewed-cleanup-only", entry.Identity, cancellationToken).ConfigureAwait(false)) ClearIntent(lifecycle, intent);
        }
        catch (CoordinationException) { }
    }

    private async Task<TransportOutcome?> ReleaseEntryAsync(
        Lifecycle lifecycle, LeaseEntry entry, CancellationToken cancellationToken, bool bounded)
    {
        var intent = entry.ReleaseIntent ?? ReserveReleaseIntent(lifecycle, entry);
        if (!intent.PreparedWritten && !await PrepareAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.Unknown; entry.ReleaseReason = "checkpoint-prepare-failed"; }
            return null;
        }
        ReleaseTransportResult? result = null;
        try
        {
            var request = new ReleaseRequest(entry.Identity.LeaseId, lifecycle.Owner, entry.Identity.FencingToken, intent.Prepared.RequestId);
            var task = _transport.ReleaseAsync(request, cancellationToken);
            if (task is not null)
            {
                if (bounded)
                {
                    var completed = await Task.WhenAny(task, Task.Delay(_rollbackTimeout)).ConfigureAwait(false);
                    if (ReferenceEquals(completed, task)) result = await task.ConfigureAwait(false);
                    else _ = task.ContinueWith(item => _ = item.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                else result = await task.ConfigureAwait(false);
            }
        }
        catch (Exception) { }
        if (result?.Outcome == TransportOutcome.Released)
        {
            if (await ProgressAsync(intent, "released", entry.Identity, cancellationToken).ConfigureAwait(false))
            {
                lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.Released; entry.ReleaseReason = "released"; }
            }
        }
        else
        {
            lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.Unknown; entry.ReleaseReason = "release-uncertain"; }
        }
        return result?.Outcome;
    }

    private async Task ProveNotOwnedAsync(Lifecycle lifecycle, LeaseEntry entry, CancellationToken cancellationToken)
    {
        var intent = ReserveIntent(lifecycle, CoordinationIntentKind.Inspect, entry.Evidence.Resource, entry.Identity, FreshRequestId());
        if (!await PrepareAsync(intent, cancellationToken).ConfigureAwait(false)) { ClearIntent(lifecycle, intent, preserveRelease: true); return; }
        InspectTransportResult? result;
        try
        {
            var task = _transport.InspectAsync(new InspectRequest(entry.Evidence.Resource, lifecycle.Owner, intent.Prepared.RequestId), cancellationToken);
            result = task is null ? null : await task.ConfigureAwait(false);
        }
        catch (Exception) { ClearIntent(lifecycle, intent, preserveRelease: true); return; }
        if (result is null || result.Outcome != TransportOutcome.Inspected) { ClearIntent(lifecycle, intent, preserveRelease: true); return; }
        var notOwned = !result.Held || !result.CallerOwnsLease ||
            result.LeaseId != entry.Identity.LeaseId || result.FencingToken != entry.Identity.FencingToken;
        if (!notOwned) { ClearIntent(lifecycle, intent, preserveRelease: true); return; }
        if (await ProgressAsync(intent, "original-not-owned", null, cancellationToken).ConfigureAwait(false))
        {
            lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.NotOwned; entry.ReleaseReason = "original-not-owned"; }
        }
        ClearIntent(lifecycle, intent, preserveRelease: true);
    }

    private OperationIntent ReserveIntent(
        Lifecycle lifecycle, CoordinationIntentKind kind, CoordinationResource resource,
        CheckpointLeaseIdentity? prior, string requestId)
    {
        lock (_ledgerGate)
        {
            if (lifecycle.PendingIntent is not null) throw new CoordinationUncertainException("logical-operation-overlap");
            var prepared = new PreparedCoordinationIntent(lifecycle.Id, lifecycle.NextSequence++, kind, resource,
                lifecycle.Owner, requestId, _clock.Elapsed, _clock.DomainId, DefaultTtl, prior);
            var intent = new OperationIntent(prepared);
            lifecycle.PendingIntent = intent;
            return intent;
        }
    }

    private OperationIntent ReserveReleaseIntent(Lifecycle lifecycle, LeaseEntry entry)
    {
        lock (_ledgerGate)
        {
            if (entry.ReleaseIntent is not null) return entry.ReleaseIntent;
            var prepared = new PreparedCoordinationIntent(lifecycle.Id, lifecycle.NextSequence++, CoordinationIntentKind.Release,
                entry.Evidence.Resource, lifecycle.Owner, FreshRequestId(), _clock.Elapsed, _clock.DomainId, DefaultTtl, entry.Identity);
            entry.ReleaseIntent = new OperationIntent(prepared);
            return entry.ReleaseIntent;
        }
    }

    private async Task<bool> PrepareAsync(OperationIntent intent, CancellationToken cancellationToken)
    {
        if (intent.PreparedWritten) return true;
        try
        {
            await _checkpoints.AppendPreparedAsync(intent.Prepared, cancellationToken).ConfigureAwait(false);
            lock (_ledgerGate) intent.PreparedWritten = true;
            return true;
        }
        catch (Exception) { return false; }
    }

    private async Task<bool> ProgressAsync(
        OperationIntent intent, string code, CheckpointLeaseIdentity? lease, CancellationToken cancellationToken)
    {
        try
        {
            await _checkpoints.AppendProgressAsync(
                new CoordinationIntentProgress(intent.Prepared.LifecycleId, intent.Prepared.OperationSequence, code, lease),
                cancellationToken).ConfigureAwait(false);
            lock (_ledgerGate) intent.Resolved = true;
            return true;
        }
        catch (Exception) { return false; }
    }

    private void ClearIntent(Lifecycle lifecycle, OperationIntent intent, bool preserveRelease = false)
    {
        lock (_ledgerGate)
        {
            if (ReferenceEquals(lifecycle.PendingIntent, intent)) lifecycle.PendingIntent = null;
            if (!preserveRelease && intent.Prepared.Kind == CoordinationIntentKind.Release &&
                lifecycle.Entries.TryGetValue(intent.Prepared.Resource, out var entry)) entry.ReleaseIntent = intent;
        }
    }

    private Lifecycle BeginAuthorityTransition(
        DispatchAuthority authority, CoordinationLifecyclePhase phase, string operation)
    {
        ArgumentNullException.ThrowIfNull(authority);
        lock (_ledgerGate)
        {
            if (!_authorities.TryGetValue(authority, out var lifecycle) || lifecycle.Busy ||
                authority.AdapterId != _adapterId || authority.ClockDomainId != _clock.DomainId ||
                authority.LifecycleId != lifecycle.Id)
                throw new CoordinationUncertainException($"{operation}-requires-current-authority");
            var live = ReferenceEquals(lifecycle.Authority, authority) && lifecycle.Phase == CoordinationLifecyclePhase.Held;
            var expiredCleanup = operation == "release" && ReferenceEquals(lifecycle.CleanupAuthority, authority) &&
                lifecycle.Phase == CoordinationLifecyclePhase.Cleaning;
            if (!live && !expiredCleanup)
                throw new CoordinationUncertainException($"{operation}-requires-current-authority");
            RevokeAuthorityUnderGate(lifecycle);
            lifecycle.CleanupAuthority = null;
            lifecycle.Phase = phase;
            lifecycle.Ownership = LeaseOwnership.Uncertain;
            lifecycle.Busy = true;
            return lifecycle;
        }
    }

    private Lifecycle ReserveReleaseContinuation(
        ReleaseContinuation continuation, CoordinationIntentKind? requirePendingKind = null)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        lock (_ledgerGate)
        {
            if (!_releaseContinuations.TryGetValue(continuation, out var lifecycle) || lifecycle.Busy ||
                !ReferenceEquals(lifecycle.ReleaseContinuation, continuation) || lifecycle.Phase == CoordinationLifecyclePhase.Closed ||
                (requirePendingKind is not null && lifecycle.PendingIntent?.Prepared.Kind != requirePendingKind))
                throw new CoordinationUncertainException("release-continuation-foreign-stale-or-busy");
            lifecycle.Busy = true;
            return lifecycle;
        }
    }

    private RenewalResult FinishFailedRenewal(Lifecycle lifecycle, string code)
    {
        lock (_ledgerGate) return FinishFailedRenewalUnderGate(lifecycle, code);
    }

    private RenewalResult FinishFailedRenewalUnderGate(Lifecycle lifecycle, string code)
    {
        lifecycle.Busy = false;
        lifecycle.Phase = CoordinationLifecyclePhase.Cleaning;
        lifecycle.Ownership = LeaseOwnership.Uncertain;
        lifecycle.Reason = code;
        EnsureReleaseContinuationUnderGate(lifecycle);
        return new RenewalResult(Snapshot(lifecycle), null, lifecycle.ReleaseContinuation);
    }

    private void RevokeAuthorityUnderGate(Lifecycle lifecycle)
    {
        if (lifecycle.Authority is not null) _authorities.Remove(lifecycle.Authority);
        lifecycle.Authority = null;
    }

    private void EnsureReleaseContinuationUnderGate(Lifecycle lifecycle)
    {
        if (lifecycle.ReleaseContinuation is not null) return;
        lifecycle.ReleaseContinuation = new ReleaseContinuation();
        _releaseContinuations.Add(lifecycle.ReleaseContinuation, lifecycle);
    }

    private void CloseUnderGate(Lifecycle lifecycle)
    {
        RevokeAuthorityUnderGate(lifecycle);
        if (lifecycle.AcquireContinuation is not null) _acquireContinuations.Remove(lifecycle.AcquireContinuation);
        if (lifecycle.ReleaseContinuation is not null) _releaseContinuations.Remove(lifecycle.ReleaseContinuation);
        lifecycle.AcquireContinuation = null;
        lifecycle.ReleaseContinuation = null;
    }

    private void SettleBusyIfNeeded(Lifecycle lifecycle)
    {
        lock (_ledgerGate)
        {
            if (!lifecycle.Busy) return;
            lifecycle.Busy = false;
            if (lifecycle.Phase != CoordinationLifecyclePhase.Closed)
            {
                lifecycle.Phase = CoordinationLifecyclePhase.Cleaning;
                lifecycle.Ownership = LeaseOwnership.Uncertain;
                EnsureReleaseContinuationUnderGate(lifecycle);
            }
        }
    }

    private async Task<QuiescenceEvidence?> GetQuiescenceAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        try
        {
            var task = _quiescence.ObserveAsync(lifecycle.Owner, cancellationToken);
            return task is null ? null : await task.ConfigureAwait(false);
        }
        catch (Exception) { return null; }
    }

    private async Task<bool> ObserveQuiescenceAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        var started = _clock.Elapsed;
        var evidence = await GetQuiescenceAsync(lifecycle, cancellationToken).ConfigureAwait(false);
        return evidence is not null && IsValidQuiescence(evidence, lifecycle.Owner, started, _clock.Elapsed);
    }

    private bool IsValidQuiescence(
        QuiescenceEvidence evidence, LeaseOwner owner, TimeSpan started, TimeSpan finished) =>
        evidence.Quiescent && evidence.Owner == owner &&
        evidence.IssuerId == _quiescenceBinding.IssuerId && evidence.SourceId == _quiescenceBinding.SourceId &&
        evidence.ClockDomainId == _quiescenceBinding.ClockDomainId && _quiescenceBinding.ClockDomainId == _clock.DomainId &&
        !string.IsNullOrWhiteSpace(evidence.EvidenceId) && !evidence.EvidenceId.Any(char.IsControl) &&
        finished >= started && finished - started <= _quiescenceBinding.MaximumObservationLatency &&
        evidence.ObservedAt >= started && evidence.ObservedAt <= finished &&
        finished - evidence.ObservedAt <= _quiescenceBinding.FreshnessLimit;

    private LeaseEvidence CreateGrantEvidence(
        CoordinationResource resource, LeaseOwner owner, string leaseId, string fencingToken,
        DateTimeOffset serverTime, DateTimeOffset expiresAt, TimeSpan firstSend, TimeSpan receivedAt, string generation)
    {
        if (expiresAt - serverTime != DefaultTtl)
            throw new CoordinationProtocolException("grant-ttl-invalid");
        if (receivedAt < firstSend) throw new CoordinationProtocolException("monotonic-clock-went-backwards");
        TimeSpan deadline;
        try { deadline = firstSend + DefaultTtl; }
        catch (OverflowException) { throw new CoordinationProtocolException("deadline-overflow"); }
        return new LeaseEvidence(resource, owner, leaseId, fencingToken, serverTime, expiresAt,
            deadline, firstSend + RenewalTarget, generation);
    }

    private LeaseEvidence CreateInspectEvidence(
        CoordinationResource resource, LeaseOwner owner, string leaseId, string fencingToken,
        DateTimeOffset serverTime, DateTimeOffset expiresAt, TimeSpan firstSend, TimeSpan receivedAt)
    {
        if (receivedAt < firstSend || expiresAt <= serverTime) throw new CoordinationProtocolException("inspect-time-invalid");
        TimeSpan deadline;
        try { deadline = firstSend + (expiresAt - serverTime); }
        catch (OverflowException) { throw new CoordinationProtocolException("deadline-overflow"); }
        return new LeaseEvidence(resource, owner, leaseId, fencingToken, serverTime, expiresAt,
            deadline, deadline - TimeSpan.FromTicks(1), FreshRequestId());
    }

    private static LeaseEvidence CreateCleanupEvidence(
        CoordinationResource resource, LeaseOwner owner, string leaseId, string fencingToken,
        DateTimeOffset serverTime, DateTimeOffset expiresAt, string generation)
    {
        if (expiresAt - serverTime != DefaultTtl)
            throw new CoordinationProtocolException("grant-ttl-invalid");
        return new LeaseEvidence(resource, owner, leaseId, fencingToken, serverTime, expiresAt,
            TimeSpan.Zero, TimeSpan.MinValue, generation);
    }

    private CoordinationInspection ValidateInspection(
        CoordinationResource resource, LeaseOwner owner, InspectTransportResult? result,
        TimeSpan firstSend, TimeSpan receivedAt)
    {
        if (result is null || result.Outcome != TransportOutcome.Inspected)
            throw new CoordinationProtocolException("inspect-not-authoritative");
        if (!result.Held)
        {
            if (result.CallerOwnsLease || result.LeaseId is not null || result.FencingToken is not null ||
                result.ServerTime is not null || result.ExpiresAt is not null)
                throw new CoordinationProtocolException("available-inspect-leaked-details");
            return new CoordinationInspection(InspectionState.Available, null, "available");
        }
        if (!result.CallerOwnsLease)
            return new CoordinationInspection(InspectionState.HeldByOther, null, "held-by-other");
        if (result.ServerTime is null || result.ExpiresAt is null)
            throw new CoordinationProtocolException("owned-inspect-missing-time");
        var evidence = CreateInspectEvidence(resource, owner, result.LeaseId ?? "", result.FencingToken ?? "",
            result.ServerTime.Value, result.ExpiresAt.Value, firstSend, receivedAt);
        return new CoordinationInspection(InspectionState.HeldByCaller, evidence, "held-by-caller");
    }

    private void RetainStructuralIdentity(Lifecycle lifecycle, OperationIntent intent, AcquireTransportResult response)
    {
        try
        {
            if (response.ServerTime is null || response.ExpiresAt is null) return;
            var evidence = CreateGrantEvidence(intent.Prepared.Resource, lifecycle.Owner, response.LeaseId ?? "",
                response.FencingToken ?? "", response.ServerTime.Value, response.ExpiresAt.Value,
                intent.Prepared.FirstSendMonotonic, _clock.Elapsed, FreshRequestId());
            lock (_ledgerGate) lifecycle.Entries[intent.Prepared.Resource] = new LeaseEntry(evidence);
        }
        catch (CoordinationException) { }
    }

    private static CheckpointLeaseIdentity Identity(LeaseEvidence evidence) =>
        new(evidence.Resource, evidence.Owner, evidence.LeaseId, evidence.FencingToken, evidence.Generation,
            evidence.ServerTime, evidence.ExpiresAt, evidence.SafeDeadline, evidence.RenewAt);

    private static LeaseEntry RecoveredEntry(CheckpointLeaseIdentity identity)
    {
        return new LeaseEntry(CreateCleanupEvidence(identity.Resource, identity.Owner, identity.LeaseId,
            identity.FencingToken, identity.ServerTime, identity.ExpiresAt, identity.Generation));
    }

    private static LeaseEvidence Evidence(CheckpointLeaseIdentity identity) =>
        new(identity.Resource, identity.Owner, identity.LeaseId, identity.FencingToken,
            identity.ServerTime, identity.ExpiresAt, identity.SafeDeadline, identity.RenewAt, identity.Generation);

    private static void ValidateRecovered(PreparedCoordinationIntent intent, Lifecycle lifecycle)
    {
        if (intent.LifecycleId != lifecycle.Id || intent.Owner != lifecycle.Owner ||
            !lifecycle.Resources.Contains(intent.Resource) || intent.ClockDomainId.Length is < 1 or > 256 ||
            !Guid.TryParseExact(intent.RequestId, "D", out _) || intent.OperationSequence < 1 ||
            intent.Ttl != DefaultTtl)
            throw new CoordinationProtocolException("checkpoint-intent-binding-invalid");
    }

    private static void ValidateRecoveredIdentity(
        CheckpointLeaseIdentity identity, PreparedCoordinationIntent source, Lifecycle lifecycle)
    {
        if (identity.Owner != lifecycle.Owner || identity.Resource != source.Resource ||
            string.IsNullOrWhiteSpace(identity.LeaseId) || string.IsNullOrWhiteSpace(identity.FencingToken) ||
            !Guid.TryParseExact(identity.Generation, "D", out _))
            throw new CoordinationProtocolException("checkpoint-lease-binding-invalid");
    }

    private bool CanBeHeld(Lifecycle lifecycle) =>
        lifecycle.PendingIntent is null && lifecycle.Entries.Count == lifecycle.Resources.Length &&
        lifecycle.Resources.All(resource => lifecycle.Entries.TryGetValue(resource, out var entry) &&
            entry.ReleaseState == LeaseReleaseState.Pending && _clock.Elapsed < entry.Evidence.SafeDeadline);

    private static bool IsClosed(Lifecycle lifecycle) =>
        lifecycle.AcquisitionResolved && lifecycle.PendingIntent is null && lifecycle.Entries.Values.All(entry =>
            entry.ReleaseState is LeaseReleaseState.Released or LeaseReleaseState.NotOwned);

    private static LeaseEntry[] ActiveEntries(Lifecycle lifecycle) =>
        lifecycle.Entries.Values.Where(entry => entry.ReleaseState is not LeaseReleaseState.Released and not LeaseReleaseState.NotOwned)
            .OrderBy(entry => entry.Evidence.Resource, Utf8LexicalComparer.Instance).ToArray();

    private DispatchAuthority NewAuthority(Lifecycle lifecycle) =>
        new(_adapterId, _clock.DomainId, lifecycle.Id, FreshRequestId());

    private static LeaseSnapshot Snapshot(Lifecycle lifecycle) =>
        new(lifecycle.Owner,
            lifecycle.Entries.Values.OrderBy(entry => entry.Evidence.Resource, Utf8LexicalComparer.Instance).Select(entry => entry.Evidence),
            lifecycle.Ownership, lifecycle.Phase, lifecycle.Reason);

    private static IReadOnlyList<LeaseReleaseProgress> Progress(Lifecycle lifecycle) =>
        lifecycle.Entries.Values.OrderBy(entry => entry.Evidence.Resource, Utf8LexicalComparer.Instance)
            .Select(entry => new LeaseReleaseProgress(entry.Evidence.Resource, entry.Identity.LeaseId,
                entry.Identity.Generation, entry.ReleaseIntent?.Prepared.RequestId ?? "", entry.ReleaseState, entry.ReleaseReason)).ToArray();

    private static AcquisitionResult Acquisition(Lifecycle lifecycle, AcquireUncertainty? uncertainty) =>
        new(Snapshot(lifecycle), lifecycle.Authority, lifecycle.AcquireContinuation,
            lifecycle.ReleaseContinuation, uncertainty, Progress(lifecycle));

    private static ReleaseResult Release(Lifecycle lifecycle, IReadOnlyList<QuiescenceEvidence> evidence) =>
        new(Snapshot(lifecycle), Progress(lifecycle), lifecycle.ReleaseContinuation, evidence);

    private static AcquireUncertainty Uncertainty(OperationIntent intent, string code) =>
        new(intent.Prepared.Resource, intent.Prepared.RequestId, intent.Prepared.Owner, code);

    private static void ValidateQuiescenceBinding(QuiescenceSourceBinding? binding, IMonotonicClock clock)
    {
        if (binding is null || !binding.BoundsKnown || binding.MaximumObservationLatency <= TimeSpan.Zero ||
            binding.FreshnessLimit <= TimeSpan.Zero || binding.ClockDomainId != clock.DomainId ||
            string.IsNullOrWhiteSpace(binding.IssuerId) || string.IsNullOrWhiteSpace(binding.SourceId))
            throw new CoordinationUnavailableException("Quiescence source requires known exact issuer/source/clock bounds.");
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

    private sealed class Lifecycle
    {
        public Lifecycle(LeaseOwner owner, CoordinationResource[] resources, string? id = null)
        {
            Id = id ?? Guid.NewGuid().ToString("D");
            Owner = owner;
            Resources = resources;
        }

        public string Id { get; }
        public LeaseOwner Owner { get; }
        public CoordinationResource[] Resources { get; }
        public Dictionary<CoordinationResource, LeaseEntry> Entries { get; } = [];
        public OperationIntent? PendingIntent { get; set; }
        public DispatchAuthority? Authority { get; set; }
        public DispatchAuthority? CleanupAuthority { get; set; }
        public AcquireContinuation? AcquireContinuation { get; set; }
        public ReleaseContinuation? ReleaseContinuation { get; set; }
        public LeaseOwnership Ownership { get; set; } = LeaseOwnership.Uncertain;
        public CoordinationLifecyclePhase Phase { get; set; } = CoordinationLifecyclePhase.Acquiring;
        public string Reason { get; set; } = "acquiring";
        public bool Busy { get; set; }
        public bool AcquisitionResolved { get; set; }
        public long NextSequence { get; set; } = 1;
    }

    private sealed class LeaseEntry
    {
        public LeaseEntry(LeaseEvidence evidence)
        {
            Evidence = evidence;
            Identity = ContinuumCoordinationAdapter.Identity(evidence);
        }
        public LeaseEvidence Evidence { get; set; }
        public CheckpointLeaseIdentity Identity { get; set; }
        public OperationIntent? ReleaseIntent { get; set; }
        public LeaseReleaseState ReleaseState { get; set; } = LeaseReleaseState.Pending;
        public string ReleaseReason { get; set; } = "release-not-attempted";
    }

    private sealed class OperationIntent(PreparedCoordinationIntent prepared)
    {
        public PreparedCoordinationIntent Prepared { get; } = prepared;
        public bool PreparedWritten { get; set; }
        public bool Resolved { get; set; }
    }

    private sealed class VolatileCheckpointStore : ICoordinationCheckpointStore
    {
        private readonly List<CoordinationCheckpointRecord> _records = [];
        public Task AppendPreparedAsync(PreparedCoordinationIntent intent, CancellationToken cancellationToken = default)
        {
            lock (_records) _records.Add(new CoordinationCheckpointRecord(intent, null));
            return Task.CompletedTask;
        }
        public Task AppendProgressAsync(CoordinationIntentProgress progress, CancellationToken cancellationToken = default)
        {
            lock (_records) _records.Add(new CoordinationCheckpointRecord(null, progress));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<CoordinationCheckpointRecord>> ReadLifecycleAsync(
            string lifecycleId, CancellationToken cancellationToken = default)
        {
            lock (_records)
                return Task.FromResult<IReadOnlyList<CoordinationCheckpointRecord>>(
                    _records.Where(record => record.Prepared?.LifecycleId == lifecycleId || record.Progress?.LifecycleId == lifecycleId).ToArray());
        }
    }
}
