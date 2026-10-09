namespace DeliveryForge.Coordination.Tests;

public sealed class CoordinationContractTests
{
    [Fact]
    public async Task Production_remains_strictly_unavailable()
    {
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities(fixture: false));

        var error = await Assert.ThrowsAsync<CoordinationUnavailableException>(() =>
            ContinuumCoordinationAdapter.CreateProductionAsync(transport, TestContext.Current.CancellationToken));

        Assert.Contains("INCOMPLETE", error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, CoordinationCapabilities> InvalidCapabilities => new()
    {
        { "availability", CoordinationContractFixture.Capabilities(availability: CoordinationAvailability.Incomplete) },
        { "version", CoordinationContractFixture.Capabilities(contractVersion: "continuum.coordination.v2") },
        { "operations", CoordinationContractFixture.Capabilities(operations: new HashSet<CoordinationOperation> { CoordinationOperation.Acquire }) },
        { "minimum TTL", CoordinationContractFixture.Capabilities(minimumTtl: TimeSpan.FromSeconds(301)) },
        { "maximum TTL", CoordinationContractFixture.Capabilities(maximumTtl: TimeSpan.FromSeconds(299)) },
        { "idempotency", CoordinationContractFixture.Capabilities(idempotencyHorizon: TimeSpan.FromHours(23)) },
        { "fencing", CoordinationContractFixture.Capabilities(decimalStringFencing: false) },
        { "masking", CoordinationContractFixture.Capabilities(holderIdentityMasking: false) },
        { "scope", CoordinationContractFixture.Capabilities(authenticatedExactScope: false) },
    };

    [Theory]
    [MemberData(nameof(InvalidCapabilities))]
    public async Task Contract_fixture_rejects_each_invalid_capability_field(string field, CoordinationCapabilities capabilities)
    {
        var error = await Assert.ThrowsAsync<CoordinationUnavailableException>(() => CreateAdapterAsync(
            new RecordingTransport(capabilities)));

        Assert.False(string.IsNullOrWhiteSpace(field));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task Dispatch_authority_is_adapter_owned_nonforgeable_and_revoked_before_release()
    {
        var clock = new FakeMonotonicClock("process-clock-a");
        var source = new RecordingQuiescenceSource(clock, [true]);
        var transport = SingleLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, clock, source);
        var acquisition = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl);
        var foreignAdapter = await CreateAdapterAsync(SingleLeaseTransport(), new FakeMonotonicClock("process-clock-b"),
            new RecordingQuiescenceSource(new FakeMonotonicClock("process-clock-b"), [true]));

        Assert.NotNull(acquisition.Authority);
        Assert.True(adapter.MayDispatch(acquisition.Authority!));
        Assert.False(foreignAdapter.MayDispatch(acquisition.Authority!));
        Assert.False(adapter.MayDispatch(CoordinationContractFixture.CopyAuthority(acquisition.Authority!)));
        Assert.False(adapter.MayDispatch(CoordinationContractFixture.ForgeAuthority(acquisition.Authority!, nonce: "forged")));
        Assert.Empty(typeof(DispatchAuthority).GetConstructors());

        source.BeforeObservation = () => Assert.False(adapter.MayDispatch(acquisition.Authority!));
        var released = await adapter.ReleaseAllAsync(acquisition.Authority!);

        Assert.Equal(LeaseOwnership.Released, released.Snapshot.Ownership);
        Assert.False(adapter.MayDispatch(acquisition.Authority!));
    }

