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
        Assert.Equal(0, transport.CapabilityDetections);
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
        var acquisition = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var foreignAdapter = await CreateAdapterAsync(SingleLeaseTransport(), new FakeMonotonicClock("process-clock-b"),
            new RecordingQuiescenceSource(new FakeMonotonicClock("process-clock-b"), [true]));

        Assert.NotNull(acquisition.Authority);
        Assert.True(adapter.MayDispatch(acquisition.Authority!));
        Assert.False(foreignAdapter.MayDispatch(acquisition.Authority!));
        Assert.False(adapter.MayDispatch(CoordinationContractFixture.CopyAuthority(acquisition.Authority!)));
        Assert.False(adapter.MayDispatch(CoordinationContractFixture.ForgeAuthority(acquisition.Authority!, nonce: "forged")));
        Assert.Empty(typeof(DispatchAuthority).GetConstructors());

        source.BeforeObservation = () => Assert.False(adapter.MayDispatch(acquisition.Authority!));
        var released = await adapter.ReleaseAllAsync(acquisition.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(LeaseOwnership.Released, released.Snapshot.Ownership);
        Assert.False(adapter.MayDispatch(acquisition.Authority!));
    }

    [Fact]
    public async Task Deadline_and_renewal_revoke_the_original_authority()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = SingleLeaseTransport();
        transport.RenewResults.Enqueue(RenewTransportResult.Renewed(ServerNow.AddMinutes(1), ServerNow.AddMinutes(6)));
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true]));
        var acquisition = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(1));
        var renewal = await adapter.RenewAllAsync(acquisition.Authority!, TestContext.Current.CancellationToken);

        Assert.False(adapter.MayDispatch(acquisition.Authority!));
        Assert.NotNull(renewal.Authority);
        Assert.True(adapter.MayDispatch(renewal.Authority!));
        clock.Advance(TimeSpan.FromSeconds(300));
        Assert.False(adapter.MayDispatch(renewal.Authority!));
        var cleanup = await adapter.ReleaseAllAsync(renewal.Authority!, TestContext.Current.CancellationToken);
        Assert.Equal(LeaseOwnership.Released, cleanup.Snapshot.Ownership);
    }

    [Fact]
    public async Task Uncertain_acquire_is_saved_before_invoke_and_reconciled_with_the_same_request_id()
    {
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities());
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-a", "1", ServerNow, ServerNow.AddMinutes(5)));
        transport.AcquireExceptions.Enqueue(new TimeoutException("response lost"));
        var adapter = await CreateAdapterAsync(transport);

        var uncertain = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        Assert.Equal(LeaseOwnership.Uncertain, uncertain.Snapshot.Ownership);
        Assert.NotNull(uncertain.AcquireContinuation);
        Assert.Equal(Issue(7), uncertain.Uncertainty!.Resource);
        Assert.Equal(transport.AcquireRequests[1].RequestId, uncertain.Uncertainty.RequestId);
        Assert.Equal(Owner, uncertain.Uncertainty.Owner);
        Assert.Single(uncertain.ReleaseProgress);
        Assert.Equal(LeaseReleaseState.Released, uncertain.ReleaseProgress[0].State);

        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-b", "2", ServerNow, ServerNow.AddMinutes(5)));
        var reconciled = await adapter.ReconcileAcquireAsync(
            uncertain.AcquireContinuation!, TestContext.Current.CancellationToken);

        Assert.Equal(transport.AcquireRequests[1].RequestId, transport.AcquireRequests[2].RequestId);
        Assert.Null(reconciled.Authority);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, reconciled.Snapshot.Phase);
        Assert.NotNull(reconciled.ReleaseContinuation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelled_or_malformed_acquire_retains_same_id_reconciliation(bool cancelled)
    {
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities());
        if (cancelled) transport.AcquireExceptions.Enqueue(new OperationCanceledException("response may have been lost"));
        else transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("", "007", ServerNow, ServerNow.AddMinutes(5)));
        var adapter = await CreateAdapterAsync(transport);

        var uncertain = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var originalRequestId = transport.AcquireRequests.Single().RequestId;
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-a", "1", ServerNow, ServerNow.AddMinutes(5)));

        var reconciled = await adapter.ReconcileAcquireAsync(
            uncertain.AcquireContinuation!, TestContext.Current.CancellationToken);

        Assert.Equal(LeaseOwnership.Uncertain, uncertain.Snapshot.Ownership);
        Assert.Equal(originalRequestId, uncertain.Uncertainty!.RequestId);
        Assert.Equal(originalRequestId, transport.AcquireRequests.Last().RequestId);
        Assert.Equal(LeaseOwnership.Uncertain, reconciled.Snapshot.Ownership);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, reconciled.Snapshot.Phase);
        Assert.Null(reconciled.Authority);
        Assert.NotNull(reconciled.ReleaseContinuation);
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

        var result = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(LeaseOwnership.Uncertain, result.Snapshot.Ownership);
        Assert.NotNull(result.ReleaseContinuation);
        Assert.Equal(LeaseReleaseState.Unknown, result.ReleaseProgress.Single().State);
        hungRelease.TrySetResult(ReleaseTransportResult.Released());
        await hungRelease.Task;
    }

    [Fact]
    public async Task Pending_acquire_and_unknown_prefix_are_neither_held_nor_closed()
    {
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities());
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-a", "1", ServerNow, ServerNow.AddMinutes(5)));
        transport.AcquireExceptions.Enqueue(new TimeoutException("second-acquire-lost"));
        var hungRelease = new TaskCompletionSource<ReleaseTransportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.ReleaseHandler = (_, _) => hungRelease.Task;
        var adapter = await CreateAdapterAsync(transport, rollbackTimeout: TimeSpan.FromMilliseconds(20));

        var result = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);

        Assert.Equal(LeaseOwnership.Uncertain, result.Snapshot.Ownership);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, result.Snapshot.Phase);
        Assert.NotNull(result.AcquireContinuation);
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
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true, true, true, true]));
        var acquisition = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var partial = await adapter.ReleaseAllAsync(acquisition.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(LeaseOwnership.Uncertain, partial.Snapshot.Ownership);
        Assert.Equal(2, partial.Leases.Count);
        Assert.Contains(partial.Leases, item => item.State == LeaseReleaseState.Released);
        var unknown = Assert.Single(partial.Leases, item => item.State == LeaseReleaseState.Unknown);
        Assert.NotNull(partial.Continuation);
        var firstRequestIds = transport.ReleaseRequests.ToDictionary(request => request.LeaseId, request => request.RequestId);

        transport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        var stillUnknown = await adapter.ContinueReleaseAsync(partial.Continuation!, TestContext.Current.CancellationToken);
        Assert.Equal(LeaseOwnership.Uncertain, stillUnknown.Snapshot.Ownership);
        Assert.Equal(firstRequestIds[unknown.LeaseId], transport.ReleaseRequests.Last().RequestId);
        Assert.Equal(3, transport.ReleaseRequests.Count);

        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        var complete = await adapter.ContinueReleaseAsync(stillUnknown.Continuation!, TestContext.Current.CancellationToken);
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

        var foreign = await adapter.InspectAsync(Issue(6), Owner, TestContext.Current.CancellationToken);
        var own = await adapter.InspectAsync(Issue(6), Owner, TestContext.Current.CancellationToken);

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

        await Assert.ThrowsAsync<CoordinationUncertainException>(() =>
            adapter.InspectAsync(Issue(6), Owner, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Release_reobserves_bound_quiescence_before_each_lease_and_stops_on_resumed_activity()
    {
        var clock = new FakeMonotonicClock("clock-domain");
        var source = new RecordingQuiescenceSource(clock, [true, false]);
        var transport = TwoLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        var adapter = await CreateAdapterAsync(transport, clock, source);
        var acquisition = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var result = await adapter.ReleaseAllAsync(acquisition.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(2, source.Observations);
        Assert.Single(transport.ReleaseRequests);
        Assert.Equal(LeaseOwnership.Uncertain, result.Snapshot.Ownership);
        Assert.NotNull(result.Continuation);
    }

    [Fact]
    public async Task Unknown_quiescence_bounds_fail_adapter_construction()
    {
        var clock = new FakeMonotonicClock("clock-domain");
        var source = new RecordingQuiescenceSource(clock, [true], boundsKnown: false);

        await Assert.ThrowsAsync<CoordinationUnavailableException>(() =>
            ContinuumCoordinationAdapter.CreateForContractTestsAsync(
                SingleLeaseTransport(), source, TestContext.Current.CancellationToken, clock));
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
        var acquisition = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var partial = await adapter.RenewAllAsync(acquisition.Authority!, TestContext.Current.CancellationToken);

        Assert.Null(partial.Authority);
        Assert.NotNull(partial.ReleaseContinuation);
        Assert.False(adapter.MayDispatch(acquisition.Authority!));
        Assert.Equal(TimeSpan.FromSeconds(300), partial.Snapshot.Leases[0].SafeDeadline);
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
            var held = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
                TestContext.Current.CancellationToken);
            var failed = await adapter.RenewAllAsync(held.Authority!, TestContext.Current.CancellationToken);
            Assert.Null(failed.Authority);
            Assert.NotNull(failed.ReleaseContinuation);
        }

        var throwing = SingleLeaseTransport();
        throwing.RenewExceptions.Enqueue(new TimeoutException("renew timeout"));
        var throwingAdapter = await CreateAdapterAsync(throwing);
        var acquisition = await throwingAdapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var uncertain = await throwingAdapter.RenewAllAsync(acquisition.Authority!, TestContext.Current.CancellationToken);
        Assert.Null(uncertain.Authority);
        Assert.NotNull(uncertain.ReleaseContinuation);
    }

    [Fact]
    public async Task Two_logical_renewals_use_fresh_ids_and_require_real_expiry_progress()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = SingleLeaseTransport();
        transport.RenewResults.Enqueue(RenewTransportResult.Renewed(
            ServerNow.AddMinutes(1), ServerNow.AddMinutes(6)));
        transport.RenewResults.Enqueue(RenewTransportResult.Renewed(
            ServerNow.AddMinutes(2), ServerNow.AddMinutes(7)));
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true]));
        var acquired = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(1));
        var first = await adapter.RenewAllAsync(acquired.Authority!, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await adapter.RenewAllAsync(first.Authority!, TestContext.Current.CancellationToken);

        Assert.NotEqual(transport.RenewRequests[0].RequestId, transport.RenewRequests[1].RequestId);
        Assert.True(second.Snapshot.Leases.Single().ExpiresAt > first.Snapshot.Leases.Single().ExpiresAt);
        Assert.True(second.Snapshot.Leases.Single().SafeDeadline > first.Snapshot.Leases.Single().SafeDeadline);
        Assert.True(adapter.MayDispatch(second.Authority!));
    }

    [Fact]
    public async Task Nonprogressing_renewal_cannot_mint_authority()
    {
        var transport = SingleLeaseTransport();
        transport.RenewResults.Enqueue(RenewTransportResult.Renewed(ServerNow, ServerNow.AddMinutes(5)));
        var adapter = await CreateAdapterAsync(transport);
        var acquired = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var renewal = await adapter.RenewAllAsync(acquired.Authority!, TestContext.Current.CancellationToken);

        Assert.Null(renewal.Authority);
        Assert.NotNull(renewal.ReleaseContinuation);
        Assert.False(adapter.MayDispatch(acquired.Authority!));
    }

    public static TheoryData<DateTimeOffset, DateTimeOffset, int> NonprogressingRenewals => new()
    {
        { ServerNow, ServerNow.AddMinutes(5), 0 },
        { ServerNow.AddMinutes(-1), ServerNow.AddMinutes(4), 1 },
        { ServerNow.AddMinutes(1), ServerNow.AddMinutes(6), 0 },
    };

    [Theory]
    [MemberData(nameof(NonprogressingRenewals))]
    public async Task Renewal_requires_expiry_server_time_and_safe_deadline_to_all_progress(
        DateTimeOffset serverTime, DateTimeOffset expiresAt, int advanceSeconds)
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = SingleLeaseTransport();
        transport.RenewResults.Enqueue(RenewTransportResult.Renewed(serverTime, expiresAt));
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true]));
        var acquired = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(advanceSeconds));

        var renewal = await adapter.RenewAllAsync(acquired.Authority!, TestContext.Current.CancellationToken);

        Assert.Null(renewal.Authority);
        Assert.NotNull(renewal.ReleaseContinuation);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, renewal.Snapshot.Phase);
    }

    [Theory]
    [InlineData(250)]
    [InlineData(400)]
    public async Task Replayed_acquire_after_delay_is_cleanup_only_and_never_mints_authority(int elapsedSeconds)
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities());
        transport.AcquireExceptions.Enqueue(new TimeoutException("committed response lost"));
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true, true]));
        var uncertain = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired(
            "lease-a", "1", ServerNow, ServerNow.AddMinutes(5)));

        var reconciled = await adapter.ReconcileAcquireAsync(
            uncertain.AcquireContinuation!, TestContext.Current.CancellationToken);

        Assert.Equal(transport.AcquireRequests[0].RequestId, transport.AcquireRequests[1].RequestId);
        Assert.Null(reconciled.Authority);
        Assert.NotEqual(LeaseOwnership.Held, reconciled.Snapshot.Ownership);
        Assert.NotNull(reconciled.ReleaseContinuation);
    }

    [Fact]
    public async Task Lost_renewal_reply_retries_the_same_intent_payload_id_and_first_send_for_cleanup_only()
    {
        var clock = new FakeMonotonicClock("clock");
        var baseTime = ServerNow;
        var transport = new IdempotentCoordinationFixture(() => baseTime + clock.Elapsed);
        var checkpoints = new FixtureCheckpointStore();
        var source = new RecordingQuiescenceSource(clock, [true]);
        var adapter = await CreateAdapterAsync(transport, clock, source, checkpoints: checkpoints);
        var acquired = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        transport.LoseNextReplyAfterCommit = CoordinationIntentKind.Renew;

        var failed = await adapter.RenewAllAsync(acquired.Authority!, TestContext.Current.CancellationToken);
        var prepared = Assert.Single(checkpoints.Records,
            record => record.Prepared?.Kind == CoordinationIntentKind.Renew).Prepared!;
        clock.Advance(TimeSpan.FromSeconds(10));
        var reconciled = await adapter.ReconcileRenewalAsync(failed.ReleaseContinuation!, TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.RenewRequests.Count);
        Assert.Equal(prepared.RequestId, transport.RenewRequests[1].RequestId);
        Assert.Equal(transport.RenewRequests[0].Lease.LeaseId, transport.RenewRequests[1].Lease.LeaseId);
        Assert.Equal(transport.RenewRequests[0].Lease.FencingToken, transport.RenewRequests[1].Lease.FencingToken);
        Assert.Equal(transport.RenewRequests[0].Lease.ServerTime, transport.RenewRequests[1].Lease.ServerTime);
        Assert.Equal(transport.RenewRequests[0].Lease.ExpiresAt, transport.RenewRequests[1].Lease.ExpiresAt);
        Assert.Equal(transport.RenewRequests[0].Lease.SafeDeadline, transport.RenewRequests[1].Lease.SafeDeadline);
        Assert.Equal(TimeSpan.FromSeconds(1), prepared.FirstSendMonotonic);
        Assert.Null(reconciled.Authority);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, reconciled.Snapshot.Phase);
    }

    [Fact]
    public async Task Checkpoint_prepare_failure_prevents_rpc()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = new IdempotentCoordinationFixture(() => ServerNow + clock.Elapsed);
        var checkpoints = new FixtureCheckpointStore { FailNextPrepare = true };
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true]),
            checkpoints: checkpoints);

        var result = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        Assert.Empty(transport.AcquireRequests);
        Assert.Equal(CoordinationLifecyclePhase.Closed, result.Snapshot.Phase);
        Assert.Null(result.AcquireContinuation);
        Assert.Null(result.ReleaseContinuation);
        Assert.Null(result.Uncertainty);
    }

    [Fact]
    public async Task Replayed_nonauthoritative_renew_does_not_block_release_or_closure()
    {
        var transport = SingleLeaseTransport();
        transport.RenewResults.Enqueue(RenewTransportResult.Uncertain("first-response-lost"));
        transport.RenewResults.Enqueue(RenewTransportResult.Uncertain("same-id-not-renewed"));
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        var adapter = await CreateAdapterAsync(transport);
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var renewal = await adapter.RenewAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        var cleaned = await adapter.ContinueReleaseAsync(renewal.ReleaseContinuation!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, cleaned.Snapshot.Phase);
        Assert.Null(cleaned.Continuation);
        Assert.Equal(2, transport.RenewRequests.Count);
        Assert.Equal(transport.RenewRequests[0].RequestId, transport.RenewRequests[1].RequestId);
        Assert.Single(transport.ReleaseRequests);
    }

    [Fact]
    public async Task Recovery_reuses_prepared_release_identity_and_can_close()
    {
        var transport = SingleLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Uncertain("response-lost"));
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        var checkpoints = new FixtureCheckpointStore();
        var first = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var held = await first.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var partial = await first.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        var preparedRelease = Assert.Single(checkpoints.Records,
            record => record.Prepared?.Kind == CoordinationIntentKind.Release).Prepared!;
        var lifecycleId = Assert.Single(checkpoints.Records,
            record => record.Prepared?.Kind == CoordinationIntentKind.Acquire).Prepared!.LifecycleId;

        var restarted = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var recovered = await restarted.RecoverForCleanupAsync(lifecycleId, Owner, [Issue(6)],
            TestContext.Current.CancellationToken);
        var cleaned = await restarted.ContinueReleaseAsync(recovered.Continuation!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, cleaned.Snapshot.Phase);
        Assert.Null(cleaned.Continuation);
        Assert.Equal(preparedRelease.RequestId, transport.ReleaseRequests.Last().RequestId);
        Assert.Equal(partial.Leases.Single().RequestId, transport.ReleaseRequests.Last().RequestId);
    }

    [Fact]
    public async Task Release_rejects_quiescence_observed_before_the_call_started()
    {
        var clock = new FakeMonotonicClock("clock");
        clock.Advance(TimeSpan.FromSeconds(10));
        var source = new RecordingQuiescenceSource(clock, [true])
        {
            ObservedAtOffset = TimeSpan.FromSeconds(-1),
        };
        var transport = SingleLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, clock, source);
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var cleanup = await adapter.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Cleaning, cleanup.Snapshot.Phase);
        Assert.Empty(transport.ReleaseRequests);
        Assert.NotNull(cleanup.Continuation);
    }

    [Fact]
    public async Task Expired_authority_cannot_be_renewed_and_remains_releasable()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = SingleLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true]));
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        clock.Advance(ContinuumCoordinationAdapter.DefaultTtl);

        await Assert.ThrowsAsync<CoordinationUncertainException>(() =>
            adapter.RenewAllAsync(held.Authority!, TestContext.Current.CancellationToken));
        var cleanup = await adapter.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);

        Assert.Empty(transport.RenewRequests);
        Assert.Equal(CoordinationLifecyclePhase.Closed, cleanup.Snapshot.Phase);
    }

    [Fact]
    public async Task Renew_prepare_failure_never_calls_transport_and_cleanup_can_close()
    {
        var transport = SingleLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        var checkpoints = new FixtureCheckpointStore();
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        checkpoints.FailNextPrepare = true;

        var renewal = await adapter.RenewAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        var cleanup = await adapter.ContinueReleaseAsync(renewal.ReleaseContinuation!, TestContext.Current.CancellationToken);

        Assert.Empty(transport.RenewRequests);
        Assert.Equal(CoordinationLifecyclePhase.Closed, cleanup.Snapshot.Phase);
        Assert.Null(cleanup.Continuation);
    }

    [Fact]
    public async Task Recovery_handles_multiple_unresolved_releases_with_stable_request_ids()
    {
        var transport = TwoLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Uncertain("first-response-lost"));
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Uncertain("second-response-lost"));
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        var checkpoints = new FixtureCheckpointStore();
        var clock = new FakeMonotonicClock("clock");
        var first = await CreateAdapterAsync(
            transport,
            clock,
            new RecordingQuiescenceSource(clock, [true, true]),
            checkpoints: checkpoints);
        var held = await first.AcquireAllAsync([Issue(6), Issue(7)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var partial = await first.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        var expectedIds = partial.Leases.ToDictionary(item => item.LeaseId, item => item.RequestId);
        var lifecycleId = checkpoints.Records.First(record =>
            record.Prepared?.Kind == CoordinationIntentKind.Acquire).Prepared!.LifecycleId;

        var restarted = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var recovered = await restarted.RecoverForCleanupAsync(lifecycleId, Owner, [Issue(6), Issue(7)],
            TestContext.Current.CancellationToken);
        var cleaned = await restarted.ContinueReleaseAsync(recovered.Continuation!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, cleaned.Snapshot.Phase);
        Assert.Null(cleaned.Continuation);
        foreach (var request in transport.ReleaseRequests.Skip(2))
            Assert.Equal(expectedIds[request.LeaseId], request.RequestId);
    }

    [Fact]
    public async Task Recovery_preserves_original_not_owned_proof_as_terminal()
    {
        var transport = SingleLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        transport.InspectResults.Enqueue(InspectTransportResult.Available());
        var checkpoints = new FixtureCheckpointStore();
        var first = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var held = await first.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var cleanup = await first.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        var lifecycleId = checkpoints.Records.First(record =>
            record.Prepared?.Kind == CoordinationIntentKind.Acquire).Prepared!.LifecycleId;
        Assert.Equal(CoordinationLifecyclePhase.Closed, cleanup.Snapshot.Phase);

        var restarted = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var recovered = await restarted.RecoverForCleanupAsync(lifecycleId, Owner, [Issue(6)],
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, recovered.Snapshot.Phase);
        Assert.Equal(LeaseReleaseState.NotOwned, recovered.Leases.Single().State);
        Assert.Null(recovered.Continuation);
        Assert.Single(transport.ReleaseRequests);
        Assert.Single(transport.InspectRequests);
    }

    [Fact]
    public async Task Progress_failure_roundtrips_prepared_state_and_restart_recovers_cleanup_only()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = new IdempotentCoordinationFixture(() => ServerNow + clock.Elapsed);
        var checkpoints = new FixtureCheckpointStore { FailNextProgress = true };
        var first = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true]),
            checkpoints: checkpoints);

        var uncertain = await first.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var prepared = Assert.Single(checkpoints.Records,
            record => record.Prepared?.Kind == CoordinationIntentKind.Acquire).Prepared!;
        Assert.Null(uncertain.Authority);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, uncertain.Snapshot.Phase);

        var restarted = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true, true]),
            checkpoints: checkpoints);
        var recovered = await restarted.RecoverForCleanupAsync(prepared.LifecycleId, Owner, [Issue(6)],
            TestContext.Current.CancellationToken);
        var cleaned = await restarted.ContinueReleaseAsync(recovered.Continuation!, TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.AcquireRequests.Count);
        Assert.Equal(transport.AcquireRequests[0], transport.AcquireRequests[1]);
        Assert.True(cleaned.Snapshot.Phase == CoordinationLifecyclePhase.Closed,
            $"phase={cleaned.Snapshot.Phase}; reason={cleaned.Snapshot.Reason}; states={string.Join(',', cleaned.Leases.Select(item => item.State))}");
        Assert.Null(cleaned.Continuation);
        Assert.True(transport.ReleaseRequests.Count >= 2);
    }

    public static TheoryData<InspectTransportResult> NotOwnedInspections => new()
    {
        InspectTransportResult.Available(),
        InspectTransportResult.HeldByOther("replacement", "99"),
        InspectTransportResult.HeldByCaller("different-lease", "99", ServerNow, ServerNow.AddMinutes(5)),
    };

    [Theory]
    [MemberData(nameof(NotOwnedInspections))]
    public async Task Lease_lost_requires_fresh_inspect_and_never_releases_observed_replacement(
        InspectTransportResult inspection)
    {
        var transport = SingleLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        transport.InspectResults.Enqueue(inspection);
        var clock = new FakeMonotonicClock("clock");
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true]));
        var acquired = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var cleanup = await adapter.ReleaseAllAsync(acquired.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, cleanup.Snapshot.Phase);
        Assert.Equal(LeaseReleaseState.NotOwned, cleanup.Leases.Single().State);
        Assert.Single(transport.ReleaseRequests);
        Assert.Single(transport.InspectRequests);
        Assert.Equal("lease-a", transport.ReleaseRequests.Single().LeaseId);
    }

    [Fact]
    public async Task Mutable_quiescence_binding_cannot_replace_the_construction_time_binding()
    {
        var clock = new FakeMonotonicClock("clock");
        var source = new RecordingQuiescenceSource(clock, [true]);
        var transport = SingleLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, clock, source);
        var acquired = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        source.Binding = source.Binding with { IssuerId = "replacement-issuer", FreshnessLimit = TimeSpan.FromHours(1) };

        var cleanup = await adapter.ReleaseAllAsync(acquired.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Cleaning, cleanup.Snapshot.Phase);
        Assert.Empty(transport.ReleaseRequests);
        Assert.NotNull(cleanup.Continuation);
    }

    [Fact]
    public async Task Independent_lifecycles_share_one_ledger_while_same_lifecycle_overlap_is_rejected()
    {
        var transport = TwoLeaseTransport();
        var firstBarrier = new TaskCompletionSource<RenewTransportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.RenewHandler = (request, _) => request.Lease.Resource == Issue(6)
            ? firstBarrier.Task
            : Task.FromResult(RenewTransportResult.Renewed(ServerNow.AddMinutes(1), ServerNow.AddMinutes(6)));
        var clock = new FakeMonotonicClock("shared");
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true, true]));
        var firstHeld = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var secondHeld = await adapter.AcquireAllAsync([Issue(7)], new LeaseOwner("principal-2", "run-2"),
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));

        var blockedRenewal = adapter.RenewAllAsync(firstHeld.Authority!, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<CoordinationUncertainException>(() =>
            adapter.ReleaseAllAsync(firstHeld.Authority!, TestContext.Current.CancellationToken));
        var independent = await adapter.RenewAllAsync(secondHeld.Authority!, TestContext.Current.CancellationToken);
        firstBarrier.SetResult(RenewTransportResult.Renewed(ServerNow.AddMinutes(1), ServerNow.AddMinutes(6)));
        var completed = await blockedRenewal;

        Assert.NotNull(independent.Authority);
        Assert.NotNull(completed.Authority);
    }

    [Fact]
    public async Task Null_transport_tasks_and_results_fail_typed_and_preserve_cleanup_handles()
    {
        var acquireNull = new RecordingTransport(CoordinationContractFixture.Capabilities())
        {
            AcquireHandler = (_, _) => null!
        };
        var acquireAdapter = await CreateAdapterAsync(acquireNull);
        var acquire = await acquireAdapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        Assert.NotNull(acquire.AcquireContinuation);
        Assert.NotNull(acquire.ReleaseContinuation);

        var inspectNull = new RecordingTransport(CoordinationContractFixture.Capabilities())
        {
            InspectHandler = (_, _) => Task.FromResult<InspectTransportResult>(null!)
        };
        var inspectAdapter = await CreateAdapterAsync(inspectNull);
        var error = await Assert.ThrowsAsync<CoordinationUncertainException>(() =>
            inspectAdapter.InspectAsync(Issue(6), Owner, TestContext.Current.CancellationToken));
        Assert.Null(error.InnerException);

        var renewNull = SingleLeaseTransport();
        renewNull.RenewHandler = (_, _) => Task.FromResult<RenewTransportResult>(null!);
        var renewAdapter = await CreateAdapterAsync(renewNull);
        var held = await renewAdapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var renewal = await renewAdapter.RenewAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        Assert.NotNull(renewal.ReleaseContinuation);

        var releaseNull = SingleLeaseTransport();
        releaseNull.ReleaseHandler = (_, _) => Task.FromResult<ReleaseTransportResult>(null!);
        var releaseAdapter = await CreateAdapterAsync(releaseNull);
        var releaseHeld = await releaseAdapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var release = await releaseAdapter.ReleaseAllAsync(releaseHeld.Authority!, TestContext.Current.CancellationToken);
        Assert.NotNull(release.Continuation);
        Assert.Equal(LeaseReleaseState.Unknown, release.Leases.Single().State);
    }

    [Fact]
    public async Task Cancellation_from_each_transport_operation_is_masked_and_settled()
    {
        var acquireTransport = new RecordingTransport(CoordinationContractFixture.Capabilities())
        {
            AcquireHandler = (_, _) => throw new OperationCanceledException("private-acquire")
        };
        var acquireAdapter = await CreateAdapterAsync(acquireTransport);
        var acquire = await acquireAdapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        Assert.NotNull(acquire.ReleaseContinuation);

        var inspectTransport = new RecordingTransport(CoordinationContractFixture.Capabilities())
        {
            InspectHandler = (_, _) => throw new OperationCanceledException("private-inspect")
        };
        var inspectAdapter = await CreateAdapterAsync(inspectTransport);
        var inspectError = await Assert.ThrowsAsync<CoordinationUncertainException>(() =>
            inspectAdapter.InspectAsync(Issue(6), Owner, TestContext.Current.CancellationToken));
        Assert.Equal("inspect-transport-uncertain", inspectError.Message);
        Assert.Null(inspectError.InnerException);

        var renewTransport = SingleLeaseTransport();
        renewTransport.RenewHandler = (_, _) => throw new OperationCanceledException("private-renew");
        var renewAdapter = await CreateAdapterAsync(renewTransport);
        var renewHeld = await renewAdapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var renewal = await renewAdapter.RenewAllAsync(renewHeld.Authority!, TestContext.Current.CancellationToken);
        Assert.NotNull(renewal.ReleaseContinuation);

        var releaseTransport = SingleLeaseTransport();
        releaseTransport.ReleaseHandler = (_, _) => throw new OperationCanceledException("private-release");
        var releaseAdapter = await CreateAdapterAsync(releaseTransport);
        var releaseHeld = await releaseAdapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var release = await releaseAdapter.ReleaseAllAsync(releaseHeld.Authority!, TestContext.Current.CancellationToken);
        Assert.NotNull(release.Continuation);
        Assert.Equal("release-uncertain", release.Leases.Single().Reason);
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
        ICoordinationTransport transport,
        FakeMonotonicClock? clock = null,
        RecordingQuiescenceSource? source = null,
        TimeSpan? rollbackTimeout = null,
        ICoordinationCheckpointStore? checkpoints = null)
    {
        clock ??= new FakeMonotonicClock("test-clock");
        source ??= new RecordingQuiescenceSource(clock, [true, true, true, true]);
        return ContinuumCoordinationAdapter.CreateForContractTestsAsync(
            transport, source, TestContext.Current.CancellationToken, clock, rollbackTimeout, checkpoints);
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

        public RecordingQuiescenceSource(FakeMonotonicClock clock, IEnumerable<bool> observations, bool boundsKnown = true)
        {
            _clock = clock;
            _observations = new Queue<bool>(observations);
            Binding = new QuiescenceSourceBinding("test-issuer", "test-source", clock.DomainId,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), boundsKnown);
        }

        public QuiescenceSourceBinding Binding { get; set; }
        public int Observations { get; private set; }
        public Action? BeforeObservation { get; set; }
        public TimeSpan ObservedAtOffset { get; set; }

        public Task<QuiescenceEvidence> ObserveAsync(LeaseOwner owner, CancellationToken cancellationToken = default)
        {
            BeforeObservation?.Invoke();
            Observations++;
            var quiet = _observations.Count > 0 && _observations.Dequeue();
            return Task.FromResult(new QuiescenceEvidence(owner, Binding.IssuerId, Binding.SourceId,
                Binding.ClockDomainId, _clock.Elapsed + ObservedAtOffset, $"evidence-{Observations}", quiet));
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
        public Func<AcquireRequest, CancellationToken, Task<AcquireTransportResult>>? AcquireHandler { get; set; }
        public Func<InspectRequest, CancellationToken, Task<InspectTransportResult>>? InspectHandler { get; set; }
        public int CapabilityDetections { get; private set; }

        public Task<CoordinationCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            CapabilityDetections++;
            return Task.FromResult(capabilities);
        }

        public Task<AcquireTransportResult> AcquireAsync(AcquireRequest request, CancellationToken cancellationToken = default)
        {
            AcquireRequests.Add(request);
            if (AcquireHandler is not null) return AcquireHandler(request, cancellationToken);
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
            if (InspectHandler is not null) return InspectHandler(request, cancellationToken);
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
