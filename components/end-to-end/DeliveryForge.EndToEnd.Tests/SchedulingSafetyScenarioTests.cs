using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Scheduling;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

[Collection(LocalSingleFixtureCollection.Name)]
public sealed class SchedulingSafetyScenarioTests
{
    private static ScenarioOptions Options => new(ScenarioProfile.LocalContract, [],
        new(TimeSpan.FromSeconds(120), 2, 4096, 1000, 0m), 11017);

    [Fact(Timeout = 120_000)]
    public async Task Unknown_maps_retain_contention_until_actual_owned_child_exit()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await SchedulingScenarioFixture.SpecAsync(run, "scheduling.unknown-map-contention", TestContext.Current.CancellationToken);
        var result = await new ScenarioRunner(run).RunAsync("scheduling.unknown-map-contention", Options, TestContext.Current.CancellationToken);
        AssertNonpass(result, "unknown-map-serialized", "live-child-retains-slot", "quiescence-allows-next-fixture-item");
        var observation = await Observation(run, result);
        Assert.Equal(1, observation.Payload.GetProperty("firstWaveCount").GetInt32());
        Assert.Equal(0, observation.Payload.GetProperty("blockedWaveCount").GetInt32());
        Assert.Equal(1, observation.Payload.GetProperty("afterQuiescenceWaveCount").GetInt32());
        Assert.True(observation.Payload.GetProperty("childInitiallyLive").GetBoolean());
        Assert.True(observation.Payload.GetProperty("childFinallyQuiescent").GetBoolean());
        Assert.Equal(ProcessControlOutcome.AlreadyExited, Assert.Single(run.Processes.ObserveOwned(run.RunId)).Outcome);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Open_pr_style_claim_and_unmerged_commit_cannot_replace_actual_integration_and_new_plan()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await SchedulingScenarioFixture.SpecAsync(run, "scheduling.real-integrated-prerequisite", TestContext.Current.CancellationToken);
        var result = await new ScenarioRunner(run).RunAsync("scheduling.real-integrated-prerequisite", Options, TestContext.Current.CancellationToken);
        AssertNonpass(result, "open-claim-not-integrated", "unmerged-commit-denied", "actual-merge-new-plan-required");
        var observation = await Observation(run, result);
        Assert.False(observation.Payload.GetProperty("candidateBeforeIntegrated").GetBoolean());
        Assert.True(observation.Payload.GetProperty("candidateAfterIntegrated").GetBoolean());
        Assert.True(observation.Payload.GetProperty("originalPinnedStillUnintegrated").GetBoolean());
        Assert.False(Directory.Exists(observation.Payload.GetProperty("workerWorktree").GetString()));
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Actual_main_base_drift_after_reservation_is_denied_before_worktree_provisioning()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await SchedulingScenarioFixture.SpecAsync(run, "scheduling.stale-base-before-provision", TestContext.Current.CancellationToken);
        var result = await new ScenarioRunner(run).RunAsync("scheduling.stale-base-before-provision", Options, TestContext.Current.CancellationToken);
        AssertNonpass(result, "stale-base-denied-before-provision", "reservation-positively-closed");
        var observation = await Observation(run, result);
        Assert.NotEqual(observation.Payload.GetProperty("frozenBaseCommit").GetString(), observation.Payload.GetProperty("observedHeadCommit").GetString());
        Assert.False(Directory.Exists(observation.Payload.GetProperty("workerWorktree").GetString()));
        var workerRunId = observation.Payload.GetProperty("workerRunId").GetString()!;
        Assert.Empty((await run.Store.RecoverAsync(workerRunId, TestContext.Current.CancellationToken)).Records);
        var journal = await run.Store.RecoverAsync(workerRunId + "-scheduler", TestContext.Current.CancellationToken);
        Assert.Equal("stopped", journal.Latest!.Payload.GetProperty("phase").GetString());
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Real_ancestry_control_requires_Git_merge_and_retains_old_immutable_snapshot()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await SchedulingScenarioFixture.SpecAsync(run, "real-ancestry-control", TestContext.Current.CancellationToken);
        var candidate = await SchedulingScenarioFixture.CreateTopicAsync(run, TestContext.Current.CancellationToken);
        var before = await run.Reader.ReadAsync(run.Checkout, "refs/heads/main", TestContext.Current.CancellationToken);
        Assert.False(await run.Reader.IsAncestorAsync(before, candidate, TestContext.Current.CancellationToken));
        await run.GitAsync(TestContext.Current.CancellationToken, "merge", "--ff-only", "topic");
        var after = await run.Reader.ReadAsync(run.Checkout, "refs/heads/main", TestContext.Current.CancellationToken);
        Assert.True(await run.Reader.IsAncestorAsync(after, candidate, TestContext.Current.CancellationToken));
        Assert.False(await run.Reader.IsAncestorAsync(before, candidate, TestContext.Current.CancellationToken));
        Assert.NotEqual(before.Commit, after.Commit);
        Assert.Equal(OwnedScenarioRun.InitialIntegratedBase, run.IntegratedSource.Commit);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Real_frozen_local_reservation_and_Stop_control_produces_only_owned_stopped_journal()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await SchedulingScenarioFixture.SpecAsync(run, "local-reservation-stop-control", TestContext.Current.CancellationToken);
        var plan = await SchedulingScenarioFixture.FreezeAsync(run, TestContext.Current.CancellationToken);
        var worker = SchedulingScenarioFixture.Worker(run, plan, "local-control");
        var worktrees = new WorktreeManager();
        using var execution = new ExecutionCoordinator(run.Store, worktrees, run.Processes);
        var scheduler = LocalSingleScheduler.Create(plan, worker, SchedulingScenarioFixture.Demand,
            SchedulingScenarioFixture.Budget(), run.Store, execution, worktrees, run.Reader);
        try
        {
            var ticket = await scheduler.ReserveAsync(TestContext.Current.CancellationToken);
            Assert.Equal(worker.RunId, ticket.RunId);
            Assert.False(Directory.Exists(worker.Worktree));
            Assert.True(await scheduler.StopAsync(TestContext.Current.CancellationToken), scheduler.CleanupLimitation);
            Assert.Empty((await run.Store.RecoverAsync(worker.RunId, TestContext.Current.CancellationToken)).Records);
            var journal = await run.Store.RecoverAsync(worker.RunId + "-scheduler", TestContext.Current.CancellationToken);
            Assert.Equal("stopped", journal.Latest!.Payload.GetProperty("phase").GetString());
            Assert.Equal(plan.Identity, journal.Latest!.Payload.GetProperty("planIdentity").GetString());
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReserveAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Assert.True(await scheduler.StopAsync(cleanup.Token), scheduler.CleanupLimitation);
        }
        run.MarkAssertionsPassed();
    }

    private static void AssertNonpass(ScenarioResult result, params string[] keys)
    {
        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.True(result.Fixture);
        Assert.False(result.FullRealProfileCertified);
        Assert.Equal(OwnedScenarioRun.InitialIntegratedBase, result.IntegratedBaseCommit);
        Assert.Equal(11017, result.Seed);
        foreach (var key in keys) Assert.True(result.Invariants[key]);
        Assert.True(result.Invariants["no-publication-writes"]);
    }

    private static async Task<StoredRunRecord> Observation(OwnedScenarioRun run, ScenarioResult result)
    {
        var record = Assert.Single((await run.Store.RecoverAsync(run.RunId, TestContext.Current.CancellationToken)).Records,
            record => record.Kind == "scenario-scheduling-observation");
        Assert.Contains(record.Sha256, result.ReceiptIds);
        Assert.Equal(OwnedScenarioRun.InitialIntegratedBase, record.Payload.GetProperty("integratedBaseCommit").GetString());
        Assert.True(record.Payload.GetProperty("fixture").GetBoolean());
        Assert.False(record.Payload.GetProperty("productionCertified").GetBoolean());
        return record;
    }
}
