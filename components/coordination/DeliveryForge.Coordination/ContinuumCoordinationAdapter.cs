namespace DeliveryForge.Coordination;

public sealed class ContinuumCoordinationAdapter
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan RenewalTarget = TimeSpan.FromSeconds(100);
    private readonly ICoordinationTransport _transport;
    private readonly IMonotonicClock _clock;

    private ContinuumCoordinationAdapter(ICoordinationTransport transport, CoordinationCapabilities capabilities, IMonotonicClock clock)
    {
        _transport = transport;
        Capabilities = capabilities;
        _clock = clock;
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
        CancellationToken cancellationToken = default,
        IMonotonicClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var capabilities = await transport.DetectCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var reason = ValidateCapabilities(capabilities, requireFixture: true);
        if (reason is not null) throw new CoordinationUnavailableException(reason);
        return new ContinuumCoordinationAdapter(transport, capabilities, clock ?? new StopwatchMonotonicClock());
    }

    public bool MayDispatch(LeaseAcquisition acquisition)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        var now = _clock.Elapsed;
        return acquisition.Ownership == LeaseOwnership.Held && acquisition.Leases.Count > 0 &&
            acquisition.Leases.All(lease => now < lease.SafeDeadline);
    }

    public async Task<LeaseAcquisition> AcquireAllAsync(
        IEnumerable<CoordinationResource> resources,
        LeaseOwner owner,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(owner);
        if (ttl != DefaultTtl) throw new ArgumentOutOfRangeException(nameof(ttl), "V1 production ownership uses the configured 300-second TTL exactly.");
        var ordered = resources.Distinct().Order(Utf8LexicalComparer.Instance).ToArray();
        if (ordered.Length == 0) throw new ArgumentException("At least one coordination resource is required.", nameof(resources));

        var acquired = new List<LeaseGrant>(ordered.Length);
        foreach (var resource in ordered)
        {
            AcquireTransportResult response;
            var sentAt = _clock.Elapsed;
            try
            {
                response = await _transport.AcquireAsync(
                    new AcquireRequest(resource, owner, ttl, FreshRequestId()), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await RollBackPrefixAsync(acquired, owner, CancellationToken.None).ConfigureAwait(false);
                throw new CoordinationUncertainException("Acquire transport failed; ownership is uncertain and dispatch is forbidden.", exception);
            }
            var receivedAt = _clock.Elapsed;

            if (response.Outcome == TransportOutcome.Contended)
            {
                var cleanRollback = await RollBackPrefixAsync(acquired, owner, CancellationToken.None).ConfigureAwait(false);
                return new LeaseAcquisition(owner, [], cleanRollback ? LeaseOwnership.Contended : LeaseOwnership.Uncertain,
                    cleanRollback ? response.Reason : "Contention occurred and acquired-prefix release was uncertain.");
            }
            if (response.Outcome != TransportOutcome.Acquired)
            {
                await RollBackPrefixAsync(acquired, owner, CancellationToken.None).ConfigureAwait(false);
                throw new CoordinationUncertainException($"Acquire did not return authoritative ownership: {response.Reason}");
            }

            var possibleLease = new LeaseGrant(resource, owner,
                response.LeaseId ?? "", response.FencingToken ?? "",
                response.ServerTime ?? default, response.ExpiresAt ?? default, RenewalTarget,
                receivedAt + ttl, receivedAt + RenewalTarget);
            acquired.Add(possibleLease);
            try
            {
                if (response.ServerTime is null || response.ExpiresAt is null || response.ExpiresAt.Value - response.ServerTime.Value != ttl)
                    throw new CoordinationProtocolException("Acquire response did not grant the exact requested TTL.");
                var safeDeadline = LeaseDeadline.ComputeSafeDeadline(
                    response.ServerTime.Value, response.ExpiresAt.Value, receivedAt - sentAt, receivedAt, TimeSpan.Zero);
                possibleLease = possibleLease with
                {
                    ServerTime = response.ServerTime.Value,
                    ExpiresAt = response.ExpiresAt.Value,
                    SafeDeadline = safeDeadline
                };
                possibleLease.Validate();
                acquired[^1] = possibleLease;
            }
            catch (CoordinationException exception)
            {
                var cleanRollback = await RollBackPrefixAsync(acquired, owner, CancellationToken.None).ConfigureAwait(false);
                throw new CoordinationUncertainException(
                    cleanRollback
                        ? "Acquire response was invalid; possible ownership was released and dispatch is forbidden."
                        : "Acquire response was invalid and possible ownership could not be released authoritatively.", exception);
            }
        }

        return new LeaseAcquisition(owner, acquired.AsReadOnly(), LeaseOwnership.Held, "All resources are authoritatively held.");
    }

    public async Task<LeaseAcquisition> RenewAllAsync(
        LeaseAcquisition acquisition,
        CancellationToken cancellationToken = default)
    {
        if (!MayDispatch(acquisition))
            throw new CoordinationUncertainException("Renewal requires complete ownership before the conservative safe deadline.");
        var renewed = new List<LeaseGrant>(acquisition.Leases.Count);
        foreach (var lease in acquisition.Leases)
        {
            RenewTransportResult result;
            var sentAt = _clock.Elapsed;
            try
            {
                result = await _transport.RenewAsync(new RenewRequest(lease, FreshRequestId()), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new CoordinationUncertainException("Renew transport failed; halt dispatch and publication before the safe deadline.", exception);
            }
            var receivedAt = _clock.Elapsed;
            if (result.Outcome != TransportOutcome.Renewed || result.ServerTime is null || result.ExpiresAt is null)
                throw new CoordinationUncertainException($"Renewal was not authoritative: {result.Reason}");
            if (result.ExpiresAt.Value - result.ServerTime.Value != DefaultTtl)
                throw new CoordinationUncertainException("Renewal did not grant the exact requested TTL.");
            var safeDeadline = LeaseDeadline.ComputeSafeDeadline(
                result.ServerTime.Value, result.ExpiresAt.Value, receivedAt - sentAt, receivedAt, TimeSpan.Zero);
            var replacement = lease with {
                ServerTime = result.ServerTime.Value, ExpiresAt = result.ExpiresAt.Value,
                SafeDeadline = safeDeadline, RenewAt = receivedAt + RenewalTarget };
            replacement.Validate();
            renewed.Add(replacement);
        }
        return new LeaseAcquisition(acquisition.Owner, renewed.AsReadOnly(), LeaseOwnership.Held, "All leases renewed authoritatively.");
    }

    public Task<InspectTransportResult> InspectAsync(
        CoordinationResource resource,
        LeaseOwner owner,
        CancellationToken cancellationToken = default) =>
        _transport.InspectAsync(new InspectRequest(resource, owner, FreshRequestId()), cancellationToken);

    public async Task<LeaseAcquisition> ReleaseAllAsync(
        LeaseAcquisition acquisition,
        IOwnedRunQuiescence quiescence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        if (acquisition.Ownership != LeaseOwnership.Held || acquisition.Leases.Count == 0)
            throw new CoordinationUncertainException("Release requires a previously complete ownership set.");
        ArgumentNullException.ThrowIfNull(quiescence);
        var evidence = await quiescence.ObserveAsync(acquisition.Owner, cancellationToken).ConfigureAwait(false);
        if (evidence is null || !evidence.Quiescent || string.IsNullOrWhiteSpace(evidence.EvidenceReference))
            throw new CoordinationUncertainException("Positive owned-run quiescence evidence is required before lease release.");

        foreach (var lease in acquisition.Leases.Reverse())
        {
            ReleaseTransportResult result;
            try
            {
                result = await _transport.ReleaseAsync(
                    new ReleaseRequest(lease.LeaseId, acquisition.Owner, lease.FencingToken, FreshRequestId()), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new CoordinationUncertainException("Release transport failed; ownership remains uncertain.", exception);
            }
            if (result.Outcome != TransportOutcome.Released)
                throw new CoordinationUncertainException($"Lease release was not authoritative: {result.Reason}");
        }
        return new LeaseAcquisition(acquisition.Owner, [], LeaseOwnership.Released,
            $"Released after quiescence evidence {evidence.EvidenceReference}.");
    }

    private async Task<bool> RollBackPrefixAsync(IReadOnlyList<LeaseGrant> acquired, LeaseOwner owner, CancellationToken cancellationToken)
    {
        using var boundedRollback = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        boundedRollback.CancelAfter(TimeSpan.FromSeconds(10));
        var certain = true;
        foreach (var lease in acquired.Reverse())
        {
            try
            {
                var result = await _transport.ReleaseAsync(
                    new ReleaseRequest(lease.LeaseId, owner, lease.FencingToken, FreshRequestId()), boundedRollback.Token).ConfigureAwait(false);
                certain &= result.Outcome == TransportOutcome.Released;
            }
            catch (Exception)
            {
                certain = false;
            }
        }
        return certain;
    }

    private static string? ValidateCapabilities(CoordinationCapabilities? capabilities, bool requireFixture)
    {
        if (capabilities is null) return "Coordination capability response is missing.";
        if (requireFixture != capabilities.Fixture)
            return requireFixture
                ? "Contract-test construction requires an explicitly marked fixture capability."
                : "A fixture capability cannot construct a production coordination adapter.";
        if (capabilities.Availability != CoordinationAvailability.Available) return "Production coordination capability is unavailable or incomplete.";
        if (capabilities.ContractVersion != CoordinationCapabilities.V1ContractVersion) return "Coordination contract version is unsupported.";
        if (capabilities.Operations is null || !Enum.GetValues<CoordinationOperation>().All(capabilities.Operations.Contains)) return "Required coordination operations are missing.";
        if (capabilities.MinimumTtl > DefaultTtl || capabilities.MaximumTtl < DefaultTtl) return "The required 300-second TTL is unsupported.";
        if (capabilities.IdempotencyHorizon < TimeSpan.FromHours(24)) return "The required idempotency horizon is unsupported.";
        if (!capabilities.DecimalStringFencing || !capabilities.HolderIdentityMasking || !capabilities.AuthenticatedExactScope)
            return "Fencing, masking, or authenticated exact-scope capability is incomplete.";
        return null;
    }

    private static string FreshRequestId() => Guid.NewGuid().ToString("D");
}