    [Fact]
    public async Task Deadline_and_renewal_revoke_the_original_authority()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = SingleLeaseTransport();
        transport.RenewResults.Enqueue(RenewTransportResult.Renewed(ServerNow, ServerNow.AddMinutes(5)));
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true]));
        var acquisition = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl);

        var renewal = await adapter.RenewAllAsync(acquisition.Authority!);

        Assert.False(adapter.MayDispatch(acquisition.Authority!));
        Assert.NotNull(renewal.Authority);
        Assert.True(adapter.MayDispatch(renewal.Authority!));
        clock.Advance(TimeSpan.FromSeconds(300));
        Assert.False(adapter.MayDispatch(renewal.Authority!));
    }

    [Fact]
    public async Task Uncertain_acquire_is_saved_before_invoke_and_reconciled_with_the_same_request_id()
    {
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities());
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-a", "1", ServerNow, ServerNow.AddMinutes(5)));
        transport.AcquireExceptions.Enqueue(new TimeoutException("response lost"));
        var adapter = await CreateAdapterAsync(transport);

        var uncertain = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner, ContinuumCoordinationAdapter.DefaultTtl);

        Assert.Equal(LeaseOwnership.Uncertain, uncertain.Snapshot.Ownership);
        Assert.NotNull(uncertain.AcquireContinuation);
        Assert.Equal(Issue(7), uncertain.Uncertainty!.Resource);
        Assert.Equal(transport.AcquireRequests[1].RequestId, uncertain.Uncertainty.RequestId);
        Assert.Equal(Owner, uncertain.Uncertainty.Owner);
        Assert.Single(uncertain.ReleaseProgress);
        Assert.Equal(LeaseReleaseState.Released, uncertain.ReleaseProgress[0].State);

        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-b", "2", ServerNow, ServerNow.AddMinutes(5)));
        var reconciled = await adapter.ReconcileAcquireAsync(uncertain.AcquireContinuation!);

        Assert.Equal(transport.AcquireRequests[1].RequestId, transport.AcquireRequests[2].RequestId);
        Assert.Equal(LeaseOwnership.Held, reconciled.Snapshot.Ownership);
        Assert.True(adapter.MayDispatch(reconciled.Authority!));
    }

    [Fact]
    public async Task Noncooperative_rollback_is_bounded_and_retains_unknown_cleanup()
    {
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities());
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-a", "1", ServerNow, ServerNow.AddMinutes(5)));
        transport.AcquireResults.Enqueue(AcquireTransportResult.Contended());
        var hungRelease = new TaskCompletionSource<ReleaseTransportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.ReleaseHandler = (_, _) => hungRelease.Task;
        var adapter = await CreateAdapterAsync(transport, rollbackTimeout: TimeSpan.FromMilliseconds(20));

        var result = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner, ContinuumCoordinationAdapter.DefaultTtl)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(LeaseOwnership.Uncertain, result.Snapshot.Ownership);
        Assert.NotNull(result.ReleaseContinuation);
        Assert.Equal(LeaseReleaseState.Unknown, result.ReleaseProgress.Single().State);
        hungRelease.TrySetResult(ReleaseTransportResult.Released());
        await hungRelease.Task;
    }

    [Fact]
    public async Task Partial_release_reports_progress_reuses_ids_and_does_not_treat_lease_lost_as_released()
    {
        var transport = TwoLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Uncertain("lost response"));
        var clock = new FakeMonotonicClock("clock");
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true, true, true]));
        var acquisition = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner, ContinuumCoordinationAdapter.DefaultTtl);

        var partial = await adapter.ReleaseAllAsync(acquisition.Authority!);

        Assert.Equal(LeaseOwnership.Uncertain, partial.Snapshot.Ownership);
        Assert.Equal(2, partial.Leases.Count);
        Assert.Contains(partial.Leases, item => item.State == LeaseReleaseState.Released);
        var unknown = Assert.Single(partial.Leases, item => item.State == LeaseReleaseState.Unknown);
        Assert.NotNull(partial.Continuation);
        var firstRequestIds = transport.ReleaseRequests.ToDictionary(request => request.LeaseId, request => request.RequestId);

        transport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        var stillUnknown = await adapter.ContinueReleaseAsync(partial.Continuation!);
        Assert.Equal(LeaseOwnership.Uncertain, stillUnknown.Snapshot.Ownership);
        Assert.Equal(firstRequestIds[unknown.LeaseId], transport.ReleaseRequests.Last().RequestId);
        Assert.Equal(3, transport.ReleaseRequests.Count);

        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        var complete = await adapter.ContinueReleaseAsync(stillUnknown.Continuation!);
        Assert.Equal(LeaseOwnership.Released, complete.Snapshot.Ownership);
        Assert.Equal(4, transport.ReleaseRequests.Count);
        Assert.Equal(firstRequestIds[unknown.LeaseId], transport.ReleaseRequests.Last().RequestId);
    }

    [Fact]
    public async Task Inspect_masks_foreign_holder_and_validates_owned_details()
    {
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities());
        transport.InspectResults.Enqueue(InspectTransportResult.HeldByOther("leaked-id", "99"));
        transport.InspectResults.Enqueue(InspectTransportResult.HeldByCaller("lease-a", "7", ServerNow, ServerNow.AddMinutes(5)));
        var adapter = await CreateAdapterAsync(transport);

        var foreign = await adapter.InspectAsync(Issue(6), Owner);
        var own = await adapter.InspectAsync(Issue(6), Owner);

        Assert.Equal(InspectionState.HeldByOther, foreign.State);
        Assert.Null(foreign.OwnedLease);
        Assert.Equal(InspectionState.HeldByCaller, own.State);
        Assert.Equal(Issue(6), own.OwnedLease!.Resource);
        Assert.Equal(Owner, own.OwnedLease.Owner);
        Assert.Equal("7", own.OwnedLease.FencingToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inspect_malformed_or_exception_is_typed_uncertainty(bool throws)
    {
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities());
        if (throws) transport.InspectExceptions.Enqueue(new TimeoutException("inspect timeout"));
        else transport.InspectResults.Enqueue(InspectTransportResult.HeldByCaller("", "007", ServerNow, ServerNow));
        var adapter = await CreateAdapterAsync(transport);

        await Assert.ThrowsAsync<CoordinationUncertainException>(() => adapter.InspectAsync(Issue(6), Owner));
    }

    [Fact]
    public async Task Release_reobserves_bound_quiescence_before_each_lease_and_stops_on_resumed_activity()
    {
        var clock = new FakeMonotonicClock("clock-domain");
        var source = new RecordingQuiescenceSource(clock, [true, false]);
        var transport = TwoLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        var adapter = await CreateAdapterAsync(transport, clock, source);
        var acquisition = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner, ContinuumCoordinationAdapter.DefaultTtl);

        var result = await adapter.ReleaseAllAsync(acquisition.Authority!);

        Assert.Equal(2, source.Observations);
        Assert.Single(transport.ReleaseRequests);
        Assert.Equal(LeaseOwnership.Uncertain, result.Snapshot.Ownership);
        Assert.NotNull(result.Continuation);
    }

    [Fact]
    public async Task Renewal_success_uses_rtt_capped_ttl_and_partial_or_error_revokes_dispatch()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = TwoLeaseTransport();
        transport.RenewHandler = (request, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(request.Lease.Resource == Issue(6)
                ? RenewTransportResult.Renewed(ServerNow, ServerNow.AddMinutes(5))
                : RenewTransportResult.Uncertain("second renewal uncertain"));
        };
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true]));
        var acquisition = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner, ContinuumCoordinationAdapter.DefaultTtl);

        var partial = await adapter.RenewAllAsync(acquisition.Authority!);

        Assert.Null(partial.Authority);
        Assert.NotNull(partial.ReleaseContinuation);
        Assert.False(adapter.MayDispatch(acquisition.Authority!));
        Assert.Equal(TimeSpan.FromSeconds(302), partial.Snapshot.Leases[0].SafeDeadline);
    }

    [Fact]
    public async Task Renewal_rejects_short_or_long_ttl_and_transport_errors()
    {
        foreach (var response in new Func<RenewTransportResult>[]
        {
            () => RenewTransportResult.Renewed(ServerNow, ServerNow.AddSeconds(299)),
            () => RenewTransportResult.Renewed(ServerNow, ServerNow.AddSeconds(301)),
        })
        {
            var transport = SingleLeaseTransport();
            transport.RenewResults.Enqueue(response());
            var adapter = await CreateAdapterAsync(transport);
            var held = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl);
            var failed = await adapter.RenewAllAsync(held.Authority!);
            Assert.Null(failed.Authority);
            Assert.NotNull(failed.ReleaseContinuation);
        }

        var throwing = SingleLeaseTransport();
        throwing.RenewExceptions.Enqueue(new TimeoutException("renew timeout"));
        var throwingAdapter = await CreateAdapterAsync(throwing);
        var acquisition = await throwingAdapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl);
        var uncertain = await throwingAdapter.RenewAllAsync(acquisition.Authority!);
        Assert.Null(uncertain.Authority);
        Assert.NotNull(uncertain.ReleaseContinuation);
    }

    private static readonly DateTimeOffset ServerNow = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly LeaseOwner Owner = new("principal-1", "run-1");

    private static CoordinationResource Issue(long number) => CoordinationResource.GitHubIssue("delivery-forge", "Nucleoid", ".github", number);

    private static RecordingTransport SingleLeaseTransport()
    {
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities());
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-a", "1", ServerNow, ServerNow.AddMinutes(5)));
        return transport;
    }

    private static RecordingTransport TwoLeaseTransport()
    {
        var transport = SingleLeaseTransport();
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-b", "2", ServerNow, ServerNow.AddMinutes(5)));
        return transport;
    }

    private static Task<ContinuumCoordinationAdapter> CreateAdapterAsync(
        RecordingTransport transport,
        FakeMonotonicClock? clock = null,
        RecordingQuiescenceSource? source = null,
        TimeSpan? rollbackTimeout = null)
    {
        clock ??= new FakeMonotonicClock("test-clock");
        source ??= new RecordingQuiescenceSource(clock, [true, true, true, true]);
        return ContinuumCoordinationAdapter.CreateForContractTestsAsync(
            transport, source, TestContext.Current.CancellationToken, clock, rollbackTimeout);
    }

    private sealed class FakeMonotonicClock(string domainId) : IMonotonicClock
    {
        public string DomainId { get; } = domainId;
        public TimeSpan Elapsed { get; private set; }
        public void Advance(TimeSpan duration) => Elapsed += duration;
    }

    private sealed class RecordingQuiescenceSource : IOwnedRunQuiescenceSource
    {
        private readonly FakeMonotonicClock _clock;
        private readonly Queue<bool> _observations;

        public RecordingQuiescenceSource(FakeMonotonicClock clock, IEnumerable<bool> observations)
        {
            _clock = clock;
            _observations = new Queue<bool>(observations);
            Binding = new QuiescenceSourceBinding("test-issuer", "test-source", clock.DomainId,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), true);
        }

        public QuiescenceSourceBinding Binding { get; }
        public int Observations { get; private set; }
        public Action? BeforeObservation { get; set; }

        public Task<QuiescenceEvidence> ObserveAsync(LeaseOwner owner, CancellationToken cancellationToken = default)
        {
            BeforeObservation?.Invoke();
            Observations++;
            var quiet = _observations.Count > 0 && _observations.Dequeue();
            return Task.FromResult(new QuiescenceEvidence(owner, Binding.IssuerId, Binding.SourceId,
                Binding.ClockDomainId, _clock.Elapsed, $"evidence-{Observations}", quiet));
        }
    }

    private sealed class RecordingTransport(CoordinationCapabilities capabilities) : ICoordinationTransport
    {
        public Queue<AcquireTransportResult> AcquireResults { get; } = new();
        public Queue<Exception> AcquireExceptions { get; } = new();
        public Queue<RenewTransportResult> RenewResults { get; } = new();
        public Queue<Exception> RenewExceptions { get; } = new();
        public Queue<InspectTransportResult> InspectResults { get; } = new();
        public Queue<Exception> InspectExceptions { get; } = new();
        public Queue<ReleaseTransportResult> ReleaseResults { get; } = new();
        public List<AcquireRequest> AcquireRequests { get; } = [];
        public List<RenewRequest> RenewRequests { get; } = [];
        public List<InspectRequest> InspectRequests { get; } = [];
        public List<ReleaseRequest> ReleaseRequests { get; } = [];
        public Func<RenewRequest, CancellationToken, Task<RenewTransportResult>>? RenewHandler { get; set; }
        public Func<ReleaseRequest, CancellationToken, Task<ReleaseTransportResult>>? ReleaseHandler { get; set; }

        public Task<CoordinationCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(capabilities);

        public Task<AcquireTransportResult> AcquireAsync(AcquireRequest request, CancellationToken cancellationToken = default)
        {
            AcquireRequests.Add(request);
            if (AcquireExceptions.Count > 0 && AcquireRequests.Count > AcquireResults.Count) throw AcquireExceptions.Dequeue();
            return Task.FromResult(AcquireResults.Dequeue());
        }

        public Task<RenewTransportResult> RenewAsync(RenewRequest request, CancellationToken cancellationToken = default)
        {
            RenewRequests.Add(request);
            if (RenewExceptions.Count > 0) throw RenewExceptions.Dequeue();
            return RenewHandler?.Invoke(request, cancellationToken) ?? Task.FromResult(RenewResults.Dequeue());
        }

        public Task<InspectTransportResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default)
        {
            InspectRequests.Add(request);
            if (InspectExceptions.Count > 0) throw InspectExceptions.Dequeue();
            return Task.FromResult(InspectResults.Dequeue());
        }

        public Task<ReleaseTransportResult> ReleaseAsync(ReleaseRequest request, CancellationToken cancellationToken = default)
        {
            ReleaseRequests.Add(request);
            if (ReleaseHandler is not null) return ReleaseHandler(request, cancellationToken);
            return Task.FromResult(ReleaseResults.Count > 0 ? ReleaseResults.Dequeue() : ReleaseTransportResult.Released());
        }
    }
}
