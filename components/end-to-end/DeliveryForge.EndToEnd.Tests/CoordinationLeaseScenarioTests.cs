using System.Text.Json;
using DeliveryForge.Coordination;
using DeliveryForge.Execution;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class CoordinationLeaseScenarioTests
{
    private static async Task<CoordinationContractFixtureHost> Create(OwnedScenarioRun run,
        CountingCoordinationFixtureTransport transport, IMonotonicClock clock)
    {
        // Diagnostic source/budget data is never consumed as lease or dispatch authority.
        var sourceBytes = await File.ReadAllBytesAsync(Path.Combine(run.Root, "source.json"), TestContext.Current.CancellationToken);
        await run.WriteNewJsonAsync("lease-spec-" + Guid.NewGuid().ToString("N") + ".json",
            new
            {
                fixture = true,
                productionCertified = false,
                firstFullRealRunCertified = false,
                seed = 11017,
                sourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant(),
                maximumScenarioSeconds = 120,
                maximumHostOperationSeconds = 2,
                maximumOwnedChildSeconds = 30,
                cpuCores = 2,
                memoryMiB = 4096,
                tokens = 1000,
                cost = 0,
                clockDomain = clock.DomainId,
                limitation = "Fixture deadline/transport only; real owned file/native observations."
            }, TestContext.Current.CancellationToken);
        return await CoordinationContractFixtureHost.CreateAsync(transport, new("fixture-parent", run.RunId),
            [new("fixture-e2e", "github:fixture/delivery-forge:issue:11")],
            new OwnedFixtureQuiescence(run, new("fixture-parent", run.RunId), clock), clock,
            new OwnedFixtureCheckpointStore(Path.Combine(run.Root, "lease-journal-" + Guid.NewGuid().ToString("N"))),
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 120_000)]
    public async Task Progressing_renewal_replaces_opaque_handle_without_changing_lease_generation()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new AdvancingFixtureClock(run.RunId);
        var transport = new CountingCoordinationFixtureTransport(progressingRenewal: true);
        var host = await Create(run, transport, clock);
        var acquired = (await host.AcquireAsync(TestContext.Current.CancellationToken)).Value;
        Assert.NotNull(acquired.Authority);
        var before = Assert.Single(acquired.Snapshot.Leases);
        clock.Advance(TimeSpan.FromSeconds(100));
        var observation = await host.RenewAsync(acquired.Authority, TestContext.Current.CancellationToken);
        var renewed = observation.Value;
        Assert.True(observation.Fixture);
        Assert.False(observation.ProductionCapable);
        Assert.Equal(LeaseOwnership.Held, renewed.Snapshot.Ownership);
        Assert.NotNull(renewed.Authority);
        Assert.NotSame(acquired.Authority, renewed.Authority);
        var after = Assert.Single(renewed.Snapshot.Leases);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.LeaseId, after.LeaseId);
        Assert.True(after.SafeDeadline > before.SafeDeadline);
        Assert.True(after.ExpiresAt > before.ExpiresAt);
        Assert.False(host.MayDispatchFixture(acquired.Authority));
        Assert.True(host.MayDispatchFixture(renewed.Authority));
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => host.RenewAsync(acquired.Authority, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => host.ReleaseAsync(acquired.Authority, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.ReleaseCalls);
        var records = await ReadRecords(run);
        Assert.Contains(records, record => record.Progress?.AdapterCode == "renewed");
        var released = (await host.ReleaseAsync(renewed.Authority, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(LeaseOwnership.Released, released.Snapshot.Ownership);
        Assert.Equal(1, transport.ReleaseCalls);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Foreign_same_facts_handle_cannot_send_renewal()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new FixtureMonotonicClock(run.RunId);
        var ownerTransport = new CountingCoordinationFixtureTransport();
        var foreignTransport = new CountingCoordinationFixtureTransport(progressingRenewal: true);
        var owner = await Create(run, ownerTransport, clock);
        var foreign = await Create(run, foreignTransport, clock);
        var acquired = (await owner.AcquireAsync(TestContext.Current.CancellationToken)).Value;
        Assert.NotNull(acquired.Authority);
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => foreign.RenewAsync(acquired.Authority, TestContext.Current.CancellationToken));
        Assert.Equal(0, foreignTransport.MutationCalls);
        Assert.True(owner.MayDispatchFixture(acquired.Authority));
        var released = (await owner.ReleaseAsync(acquired.Authority, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(LeaseOwnership.Released, released.Snapshot.Ownership);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Nonprogressing_renewal_revokes_dispatch_and_retains_actual_checkpoint_journal()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        run.RetainArtifacts("Invalid renewal remains uncertain; retained real journal is fixture data, not ownership proof.");
        var clock = new AdvancingFixtureClock(run.RunId);
        var transport = new CountingCoordinationFixtureTransport();
        var host = await Create(run, transport, clock);
        var acquired = (await host.AcquireAsync(TestContext.Current.CancellationToken)).Value;
        Assert.NotNull(acquired.Authority);
        clock.Advance(TimeSpan.FromSeconds(100));
        var renewed = (await host.RenewAsync(acquired.Authority, TestContext.Current.CancellationToken)).Value;
        Assert.Null(renewed.Authority);
        Assert.NotNull(renewed.ReleaseContinuation);
        Assert.Equal(LeaseOwnership.Uncertain, renewed.Snapshot.Ownership);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, renewed.Snapshot.Phase);
        Assert.False(host.MayDispatchFixture(acquired.Authority));
        Assert.Equal(0, transport.ReleaseCalls);
        var records = await ReadRecords(run);
        Assert.Contains(records, record => record.Prepared?.Kind == CoordinationIntentKind.Renew);
        Assert.DoesNotContain(records, record => record.Progress?.AdapterCode == "renewed");
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Expiry_refuses_dispatch_but_live_native_child_still_prevents_release()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new AdvancingFixtureClock(run.RunId);
        var transport = new CountingCoordinationFixtureTransport();
        var host = await Create(run, transport, clock);
        var acquired = (await host.AcquireAsync(TestContext.Current.CancellationToken)).Value;
        Assert.NotNull(acquired.Authority);
        var child = run.StartBoundedChild();
        var live = run.Processes.Observe(child.Identity);
        await run.WriteNewJsonAsync("expiry-child-initial-observation.json",
            new { fixture = true, child.Identity, child.RunId, observation = live }, TestContext.Current.CancellationToken);
        Assert.True(live.Outcome == ProcessControlOutcome.LiveOwned,
            $"Expected LiveOwned; actual {live.Outcome}: {live.Reason}; owned PID {child.Identity.ProcessId}, start {child.Identity.PlatformStartIdentity}, executable {child.Identity.ExecutablePath}, argv digest {child.Identity.ArgumentDigest}; retained run {run.Root}");
        clock.Advance(TimeSpan.FromSeconds(301));
        Assert.False(host.MayDispatchFixture(acquired.Authority));
        var retained = (await host.ReleaseAsync(acquired.Authority, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(LeaseOwnership.Uncertain, retained.Snapshot.Ownership);
        Assert.NotNull(retained.Continuation);
        Assert.Equal(0, transport.ReleaseCalls);
        var stopped = await run.Processes.QuiesceAsync(child.Identity, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await run.WriteNewJsonAsync("expiry-child-quiescence-observation.json",
            new { fixture = true, child.Identity, observation = stopped }, TestContext.Current.CancellationToken);
        Assert.True(stopped.Outcome is ProcessControlOutcome.Quiesced or ProcessControlOutcome.AlreadyExited, stopped.Reason);
        Assert.Equal(ProcessControlOutcome.AlreadyExited, run.Processes.Observe(child.Identity).Outcome);
        var closed = (await host.ReleaseAsync(retained.Continuation, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(LeaseOwnership.Released, closed.Snapshot.Ownership);
        Assert.Equal(CoordinationLifecyclePhase.Closed, closed.Snapshot.Phase);
        Assert.Equal(1, transport.ReleaseCalls);
        Assert.All(closed.QuiescenceEvidence, evidence => Assert.True(evidence.Quiescent));
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Malformed_acquired_decimal_fence_never_becomes_dispatch_authority()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        run.RetainArtifacts("Malformed acquired response retains the actual checkpoint journal and uncertainty.");
        var transport = new CountingCoordinationFixtureTransport(malformedAcquire: true);
        var host = await Create(run, transport, new FixtureMonotonicClock(run.RunId));
        var acquired = (await host.AcquireAsync(TestContext.Current.CancellationToken)).Value;
        Assert.Null(acquired.Authority);
        Assert.Equal(LeaseOwnership.Uncertain, acquired.Snapshot.Ownership);
        Assert.NotNull(acquired.Uncertainty);
        Assert.Equal(0, transport.ReleaseCalls);
        var records = await ReadRecords(run);
        Assert.Contains(records, record => record.Prepared?.Kind == CoordinationIntentKind.Acquire);
        Assert.DoesNotContain(records, record => record.Progress?.AdapterCode == "acquired");
        run.MarkAssertionsPassed();
    }

    private static async Task<IReadOnlyList<CoordinationCheckpointRecord>> ReadRecords(OwnedScenarioRun run)
    {
        var paths = Directory.EnumerateFiles(run.Root, "*-record.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(paths);
        var records = new List<CoordinationCheckpointRecord>();
        foreach (var path in paths)
            records.Add(JsonSerializer.Deserialize<CoordinationCheckpointRecord>(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken))
                ?? throw new InvalidDataException("Actual fixture checkpoint was empty."));
        return records.AsReadOnly();
    }
}
