namespace DeliveryForge.Coordination.Tests;

public sealed class CoordinationContractTests
{
    [Fact]
    public async Task Production_adapter_rejects_a_test_fixture_capability()
    {
        var transport = new RecordingTransport(CoordinationCapabilities.ValidV1(fixture: true));

        var error = await Assert.ThrowsAsync<CoordinationUnavailableException>(() =>
            ContinuumCoordinationAdapter.CreateProductionAsync(transport, TestContext.Current.CancellationToken));

        Assert.Contains("fixture", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_contention_releases_the_acquired_prefix_and_never_authorizes_dispatch()
    {
        var transport = new RecordingTransport(CoordinationCapabilities.ValidV1());
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-a", "9007199254740993", ServerNow, ServerNow.AddMinutes(5)));
        transport.AcquireResults.Enqueue(AcquireTransportResult.Contended());
        var adapter = await ContinuumCoordinationAdapter.CreateProductionAsync(transport, TestContext.Current.CancellationToken);
        var owner = new LeaseOwner("principal-1", "run-1");
        var resources = new[]
        {
            new CoordinationResource("scope", "github:nucleoid/delivery-forge:issue:7"),
            new CoordinationResource("scope", "github:nucleoid/delivery-forge:issue:6")
        };

        var result = await adapter.AcquireAllAsync(resources, owner, TimeSpan.FromSeconds(300), TestContext.Current.CancellationToken);

        Assert.False(result.MayDispatch);
        Assert.Equal(LeaseOwnership.Contended, result.Ownership);
        Assert.Equal(new[]
        {
            "github:nucleoid/delivery-forge:issue:6",
            "github:nucleoid/delivery-forge:issue:7"
        }, transport.AcquireRequests.Select(request => request.Resource.ResourceKey));
        Assert.Single(transport.ReleaseRequests);
        Assert.Equal("lease-a", transport.ReleaseRequests[0].LeaseId);
        Assert.Equal(3, transport.AllRequestIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(transport.AllRequestIds, requestId => Assert.True(Guid.TryParse(requestId, out _)));
    }

    [Fact]
    public async Task Release_requires_positive_owned_quiescence()
    {
        var transport = new RecordingTransport(CoordinationCapabilities.ValidV1());
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-a", "42", ServerNow, ServerNow.AddMinutes(5)));
        var adapter = await ContinuumCoordinationAdapter.CreateProductionAsync(transport, TestContext.Current.CancellationToken);
        var owner = new LeaseOwner("principal-1", "run-1");
        var acquired = await adapter.AcquireAllAsync(
            [new CoordinationResource("scope", "github:nucleoid/delivery-forge:issue:6")], owner,
            TimeSpan.FromSeconds(300), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<CoordinationUncertainException>(() => adapter.ReleaseAllAsync(
            acquired, new FixedQuiescence(false), TestContext.Current.CancellationToken));

        Assert.Contains("quiescence", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.ReleaseRequests);
    }

    [Fact]
    public void Safe_deadline_subtracts_round_trip_and_clock_uncertainty_from_server_expiry()
    {
        var deadline = LeaseDeadline.ComputeSafeDeadline(
            ServerNow,
            ServerNow.AddSeconds(300),
            TimeSpan.FromMilliseconds(800),
            TimeSpan.FromSeconds(50),
            TimeSpan.FromMilliseconds(200));

        Assert.Equal(TimeSpan.FromSeconds(349), deadline);
    }

    [Fact]
    public void Production_profile_is_explicitly_unavailable_without_live_external_capability()
    {
        var profile = ProductionCoordinationProfile.CurrentUnavailable();

        Assert.Equal(CoordinationAvailability.Unavailable, profile.Availability);
        Assert.Empty(profile.Operations);
        Assert.Contains(profile.Limitations, limitation => limitation.Contains("continuum#7", StringComparison.Ordinal));
        Assert.Contains(profile.Limitations, limitation => limitation.Contains("INCOMPLETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_required_capability_is_rejected_before_any_acquire()
    {
        var capabilities = CoordinationCapabilities.ValidV1() with
        {
            Operations = new HashSet<CoordinationOperation> { CoordinationOperation.Acquire }
        };
        var transport = new RecordingTransport(capabilities);

        await Assert.ThrowsAsync<CoordinationUnavailableException>(() =>
            ContinuumCoordinationAdapter.CreateProductionAsync(transport, TestContext.Current.CancellationToken));
        Assert.Empty(transport.AcquireRequests);
    }

    [Theory]
    [InlineData("github:nucleoid/delivery-forge:issue:0")]
    [InlineData(" github:nucleoid/delivery-forge:issue:6")]
    [InlineData("secret-token")]
    [InlineData("github:nucleoid/delivery-forge:issue:6\n")]
    public void Non_canonical_or_sensitive_resource_keys_are_rejected(string key)
    {
        Assert.Throws<ArgumentException>(() => new CoordinationResource("scope", key));
    }

    [Fact]
    public async Task Decimal_fencing_larger_than_javascript_safe_integer_round_trips_as_a_string()
    {
        var transport = new RecordingTransport(CoordinationCapabilities.ValidV1());
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired(
            "lease-a", "184467440737095516160000", ServerNow, ServerNow.AddMinutes(5)));
        var adapter = await ContinuumCoordinationAdapter.CreateProductionAsync(transport, TestContext.Current.CancellationToken);
        var owner = new LeaseOwner("principal", "run");
        var acquisition = await adapter.AcquireAllAsync(
            [new CoordinationResource("scope", "github:nucleoid/delivery-forge:issue:6")], owner,
            TimeSpan.FromSeconds(300), TestContext.Current.CancellationToken);

        await adapter.ReleaseAllAsync(acquisition, new FixedQuiescence(true), TestContext.Current.CancellationToken);

        Assert.Equal("184467440737095516160000", transport.ReleaseRequests.Single().FencingToken);
    }

    [Fact]
    public async Task Invalid_acquired_fencing_attempts_release_and_blocks_dispatch_as_uncertain()
    {
        var transport = new RecordingTransport(CoordinationCapabilities.ValidV1());
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired(
            "lease-a", "9.25", ServerNow, ServerNow.AddMinutes(5)));
        var adapter = await ContinuumCoordinationAdapter.CreateProductionAsync(transport, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<CoordinationUncertainException>(() => adapter.AcquireAllAsync(
            [new CoordinationResource("scope", "github:nucleoid/delivery-forge:issue:6")],
            new LeaseOwner("principal", "run"), TimeSpan.FromSeconds(300), TestContext.Current.CancellationToken));

        Assert.Single(transport.ReleaseRequests);
        Assert.Equal("9.25", transport.ReleaseRequests.Single().FencingToken);
    }

    [Fact]
    public async Task Cancellation_after_possible_acquire_rolls_back_prefix_and_is_never_a_clean_cancel()
    {
        var transport = new RecordingTransport(CoordinationCapabilities.ValidV1());
        transport.AcquireResults.Enqueue(AcquireTransportResult.Acquired("lease-a", "1", ServerNow, ServerNow.AddMinutes(5)));
        transport.AcquireExceptions.Enqueue(new OperationCanceledException("response lost"));
        var adapter = await ContinuumCoordinationAdapter.CreateProductionAsync(transport, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<CoordinationUncertainException>(() => adapter.AcquireAllAsync(
            [
                new CoordinationResource("scope", "github:nucleoid/delivery-forge:issue:6"),
                new CoordinationResource("scope", "github:nucleoid/delivery-forge:issue:7")
            ], new LeaseOwner("principal", "run"), TimeSpan.FromSeconds(300), TestContext.Current.CancellationToken));

        Assert.Single(transport.ReleaseRequests);
        Assert.Equal("lease-a", transport.ReleaseRequests.Single().LeaseId);
    }

    private static readonly DateTimeOffset ServerNow = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedQuiescence(bool quiescent) : IOwnedRunQuiescence
    {
        public Task<QuiescenceEvidence> ObserveAsync(LeaseOwner owner, CancellationToken cancellationToken = default) =>
            Task.FromResult(new QuiescenceEvidence(quiescent, quiescent ? "owned-processes-empty" : "owned-process-live"));
    }

    private sealed class RecordingTransport(CoordinationCapabilities capabilities) : ICoordinationTransport
    {
        public Queue<AcquireTransportResult> AcquireResults { get; } = new();
        public Queue<Exception> AcquireExceptions { get; } = new();
        public List<AcquireRequest> AcquireRequests { get; } = [];
        public List<ReleaseRequest> ReleaseRequests { get; } = [];
        public IEnumerable<string> AllRequestIds => AcquireRequests.Select(request => request.RequestId)
            .Concat(ReleaseRequests.Select(request => request.RequestId));

        public Task<CoordinationCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(capabilities);

        public Task<AcquireTransportResult> AcquireAsync(AcquireRequest request, CancellationToken cancellationToken = default)
        {
            AcquireRequests.Add(request);
            if (AcquireExceptions.Count > 0 && AcquireRequests.Count > AcquireResults.Count) throw AcquireExceptions.Dequeue();
            return Task.FromResult(AcquireResults.Dequeue());
        }

        public Task<RenewTransportResult> RenewAsync(RenewRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InspectTransportResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReleaseTransportResult> ReleaseAsync(ReleaseRequest request, CancellationToken cancellationToken = default)
        {
            ReleaseRequests.Add(request);
            return Task.FromResult(ReleaseTransportResult.Released());
        }
    }
}
