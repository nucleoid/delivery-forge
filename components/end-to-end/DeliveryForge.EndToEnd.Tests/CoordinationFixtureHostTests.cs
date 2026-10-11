using DeliveryForge.Coordination;
using DeliveryForge.Execution;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class CoordinationFixtureHostTests
{
    private static LeaseOwner Receiver(OwnedScenarioRun run) => new("fixture-parent", run.RunId);
    private static CoordinationResource[] Resources => [new("fixture-e2e", "github:fixture/delivery-forge:issue:11")];

    private static Task<CoordinationContractFixtureHost> Create(OwnedScenarioRun run, CountingCoordinationFixtureTransport transport,
        IMonotonicClock clock, bool mismatchedClock = false) => CoordinationContractFixtureHost.CreateAsync(transport,
            Receiver(run), Resources, new OwnedFixtureQuiescence(run, Receiver(run), clock, mismatchedClock), clock,
            new OwnedFixtureCheckpointStore(Path.Combine(run.Root, "coordination-checkpoints-" + Guid.NewGuid().ToString("N"))),
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);

    [Fact(Timeout = 120_000)]
    public async Task Marked_fixture_acquires_and_releases_with_real_file_checkpoint_observations()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var transport = new CountingCoordinationFixtureTransport();
        var host = await Create(run, transport, new FixtureMonotonicClock(run.RunId));
        var acquired = await host.AcquireAsync(TestContext.Current.CancellationToken);
        var authority = acquired.Value.Authority;

        Assert.True(host.Fixture);
        Assert.False(host.ProductionCapable);
        Assert.True(acquired.Fixture);
        Assert.False(acquired.ProductionCapable);
        Assert.NotNull(authority);
        Assert.Equal(LeaseOwnership.Held, acquired.Value.Snapshot.Ownership);
        Assert.True(host.MayDispatchFixture(authority));
        Assert.NotEmpty(Directory.EnumerateFiles(run.Root, "*-record.json", SearchOption.AllDirectories));
        var released = await host.ReleaseAsync(authority, TestContext.Current.CancellationToken);
        Assert.True(released.Fixture);
        Assert.False(released.ProductionCapable);
        Assert.Equal(LeaseOwnership.Released, released.Value.Snapshot.Ownership);
        Assert.Equal(CoordinationLifecyclePhase.Closed, released.Value.Snapshot.Phase);
        Assert.False(host.MayDispatchFixture(authority));
        Assert.Equal(1, transport.ReleaseCalls);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Unmarked_available_transport_is_not_relabelled_fixture()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var transport = new CountingCoordinationFixtureTransport(marked: false);
        await Assert.ThrowsAsync<CoordinationUnavailableException>(() => Create(run, transport, new FixtureMonotonicClock(run.RunId)));
        Assert.Equal(0, transport.MutationCalls);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Quiescence_clock_mismatch_cannot_acquire()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var transport = new CountingCoordinationFixtureTransport();
        await Assert.ThrowsAsync<CoordinationUnavailableException>(() => Create(run, transport, new FixtureMonotonicClock(run.RunId), mismatchedClock: true));
        Assert.Equal(0, transport.MutationCalls);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Foreign_handle_with_identical_receiver_facts_cannot_release()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new FixtureMonotonicClock(run.RunId);
        var transportA = new CountingCoordinationFixtureTransport();
        var transportB = new CountingCoordinationFixtureTransport();
        var hostA = await Create(run, transportA, clock);
        var hostB = await Create(run, transportB, clock);
        var acquired = await hostA.AcquireAsync(TestContext.Current.CancellationToken);
        var authority = acquired.Value.Authority;
        Assert.NotNull(authority);
        Assert.False(hostB.MayDispatchFixture(authority));
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => hostB.ReleaseAsync(authority, TestContext.Current.CancellationToken));
        Assert.Equal(0, transportB.MutationCalls);
        Assert.True(hostA.MayDispatchFixture(authority));
        var released = await hostA.ReleaseAsync(authority, TestContext.Current.CancellationToken);
        Assert.Equal(LeaseOwnership.Released, released.Value.Snapshot.Ownership);
        Assert.Equal(CoordinationLifecyclePhase.Closed, released.Value.Snapshot.Phase);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Real_live_child_prevents_release_until_fresh_positive_quiescence()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var transport = new CountingCoordinationFixtureTransport();
        var host = await Create(run, transport, new FixtureMonotonicClock(run.RunId));
        var acquired = await host.AcquireAsync(TestContext.Current.CancellationToken);
        var authority = acquired.Value.Authority;
        Assert.NotNull(authority);
        var child = run.StartBoundedChild();
        Assert.Equal(ProcessControlOutcome.LiveOwned, run.Processes.Observe(child.Identity).Outcome);
        var retained = await host.ReleaseAsync(authority, TestContext.Current.CancellationToken);
        Assert.Equal(LeaseOwnership.Uncertain, retained.Value.Snapshot.Ownership);
        Assert.Equal(0, transport.ReleaseCalls);
        Assert.False(host.MayDispatchFixture(authority));
        var continuation = retained.Value.Continuation;
        Assert.NotNull(continuation);
        var stopped = await run.Processes.QuiesceAsync(child.Identity, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(stopped.Outcome is ProcessControlOutcome.Quiesced or ProcessControlOutcome.AlreadyExited, stopped.Reason);
        Assert.Equal(ProcessControlOutcome.AlreadyExited, run.Processes.Observe(child.Identity).Outcome);
        var released = await host.ReleaseAsync(continuation, TestContext.Current.CancellationToken);
        Assert.Equal(LeaseOwnership.Released, released.Value.Snapshot.Ownership);
        Assert.Equal(CoordinationLifecyclePhase.Closed, released.Value.Snapshot.Phase);
        Assert.Equal(1, transport.ReleaseCalls);
        Assert.All(released.Value.QuiescenceEvidence, evidence => Assert.True(evidence.Quiescent));
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Production_factory_does_not_call_even_an_available_looking_fixture_transport()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var transport = new CountingCoordinationFixtureTransport();
        await Assert.ThrowsAsync<CoordinationUnavailableException>(() => ContinuumCoordinationAdapter.CreateProductionAsync(transport, TestContext.Current.CancellationToken));
        Assert.Empty(transport.Calls);
        run.MarkAssertionsPassed();
    }
}
