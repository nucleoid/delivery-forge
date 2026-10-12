using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Planning;
using DeliveryForge.Scheduling;

namespace DeliveryForge.EndToEnd.Tests;

internal static class SchedulingSafetyOrchestrator
{
    public static Task ObserveAsync(OwnedScenarioRun run, string scenario, List<string> receipts,
        Dictionary<string, bool> invariants, CancellationToken cancellationToken) => scenario switch
        {
            "scheduling.unknown-map-contention" => UnknownMapAsync(run, receipts, invariants, cancellationToken),
            "scheduling.real-integrated-prerequisite" => IntegratedAsync(run, receipts, invariants, cancellationToken),
            "scheduling.stale-base-before-provision" => StaleBaseAsync(run, receipts, invariants, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

    private static async Task UnknownMapAsync(OwnedScenarioRun run, List<string> receipts,
        Dictionary<string, bool> invariants, CancellationToken cancellationToken)
    {
        var queueId = run.RunId + "-fixture-queue";
        var graph = DependencyGraph.Build([new DependencyNode("first", [], "root"), new("next", [], "root")], 2);
        var demand = SchedulingScenarioFixture.Demand;
        var scheduler = await Scheduler.CreateContractFixtureAsync(queueId, graph,
            [new("first", new([], [], Unknown: true), demand), new("next", new(["subject.txt"], []), demand)],
            SchedulingScenarioFixture.Budget(), new("parent-fixture-fifo", true, 2), run.Store, cancellationToken);
        var integrated = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var first = await scheduler.AllocateFixtureWaveAsync(integrated, cancellationToken);
            if (first.Count != 1) throw new InvalidOperationException("Unknown change maps did not serialize fixture allocation.");
            await scheduler.StartFixtureAsync(first[0], cancellationToken);
            var child = run.StartBoundedChild();
            var live = run.Processes.Observe(child.Identity);
            var native = await run.Store.AppendAsync(run.RunId, "scenario-native-observation", new
            {
                fixture = true,
                child.Identity,
                observation = live,
                integratedBaseCommit = run.IntegratedSource.Commit,
                limitation = "Actual owned native sleep only, not a worker execution receipt."
            }, cancellationToken);
            receipts.Add(native.Sha256);
            if (live.Outcome != ProcessControlOutcome.LiveOwned)
                throw new InvalidOperationException("Owned child was not positively live: " + live.Reason);
            await scheduler.RecordFixtureOutcomeAsync(first[0], ScheduledOutcome.Incomplete,
                live.Outcome == ProcessControlOutcome.AlreadyExited, cancellationToken);
            var blocked = await scheduler.AllocateFixtureWaveAsync(integrated, cancellationToken);
            var stopped = await run.Processes.QuiesceAsync(child.Identity, TimeSpan.FromSeconds(1), cancellationToken);
            var settled = run.Processes.Observe(child.Identity);
            var quiescent = (stopped.Outcome is ProcessControlOutcome.Quiesced or ProcessControlOutcome.AlreadyExited) &&
                settled.Outcome == ProcessControlOutcome.AlreadyExited;
            if (!quiescent) throw new InvalidOperationException("Owned child cleanup remained uncertain: " + stopped.Reason + "; " + settled.Reason);
            await scheduler.RecordFixtureOutcomeAsync(first[0], ScheduledOutcome.Incomplete, quiescent, cancellationToken);
            var after = await scheduler.AllocateFixtureWaveAsync(integrated, cancellationToken);
            invariants["unknown-map-serialized"] = first.Count == 1;
            invariants["live-child-retains-slot"] = blocked.Count == 0 && live.Outcome == ProcessControlOutcome.LiveOwned;
            invariants["quiescence-allows-next-fixture-item"] = quiescent && after.Count == 1 && after[0].NodeId != first[0].NodeId;
            await scheduler.StopAsync(cancellationToken); // Revokes only the remaining unstarted fixture ticket.
            var history = await run.Store.RecoverAsync(queueId, cancellationToken);
            var record = await run.Store.AppendAsync(run.RunId, "scenario-scheduling-observation", new
            {
                fixture = true,
                productionCertified = false,
                firstFullRealRunCertified = false,
                integratedBaseCommit = run.IntegratedSource.Commit,
                implementationHead = run.IntegratedSource.HeadCommit,
                firstWaveCount = first.Count,
                blockedWaveCount = blocked.Count,
                afterQuiescenceWaveCount = after.Count,
                childInitiallyLive = live.Outcome == ProcessControlOutcome.LiveOwned,
                childFinallyQuiescent = quiescent,
                child.Identity,
                stopObservation = stopped,
                settledObservation = settled,
                journalHashes = history.Records.Select(item => item.Sha256).ToArray(),
                limitation = "Fixture queue has no worker port. Quiescence supplied only from the actual owned child; no worker/quality PASS."
            }, cancellationToken);
            receipts.Add(record.Sha256);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await scheduler.StopAsync(cleanup.Token);
        }
    }

    private static async Task IntegratedAsync(OwnedScenarioRun run, List<string> receipts,
        Dictionary<string, bool> invariants, CancellationToken cancellationToken)
    {
        var candidate = await SchedulingScenarioFixture.CreateTopicAsync(run, cancellationToken);
        var before = await run.Reader.ReadAsync(run.Checkout, "refs/heads/main", cancellationToken);
        var beforeIntegrated = await run.Reader.IsAncestorAsync(before, candidate, cancellationToken);
        var worktrees = new WorktreeManager();
        using var execution = new ExecutionCoordinator(run.Store, worktrees, run.Processes);
        var openPlan = await SchedulingScenarioFixture.FreezeAsync(run, cancellationToken, "open-pr");
        var openWorker = SchedulingScenarioFixture.Worker(run, openPlan, "open-claim");
        var openDenied = false;
        try
        {
            _ = LocalSingleScheduler.Create(openPlan, openWorker, SchedulingScenarioFixture.Demand,
            SchedulingScenarioFixture.Budget(), run.Store, execution, worktrees, run.Reader);
        }
        catch (SchedulingException exception) when (exception.Message.Contains("commit:<full-object-id>", StringComparison.Ordinal)) { openDenied = true; }
        var oldPlan = await SchedulingScenarioFixture.FreezeAsync(run, cancellationToken, "commit:" + candidate);
        var oldWorker = SchedulingScenarioFixture.Worker(run, oldPlan, "unmerged-commit");
        var old = LocalSingleScheduler.Create(oldPlan, oldWorker, SchedulingScenarioFixture.Demand,
            SchedulingScenarioFixture.Budget(), run.Store, execution, worktrees, run.Reader);
        var unmergedDenied = false;
        try
        {
            try { _ = await old.ReserveAsync(cancellationToken); }
            catch (SchedulingException exception) when (exception.Message.Contains("not integrated", StringComparison.Ordinal)) { unmergedDenied = true; }
        }
        finally { await CloseAsync(old); }
        if (!openDenied || !unmergedDenied || beforeIntegrated)
            throw new InvalidOperationException("Open claims or unmerged objects incorrectly satisfied local dependency admission.");
        await run.GitAsync(cancellationToken, "merge", "--ff-only", "topic");
        var after = await run.Reader.ReadAsync(run.Checkout, "refs/heads/main", cancellationToken);
        var afterIntegrated = await run.Reader.IsAncestorAsync(after, candidate, cancellationToken);
        var originalStillUnintegrated = !await run.Reader.IsAncestorAsync(before, candidate, cancellationToken);
        var newPlan = await SchedulingScenarioFixture.FreezeAsync(run, cancellationToken, "commit:" + candidate);
        var newWorker = SchedulingScenarioFixture.Worker(run, newPlan, "actually-integrated");
        var admitted = LocalSingleScheduler.Create(newPlan, newWorker, SchedulingScenarioFixture.Demand,
            SchedulingScenarioFixture.Budget(), run.Store, execution, worktrees, run.Reader);
        var admittedRun = "";
        try { admittedRun = (await admitted.ReserveAsync(cancellationToken)).RunId; }
        finally { await CloseAsync(admitted); }
        invariants["open-claim-not-integrated"] = openDenied && !beforeIntegrated;
        invariants["unmerged-commit-denied"] = unmergedDenied;
        invariants["actual-merge-new-plan-required"] = afterIntegrated && originalStillUnintegrated &&
            newPlan.BaseCommit != oldPlan.BaseCommit && newPlan.Identity != oldPlan.Identity && admittedRun == newWorker.RunId;
        var journal = await run.Store.RecoverAsync(newWorker.RunId + "-scheduler", cancellationToken);
        var record = await run.Store.AppendAsync(run.RunId, "scenario-scheduling-observation", new
        {
            fixture = true,
            productionCertified = false,
            firstFullRealRunCertified = false,
            integratedBaseCommit = run.IntegratedSource.Commit,
            implementationHead = run.IntegratedSource.HeadCommit,
            candidateCommit = candidate,
            candidateBeforeIntegrated = beforeIntegrated,
            candidateAfterIntegrated = afterIntegrated,
            originalPinnedStillUnintegrated = originalStillUnintegrated,
            oldBaseCommit = oldPlan.BaseCommit,
            newBaseCommit = newPlan.BaseCommit,
            oldPlanIdentity = oldPlan.Identity,
            newPlanIdentity = newPlan.Identity,
            workerWorktree = newWorker.Worktree,
            journalHashes = journal.Records.Select(item => item.Sha256).ToArray(),
            limitation = "Open-PR-style condition is input data; actual Git merge/new plan permits reservation only, not worker provisioning or publication."
        }, cancellationToken);
        receipts.Add(record.Sha256);
    }

    private static async Task StaleBaseAsync(OwnedScenarioRun run, List<string> receipts,
        Dictionary<string, bool> invariants, CancellationToken cancellationToken)
    {
        var plan = await SchedulingScenarioFixture.FreezeAsync(run, cancellationToken);
        var worker = SchedulingScenarioFixture.Worker(run, plan, "stale-base");
        var worktrees = new WorktreeManager();
        using var execution = new ExecutionCoordinator(run.Store, worktrees, run.Processes);
        var scheduler = LocalSingleScheduler.Create(plan, worker, SchedulingScenarioFixture.Demand,
            SchedulingScenarioFixture.Budget(), run.Store, execution, worktrees, run.Reader);
        var baseDenied = false;
        var closed = false;
        try
        {
            var ticket = await scheduler.ReserveAsync(cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(run.Checkout, "subject.txt"), "stale parent base fixture\n", cancellationToken);
            await SchedulingScenarioFixture.CommitAsync(run, "actual parent base drift", cancellationToken);
            try { _ = await scheduler.ProvisionAndPrepareAsync(ticket, cancellationToken); }
            catch (SchedulingException exception) when (exception.Message.Contains("base drift", StringComparison.OrdinalIgnoreCase)) { baseDenied = true; }
            closed = await scheduler.StopAsync(cancellationToken);
        }
        finally { await CloseAsync(scheduler); }
        var actual = await run.Reader.ReadAsync(run.Checkout, "refs/heads/main", cancellationToken);
        var workerHistory = await run.Store.RecoverAsync(worker.RunId, cancellationToken);
        var journal = await run.Store.RecoverAsync(worker.RunId + "-scheduler", cancellationToken);
        invariants["stale-base-denied-before-provision"] = baseDenied && !Directory.Exists(worker.Worktree) &&
            !File.Exists(worker.Worktree) && workerHistory.Records.Count == 0;
        invariants["reservation-positively-closed"] = closed && journal.Latest?.Payload.GetProperty("phase").GetString() == "stopped";
        var record = await run.Store.AppendAsync(run.RunId, "scenario-scheduling-observation", new
        {
            fixture = true,
            productionCertified = false,
            firstFullRealRunCertified = false,
            integratedBaseCommit = run.IntegratedSource.Commit,
            implementationHead = run.IntegratedSource.HeadCommit,
            frozenBaseCommit = plan.BaseCommit,
            observedHeadCommit = actual.HeadCommit,
            actual.HeadTree,
            workerWorktree = worker.Worktree,
            workerRunId = worker.RunId,
            typedBaseDriftDenied = baseDenied,
            positivelyStopped = closed,
            journalHashes = journal.Records.Select(item => item.Sha256).ToArray(),
            limitation = "Fresh-base SchedulingException must precede provisioning; nested runtime placement is not claimed as a valid positive worktree destination."
        }, cancellationToken);
        receipts.Add(record.Sha256);
    }

    private static async Task CloseAsync(LocalSingleScheduler scheduler)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (!await scheduler.StopAsync(cleanup.Token))
            throw new InvalidOperationException("Owned reservation cleanup stayed uncertain: " + scheduler.CleanupLimitation);
    }
}
