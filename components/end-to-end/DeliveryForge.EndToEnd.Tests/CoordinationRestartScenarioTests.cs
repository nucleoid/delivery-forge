using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Coordination;
using DeliveryForge.Execution;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class CoordinationRestartScenarioTests
{
    private static LeaseOwner Receiver(OwnedScenarioRun run) => new("fixture-parent", run.RunId);
    private static Task<CoordinationContractFixtureHost> Create(OwnedScenarioRun run, CountingCoordinationFixtureTransport transport,
        OwnedRestartCheckpointStore store, string domain)
    {
        var clock = new FixtureMonotonicClock(domain);
        return CoordinationContractFixtureHost.CreateAsync(transport,
            Receiver(run), [new("fixture-e2e", "github:fixture/delivery-forge:issue:11")],
            new OwnedFixtureQuiescence(run, Receiver(run), clock), clock, store,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 120_000)]
    public async Task Fresh_fixture_lifetime_recovers_only_cleanup_and_waits_for_real_child_quiescence()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await Spec(run, "cleanup-only-fixture-store-lifetime-restart");
        var root = Path.Combine(run.Root, "restart-journal");
        var reference = "fixture-takeover-" + Guid.NewGuid().ToString("N");
        using var originalStore = new OwnedRestartCheckpointStore(root, reference);
        var transport = new CountingCoordinationFixtureTransport(inspectOwnedLease: true);
        var original = await Create(run, transport, originalStore, "original-" + run.RunId);
        var acquired = (await original.AcquireAsync(TestContext.Current.CancellationToken)).Value;
        Assert.NotNull(acquired.Authority);
        var lifecycle = await ReadLifecycle(root);
        var child = run.StartBoundedChild();
        var live = run.Processes.Observe(child.Identity);
        Assert.True(live.Outcome == ProcessControlOutcome.LiveOwned, live.Reason);
        var pausedCleanup = (await original.ReleaseAsync(acquired.Authority, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(LeaseOwnership.Uncertain, pausedCleanup.Snapshot.Ownership);
        Assert.NotNull(pausedCleanup.Continuation);
        Assert.False(original.MayDispatchFixture(acquired.Authority));
        Assert.Equal(0, transport.ReleaseCalls);
        originalStore.Dispose(); // Explicitly end this fixture writer lifetime; never infer a process crash from bytes.
        using var freshStore = new OwnedRestartCheckpointStore(root, reference);
        var fresh = await Create(run, transport, freshStore, "fresh-" + run.RunId);
        var recoveredObservation = await fresh.RecoverForCleanupAsync(lifecycle.Id, reference, TestContext.Current.CancellationToken);
        var recovered = recoveredObservation.Value;
        Assert.True(recoveredObservation.Fixture);
        Assert.False(recoveredObservation.ProductionCapable);
        Assert.Equal(LeaseOwnership.Uncertain, recovered.Snapshot.Ownership);
        Assert.Equal(CoordinationLifecyclePhase.Cleaning, recovered.Snapshot.Phase);
        Assert.NotNull(recovered.Continuation);
        Assert.False(fresh.MayDispatchFixture(acquired.Authority));
        Assert.Equal(1, transport.Calls.Count(call => call == "acquire"));
        Assert.Equal(0, transport.ReleaseCalls);
        var retained = (await fresh.ReleaseAsync(recovered.Continuation, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(LeaseOwnership.Uncertain, retained.Snapshot.Ownership);
        Assert.NotNull(retained.Continuation);
        Assert.Equal(0, transport.ReleaseCalls);
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => fresh.ReleaseAsync(pausedCleanup.Continuation!, TestContext.Current.CancellationToken));
        var stopped = await run.Processes.QuiesceAsync(child.Identity, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(stopped.Outcome is ProcessControlOutcome.Quiesced or ProcessControlOutcome.AlreadyExited, stopped.Reason);
        Assert.Equal(ProcessControlOutcome.AlreadyExited, run.Processes.Observe(child.Identity).Outcome);
        var closed = (await fresh.ReleaseAsync(retained.Continuation, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(LeaseOwnership.Released, closed.Snapshot.Ownership);
        Assert.Equal(CoordinationLifecyclePhase.Closed, closed.Snapshot.Phase);
        Assert.Equal(1, transport.ReleaseCalls);
        Assert.False(fresh.MayDispatchFixture(acquired.Authority));
        var after = await ReadLifecycle(root);
        Assert.Contains(after.Records, record => record.WriterEpoch != lifecycle.Records[0].WriterEpoch);
        Assert.All(closed.QuiescenceEvidence, evidence => Assert.True(evidence.Quiescent));
        run.MarkAssertionsPassed();
    }

    [Theory(Timeout = 120_000)]
    [InlineData("held-writer")]
    [InlineData("wrong-takeover-reference")]
    public async Task Held_real_writer_or_foreign_reference_cannot_be_taken_over(string obstruction)
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await Spec(run, "actual-writer-refuses-fixture-takeover-" + obstruction);
        run.RetainArtifacts("Failed takeover remains nonpass evidence; no closure inferred from the fixture reference.");
        var root = Path.Combine(run.Root, "held-writer-journal");
        var reference = "fixture-takeover-" + Guid.NewGuid().ToString("N");
        using var originalStore = new OwnedRestartCheckpointStore(root, reference);
        using var otherStore = new OwnedRestartCheckpointStore(root, reference);
        var transport = new CountingCoordinationFixtureTransport(inspectOwnedLease: true);
        var original = await Create(run, transport, originalStore, "original-" + run.RunId);
        var acquired = (await original.AcquireAsync(TestContext.Current.CancellationToken)).Value;
        Assert.NotNull(acquired.Authority);
        var before = await ReadLifecycle(root);
        if (obstruction == "wrong-takeover-reference") originalStore.Dispose();
        var fresh = await Create(run, transport, otherStore, "fresh-" + run.RunId);
        var supplied = obstruction == "wrong-takeover-reference" ? "foreign-fixture-reference" : reference;
        await Assert.ThrowsAnyAsync<CoordinationException>(() => fresh.RecoverForCleanupAsync(before.Id, supplied, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.ReleaseCalls);
        Assert.Equal(1, transport.Calls.Count(call => call == "acquire"));
        Assert.Equal(before.Hashes, (await ReadLifecycle(root)).Hashes);
        if (obstruction == "held-writer")
        {
            Assert.True(original.MayDispatchFixture(acquired.Authority));
            var closed = (await original.ReleaseAsync(acquired.Authority, TestContext.Current.CancellationToken)).Value;
            Assert.Equal(LeaseOwnership.Released, closed.Snapshot.Ownership);
        }
        run.MarkAssertionsPassed();
    }

    [Theory(Timeout = 120_000)]
    [InlineData("corrupt-bytes")]
    [InlineData("unknown-version")]
    [InlineData("foreign-owner")]
    [InlineData("noncanonical-fence")]
    public async Task Malformed_actual_journal_never_grants_cleanup_or_dispatch(string defect)
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await Spec(run, "malformed-actual-journal-" + defect);
        run.RetainArtifacts("Untrusted altered checkpoint bytes retained; no repair or successful closure claimed.");
        var root = Path.Combine(run.Root, "malformed-journal");
        var reference = "fixture-takeover-" + Guid.NewGuid().ToString("N");
        using var originalStore = new OwnedRestartCheckpointStore(root, reference);
        var transport = new CountingCoordinationFixtureTransport(inspectOwnedLease: true);
        var original = await Create(run, transport, originalStore, "original-" + run.RunId);
        var acquired = (await original.AcquireAsync(TestContext.Current.CancellationToken)).Value;
        Assert.NotNull(acquired.Authority);
        var before = await ReadLifecycle(root);
        originalStore.Dispose();
        var pathIndex = defect == "noncanonical-fence" ? 1 : 0;
        var path = before.Paths[pathIndex];
        var record = before.Records[pathIndex];
        if (defect == "corrupt-bytes") await File.AppendAllTextAsync(path, "not-json", TestContext.Current.CancellationToken);
        else
        {
            var altered = defect switch
            {
                "unknown-version" => record with { SchemaVersion = 99 },
                "foreign-owner" => record with { Prepared = record.Prepared! with { Owner = new("foreign-parent", run.RunId) } },
                _ => record with { Progress = record.Progress! with { Lease = record.Progress!.Lease! with { FencingToken = "01" } } }
            };
            await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(altered), TestContext.Current.CancellationToken);
        }
        var alteredHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
        Assert.NotEqual(before.Hashes[pathIndex], alteredHash);
        await run.WriteNewJsonAsync("altered-checkpoint-diagnostic.json",
            new { fixture = true, defect, path, expectedSha256 = before.Hashes[pathIndex], observedSha256 = alteredHash }, TestContext.Current.CancellationToken);
        using var freshStore = new OwnedRestartCheckpointStore(root, reference);
        var fresh = await Create(run, transport, freshStore, "fresh-" + run.RunId);
        await Assert.ThrowsAnyAsync<CoordinationException>(() => fresh.RecoverForCleanupAsync(before.Id, reference, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.ReleaseCalls);
        Assert.Equal(1, transport.Calls.Count(call => call == "acquire"));
        Assert.False(fresh.MayDispatchFixture(acquired.Authority));
        Assert.Equal(alteredHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken))));
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Actual_file_port_refuses_old_epoch_and_disposed_writer_without_touching_record_bytes()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await Spec(run, "actual-file-writer-epoch-positive-control");
        var root = Path.Combine(run.Root, "epoch-control-journal");
        var reference = "fixture-takeover-" + Guid.NewGuid().ToString("N");
        using var originalStore = new OwnedRestartCheckpointStore(root, reference);
        var transport = new CountingCoordinationFixtureTransport();
        var original = await Create(run, transport, originalStore, "original-" + run.RunId);
        var acquired = (await original.AcquireAsync(TestContext.Current.CancellationToken)).Value;
        Assert.NotNull(acquired.Authority);
        Assert.Equal(LeaseOwnership.Released, (await original.ReleaseAsync(acquired.Authority, TestContext.Current.CancellationToken)).Value.Snapshot.Ownership);
        var before = await ReadLifecycle(root);
        var oldEpoch = before.Records[0].WriterEpoch;
        var stale = new CoordinationWriterClaim(before.Id, oldEpoch, before.Records.Count + 1);
        originalStore.Dispose();
        using var freshStore = new OwnedRestartCheckpointStore(root, reference);
        var current = await freshStore.ClaimWriterAsync(before.Id, Guid.NewGuid().ToString("D"), reference, TestContext.Current.CancellationToken);
        Assert.NotEqual(stale.WriterEpoch, current.WriterEpoch);
        var attempted = before.Records[0] with { RecordSequence = current.NextRecordSequence, WriterEpoch = stale.WriterEpoch };
        await Assert.ThrowsAsync<CoordinationCheckpointNotWrittenException>(() => originalStore.AppendAsync(stale, current.NextRecordSequence, attempted, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoordinationCheckpointNotWrittenException>(() => freshStore.AppendAsync(stale, current.NextRecordSequence, attempted, TestContext.Current.CancellationToken));
        Assert.Equal(before.Hashes, (await ReadLifecycle(root)).Hashes);
        run.MarkAssertionsPassed();
    }

    private sealed record Journal(string Id, IReadOnlyList<string> Paths, IReadOnlyList<CoordinationCheckpointRecord> Records, string[] Hashes);
    private static async Task<Journal> ReadLifecycle(string root)
    {
        var directory = Assert.Single(Directory.EnumerateDirectories(root));
        var paths = Directory.EnumerateFiles(directory, "*-record.json").Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(paths);
        var records = new List<CoordinationCheckpointRecord>();
        var hashes = new List<string>();
        foreach (var path in paths)
        {
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            hashes.Add(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            records.Add(JsonSerializer.Deserialize<CoordinationCheckpointRecord>(bytes) ?? throw new InvalidDataException("Missing actual journal record."));
        }
        return new(Path.GetFileName(directory), paths, records.AsReadOnly(), hashes.ToArray());
    }

    private static async Task Spec(OwnedScenarioRun run, string scenario)
    {
        var source = await File.ReadAllBytesAsync(Path.Combine(run.Root, "source.json"), TestContext.Current.CancellationToken);
        await run.WriteNewJsonAsync("restart-scenario-spec.json", new
        {
            fixture = true, productionCertified = false, firstFullRealRunCertified = false, scenario,
            sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source)), seed = 11017,
            scenarioSeconds = 120, hostOperationSeconds = 2, childSeconds = 30, cpu = 2, memoryMiB = 4096, tokens = 1000, cost = 0,
            limitation = "Actual file/native boundaries; explicit fixture-store lifetime replacement, no claimed OS parent crash or production authority."
        }, TestContext.Current.CancellationToken);
    }
}
