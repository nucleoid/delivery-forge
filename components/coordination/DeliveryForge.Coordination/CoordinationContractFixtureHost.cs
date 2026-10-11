namespace DeliveryForge.Coordination;

// Explicit test-profile boundary. No production constructor, reducer or opaque handle is exposed.
// Public stubs await parent-owned compiled RED before delegating the existing internal fixture factory.
public sealed class CoordinationContractFixtureHost
{
    private CoordinationContractFixtureHost() { }
    public bool Fixture => true;
    public bool ProductionCapable => false;
    public LeaseOwner Receiver => throw new NotImplementedException("Contract fixture host awaits compiled RED.");

    public static Task<CoordinationContractFixtureHost> CreateAsync(
        ICoordinationTransport fixtureTransport, LeaseOwner receiver, IReadOnlyList<CoordinationResource> resources,
        IOwnedRunQuiescenceSource quiescence, IMonotonicClock clock, ICoordinationCheckpointStore checkpoints,
        TimeSpan operationTimeout, TimeSpan maximumClockUncertainty, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Contract fixture host awaits compiled RED.");

    public Task<CoordinationFixtureObservation<AcquisitionResult>> AcquireAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Contract fixture acquisition awaits compiled RED.");

    public bool MayDispatchFixture(DispatchAuthority authority) =>
        throw new NotImplementedException("Contract fixture authority observation awaits compiled RED.");

    public Task<CoordinationFixtureObservation<ReleaseResult>> ReleaseAsync(DispatchAuthority authority, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Contract fixture release awaits compiled RED.");

    public Task<CoordinationFixtureObservation<ReleaseResult>> ReleaseAsync(ReleaseContinuation continuation, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Contract fixture release continuation awaits compiled RED.");
}

public sealed class CoordinationFixtureObservation<T>
{
    internal CoordinationFixtureObservation(T value) => Value = value;
    public bool Fixture => true;
    public bool ProductionCapable => false;
    public T Value { get; }
}
