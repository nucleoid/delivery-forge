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
    private readonly TimeSpan _maximumClockUncertainty;
    private readonly string _adapterId = Guid.NewGuid().ToString("D");
    private readonly object _ledgerGate = new();
    private readonly Dictionary<DispatchAuthority, Lifecycle> _authorities = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AcquireContinuation, Lifecycle> _acquireContinuations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ReleaseContinuation, Lifecycle> _releaseContinuations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Lifecycle> _lifecycles = new(StringComparer.Ordinal);

    private ContinuumCoordinationAdapter(
        ICoordinationTransport transport,
        CoordinationCapabilities capabilities,
        IMonotonicClock clock,
        IOwnedRunQuiescenceSource quiescence,
        QuiescenceSourceBinding quiescenceBinding,
        ICoordinationCheckpointStore checkpoints,
        TimeSpan rollbackTimeout,
        TimeSpan maximumClockUncertainty)
    {
        _transport = transport;
        Capabilities = capabilities;
        _clock = clock;
        _quiescence = quiescence;
        _quiescenceBinding = quiescenceBinding;
        _checkpoints = checkpoints;
        _rollbackTimeout = rollbackTimeout;
        _maximumClockUncertainty = maximumClockUncertainty;
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
        ICoordinationCheckpointStore? checkpoints = null,
        TimeSpan? maximumClockUncertainty = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(quiescence);
        clock ??= new StopwatchMonotonicClock();
        var binding = quiescence.Binding;
        ValidateQuiescenceBinding(binding, clock);
        CoordinationCapabilities? capabilities;
        var boundedTimeout = rollbackTimeout ?? DefaultRollbackTimeout;
        if (boundedTimeout <= TimeSpan.Zero || boundedTimeout >= DefaultTtl)
            throw new ArgumentOutOfRangeException(nameof(rollbackTimeout), "Coordination calls require a known positive bound below the lease TTL.");
        try { capabilities = await InvokeBoundedStaticAsync(
            () => transport.DetectCapabilitiesAsync(cancellationToken), boundedTimeout).ConfigureAwait(false); }
        catch (Exception) { throw new CoordinationUnavailableException("Coordination capability detection failed."); }
        var reason = ValidateCapabilities(capabilities, requireFixture: true);
        if (reason is not null) throw new CoordinationUnavailableException(reason);
        var uncertainty = maximumClockUncertainty ?? TimeSpan.Zero;
        if (uncertainty < TimeSpan.Zero || uncertainty >= DefaultTtl)
            throw new ArgumentOutOfRangeException(nameof(maximumClockUncertainty));
        return new ContinuumCoordinationAdapter(transport, capabilities!, clock, quiescence, binding,
            checkpoints ?? new VolatileCheckpointStore(), boundedTimeout, uncertainty);
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
                _authorities[authority] = lifecycle;
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
        lock (_ledgerGate)
        {
            lifecycle.Busy = true;
            _lifecycles.Add(lifecycle.Id, lifecycle);
        }
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
                {
                    ClearIntent(lifecycle, intent);
                    return FinishFailedRenewal(lifecycle, "checkpoint-prepare-failed");
                }

                RenewTransportResult? result;
                try
                {
                    result = await InvokeBoundedAsync(() =>
                        _transport.RenewAsync(new RenewRequest(entry.Evidence, intent.Prepared.RequestId), cancellationToken))
                        .ConfigureAwait(false);
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
                        renewed.SafeDeadline <= entry.Evidence.SafeDeadline ||
                        renewed.RenewAt <= entry.Evidence.RenewAt)
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
        if (!await ClaimOriginalWriterAsync(lifecycle, cancellationToken).ConfigureAwait(false))
            throw new CoordinationUncertainException("inspect-checkpoint-claim-failed");
        var intent = ReserveIntent(lifecycle, CoordinationIntentKind.Inspect, resource, null, FreshRequestId());
        if (!await PrepareAsync(intent, cancellationToken).ConfigureAwait(false))
            throw new CoordinationUncertainException("inspect-checkpoint-prepare-failed");
        InspectTransportResult? result;
        try
        {
            result = await InvokeBoundedAsync(() =>
                _transport.InspectAsync(new InspectRequest(resource, owner, intent.Prepared.RequestId), cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception) { throw new CoordinationUncertainException("inspect-transport-uncertain"); }
        if (result is null) throw new CoordinationUncertainException("inspect-transport-uncertain");
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
        string trustedTakeoverProof,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(lifecycleId, "D", out _))
            throw new CoordinationProtocolException("recovery-lifecycle-invalid");
        var canonical = resources.Distinct().Order(Utf8LexicalComparer.Instance).ToArray();
        if (canonical.Length == 0) throw new CoordinationProtocolException("recovery-resources-empty");
        if (string.IsNullOrWhiteSpace(trustedTakeoverProof) || trustedTakeoverProof.Any(char.IsControl))
            throw new CoordinationProtocolException("recovery-takeover-proof-invalid");
        CoordinationWriterClaim claim;
        try
        {
            claim = await InvokeBoundedAsync(() => _checkpoints.ClaimWriterAsync(
                lifecycleId, FreshRequestId(), trustedTakeoverProof, cancellationToken)).ConfigureAwait(false)
                ?? throw new CoordinationUncertainException("checkpoint-claim-missing");
        }
        catch (Exception exception) when (exception is not CoordinationException)
        {
            throw new CoordinationUncertainException("checkpoint-claim-failed");
        }
        IReadOnlyList<CoordinationCheckpointRecord> records;
        try { records = await InvokeBoundedAsync(
            () => _checkpoints.ReadLifecycleAsync(lifecycleId, cancellationToken)).ConfigureAwait(false)
            ?? throw new CoordinationUncertainException("checkpoint-read-missing"); }
        catch (Exception) { throw new CoordinationUncertainException("checkpoint-read-failed"); }

        var lifecycle = new Lifecycle(owner, canonical, lifecycleId)
        {
            Phase = CoordinationLifecyclePhase.Cleaning,
            WriterClaim = claim,
            NextRecordSequence = claim.NextRecordSequence,
        };
        var prepared = new Dictionary<long, PreparedCoordinationIntent>();
        var progress = new Dictionary<long, CoordinationIntentProgress>();
        var canonicalRecords = new List<CoordinationCheckpointRecord>();
        foreach (var group in records.GroupBy(item => item.RecordSequence).OrderBy(item => item.Key))
        {
            var record = group.First();
            if (group.Any(item => item != record))
                throw new CoordinationProtocolException("checkpoint-record-conflicting-duplicate");
            canonicalRecords.Add(record);
        }
        for (var index = 0; index < canonicalRecords.Count; index++)
        {
            var record = canonicalRecords[index];
            if (record.SchemaVersion != CoordinationCheckpointRecord.CurrentSchemaVersion)
                throw new CoordinationProtocolException("checkpoint-version-unsupported");
            if (record.RecordSequence != index + 1)
                throw new CoordinationProtocolException("checkpoint-record-gap");
            if (record.OperationSequence < 1 || record.ProgressRevision < 0 ||
                !Guid.TryParseExact(record.WriterEpoch, "D", out _))
                throw new CoordinationProtocolException("checkpoint-envelope-invalid");
            if ((record.Prepared is null) == (record.Progress is null))
                throw new CoordinationProtocolException("checkpoint-record-invalid");
            if (record.Prepared is not null)
            {
                ValidateRecovered(record.Prepared, lifecycle);
                if (record.OperationSequence != record.Prepared.OperationSequence ||
                    record.Kind != record.Prepared.Kind || record.ProgressRevision != 0)
                    throw new CoordinationProtocolException("checkpoint-prepare-envelope-invalid");
                if (!prepared.TryAdd(record.Prepared.OperationSequence, record.Prepared))
                    throw new CoordinationProtocolException("checkpoint-prepare-duplicate");
            }
            else
            {
                var item = record.Progress!;
                if (item.LifecycleId != lifecycleId || record.OperationSequence != item.OperationSequence ||
                    record.Kind != prepared.GetValueOrDefault(item.OperationSequence)?.Kind ||
                    record.ProgressRevision != item.Revision || item.Revision < 1)
                    throw new CoordinationProtocolException("checkpoint-progress-invalid");
                if (item.AdapterCode == "release-observed" &&
                    (item.Quiescence is null || item.Quiescence.Owner != lifecycle.Owner ||
                     string.IsNullOrWhiteSpace(item.Quiescence.IssuerId) ||
                     string.IsNullOrWhiteSpace(item.Quiescence.SourceId) ||
                     string.IsNullOrWhiteSpace(item.Quiescence.ClockDomainId) ||
                     string.IsNullOrWhiteSpace(item.Quiescence.EvidenceId)))
                    throw new CoordinationProtocolException("checkpoint-quiescence-binding-invalid");
                if (progress.TryGetValue(item.OperationSequence, out var prior) && item.Revision <= prior.Revision)
                    throw new CoordinationProtocolException("checkpoint-progress-transition-invalid");
                ValidateProgressTransition(record.Kind, prior, item);
                progress[item.OperationSequence] = item;
            }
        }

        if (canonicalRecords.Count == 0)
            throw new CoordinationProtocolException("checkpoint-lifecycle-empty");

        var unresolvedLifecycle = new List<PreparedCoordinationIntent>();
        foreach (var source in prepared.Values.OrderBy(item => item.OperationSequence))
        {
            progress.TryGetValue(source.OperationSequence, out var resolution);
            if (resolution is not null && !prepared.ContainsKey(resolution.OperationSequence))
                throw new CoordinationProtocolException("checkpoint-progress-without-prepare");

            LeaseEntry? entry = null;
            if (source.PreviousLease is not null)
            {
                ValidateRecoveredIdentity(source.PreviousLease, source, lifecycle);
                if (!lifecycle.Entries.TryGetValue(source.Resource, out entry))
                {
                    entry = RecoveredEntry(source.PreviousLease);
                    lifecycle.Entries[source.Resource] = entry;
                }
            }
            if (resolution?.Lease is not null)
            {
                ValidateRecoveredIdentity(resolution.Lease, source, lifecycle);
                if (entry is null)
                {
                    entry = RecoveredEntry(resolution.Lease);
                    lifecycle.Entries[source.Resource] = entry;
                }
                else if (!SameIdentity(entry.Identity, resolution.Lease))
                {
                    throw new CoordinationProtocolException("checkpoint-identity-drift");
                }
            }

            switch (source.Kind)
            {
                case CoordinationIntentKind.Acquire:
                    if (resolution is null) unresolvedLifecycle.Add(source);
                    break;
                case CoordinationIntentKind.Renew:
                    if (resolution is null) unresolvedLifecycle.Add(source);
                    break;
                case CoordinationIntentKind.Release:
                    if (entry is null) throw new CoordinationProtocolException("checkpoint-release-identity-missing");
                    entry.ReleaseIntent = RecoveredIntent(lifecycle, source);
                    entry.ReleaseIntent.ProgressRevision = resolution?.Revision ?? 0;
                    if (resolution?.AdapterCode == "released")
                    {
                        entry.ReleaseState = LeaseReleaseState.Released;
                        entry.ReleaseReason = "released";
                    }
                    else if (resolution is not null)
                    {
                        entry.ReleaseState = LeaseReleaseState.Unknown;
                        entry.ReleaseReason = resolution.AdapterCode;
                    }
                    break;
                case CoordinationIntentKind.Inspect:
                    if (entry is null) throw new CoordinationProtocolException("checkpoint-inspect-identity-missing");
                    if (resolution?.AdapterCode == "original-not-owned")
                    {
                        entry.ReleaseState = LeaseReleaseState.NotOwned;
                        entry.ReleaseReason = "original-not-owned";
                    }
                    else if (resolution is null)
                    {
                        entry.InspectIntent = RecoveredIntent(lifecycle, source);
                    }
                    else if (resolution.AdapterCode == "original-still-owned")
                    {
                        entry.InspectIntent = RecoveredIntent(lifecycle, source);
                        entry.InspectIntent.ProgressRevision = resolution.Revision;
                    }
                    break;
            }
        }

        if (progress.Keys.Any(sequence => !prepared.ContainsKey(sequence)))
            throw new CoordinationProtocolException("checkpoint-progress-without-prepare");
        foreach (var unresolved in unresolvedLifecycle.OrderBy(item => item.OperationSequence))
        {
            var recoveredIntent = RecoveredIntent(lifecycle, unresolved);
            if (lifecycle.PendingIntent is null) lifecycle.PendingIntent = recoveredIntent;
            else lifecycle.DeferredIntents.Enqueue(recoveredIntent);
        }

        var acquireOperations = prepared.Values.Where(item => item.Kind == CoordinationIntentKind.Acquire).ToArray();
        lifecycle.AcquisitionResolved = acquireOperations.Length > 0 &&
            acquireOperations.All(item => progress.ContainsKey(item.OperationSequence));
        lifecycle.NextSequence = prepared.Keys.DefaultIfEmpty().Max() + 1;

        lock (_ledgerGate)
        {
            if (_lifecycles.ContainsKey(lifecycle.Id))
                throw new CoordinationUncertainException("recovery-lifecycle-already-live");
            lifecycle.Phase = IsClosed(lifecycle) ? CoordinationLifecyclePhase.Closed : CoordinationLifecyclePhase.Cleaning;
            lifecycle.Ownership = lifecycle.Phase == CoordinationLifecyclePhase.Closed
                ? LeaseOwnership.Released
                : LeaseOwnership.Uncertain;
            lifecycle.Reason = lifecycle.Phase == CoordinationLifecyclePhase.Closed
                ? "cleanup-complete"
                : "recovered-cleanup-only";
            if (lifecycle.Phase != CoordinationLifecyclePhase.Closed)
            {
                _lifecycles.Add(lifecycle.Id, lifecycle);
                EnsureReleaseContinuationUnderGate(lifecycle);
            }
            return Release(lifecycle, []);
        }
    }

    private async Task<AcquisitionResult> AcquireCoreAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        try
        {
            if (!await ClaimOriginalWriterAsync(lifecycle, cancellationToken).ConfigureAwait(false))
            {
                lock (_ledgerGate) lifecycle.AcquisitionResolved = true;
                return FinishUncertainAcquire(lifecycle, null, "checkpoint-writer-claim-failed");
            }
            foreach (var resource in lifecycle.Resources)
            {
                var intent = ReserveIntent(lifecycle, CoordinationIntentKind.Acquire, resource, null, FreshRequestId());
                if (!await PrepareAsync(intent, cancellationToken).ConfigureAwait(false))
                {
                    ClearIntent(lifecycle, intent);
                    lock (_ledgerGate) lifecycle.AcquisitionResolved = true;
                    return await FailAcquireAsync(lifecycle, null, "checkpoint-prepare-failed").ConfigureAwait(false);
                }
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
                        if (lifecycle.Phase == CoordinationLifecyclePhase.Closed) CloseUnderGate(lifecycle);
                        else EnsureReleaseContinuationUnderGate(lifecycle);
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
                {
                    if (lifecycle.PendingIntent is null) lifecycle.AcquisitionResolved = true;
                    return FinishUncertainAcquireUnderGate(lifecycle, lifecycle.PendingIntent, "acquire-incomplete");
                }
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
                    var entry = MergeAcquireEvidence(lifecycle, evidence);
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
        if (!intent.PreparedWritten) return null;
        try
        {
            var request = new AcquireRequest(intent.Prepared.Resource, lifecycle.Owner, intent.Prepared.Ttl, intent.Prepared.RequestId);
            return await InvokeBoundedAsync(() => _transport.AcquireAsync(request, cancellationToken)).ConfigureAwait(false);
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
        lifecycle.Phase = IsClosed(lifecycle) ? CoordinationLifecyclePhase.Closed : CoordinationLifecyclePhase.Cleaning;
        lifecycle.Ownership = lifecycle.Phase == CoordinationLifecyclePhase.Closed
            ? LeaseOwnership.Released
            : LeaseOwnership.Uncertain;
        lifecycle.Reason = lifecycle.Phase == CoordinationLifecyclePhase.Closed ? "cleanup-complete" : code;
        if (lifecycle.Phase == CoordinationLifecyclePhase.Closed)
        {
            CloseUnderGate(lifecycle);
        }
        else
        {
            if (intent is not null && intent.Prepared.Kind == CoordinationIntentKind.Acquire)
            {
                lifecycle.AcquireContinuation ??= new AcquireContinuation();
                _acquireContinuations[lifecycle.AcquireContinuation] = lifecycle;
            }
            EnsureReleaseContinuationUnderGate(lifecycle);
        }
        return Acquisition(lifecycle, intent is null ? null : Uncertainty(intent, code));
    }

    private async Task RollBackKnownAsync(Lifecycle lifecycle)
    {
        foreach (var entry in ActiveEntries(lifecycle).Reverse())
        {
            var started = _clock.Elapsed;
            var evidence = await GetQuiescenceAsync(lifecycle, CancellationToken.None).ConfigureAwait(false);
            if (evidence is null || !IsValidQuiescence(evidence, lifecycle.Owner, started, _clock.Elapsed))
            {
                lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.Unknown; entry.ReleaseReason = "quiescence-unproved"; }
                continue;
            }
            await ReleaseEntryAsync(lifecycle, entry, evidence, CancellationToken.None).ConfigureAwait(false);
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
                var started = _clock.Elapsed;
                var observation = await GetQuiescenceAsync(lifecycle, cancellationToken).ConfigureAwait(false);
                var finished = _clock.Elapsed;
                if (observation is not null) observations.Add(observation);
                if (observation is null || !IsValidQuiescence(observation, lifecycle.Owner, started, finished))
                {
                    lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.Unknown; entry.ReleaseReason = "quiescence-unproved"; }
                    break;
                }
                var outcome = await ReleaseEntryAsync(lifecycle, entry, observation, cancellationToken).ConfigureAwait(false);
                if (outcome == TransportOutcome.LeaseLost)
                    await ProveNotOwnedAsync(lifecycle, entry, cancellationToken).ConfigureAwait(false);
            }
            await ResolveTerminalRenewIntentAsync(lifecycle, cancellationToken).ConfigureAwait(false);
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
                lock (_ledgerGate) lifecycle.AcquisitionResolved = !HasUnresolvedAcquire(lifecycle);
            }
            return;
        }
        if (response.Outcome != TransportOutcome.Acquired || response.ServerTime is null || response.ExpiresAt is null) return;
        try
        {
            var evidence = CreateCleanupEvidence(intent.Prepared.Resource, lifecycle.Owner, response.LeaseId ?? "",
                response.FencingToken ?? "", response.ServerTime.Value, response.ExpiresAt.Value, FreshRequestId());
            var entry = MergeAcquireEvidence(lifecycle, evidence);
            if (await ProgressAsync(intent, "acquired", entry.Identity, cancellationToken).ConfigureAwait(false))
            {
                ClearIntent(lifecycle, intent);
                lock (_ledgerGate) lifecycle.AcquisitionResolved = !HasUnresolvedAcquire(lifecycle);
            }
        }
        catch (CoordinationException) { RetainStructuralIdentity(lifecycle, intent, response); }
    }

    private async Task ResolvePendingRenewAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        var intent = lifecycle.PendingIntent!;
        if (intent.Prepared.Kind != CoordinationIntentKind.Renew ||
            !lifecycle.Entries.TryGetValue(intent.Prepared.Resource, out var entry)) return;
        if (entry.ReleaseState is LeaseReleaseState.Released or LeaseReleaseState.NotOwned)
        {
            await ResolveTerminalRenewIntentAsync(lifecycle, cancellationToken).ConfigureAwait(false);
            return;
        }

        RenewTransportResult? result;
        try
        {
            var request = new RenewRequest(Evidence(intent.Prepared.PreviousLease!), intent.Prepared.RequestId);
            result = await InvokeBoundedAsync(() => _transport.RenewAsync(request, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception) { return; }
        if (result is null) return;

        if (result.Outcome == TransportOutcome.Renewed && result.ServerTime is not null && result.ExpiresAt is not null)
        {
            try
            {
                var prior = intent.Prepared.PreviousLease!;
                var renewed = CreateCleanupEvidence(entry.Evidence.Resource, lifecycle.Owner, prior.LeaseId,
                    prior.FencingToken, result.ServerTime.Value, result.ExpiresAt.Value, prior.Generation);
                if (renewed.ExpiresAt <= prior.ExpiresAt || renewed.ServerTime <= prior.ServerTime)
                    throw new CoordinationProtocolException("renewal-did-not-progress");
                lock (_ledgerGate)
                {
                    entry.Evidence = renewed;
                    entry.Identity = Identity(renewed);
                }
                if (await ProgressAsync(intent, "renewed-cleanup-only", entry.Identity, cancellationToken).ConfigureAwait(false))
                    ClearIntent(lifecycle, intent);
                return;
            }
            catch (CoordinationException) { }
        }

        if (await ProgressAsync(intent, "renew-replay-nonauthoritative", entry.Identity, cancellationToken).ConfigureAwait(false))
            ClearIntent(lifecycle, intent);
    }

    private async Task ResolveTerminalRenewIntentAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        var intent = lifecycle.PendingIntent;
        if (intent?.Prepared.Kind != CoordinationIntentKind.Renew ||
            !lifecycle.Entries.TryGetValue(intent.Prepared.Resource, out var entry) ||
            entry.ReleaseState is not (LeaseReleaseState.Released or LeaseReleaseState.NotOwned)) return;
        if (await ProgressAsync(intent, "renew-superseded-by-cleanup", entry.Identity, cancellationToken).ConfigureAwait(false))
            ClearIntent(lifecycle, intent);
    }

    private async Task<TransportOutcome?> ReleaseEntryAsync(
        Lifecycle lifecycle, LeaseEntry entry, QuiescenceEvidence observation, CancellationToken cancellationToken)
    {
        var intent = entry.ReleaseIntent ?? ReserveReleaseIntent(lifecycle, entry, observation);
        if (!intent.PreparedWritten && !await PrepareAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.Unknown; entry.ReleaseReason = "checkpoint-prepare-failed"; }
            return null;
        }
        if (!await ProgressAsync(intent, "release-observed", entry.Identity, cancellationToken, observation)
                .ConfigureAwait(false))
        {
            lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.Unknown; entry.ReleaseReason = "checkpoint-observation-failed"; }
            return null;
        }
        ReleaseTransportResult? result = null;
        var now = _clock.Elapsed;
        if (!IsValidQuiescence(observation, lifecycle.Owner, observation.ObservedAt, now))
        {
            lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.Unknown; entry.ReleaseReason = "quiescence-stale-before-send"; }
            return null;
        }
        try
        {
            var request = new ReleaseRequest(entry.Identity.LeaseId, lifecycle.Owner, entry.Identity.FencingToken, intent.Prepared.RequestId);
            result = await InvokeBoundedAsync(() => _transport.ReleaseAsync(request, cancellationToken)).ConfigureAwait(false);
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
            var code = result?.Outcome == TransportOutcome.LeaseLost ? "lease-lost" : "release-uncertain";
            await ProgressAsync(intent, code, entry.Identity, cancellationToken).ConfigureAwait(false);
            lock (_ledgerGate) { entry.ReleaseState = LeaseReleaseState.Unknown; entry.ReleaseReason = code; }
        }
        return result?.Outcome;
    }

    private async Task ProveNotOwnedAsync(Lifecycle lifecycle, LeaseEntry entry, CancellationToken cancellationToken)
    {
        var intent = ReserveInspectIntent(lifecycle, entry);
        if (!await PrepareAsync(intent, cancellationToken).ConfigureAwait(false)) return;
        InspectTransportResult? result;
        try
        {
            result = await InvokeBoundedAsync(() =>
                _transport.InspectAsync(new InspectRequest(entry.Evidence.Resource, lifecycle.Owner, intent.Prepared.RequestId), cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception) { return; }
        CoordinationInspection inspection;
        try { inspection = ValidateInspectionForCleanup(entry.Evidence.Resource, lifecycle.Owner, result); }
        catch (CoordinationException) { return; }
        var notOwned = inspection.State is InspectionState.Available or InspectionState.HeldByOther ||
            inspection.OwnedLease?.LeaseId != entry.Identity.LeaseId ||
            inspection.OwnedLease?.FencingToken != entry.Identity.FencingToken;
        if (!notOwned)
        {
            if (await ProgressAsync(intent, "original-still-owned", entry.Identity, cancellationToken).ConfigureAwait(false))
                lock (_ledgerGate) entry.InspectIntent = null;
            return;
        }
        if (await ProgressAsync(intent, "original-not-owned", entry.Identity, cancellationToken).ConfigureAwait(false))
        {
            lock (_ledgerGate)
            {
                entry.ReleaseState = LeaseReleaseState.NotOwned;
                entry.ReleaseReason = "original-not-owned";
                entry.InspectIntent = null;
            }
        }
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
            var intent = new OperationIntent(lifecycle, prepared);
            lifecycle.PendingIntent = intent;
            return intent;
        }
    }

    private OperationIntent ReserveReleaseIntent(Lifecycle lifecycle, LeaseEntry entry, QuiescenceEvidence observation)
    {
        lock (_ledgerGate)
        {
            if (entry.ReleaseIntent is not null) return entry.ReleaseIntent;
            var prepared = new PreparedCoordinationIntent(lifecycle.Id, lifecycle.NextSequence++, CoordinationIntentKind.Release,
                entry.Evidence.Resource, lifecycle.Owner, FreshRequestId(), _clock.Elapsed, _clock.DomainId, DefaultTtl,
                entry.Identity, observation);
            entry.ReleaseIntent = new OperationIntent(lifecycle, prepared);
            return entry.ReleaseIntent;
        }
    }

    private OperationIntent ReserveInspectIntent(Lifecycle lifecycle, LeaseEntry entry)
    {
        lock (_ledgerGate)
        {
            if (entry.InspectIntent is not null) return entry.InspectIntent;
            var prepared = new PreparedCoordinationIntent(lifecycle.Id, lifecycle.NextSequence++, CoordinationIntentKind.Inspect,
                entry.Evidence.Resource, lifecycle.Owner, FreshRequestId(), _clock.Elapsed, _clock.DomainId, DefaultTtl, entry.Identity);
            entry.InspectIntent = new OperationIntent(lifecycle, prepared);
            return entry.InspectIntent;
        }
    }

    private async Task<bool> PrepareAsync(OperationIntent intent, CancellationToken cancellationToken)
    {
        if (intent.PreparedWritten) return true;
        var lifecycle = intent.Lifecycle;
        if (lifecycle.WriterClaim is null) return false;
        var sequence = lifecycle.NextRecordSequence;
        var record = new CoordinationCheckpointRecord(
            CoordinationCheckpointRecord.CurrentSchemaVersion, sequence, intent.Prepared.OperationSequence,
            intent.Prepared.Kind, lifecycle.WriterClaim.WriterEpoch, 0, intent.Prepared, null);
        if (!await AppendOrReconcileAsync(lifecycle, record, cancellationToken).ConfigureAwait(false)) return false;
        lock (_ledgerGate) intent.PreparedWritten = true;
        return true;
    }

    private async Task<bool> ProgressAsync(
        OperationIntent intent, string code, CheckpointLeaseIdentity? lease, CancellationToken cancellationToken,
        QuiescenceEvidence? quiescence = null)
    {
        var lifecycle = intent.Lifecycle;
        if (lifecycle.WriterClaim is null) return false;
        var revision = intent.ProgressRevision + 1;
        var progress = new CoordinationIntentProgress(
            intent.Prepared.LifecycleId, intent.Prepared.OperationSequence, code, lease, revision, quiescence);
        var record = new CoordinationCheckpointRecord(
            CoordinationCheckpointRecord.CurrentSchemaVersion, lifecycle.NextRecordSequence,
            intent.Prepared.OperationSequence, intent.Prepared.Kind, lifecycle.WriterClaim.WriterEpoch,
            revision, null, progress);
        if (!await AppendOrReconcileAsync(lifecycle, record, cancellationToken).ConfigureAwait(false)) return false;
        lock (_ledgerGate)
        {
            intent.ProgressRevision = revision;
            intent.Resolved = true;
        }
        return true;
    }

    private async Task<bool> ClaimOriginalWriterAsync(Lifecycle lifecycle, CancellationToken cancellationToken)
    {
        try
        {
            var claim = await InvokeBoundedAsync(() => _checkpoints.ClaimWriterAsync(
                lifecycle.Id, FreshRequestId(), null, cancellationToken)).ConfigureAwait(false);
            if (claim is null || claim.LifecycleId != lifecycle.Id || claim.NextRecordSequence < 1 ||
                !Guid.TryParseExact(claim.WriterEpoch, "D", out _)) return false;
            lock (_ledgerGate)
            {
                lifecycle.WriterClaim = claim;
                lifecycle.NextRecordSequence = claim.NextRecordSequence;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    private async Task<bool> AppendOrReconcileAsync(
        Lifecycle lifecycle, CoordinationCheckpointRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await InvokeBoundedAsync(async () =>
            {
                await _checkpoints.AppendAsync(lifecycle.WriterClaim!, record.RecordSequence, record, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception) { }

        try
        {
            var records = await InvokeBoundedAsync(
                () => _checkpoints.ReadLifecycleAsync(lifecycle.Id, cancellationToken)).ConfigureAwait(false);
            if (records is null) return false;
            var matches = records.Where(item => item.RecordSequence == record.RecordSequence).ToArray();
            if (matches.Length == 0 || matches.Any(item => item != record)) return false;
            lock (_ledgerGate) lifecycle.NextRecordSequence = record.RecordSequence + 1;
            return true;
        }
        catch (Exception) { return false; }
    }

    private void ClearIntent(Lifecycle lifecycle, OperationIntent intent, bool preserveRelease = false)
    {
        lock (_ledgerGate)
        {
            if (ReferenceEquals(lifecycle.PendingIntent, intent))
                lifecycle.PendingIntent = lifecycle.DeferredIntents.TryDequeue(out var next) ? next : null;
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
            if (operation == "renewal" && !CanBeHeld(lifecycle))
            {
                lifecycle.CleanupAuthority = authority;
                RevokeAuthorityUnderGate(lifecycle);
                _authorities[authority] = lifecycle;
                lifecycle.Phase = CoordinationLifecyclePhase.Cleaning;
                lifecycle.Ownership = LeaseOwnership.Uncertain;
                lifecycle.Reason = "authority-expired";
                EnsureReleaseContinuationUnderGate(lifecycle);
                throw new CoordinationUncertainException("renewal-requires-live-authority");
            }
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
        if (lifecycle.CleanupAuthority is not null) _authorities.Remove(lifecycle.CleanupAuthority);
        if (lifecycle.AcquireContinuation is not null) _acquireContinuations.Remove(lifecycle.AcquireContinuation);
        if (lifecycle.ReleaseContinuation is not null) _releaseContinuations.Remove(lifecycle.ReleaseContinuation);
        lifecycle.AcquireContinuation = null;
        lifecycle.ReleaseContinuation = null;
        lifecycle.CleanupAuthority = null;
        _lifecycles.Remove(lifecycle.Id);
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
            return await InvokeBoundedAsync(() => _quiescence.ObserveAsync(lifecycle.Owner, cancellationToken)).ConfigureAwait(false);
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
        try { deadline = LeaseDeadline.ComputeSafeDeadline(serverTime, expiresAt,
            receivedAt - firstSend, receivedAt, _maximumClockUncertainty); }
        catch (ArgumentOutOfRangeException exception) { throw new CoordinationProtocolException("deadline-input-invalid", exception); }
        return new LeaseEvidence(resource, owner, leaseId, fencingToken, serverTime, expiresAt,
            deadline, firstSend + RenewalTarget, generation);
    }

    private LeaseEvidence CreateInspectEvidence(
        CoordinationResource resource, LeaseOwner owner, string leaseId, string fencingToken,
        DateTimeOffset serverTime, DateTimeOffset expiresAt, TimeSpan firstSend, TimeSpan receivedAt)
    {
        if (receivedAt < firstSend || expiresAt <= serverTime) throw new CoordinationProtocolException("inspect-time-invalid");
        TimeSpan deadline;
        try { deadline = LeaseDeadline.ComputeSafeDeadline(serverTime, expiresAt,
            receivedAt - firstSend, receivedAt, _maximumClockUncertainty); }
        catch (ArgumentOutOfRangeException exception) { throw new CoordinationProtocolException("deadline-input-invalid", exception); }
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

    private static CoordinationInspection ValidateInspectionForCleanup(
        CoordinationResource resource, LeaseOwner owner, InspectTransportResult? result)
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
        var evidence = CreateCleanupEvidence(resource, owner, result.LeaseId ?? "", result.FencingToken ?? "",
            result.ServerTime.Value, result.ExpiresAt.Value, FreshRequestId());
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
            _ = MergeAcquireEvidence(lifecycle, evidence);
        }
        catch (CoordinationException) { }
    }

    private LeaseEntry MergeAcquireEvidence(Lifecycle lifecycle, LeaseEvidence evidence)
    {
        lock (_ledgerGate)
        {
            if (!lifecycle.Entries.TryGetValue(evidence.Resource, out var existing))
            {
                existing = new LeaseEntry(evidence);
                lifecycle.Entries.Add(evidence.Resource, existing);
                return existing;
            }
            if (!SameServerIdentity(existing.Identity, Identity(evidence)))
                throw new CoordinationProtocolException("acquire-replay-identity-drift");
            return existing;
        }
    }

    private static CheckpointLeaseIdentity Identity(LeaseEvidence evidence) =>
        new(evidence.Resource, evidence.Owner, evidence.LeaseId, evidence.FencingToken, evidence.Generation,
            evidence.ServerTime, evidence.ExpiresAt, evidence.SafeDeadline, evidence.RenewAt);

    private static LeaseEntry RecoveredEntry(CheckpointLeaseIdentity identity)
    {
        return new LeaseEntry(CreateCleanupEvidence(identity.Resource, identity.Owner, identity.LeaseId,
            identity.FencingToken, identity.ServerTime, identity.ExpiresAt, identity.Generation));
    }

    private static OperationIntent RecoveredIntent(Lifecycle lifecycle, PreparedCoordinationIntent prepared) =>
        new(lifecycle, prepared) { PreparedWritten = true };

    private static bool SameServerIdentity(CheckpointLeaseIdentity left, CheckpointLeaseIdentity right) =>
        left.Resource == right.Resource && left.Owner == right.Owner && left.LeaseId == right.LeaseId &&
        left.FencingToken == right.FencingToken;

    private static bool SameIdentity(CheckpointLeaseIdentity left, CheckpointLeaseIdentity right) =>
        SameServerIdentity(left, right) && left.Generation == right.Generation;

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

    private static void ValidateProgressTransition(
        CoordinationIntentKind kind, CoordinationIntentProgress? prior, CoordinationIntentProgress current)
    {
        var allowed = kind switch
        {
            CoordinationIntentKind.Acquire => current.AdapterCode is "acquired" or "contended",
            CoordinationIntentKind.Renew => current.AdapterCode is "renewed" or "renewed-cleanup-only" or
                "renew-replay-nonauthoritative" or "renew-superseded-by-cleanup",
            CoordinationIntentKind.Release => current.AdapterCode is "release-observed" or "released" or "lease-lost" or "release-uncertain",
            CoordinationIntentKind.Inspect => current.AdapterCode is "inspected" or "original-still-owned" or "original-not-owned",
            _ => false,
        };
        if (!allowed) throw new CoordinationProtocolException("checkpoint-progress-code-invalid");
        if (prior?.AdapterCode is "acquired" or "contended" or "released" or "original-not-owned" ||
            prior?.AdapterCode is "renewed" or "renewed-cleanup-only" or "renew-superseded-by-cleanup")
            throw new CoordinationProtocolException("checkpoint-terminal-transition-invalid");
    }

    private bool CanBeHeld(Lifecycle lifecycle) =>
        lifecycle.PendingIntent is null && lifecycle.Entries.Count == lifecycle.Resources.Length &&
        lifecycle.Resources.All(resource => lifecycle.Entries.TryGetValue(resource, out var entry) &&
            entry.ReleaseState == LeaseReleaseState.Pending && _clock.Elapsed < entry.Evidence.SafeDeadline);

    private static bool IsClosed(Lifecycle lifecycle) =>
        lifecycle.AcquisitionResolved && lifecycle.PendingIntent is null && lifecycle.DeferredIntents.Count == 0 && lifecycle.Entries.Values.All(entry =>
            entry.ReleaseState is LeaseReleaseState.Released or LeaseReleaseState.NotOwned);

    private static bool HasUnresolvedAcquire(Lifecycle lifecycle) =>
        lifecycle.PendingIntent?.Prepared.Kind == CoordinationIntentKind.Acquire ||
        lifecycle.DeferredIntents.Any(item => item.Prepared.Kind == CoordinationIntentKind.Acquire);

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

    private Task<T?> InvokeBoundedAsync<T>(Func<Task<T>> invoke) =>
        InvokeBoundedStaticAsync(invoke, _rollbackTimeout);

    private static async Task<T?> InvokeBoundedStaticAsync<T>(Func<Task<T>> invoke, TimeSpan timeout)
    {
        Task<T>? task;
        try { task = invoke(); }
        catch { return default; }
        if (task is null) return default;
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, task))
        {
            _ = task.ContinueWith(item => _ = item.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return default;
        }
        try { return await task.ConfigureAwait(false); }
        catch { return default; }
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
        public Queue<OperationIntent> DeferredIntents { get; } = [];
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
        public CoordinationWriterClaim? WriterClaim { get; set; }
        public long NextRecordSequence { get; set; } = 1;
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
        public OperationIntent? InspectIntent { get; set; }
        public LeaseReleaseState ReleaseState { get; set; } = LeaseReleaseState.Pending;
        public string ReleaseReason { get; set; } = "release-not-attempted";
    }

    private sealed class OperationIntent(Lifecycle lifecycle, PreparedCoordinationIntent prepared)
    {
        public Lifecycle Lifecycle { get; } = lifecycle;
        public PreparedCoordinationIntent Prepared { get; } = prepared;
        public bool PreparedWritten { get; set; }
        public bool Resolved { get; set; }
        public long ProgressRevision { get; set; }
    }

    private sealed class VolatileCheckpointStore : ICoordinationCheckpointStore
    {
        private readonly List<CoordinationCheckpointRecord> _records = [];
        private readonly Dictionary<string, string> _epochs = new(StringComparer.Ordinal);
        private readonly HashSet<(string LifecycleId, string Proof)> _usedTakeovers = [];

        public Task<CoordinationWriterClaim> ClaimWriterAsync(
            string lifecycleId, string proposedWriterEpoch, string? trustedTakeoverProof,
            CancellationToken cancellationToken = default)
        {
            lock (_records)
            {
                if (_epochs.ContainsKey(lifecycleId))
                {
                    if (string.IsNullOrWhiteSpace(trustedTakeoverProof) ||
                        !_usedTakeovers.Add((lifecycleId, trustedTakeoverProof)))
                        throw new CoordinationUncertainException("checkpoint-writer-already-owned");
                }
                _epochs[lifecycleId] = proposedWriterEpoch;
                var next = _records.Where(item =>
                    item.Prepared?.LifecycleId == lifecycleId || item.Progress?.LifecycleId == lifecycleId)
                    .Select(item => item.RecordSequence).DefaultIfEmpty().Max() + 1;
                return Task.FromResult(new CoordinationWriterClaim(lifecycleId, proposedWriterEpoch, next));
            }
        }

        public Task AppendAsync(
            CoordinationWriterClaim claim, long expectedRecordSequence, CoordinationCheckpointRecord record,
            CancellationToken cancellationToken = default)
        {
            lock (_records)
            {
                if (!_epochs.TryGetValue(claim.LifecycleId, out var epoch) || epoch != claim.WriterEpoch ||
                    record.WriterEpoch != epoch)
                    throw new CoordinationUncertainException("checkpoint-writer-stale");
                var lifecycleRecords = _records.Where(item =>
                    item.Prepared?.LifecycleId == claim.LifecycleId || item.Progress?.LifecycleId == claim.LifecycleId).ToArray();
                var next = lifecycleRecords.Select(item => item.RecordSequence).DefaultIfEmpty().Max() + 1;
                var duplicate = lifecycleRecords.SingleOrDefault(item => item.RecordSequence == expectedRecordSequence);
                if (duplicate is not null)
                {
                    if (duplicate != record) throw new CoordinationProtocolException("checkpoint-cas-conflicting-duplicate");
                    return Task.CompletedTask;
                }
                if (expectedRecordSequence != next || record.RecordSequence != expectedRecordSequence)
                    throw new CoordinationUncertainException("checkpoint-cas-conflict");
                _records.Add(record);
                return Task.CompletedTask;
            }
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
