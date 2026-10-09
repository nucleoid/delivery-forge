namespace DeliveryForge.Coordination;

public sealed class ContinuumCoordinationAdapter
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan RenewalTarget = TimeSpan.FromSeconds(100);
    private readonly ICoordinationTransport _transport;

    private ContinuumCoordinationAdapter(ICoordinationTransport transport, CoordinationCapabilities capabilities)
    {
        _transport = transport;
        Capabilities = capabilities;
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

        var reason = ValidateProductionCapabilities(capabilities);
        if (reason is not null) throw new CoordinationUnavailableException(reason);
        return new ContinuumCoordinationAdapter(transport, capabilities);
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
            try
            {
                response = await _transport.AcquireAsync(
                    new AcquireRequest(resource, owner, ttl, FreshRequestId()), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await RollBackPrefixAsync(acquired, owner, CancellationToken.None).ConfigureAwait(false);
                throw new CoordinationUncertainException("Acquire transport failed; ownership is uncertain and dispatch is forbidden.", exception);
            }

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

            var lease = new LeaseGrant(resource, owner,
                response.LeaseId ?? "", response.FencingToken ?? "",
                response.ServerTime ?? default, response.ExpiresAt ?? default, RenewalTarget);
            lease.Validate();
            acquired.Add(lease);
        }

        return new LeaseAcquisition(owner, acquired.AsReadOnly(), LeaseOwnership.Held, "All resources are authoritatively held.");
    }

    public async Task<LeaseAcquisition> RenewAllAsync(
        LeaseAcquisition acquisition,
        CancellationToken cancellationToken = default)
    {
        RequireHeld(acquisition);
        var renewed = new List<LeaseGrant>(acquisition.Leases.Count);
        foreach (var lease in acquisition.Leases)
        {
            RenewTransportResult result;
            try
            {
                result = await _transport.RenewAsync(new RenewRequest(lease, FreshRequestId()), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new CoordinationUncertainException("Renew transport failed; halt dispatch and publication before the safe deadline.", exception);
            }
            if (result.Outcome != TransportOutcome.Renewed || result.ServerTime is null || result.ExpiresAt is null)
                throw new CoordinationUncertainException($"Renewal was not authoritative: {result.Reason}");
            var replacement = lease with { ServerTime = result.ServerTime.Value, ExpiresAt = result.ExpiresAt.Value };
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
        RequireHeld(acquisition);
        ArgumentNullException.ThrowIfNull(quiescence);
        var evidence = await quiescence.ObserveAsync(acquisition.Owner, cancellationToken).ConfigureAwait(false);
        if (!evidence.Quiescent || string.IsNullOrWhiteSpace(evidence.EvidenceReference))
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
            catch (Exception exception) when (exception is not OperationCanceledException)
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
        var certain = true;
        foreach (var lease in acquired.Reverse())
        {
            try
            {
                var result = await _transport.ReleaseAsync(
                    new ReleaseRequest(lease.LeaseId, owner, lease.FencingToken, FreshRequestId()), cancellationToken).ConfigureAwait(false);
                certain &= result.Outcome == TransportOutcome.Released;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                certain = false;
            }
        }
        return certain;
    }

    private static string? ValidateProductionCapabilities(CoordinationCapabilities capabilities)
    {
        if (capabilities.Fixture) return "A fixture capability cannot construct a production coordination adapter.";
        if (capabilities.Availability != CoordinationAvailability.Available) return "Production coordination capability is unavailable or incomplete.";
        if (capabilities.ContractVersion != CoordinationCapabilities.V1ContractVersion) return "Coordination contract version is unsupported.";
        if (!Enum.GetValues<CoordinationOperation>().All(capabilities.Operations.Contains)) return "Required coordination operations are missing.";
        if (capabilities.MinimumTtl > DefaultTtl || capabilities.MaximumTtl < DefaultTtl) return "The required 300-second TTL is unsupported.";
        if (capabilities.IdempotencyHorizon < TimeSpan.FromHours(24)) return "The required idempotency horizon is unsupported.";
        if (!capabilities.DecimalStringFencing || !capabilities.HolderIdentityMasking || !capabilities.AuthenticatedExactScope)
            return "Fencing, masking, or authenticated exact-scope capability is incomplete.";
        return null;
    }

    private static void RequireHeld(LeaseAcquisition acquisition)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        if (!acquisition.MayDispatch) throw new CoordinationUncertainException("Operation requires complete authoritative ownership.");
    }

    private static string FreshRequestId() => Guid.NewGuid().ToString("D");
}
