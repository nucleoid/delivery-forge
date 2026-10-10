namespace DeliveryForge.Coordination.Tests;

public sealed class CoordinationContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_payload_missing_acquired_lease_fails_before_cleanup(bool omitProperty)
    {
        var original = new FixtureCheckpointStore();
        var transport = SingleLeaseTransport();
        var producer = await CreateAdapterAsync(transport, checkpoints: original);
        _ = await producer.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, original.Records.Count);
        var records = original.Records.ToArray();
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(records[1]))!;
        if (omitProperty) json["Progress"]!.AsObject().Remove("Lease");
        else json["Progress"]!["Lease"] = null;
        records[1] = System.Text.Json.JsonSerializer.Deserialize<CoordinationCheckpointRecord>(json.ToJsonString())!;
        var replay = new FixtureCheckpointStore { ReverseReads = true };
        foreach (var record in records) replay.Inject(record);
        var receiver = await CreateAdapterAsync(transport, checkpoints: replay);

        await Assert.ThrowsAsync<CoordinationProtocolException>(() => receiver.RecoverForCleanupAsync(
            records[0].Prepared!.LifecycleId, Owner, [Issue(6)], "payload-restart",
            TestContext.Current.CancellationToken));
        Assert.Equal(2, replay.Records.Count);
        Assert.Single(transport.AcquireRequests);
        Assert.Empty(transport.ReleaseRequests);
        Assert.Empty(transport.InspectRequests);
    }

    [Fact]
    public async Task Recovery_payload_valid_acquired_lease_retains_cleanup_identity()
    {
        var original = new FixtureCheckpointStore();
        var transport = SingleLeaseTransport();
        var producer = await CreateAdapterAsync(transport, checkpoints: original);
        var held = await producer.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var replay = new FixtureCheckpointStore { ReverseReads = true };
        foreach (var record in original.Records) replay.Inject(record);
        var receiver = await CreateAdapterAsync(transport, checkpoints: replay);

        var recovered = await receiver.RecoverForCleanupAsync(original.Records[0].Prepared!.LifecycleId,
            Owner, [Issue(6)], "valid-payload-restart", TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Cleaning, recovered.Snapshot.Phase);
        Assert.NotNull(recovered.Continuation);
        var retained = Assert.Single(recovered.Snapshot.Leases);
        var expected = Assert.Single(held.Snapshot.Leases);
        Assert.Equal(expected.LeaseId, retained.LeaseId);
        Assert.Equal(expected.Generation, retained.Generation);
        Assert.Equal(expected.FencingToken, retained.FencingToken);
        Assert.Equal(TimeSpan.Zero, retained.SafeDeadline);
        Assert.Empty(transport.ReleaseRequests);
    }

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
        transport.InspectResults.Enqueue(InspectTransportResult.HeldByOther());
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
        var recovered = await restarted.RecoverForCleanupAsync(lifecycleId, Owner, [Issue(6)], "trusted-host-restart",
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
        var recovered = await restarted.RecoverForCleanupAsync(lifecycleId, Owner, [Issue(6), Issue(7)], "trusted-host-restart",
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
        var recovered = await restarted.RecoverForCleanupAsync(lifecycleId, Owner, [Issue(6)], "trusted-host-restart",
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
        var recovered = await restarted.RecoverForCleanupAsync(prepared.LifecycleId, Owner, [Issue(6)], "trusted-host-restart",
            TestContext.Current.CancellationToken);
        var cleaned = await restarted.ContinueReleaseAsync(recovered.Continuation!, TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.AcquireRequests.Count);
        Assert.Equal(transport.AcquireRequests[0], transport.AcquireRequests[1]);
        Assert.True(cleaned.Snapshot.Phase == CoordinationLifecyclePhase.Closed,
            $"phase={cleaned.Snapshot.Phase}; reason={cleaned.Snapshot.Reason}; states={string.Join(',', cleaned.Leases.Select(item => item.State))}");
        Assert.Null(cleaned.Continuation);
        var release = Assert.Single(transport.ReleaseRequests);
        Assert.Equal(uncertain.ReleaseProgress.Single().RequestId, release.RequestId);
        Assert.Equal(uncertain.ReleaseProgress.Single().Generation, cleaned.Leases.Single().Generation);
    }

    public static TheoryData<InspectTransportResult> NotOwnedInspections => new()
    {
        InspectTransportResult.Available(),
        InspectTransportResult.HeldByOther(),
        InspectTransportResult.HeldByCaller("different-lease", "99", ServerNow, ServerNow.AddMinutes(5)),
    };

    public static TheoryData<InspectTransportResult> MalformedLeaseLostInspections => new()
    {
        new InspectTransportResult(TransportOutcome.Inspected, true, true, null, "1", ServerNow, ServerNow.AddMinutes(5), "missing lease"),
        new InspectTransportResult(TransportOutcome.Inspected, true, true, "", "1", ServerNow, ServerNow.AddMinutes(5), "empty lease"),
        new InspectTransportResult(TransportOutcome.Inspected, true, true, "lease-a", null, ServerNow, ServerNow.AddMinutes(5), "missing fence"),
        new InspectTransportResult(TransportOutcome.Inspected, true, true, "lease-a", "01", ServerNow, ServerNow.AddMinutes(5), "bad fence"),
        new InspectTransportResult(TransportOutcome.Inspected, true, true, "lease-a", "1", null, ServerNow.AddMinutes(5), "missing server time"),
        new InspectTransportResult(TransportOutcome.Inspected, true, true, "lease-a", "1", ServerNow, null, "missing expiry"),
        new InspectTransportResult(TransportOutcome.Inspected, false, false, "leaked", null, null, null, "available leaked identity"),
        new InspectTransportResult(TransportOutcome.Inspected, true, false, "leaked", "9", ServerNow, ServerNow.AddMinutes(1), "foreign leaked identity"),
    };

    [Theory]
    [MemberData(nameof(MalformedLeaseLostInspections))]
    public async Task Lease_lost_malformed_inspection_is_not_a_not_owned_proof(InspectTransportResult inspection)
    {
        var transport = SingleLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        transport.InspectResults.Enqueue(inspection);
        var checkpoints = new FixtureCheckpointStore();
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var acquired = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var cleanup = await adapter.ReleaseAllAsync(acquired.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Cleaning, cleanup.Snapshot.Phase);
        Assert.Equal(LeaseReleaseState.Unknown, cleanup.Leases.Single().State);
        Assert.NotNull(cleanup.Continuation);
        Assert.DoesNotContain(checkpoints.Records,
            record => record.Progress?.AdapterCode == "original-not-owned");
    }

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
    public async Task Cleanup_inspection_accepts_a_valid_partial_remaining_lifetime()
    {
        var transport = SingleLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        transport.InspectResults.Enqueue(InspectTransportResult.HeldByCaller(
            "different-lease", "99", ServerNow.AddMinutes(1), ServerNow.AddMinutes(4)));
        var adapter = await CreateAdapterAsync(transport);
        var acquired = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var cleanup = await adapter.ReleaseAllAsync(acquired.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, cleanup.Snapshot.Phase);
        Assert.Equal(LeaseReleaseState.NotOwned, cleanup.Leases.Single().State);
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

    [Fact]
    public async Task Safe_deadline_uses_nonzero_known_uncertainty_and_first_send_anchor()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = SingleLeaseTransport();
        transport.AcquireHandler = (request, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            return Task.FromResult(AcquireTransportResult.Acquired(
                "lease-a", "1", ServerNow, ServerNow.AddMinutes(5)));
        };
        var adapter = await CreateAdapterAsync(transport, clock,
            new RecordingQuiescenceSource(clock, [true]), maximumClockUncertainty: TimeSpan.FromSeconds(5));

        var acquired = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var lease = Assert.Single(acquired.Snapshot.Leases);
        Assert.Equal(TimeSpan.FromSeconds(295), lease.SafeDeadline);
        Assert.Equal(TimeSpan.FromSeconds(100), lease.RenewAt);
        clock.Advance(TimeSpan.FromSeconds(292));
        Assert.False(adapter.MayDispatch(acquired.Authority!));
    }

    [Fact]
    public async Task Slow_journal_prepare_invalidates_quiescence_without_release_and_retry_keeps_id()
    {
        var clock = new FakeMonotonicClock("clock");
        var checkpoints = new FixtureCheckpointStore();
        checkpoints.AfterAppend = record =>
        {
            if (record.Prepared?.Kind == CoordinationIntentKind.Release)
                clock.Advance(TimeSpan.FromSeconds(6));
        };
        var transport = SingleLeaseTransport();
        var source = new RecordingQuiescenceSource(clock, [true, true]);
        var adapter = await CreateAdapterAsync(transport, clock, source, checkpoints: checkpoints);
        var acquired = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var stale = await adapter.ReleaseAllAsync(acquired.Authority!, TestContext.Current.CancellationToken);
        var stableId = stale.Leases.Single().RequestId;
        Assert.Empty(transport.ReleaseRequests);
        checkpoints.AfterAppend = null;
        var cleaned = await adapter.ContinueReleaseAsync(stale.Continuation!, TestContext.Current.CancellationToken);

        var request = Assert.Single(transport.ReleaseRequests);
        Assert.Equal(stableId, request.RequestId);
        Assert.Equal(CoordinationLifecyclePhase.Closed, cleaned.Snapshot.Phase);
    }

    [Fact]
    public async Task Shuffled_json_records_and_identical_duplicates_replay_terminal_proof()
    {
        var transport = SingleLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        transport.InspectResults.Enqueue(InspectTransportResult.Available());
        var checkpoints = new FixtureCheckpointStore();
        var first = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var held = await first.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        _ = await first.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        var lifecycleId = checkpoints.Records[0].Prepared!.LifecycleId;
        checkpoints.Inject(checkpoints.Records[1]);
        checkpoints.ReverseReads = true;

        var restarted = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var recovered = await restarted.RecoverForCleanupAsync(
            lifecycleId, Owner, [Issue(6)], "trusted-shuffled-restart", TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, recovered.Snapshot.Phase);
        Assert.Equal(LeaseReleaseState.NotOwned, recovered.Leases.Single().State);
    }

    [Fact]
    public async Task Writer_epoch_claim_and_expected_sequence_cas_reject_competing_and_stale_writers()
    {
        var store = new FixtureCheckpointStore();
        var lifecycleId = Guid.NewGuid().ToString("D");
        var firstEpoch = Guid.NewGuid().ToString("D");
        var first = await store.ClaimWriterAsync(lifecycleId, firstEpoch, null, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.ClaimWriterAsync(
            lifecycleId, Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken));
        var intent = new PreparedCoordinationIntent(lifecycleId, 1, CoordinationIntentKind.Acquire, Issue(6), Owner,
            Guid.NewGuid().ToString("D"), TimeSpan.Zero, "clock", ContinuumCoordinationAdapter.DefaultTtl, null,
            CoordinationLifecyclePurpose.Ownership);
        var record = new CoordinationCheckpointRecord(CoordinationCheckpointRecord.CurrentSchemaVersion, 1, 1,
            CoordinationIntentKind.Acquire, firstEpoch, 0, intent, null);
        await store.AppendAsync(first, 1, record, TestContext.Current.CancellationToken);
        var takeover = await store.ClaimWriterAsync(lifecycleId, Guid.NewGuid().ToString("D"), "host-proof-1",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<CoordinationUncertainException>(() =>
            store.AppendAsync(first, 2, record with { RecordSequence = 2 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoordinationUncertainException>(() =>
            store.ClaimWriterAsync(lifecycleId, Guid.NewGuid().ToString("D"), "host-proof-1",
                TestContext.Current.CancellationToken));
        Assert.Equal(2, takeover.NextRecordSequence);
    }

    [Fact]
    public async Task Noncooperative_capability_transport_and_quiescence_ports_are_bounded_and_late_results_are_inert()
    {
        var capabilityBarrier = new TaskCompletionSource<CoordinationCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capabilityTransport = new RecordingTransport(CoordinationContractFixture.Capabilities())
        {
            CapabilityHandler = _ => capabilityBarrier.Task,
        };
        await Assert.ThrowsAsync<CoordinationUnavailableException>(() => CreateAdapterAsync(
            capabilityTransport, rollbackTimeout: TimeSpan.FromMilliseconds(20))).WaitAsync(
                TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        capabilityBarrier.SetResult(CoordinationContractFixture.Capabilities());

        var acquireBarrier = new TaskCompletionSource<AcquireTransportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquireTransport = new RecordingTransport(CoordinationContractFixture.Capabilities())
        {
            AcquireHandler = (_, _) => acquireBarrier.Task,
        };
        var acquireAdapter = await CreateAdapterAsync(acquireTransport, rollbackTimeout: TimeSpan.FromMilliseconds(20));
        var uncertain = await acquireAdapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        acquireBarrier.SetResult(AcquireTransportResult.Acquired("late", "1", ServerNow, ServerNow.AddMinutes(5)));
        Assert.Null(uncertain.Authority);

        var clock = new FakeMonotonicClock("clock");
        var quiescenceBarrier = new TaskCompletionSource<QuiescenceEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new RecordingQuiescenceSource(clock, [true]) { Handler = (_, _) => quiescenceBarrier.Task };
        var releaseTransport = SingleLeaseTransport();
        var releaseAdapter = await CreateAdapterAsync(releaseTransport, clock, source,
            rollbackTimeout: TimeSpan.FromMilliseconds(20));
        var held = await releaseAdapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var cleanup = await releaseAdapter.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        quiescenceBarrier.SetResult(new QuiescenceEvidence(Owner, "test-issuer", "test-source", "clock",
            clock.Elapsed, "late-evidence", true));
        Assert.Empty(releaseTransport.ReleaseRequests);
        Assert.NotNull(cleanup.Continuation);
    }

    [Fact]
    public async Task Noncooperative_checkpoint_claim_append_and_read_are_bounded_and_never_authorize_rpc()
    {
        foreach (var mode in new[] { BlockingCheckpointMode.Claim, BlockingCheckpointMode.Append })
        {
            var store = new BlockingCheckpointStore(mode);
            var transport = SingleLeaseTransport();
            var adapter = await CreateAdapterAsync(transport, rollbackTimeout: TimeSpan.FromMilliseconds(20), checkpoints: store);
            var result = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
                TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.Null(result.Authority);
            Assert.Empty(transport.AcquireRequests);
            store.Settle();
        }

        var readStore = new BlockingCheckpointStore(BlockingCheckpointMode.Read);
        var restarted = await CreateAdapterAsync(SingleLeaseTransport(), rollbackTimeout: TimeSpan.FromMilliseconds(20),
            checkpoints: readStore);
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => restarted.RecoverForCleanupAsync(
            Guid.NewGuid().ToString("D"), Owner, [Issue(6)], "trusted-read-restart",
            TestContext.Current.CancellationToken)).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        readStore.Settle();
    }

    [Fact]
    public async Task A_takeover_proof_is_single_use_so_two_recoveries_cannot_share_the_writer_epoch()
    {
        var transport = SingleLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Uncertain("lost"));
        var checkpoints = new FixtureCheckpointStore();
        var original = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var held = await original.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        _ = await original.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        var lifecycleId = checkpoints.Records[0].Prepared!.LifecycleId;
        var firstRecovery = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        _ = await firstRecovery.RecoverForCleanupAsync(lifecycleId, Owner, [Issue(6)], "single-use-proof",
            TestContext.Current.CancellationToken);
        var secondRecovery = await CreateAdapterAsync(transport, checkpoints: checkpoints);

        await Assert.ThrowsAsync<CoordinationUncertainException>(() => secondRecovery.RecoverForCleanupAsync(
            lifecycleId, Owner, [Issue(6)], "single-use-proof", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Late_never_sent_prepare_is_reconciled_and_abandoned_before_any_rpc_or_new_slot()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoints = new FixtureCheckpointStore { DelayNextAppend = barrier };
        var transport = SingleLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, rollbackTimeout: TimeSpan.FromMilliseconds(20),
            checkpoints: checkpoints);

        var uncertain = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Empty(transport.AcquireRequests);
        barrier.SetResult();
        await checkpoints.DelayedAppendCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        var reconciled = await adapter.ReconcileAcquireAsync(
            uncertain.AcquireContinuation!, TestContext.Current.CancellationToken);

        Assert.Empty(transport.AcquireRequests);
        Assert.NotNull(reconciled.ReleaseContinuation);
        Assert.Single(checkpoints.Records,
            record => record.Progress?.AdapterCode == "abandoned-before-send");
        Assert.Equal(Enumerable.Range(1, checkpoints.Records.Count).Select(value => (long)value),
            checkpoints.Records.Select(record => record.RecordSequence));
    }

    [Theory]
    [InlineData(FixtureReadBehavior.Null)]
    [InlineData(FixtureReadBehavior.Throw)]
    [InlineData(FixtureReadBehavior.Lag)]
    public async Task Acknowledged_append_consumes_its_slot_when_optional_readback_is_degraded(
        FixtureReadBehavior behavior)
    {
        var checkpoints = new FixtureCheckpointStore { NextReadBehavior = behavior };
        var transport = SingleLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);

        var acquired = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        Assert.NotNull(acquired.Authority);
        Assert.Single(transport.AcquireRequests);
        Assert.Equal([1L, 2L], checkpoints.Records.Select(record => record.RecordSequence));
        Assert.Equal([0L, 1L], checkpoints.Records.Select(record => record.ProgressRevision));
    }

    [Fact]
    public async Task Contradictory_readback_after_ack_is_protocol_failure_and_never_authorizes_rpc()
    {
        var checkpoints = new FixtureCheckpointStore { NextReadBehavior = FixtureReadBehavior.Conflict };
        var transport = SingleLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);

        var result = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Authority);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, result.Snapshot.Phase);
        Assert.Empty(transport.AcquireRequests);
        Assert.Single(checkpoints.Records);
        Assert.Equal(1, checkpoints.Records[0].RecordSequence);

        var reconciled = await adapter.ReconcileAcquireAsync(
            result.AcquireContinuation!, TestContext.Current.CancellationToken);
        _ = await adapter.ContinueReleaseAsync(
            reconciled.ReleaseContinuation!, TestContext.Current.CancellationToken);

        Assert.Empty(transport.AcquireRequests);
        Assert.Empty(transport.ReleaseRequests);
        Assert.Single(checkpoints.Records);
    }

    [Fact]
    public async Task Released_ack_then_conflicting_readback_never_closes_or_drops_continuation()
    {
        var checkpoints = new FixtureCheckpointStore();
        checkpoints.AfterAppend = record =>
        {
            if (record.Progress?.AdapterCode == "released")
                checkpoints.NextReadBehavior = FixtureReadBehavior.Conflict;
        };
        var transport = SingleLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var first = await adapter.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        var second = await adapter.ContinueReleaseAsync(first.Continuation!, TestContext.Current.CancellationToken);
        var third = await adapter.ContinueReleaseAsync(second.Continuation!, TestContext.Current.CancellationToken);

        Assert.All([first, second, third], result =>
        {
            Assert.Equal(CoordinationLifecyclePhase.Cleaning, result.Snapshot.Phase);
            Assert.Equal(LeaseOwnership.Uncertain, result.Snapshot.Ownership);
            Assert.NotNull(result.Continuation);
        });
        Assert.Single(transport.ReleaseRequests);
        Assert.Empty(transport.InspectRequests);
    }

    [Fact]
    public async Task Contended_ack_then_conflicting_readback_never_closes_or_dispatches_again()
    {
        var checkpoints = new FixtureCheckpointStore();
        checkpoints.AfterAppend = record =>
        {
            if (record.Progress?.AdapterCode == "contended")
                checkpoints.NextReadBehavior = FixtureReadBehavior.Conflict;
        };
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities());
        transport.AcquireResults.Enqueue(AcquireTransportResult.Contended());
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);

        var first = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var second = await adapter.ContinueReleaseAsync(first.ReleaseContinuation!, TestContext.Current.CancellationToken);
        var third = await adapter.ContinueReleaseAsync(second.Continuation!, TestContext.Current.CancellationToken);

        Assert.All([first.Snapshot, second.Snapshot, third.Snapshot], snapshot =>
        {
            Assert.Equal(CoordinationLifecyclePhase.Cleaning, snapshot.Phase);
            Assert.Equal(LeaseOwnership.Uncertain, snapshot.Ownership);
        });
        Assert.NotNull(first.ReleaseContinuation);
        Assert.NotNull(second.Continuation);
        Assert.NotNull(third.Continuation);
        Assert.Single(transport.AcquireRequests);
        Assert.Empty(transport.ReleaseRequests);
    }

    [Fact]
    public async Task Abandoned_ack_then_conflicting_readback_never_closes_or_invents_a_send()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoints = new FixtureCheckpointStore { DelayNextAppend = barrier };
        var transport = SingleLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, rollbackTimeout: TimeSpan.FromMilliseconds(20),
            checkpoints: checkpoints);

        try
        {
            var uncertain = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
                TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            barrier.TrySetResult();
            await checkpoints.DelayedAppendCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            checkpoints.AfterAppend = record =>
            {
                if (record.Progress?.AdapterCode == "abandoned-before-send")
                    checkpoints.NextReadBehavior = FixtureReadBehavior.Conflict;
            };

            await Assert.ThrowsAsync<CoordinationProtocolException>(() => adapter.ReconcileAcquireAsync(
                uncertain.AcquireContinuation!, TestContext.Current.CancellationToken));
            var first = await adapter.ContinueReleaseAsync(
                uncertain.ReleaseContinuation!, TestContext.Current.CancellationToken);
            var second = await adapter.ContinueReleaseAsync(first.Continuation!, TestContext.Current.CancellationToken);

            Assert.All([first, second], result =>
            {
                Assert.Equal(CoordinationLifecyclePhase.Cleaning, result.Snapshot.Phase);
                Assert.Equal(LeaseOwnership.Uncertain, result.Snapshot.Ownership);
                Assert.NotNull(result.Continuation);
            });
            Assert.Empty(transport.AcquireRequests);
            Assert.Empty(transport.ReleaseRequests);
        }
        finally
        {
            barrier.TrySetResult();
        }
    }

    [Theory]
    [InlineData(FixtureReadBehavior.Null)]
    [InlineData(FixtureReadBehavior.Lag)]
    public async Task Released_ack_with_optional_readback_degradation_advances_sequence_and_closes(
        FixtureReadBehavior behavior)
    {
        var checkpoints = new FixtureCheckpointStore();
        checkpoints.AfterAppend = record =>
        {
            if (record.Progress?.AdapterCode == "released") checkpoints.NextReadBehavior = behavior;
        };
        var transport = SingleLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        var released = await adapter.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, released.Snapshot.Phase);
        Assert.Equal(LeaseOwnership.Released, released.Snapshot.Ownership);
        Assert.Null(released.Continuation);
        Assert.Equal(Enumerable.Range(1, checkpoints.Records.Count).Select(value => (long)value),
            checkpoints.Records.Select(record => record.RecordSequence));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Successful_renewals_roundtrip_all_non_clock_evidence_for_replay(int renewalCount)
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = SingleLeaseTransport();
        var checkpoints = new FixtureCheckpointStore();
        for (var index = 1; index <= renewalCount; index++)
            transport.RenewResults.Enqueue(RenewTransportResult.Renewed(
                ServerNow.AddMinutes(index), ServerNow.AddMinutes(index + 5)));
        var adapter = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true]),
            checkpoints: checkpoints);
        var current = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);

        for (var index = 0; index < renewalCount; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var renewed = await adapter.RenewAllAsync(current.Authority!, TestContext.Current.CancellationToken);
            current = new AcquisitionResult(renewed.Snapshot, renewed.Authority, null,
                renewed.ReleaseContinuation, null, []);
        }

        var live = current.Snapshot.Leases.Single();
        var lifecycleId = checkpoints.Records[0].Prepared!.LifecycleId;
        var restarted = await CreateAdapterAsync(transport, clock, new RecordingQuiescenceSource(clock, [true]),
            checkpoints: checkpoints);
        var recovered = await restarted.RecoverForCleanupAsync(
            lifecycleId, Owner, [Issue(6)], $"renew-replay-{renewalCount}", TestContext.Current.CancellationToken);
        var replay = recovered.Snapshot.Leases.Single();

        Assert.Equal(live.Resource, replay.Resource);
        Assert.Equal(live.Owner, replay.Owner);
        Assert.Equal(live.LeaseId, replay.LeaseId);
        Assert.Equal(live.FencingToken, replay.FencingToken);
        Assert.Equal(live.Generation, replay.Generation);
        Assert.Equal(live.ServerTime, replay.ServerTime);
        Assert.Equal(live.ExpiresAt, replay.ExpiresAt);
        Assert.Equal(TimeSpan.Zero, replay.SafeDeadline);
        Assert.Equal(TimeSpan.MinValue, replay.RenewAt);
        Assert.NotNull(recovered.Continuation);
        Assert.Empty(transport.ReleaseRequests);
    }

    [Fact]
    public async Task Cleanup_summary_is_released_only_when_every_identity_has_release_proof()
    {
        var allReleasedTransport = TwoLeaseTransport();
        var allReleased = await CreateAdapterAsync(allReleasedTransport);
        var releasedHeld = await allReleased.AcquireAllAsync([Issue(6), Issue(7)], Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);
        var released = await allReleased.ReleaseAllAsync(
            releasedHeld.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, released.Snapshot.Phase);
        Assert.Equal(LeaseOwnership.Released, released.Snapshot.Ownership);
        Assert.Equal(4, (int)LeaseOwnership.Released);
        Assert.Equal(5, (int)LeaseOwnership.CleanupComplete);
        Assert.All(released.Leases, item => Assert.Equal(LeaseReleaseState.Released, item.State));

        var absentTransport = TwoLeaseTransport();
        absentTransport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        absentTransport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        absentTransport.InspectResults.Enqueue(InspectTransportResult.Available());
        absentTransport.InspectResults.Enqueue(InspectTransportResult.HeldByCaller(
            "replacement", "99", ServerNow.AddMinutes(1), ServerNow.AddMinutes(4)));
        var absent = await CreateAdapterAsync(absentTransport);
        var absentHeld = await absent.AcquireAllAsync([Issue(6), Issue(7)], Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);
        var notOwned = await absent.ReleaseAllAsync(absentHeld.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, notOwned.Snapshot.Phase);
        Assert.Equal(LeaseOwnership.CleanupComplete, notOwned.Snapshot.Ownership);
        Assert.All(notOwned.Leases, item => Assert.Equal(LeaseReleaseState.NotOwned, item.State));
        Assert.Equal(["lease-b", "lease-a"], absentTransport.ReleaseRequests.Select(item => item.LeaseId));
        Assert.DoesNotContain(absentTransport.ReleaseRequests, item => item.LeaseId == "replacement");

        var mixedTransport = TwoLeaseTransport();
        mixedTransport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        mixedTransport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        mixedTransport.InspectResults.Enqueue(InspectTransportResult.Available());
        var mixed = await CreateAdapterAsync(mixedTransport);
        var mixedHeld = await mixed.AcquireAllAsync([Issue(6), Issue(7)], Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);
        var mixedResult = await mixed.ReleaseAllAsync(mixedHeld.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, mixedResult.Snapshot.Phase);
        Assert.Equal(LeaseOwnership.CleanupComplete, mixedResult.Snapshot.Ownership);
        Assert.Contains(mixedResult.Leases, item => item.State == LeaseReleaseState.Released);
        Assert.Contains(mixedResult.Leases, item => item.State == LeaseReleaseState.NotOwned);
    }

    [Fact]
    public async Task Recovery_reduces_later_release_proof_after_older_unresolved_inspect_in_record_order()
    {
        var transport = SingleLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        transport.InspectExceptions.Enqueue(new TimeoutException("inspect response unknown"));
        var checkpoints = new FixtureCheckpointStore { ReverseReads = true };
        var clock = new FakeMonotonicClock("clock");
        var liveAdapter = await CreateAdapterAsync(
            transport,
            clock,
            new RecordingQuiescenceSource(clock, [true, true]),
            checkpoints: checkpoints);
        var held = await liveAdapter.AcquireAllAsync([Issue(6)], Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);

        var uncertain = await liveAdapter.ReleaseAllAsync(
            held.Authority!, TestContext.Current.CancellationToken);
        var liveClosed = await liveAdapter.ContinueReleaseAsync(
            uncertain.Continuation!, TestContext.Current.CancellationToken);
        var lifecycleId = checkpoints.Records[0].Prepared!.LifecycleId;
        var releaseCalls = transport.ReleaseRequests.Count;
        var inspectCalls = transport.InspectRequests.Count;

        var restarted = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var recovered = await restarted.RecoverForCleanupAsync(
            lifecycleId, Owner, [Issue(6)], "record-order-restart", TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, liveClosed.Snapshot.Phase);
        Assert.Equal(CoordinationLifecyclePhase.Closed, recovered.Snapshot.Phase);
        Assert.Equal(LeaseOwnership.Released, recovered.Snapshot.Ownership);
        Assert.Null(recovered.Continuation);
        Assert.Equal(releaseCalls, transport.ReleaseRequests.Count);
        Assert.Equal(inspectCalls, transport.InspectRequests.Count);
    }

    [Fact]
    public async Task Newer_inspect_uncertainty_is_not_suppressed_by_older_release_proof()
    {
        var transport = SingleLeaseTransport();
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.LeaseLost());
        transport.ReleaseResults.Enqueue(ReleaseTransportResult.Released());
        transport.InspectExceptions.Enqueue(new TimeoutException("inspect response unknown"));
        var sourceStore = new FixtureCheckpointStore();
        var clock = new FakeMonotonicClock("clock");
        var adapter = await CreateAdapterAsync(
            transport,
            clock,
            new RecordingQuiescenceSource(clock, [true, true]),
            checkpoints: sourceStore);
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);
        var uncertain = await adapter.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        _ = await adapter.ContinueReleaseAsync(uncertain.Continuation!, TestContext.Current.CancellationToken);

        var records = sourceStore.Records.OrderBy(record => record.RecordSequence).ToArray();
        var inspectPrepare = Assert.Single(records,
            record => record.Prepared?.Kind == CoordinationIntentKind.Inspect);
        var laterReleaseRecords = records.Where(record =>
            record.RecordSequence > inspectPrepare.RecordSequence &&
            record.Progress is not null &&
            record.Kind == CoordinationIntentKind.Release).ToArray();
        Assert.Equal(2, laterReleaseRecords.Length);
        var reorderedStore = new FixtureCheckpointStore { ReverseReads = true };
        foreach (var record in records)
        {
            var sequence = record == inspectPrepare
                ? laterReleaseRecords[^1].RecordSequence
                : laterReleaseRecords.Contains(record)
                    ? record.RecordSequence - 1
                    : record.RecordSequence;
            reorderedStore.Inject(record with { RecordSequence = sequence });
        }
        var releaseCalls = transport.ReleaseRequests.Count;
        var inspectCalls = transport.InspectRequests.Count;
        var restarted = await CreateAdapterAsync(transport, checkpoints: reorderedStore);

        var recovered = await restarted.RecoverForCleanupAsync(
            records[0].Prepared!.LifecycleId, Owner, [Issue(6)], "opposite-record-order",
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Cleaning, recovered.Snapshot.Phase);
        Assert.Equal(LeaseOwnership.Uncertain, recovered.Snapshot.Ownership);
        Assert.NotNull(recovered.Continuation);
        Assert.Equal(releaseCalls, transport.ReleaseRequests.Count);
        Assert.Equal(inspectCalls, transport.InspectRequests.Count);
    }

    [Fact]
    public async Task Acquire_progress_ambiguity_is_reconciled_before_any_same_id_retry()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = new IdempotentCoordinationFixture(() => ServerNow + clock.Elapsed);
        var checkpoints = new FixtureCheckpointStore
        {
            FailAfterAppendWhen = record => record.Progress?.AdapterCode == "acquired",
        };
        checkpoints.AfterAppend = record =>
        {
            if (record.Progress?.AdapterCode == "acquired")
                checkpoints.NextReadBehavior = FixtureReadBehavior.Null;
        };
        var adapter = await CreateAdapterAsync(
            transport,
            clock,
            new RecordingQuiescenceSource(clock, [false, false]),
            checkpoints: checkpoints);

        var uncertain = await adapter.AcquireAllAsync([Issue(6)], Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);
        checkpoints.FailAfterAppendWhen = null;
        var continued = await adapter.ContinueReleaseAsync(
            uncertain.ReleaseContinuation!, TestContext.Current.CancellationToken);

        Assert.Single(transport.AcquireRequests);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, continued.Snapshot.Phase);
        Assert.NotNull(continued.Continuation);
        Assert.Single(checkpoints.Records, record => record.Progress?.AdapterCode == "acquired");
    }

    [Theory]
    [InlineData(FixtureReadBehavior.Null)]
    [InlineData(FixtureReadBehavior.Throw)]
    [InlineData(FixtureReadBehavior.Lag)]
    public async Task Renew_progress_ambiguity_is_reconciled_before_any_same_id_retry(
        FixtureReadBehavior initialRead)
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = new IdempotentCoordinationFixture(() => ServerNow + clock.Elapsed);
        var checkpoints = new FixtureCheckpointStore
        {
            FailAfterAppendWhen = record => record.Progress?.AdapterCode == "renewed",
        };
        checkpoints.AfterAppend = record =>
        {
            if (record.Progress?.AdapterCode == "renewed")
                checkpoints.NextReadBehavior = initialRead;
        };
        var adapter = await CreateAdapterAsync(
            transport,
            clock,
            new RecordingQuiescenceSource(clock, [true]),
            checkpoints: checkpoints);
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));

        var uncertain = await adapter.RenewAllAsync(
            held.Authority!, TestContext.Current.CancellationToken);
        checkpoints.FailAfterAppendWhen = null;
        var cleaned = await adapter.ContinueReleaseAsync(
            uncertain.ReleaseContinuation!, TestContext.Current.CancellationToken);

        Assert.Single(transport.RenewRequests);
        Assert.Equal(CoordinationLifecyclePhase.Closed, cleaned.Snapshot.Phase);
        Assert.Null(cleaned.Continuation);
        Assert.Single(checkpoints.Records, record => record.Progress?.AdapterCode == "renewed");
    }

    [Fact]
    public async Task Contradictory_renew_progress_reconciliation_blocks_retry_and_closure()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = new IdempotentCoordinationFixture(() => ServerNow + clock.Elapsed);
        var checkpoints = new FixtureCheckpointStore
        {
            FailAfterAppendWhen = record => record.Progress?.AdapterCode == "renewed",
        };
        checkpoints.AfterAppend = record =>
        {
            if (record.Progress?.AdapterCode == "renewed")
                checkpoints.NextReadBehavior = FixtureReadBehavior.Null;
        };
        var adapter = await CreateAdapterAsync(transport, clock,
            new RecordingQuiescenceSource(clock, [true]), checkpoints: checkpoints);
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        var uncertain = await adapter.RenewAllAsync(
            held.Authority!, TestContext.Current.CancellationToken);
        checkpoints.FailAfterAppendWhen = null;
        checkpoints.NextReadBehavior = FixtureReadBehavior.Conflict;

        var continued = await adapter.ContinueReleaseAsync(
            uncertain.ReleaseContinuation!, TestContext.Current.CancellationToken);

        Assert.Single(transport.RenewRequests);
        Assert.Empty(transport.ReleaseRequests);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, continued.Snapshot.Phase);
        Assert.NotNull(continued.Continuation);
    }

    [Fact]
    public async Task Fixture_rejects_new_expired_renew_but_replays_committed_same_id_without_revival()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = new IdempotentCoordinationFixture(() => ServerNow + clock.Elapsed);
        var firstOwnerAcquire = await transport.AcquireAsync(
            new AcquireRequest(Issue(6), Owner, ContinuumCoordinationAdapter.DefaultTtl, Guid.NewGuid().ToString("D")),
            TestContext.Current.CancellationToken);
        var generation = Guid.NewGuid().ToString("D");
        var originalLease = new LeaseEvidence(
            Issue(6), Owner, firstOwnerAcquire.LeaseId!, firstOwnerAcquire.FencingToken!,
            firstOwnerAcquire.ServerTime!.Value, firstOwnerAcquire.ExpiresAt!.Value,
            TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(100), generation);
        clock.Advance(TimeSpan.FromMinutes(1));
        var committedRequest = new RenewRequest(originalLease, Guid.NewGuid().ToString("D"));
        var committed = await transport.RenewAsync(committedRequest, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(6));

        var expiredNewId = await transport.RenewAsync(
            committedRequest with { RequestId = Guid.NewGuid().ToString("D") },
            TestContext.Current.CancellationToken);
        var secondOwner = new LeaseOwner("principal-2", "run-2");
        var replacement = await transport.AcquireAsync(
            new AcquireRequest(Issue(6), secondOwner, ContinuumCoordinationAdapter.DefaultTtl, Guid.NewGuid().ToString("D")),
            TestContext.Current.CancellationToken);
        var replay = await transport.RenewAsync(committedRequest, TestContext.Current.CancellationToken);
        var observedByOldOwner = await transport.InspectAsync(
            new InspectRequest(Issue(6), Owner, Guid.NewGuid().ToString("D")),
            TestContext.Current.CancellationToken);

        Assert.Equal(TransportOutcome.Uncertain, expiredNewId.Outcome);
        Assert.Equal(TransportOutcome.Acquired, replacement.Outcome);
        Assert.Equal(committed, replay);
        Assert.Equal(TransportOutcome.Inspected, observedByOldOwner.Outcome);
        Assert.True(observedByOldOwner.Held);
        Assert.False(observedByOldOwner.CallerOwnsLease);
    }

    [Fact]
    public async Task Delayed_same_id_renew_replay_stays_cleanup_only_with_original_prepared_anchor()
    {
        var clock = new FakeMonotonicClock("clock");
        var transport = new IdempotentCoordinationFixture(() => ServerNow + clock.Elapsed);
        var checkpoints = new FixtureCheckpointStore();
        var adapter = await CreateAdapterAsync(transport, clock,
            new RecordingQuiescenceSource(clock, [true]), checkpoints: checkpoints);
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        transport.LoseNextReplyAfterCommit = CoordinationIntentKind.Renew;
        var uncertain = await adapter.RenewAllAsync(
            held.Authority!, TestContext.Current.CancellationToken);
        var prepared = Assert.Single(checkpoints.Records,
            record => record.Prepared?.Kind == CoordinationIntentKind.Renew).Prepared!;
        clock.Advance(TimeSpan.FromMinutes(6));

        var reconciled = await adapter.ReconcileRenewalAsync(
            uncertain.ReleaseContinuation!, TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.RenewRequests.Count);
        Assert.Equal(transport.RenewRequests[0].RequestId, transport.RenewRequests[1].RequestId);
        Assert.Equal(transport.RenewRequests[0].Lease.Resource, transport.RenewRequests[1].Lease.Resource);
        Assert.Equal(transport.RenewRequests[0].Lease.Owner, transport.RenewRequests[1].Lease.Owner);
        Assert.Equal(transport.RenewRequests[0].Lease.LeaseId, transport.RenewRequests[1].Lease.LeaseId);
        Assert.Equal(transport.RenewRequests[0].Lease.FencingToken, transport.RenewRequests[1].Lease.FencingToken);
        Assert.Equal(transport.RenewRequests[0].Lease.Generation, transport.RenewRequests[1].Lease.Generation);
        Assert.Equal(TimeSpan.FromSeconds(1), prepared.FirstSendMonotonic);
        Assert.Null(reconciled.Authority);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, reconciled.Snapshot.Phase);
        Assert.NotEqual(LeaseOwnership.Held, reconciled.Snapshot.Ownership);
        Assert.Equal(TimeSpan.Zero, reconciled.Snapshot.Leases.Single().SafeDeadline);
        Assert.Equal(TimeSpan.MinValue, reconciled.Snapshot.Leases.Single().RenewAt);
    }

    [Fact]
    public async Task Different_release_record_reconciles_pinned_acquire_prepare_without_duplicate_operation()
    {
        var checkpoints = new FixtureCheckpointStore();
        checkpoints.AfterAppend = record =>
        {
            if (record.RecordSequence == 2) checkpoints.FailIndeterminateBeforeNextAppend = true;
        };
        var transport = TwoLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);

        var uncertain = await adapter.AcquireAllAsync([Issue(6), Issue(7)], Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);
        checkpoints.AfterAppend = null;
        var reconciled = await adapter.ContinueReleaseAsync(
            uncertain.ReleaseContinuation!, TestContext.Current.CancellationToken);
        Assert.Single(transport.AcquireRequests);
        var cleaned = await adapter.ContinueReleaseAsync(
            reconciled.Continuation!, TestContext.Current.CancellationToken);

        var prepares = checkpoints.Records.Where(record => record.Prepared is not null).ToArray();
        Assert.Equal(prepares.Length, prepares.Select(record => record.OperationSequence).Distinct().Count());
        Assert.Single(transport.AcquireRequests);
        Assert.Single(checkpoints.Records,
            record => record.Progress?.AdapterCode == "abandoned-before-send");
        Assert.Equal(CoordinationLifecyclePhase.Closed, cleaned.Snapshot.Phase);

        var restarted = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var replay = await restarted.RecoverForCleanupAsync(
            checkpoints.Records[0].Prepared!.LifecycleId, Owner, [Issue(6), Issue(7)], "fresh-replay-proof",
            TestContext.Current.CancellationToken);
        Assert.Equal(CoordinationLifecyclePhase.Closed, replay.Snapshot.Phase);
    }

    [Fact]
    public async Task Reconciled_terminal_acquire_progress_is_not_resent_or_rewritten()
    {
        var checkpoints = new FixtureCheckpointStore();
        checkpoints.AfterAppend = record =>
        {
            if (record.RecordSequence == 1) checkpoints.FailIndeterminateBeforeNextAppend = true;
        };
        var transport = SingleLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);

        var uncertain = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        checkpoints.AfterAppend = null;
        var cleaned = await adapter.ContinueReleaseAsync(
            uncertain.ReleaseContinuation!, TestContext.Current.CancellationToken);

        Assert.Single(transport.AcquireRequests);
        Assert.Single(checkpoints.Records, record => record.Progress?.AdapterCode == "acquired");
        Assert.Equal(CoordinationLifecyclePhase.Closed, cleaned.Snapshot.Phase);
        var acquired = checkpoints.Records.Single(record => record.Progress?.AdapterCode == "acquired");
        Assert.Equal(1, acquired.ProgressRevision);

        var restarted = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var replay = await restarted.RecoverForCleanupAsync(
            checkpoints.Records[0].Prepared!.LifecycleId, Owner, [Issue(6)], "terminal-progress-proof",
            TestContext.Current.CancellationToken);
        Assert.Equal(CoordinationLifecyclePhase.Closed, replay.Snapshot.Phase);
    }

    [Fact]
    public async Task Indeterminate_renew_prepare_remains_reachable_until_reconciled_and_cleaned()
    {
        var checkpoints = new FixtureCheckpointStore();
        var transport = SingleLeaseTransport();
        transport.RenewResults.Enqueue(
            RenewTransportResult.Renewed(ServerNow.AddMinutes(1), ServerNow.AddMinutes(6)));
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        checkpoints.FailIndeterminateBeforeNextAppend = true;

        var uncertain = await adapter.RenewAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        Assert.Empty(transport.RenewRequests);
        var cleaned = await adapter.ContinueReleaseAsync(
            uncertain.ReleaseContinuation!, TestContext.Current.CancellationToken);

        var renewPrepare = checkpoints.Records.Single(record =>
            record.Prepared?.Kind == CoordinationIntentKind.Renew).Prepared!;
        Assert.Single(transport.RenewRequests);
        Assert.Equal(renewPrepare.RequestId, transport.RenewRequests[0].RequestId);
        Assert.Equal(CoordinationLifecyclePhase.Closed, cleaned.Snapshot.Phase);

        var restarted = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var replay = await restarted.RecoverForCleanupAsync(
            checkpoints.Records[0].Prepared!.LifecycleId, Owner, [Issue(6)], "renew-replay-proof",
            TestContext.Current.CancellationToken);
        Assert.Equal(CoordinationLifecyclePhase.Closed, replay.Snapshot.Phase);
    }

    [Fact]
    public async Task Failed_recovery_always_releases_local_reservation_for_fresh_retry()
    {
        var checkpoints = new FixtureCheckpointStore();
        var transport = SingleLeaseTransport();
        var original = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var held = await original.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        _ = await original.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);
        var lifecycleId = checkpoints.Records[0].Prepared!.LifecycleId;
        var restarted = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        checkpoints.ReturnNullNextClaim = true;

        await Assert.ThrowsAsync<CoordinationUncertainException>(() => restarted.RecoverForCleanupAsync(
            lifecycleId, Owner, [Issue(6)], "unused-null-proof", TestContext.Current.CancellationToken));
        var recovered = await restarted.RecoverForCleanupAsync(
            lifecycleId, Owner, [Issue(6)], "fresh-valid-proof", TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, recovered.Snapshot.Phase);
    }

    [Fact]
    public async Task Malformed_replay_releases_local_reservation_even_after_takeover_fences_old_epoch()
    {
        var checkpoints = new FixtureCheckpointStore();
        var transport = SingleLeaseTransport();
        var original = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        _ = await original.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var first = checkpoints.Records[0];
        checkpoints.Inject(first with { WriterEpoch = Guid.NewGuid().ToString("D") });
        var lifecycleId = first.Prepared!.LifecycleId;
        var restarted = await CreateAdapterAsync(transport, checkpoints: checkpoints);

        var firstFailure = await Assert.ThrowsAsync<CoordinationProtocolException>(() =>
            restarted.RecoverForCleanupAsync(
                lifecycleId, Owner, [Issue(6)], "malformed-proof-1", TestContext.Current.CancellationToken));
        var secondFailure = await Assert.ThrowsAsync<CoordinationProtocolException>(() =>
            restarted.RecoverForCleanupAsync(
                lifecycleId, Owner, [Issue(6)], "malformed-proof-2", TestContext.Current.CancellationToken));

        Assert.Equal("checkpoint-record-conflicting-duplicate", firstFailure.Message);
        Assert.Equal(firstFailure.Message, secondFailure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_inspection_journal_recovers_as_terminal_observation_without_cleanup_rpc(bool callerOwns)
    {
        var checkpoints = new FixtureCheckpointStore();
        var transport = new RecordingTransport(CoordinationContractFixture.Capabilities());
        transport.InspectResults.Enqueue(callerOwns
            ? InspectTransportResult.HeldByCaller("observed-lease", "7", ServerNow, ServerNow.AddSeconds(45))
            : InspectTransportResult.Available());
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        _ = await adapter.InspectAsync(Issue(6), Owner, TestContext.Current.CancellationToken);
        var lifecycleId = checkpoints.Records[0].Prepared!.LifecycleId;
        var requestCount = transport.InspectRequests.Count;
        var restarted = await CreateAdapterAsync(transport, checkpoints: checkpoints);

        var recovered = await restarted.RecoverForCleanupAsync(
            lifecycleId, Owner, [Issue(6)], "observation-replay-proof", TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, recovered.Snapshot.Phase);
        Assert.Equal(requestCount, transport.InspectRequests.Count);
        Assert.Empty(transport.ReleaseRequests);
        Assert.Null(recovered.Continuation);
    }

    [Fact]
    public async Task Rejected_same_adapter_recovery_does_not_claim_or_fence_the_live_writer()
    {
        var checkpoints = new FixtureCheckpointStore();
        var transport = SingleLeaseTransport();
        var adapter = await CreateAdapterAsync(transport, checkpoints: checkpoints);
        var held = await adapter.AcquireAllAsync([Issue(6)], Owner, ContinuumCoordinationAdapter.DefaultTtl,
            TestContext.Current.CancellationToken);
        var lifecycleId = checkpoints.Records[0].Prepared!.LifecycleId;

        await Assert.ThrowsAsync<CoordinationUncertainException>(() => adapter.RecoverForCleanupAsync(
            lifecycleId, Owner, [Issue(6)], "must-not-be-consumed", TestContext.Current.CancellationToken));
        var released = await adapter.ReleaseAllAsync(held.Authority!, TestContext.Current.CancellationToken);

        Assert.Equal(CoordinationLifecyclePhase.Closed, released.Snapshot.Phase);
        Assert.Single(transport.ReleaseRequests);
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
        ICoordinationCheckpointStore? checkpoints = null,
        TimeSpan? maximumClockUncertainty = null)
    {
        clock ??= new FakeMonotonicClock("test-clock");
        source ??= new RecordingQuiescenceSource(clock, [true, true, true, true]);
        return ContinuumCoordinationAdapter.CreateForContractTestsAsync(
            transport, source, TestContext.Current.CancellationToken, clock, rollbackTimeout, checkpoints,
            maximumClockUncertainty ?? TimeSpan.Zero);
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
        public Func<LeaseOwner, CancellationToken, Task<QuiescenceEvidence>>? Handler { get; set; }

        public Task<QuiescenceEvidence> ObserveAsync(LeaseOwner owner, CancellationToken cancellationToken = default)
        {
            if (Handler is not null) return Handler(owner, cancellationToken);
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
        public Func<CancellationToken, Task<CoordinationCapabilities>>? CapabilityHandler { get; set; }
        public int CapabilityDetections { get; private set; }

        public Task<CoordinationCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            CapabilityDetections++;
            return CapabilityHandler?.Invoke(cancellationToken) ?? Task.FromResult(capabilities);
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

    private enum BlockingCheckpointMode { Claim, Append, Read }

    private sealed class BlockingCheckpointStore(BlockingCheckpointMode mode) : ICoordinationCheckpointStore
    {
        private readonly TaskCompletionSource<CoordinationWriterClaim> _claim =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _append = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<CoordinationCheckpointRecord>> _read =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CoordinationWriterClaim> ClaimWriterAsync(
            string lifecycleId, string proposedWriterEpoch, string? trustedTakeoverProof,
            CancellationToken cancellationToken = default) =>
            mode == BlockingCheckpointMode.Claim
                ? _claim.Task
                : Task.FromResult(new CoordinationWriterClaim(lifecycleId, proposedWriterEpoch, 1));

        public Task AppendAsync(
            CoordinationWriterClaim claim, long expectedRecordSequence, CoordinationCheckpointRecord record,
            CancellationToken cancellationToken = default) =>
            mode == BlockingCheckpointMode.Append ? _append.Task : Task.CompletedTask;

        public Task<IReadOnlyList<CoordinationCheckpointRecord>> ReadLifecycleAsync(
            string lifecycleId, CancellationToken cancellationToken = default) =>
            mode == BlockingCheckpointMode.Read
                ? _read.Task
                : Task.FromResult<IReadOnlyList<CoordinationCheckpointRecord>>([]);

        public void Settle()
        {
            _claim.TrySetResult(new CoordinationWriterClaim(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 1));
            _append.TrySetResult();
            _read.TrySetResult([]);
        }
    }
}
