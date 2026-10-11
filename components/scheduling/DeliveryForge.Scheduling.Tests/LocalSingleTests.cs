using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed class LocalSingleTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static RunDemand Demand => new(new(1, 1, 1024, 0, 0, 0), [], TimeSpan.FromMinutes(1), 1000, 0);
    private static ResourceBudget Budget()
    {
        var slots = new ResourceSlots(2, 2, 4096, 1, 1, 0);
        return ResourceBudget.Measure(slots, new("approved-fixture-budget", true, slots, TimeSpan.FromMinutes(20), 10000, 0));
    }
    private static FrozenWorkerPlan Worker(PlanningRepository repository, FrozenPlan plan, string runId) => new(runId, plan.ContractIdentity, plan.BaseCommit,
        plan.Repository, repository.Checkout, Path.Combine(repository.Root, "worktree-" + runId), Path.Combine(repository.Root, "results", runId), Guid.NewGuid().ToString("N"),
        ["README.md"], ["excluded"], "fixture-agent", "1", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);

    [Fact]
    public async Task Owned_reservation_prepares_real_isolated_worktree_through_execution_core()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "local-positive");
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes, new FixtureControl());
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        try
        {
            var reservation = await scheduler.ReserveAsync(Cancellation);
            Assert.False(Directory.Exists(worker.Worktree));
            var request = await scheduler.ProvisionAndPrepareAsync(reservation, Cancellation);
            Assert.Equal(plan.ContractIdentity, request.PlanIdentity);
            Assert.Equal(plan.BaseCommit, request.BaseCommit);
            Assert.Equal(worker.Worktree, request.Worktree);
            Assert.True((await worktrees.ObserveAsync(request.Worktree, Cancellation)).IsClean);
            Assert.Contains((await store.RecoverAsync(worker.RunId, Cancellation)).Records, record => record.Kind == "worker-prepared");
            await execution.AcceptAsync(Accepted(request), Cancellation);
            Assert.True(await scheduler.StopAsync(Cancellation));
        }
        finally { await scheduler.StopAsync(Cancellation); }
    }

    [Fact]
    public async Task Local_profile_allows_one_reserved_issue_even_with_spare_agent_slots()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var first = LocalSingleScheduler.Create(plan, Worker(repository, plan, "one"), Demand, Budget(), store, execution, worktrees, repository.Reader);
        var second = LocalSingleScheduler.Create(plan, Worker(repository, plan, "two"), Demand, Budget(), store, execution, worktrees, repository.Reader);
        try
        {
            await first.ReserveAsync(Cancellation);
            await Assert.ThrowsAsync<SchedulingException>(() => second.ReserveAsync(Cancellation));
        }
        finally { Assert.True(await first.StopAsync(Cancellation)); }
    }

    [Fact]
    public async Task Pause_before_provisioning_revokes_ticket_and_creates_no_worktree()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "pause");
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        try
        {
            var reservation = await scheduler.ReserveAsync(Cancellation);
            await scheduler.PauseAsync(Cancellation);
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ProvisionAndPrepareAsync(reservation, Cancellation));
            Assert.False(Directory.Exists(worker.Worktree));
        }
        finally { Assert.True(await scheduler.StopAsync(Cancellation)); }
    }

    [Fact]
    public async Task Fresh_base_drift_blocks_before_allocation_or_provisioning()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        await repository.CommitAsync("drift");
        var worker = Worker(repository, plan, "drift");
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        try
        {
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReserveAsync(Cancellation));
            Assert.False(Directory.Exists(worker.Worktree));
            Assert.Empty((await store.RecoverAsync(worker.RunId, Cancellation)).Records);
        }
        finally { Assert.True(await scheduler.StopAsync(Cancellation)); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Worker_cannot_substitute_plan_or_escalate_ceiling(bool wrongPlan)
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "wrong");
        worker = wrongPlan ? worker with { PlanIdentity = "sha256:" + new string('a', 64) } : worker with { AuthorizationCeiling = "merge" };
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes);
        Assert.Throws<SchedulingException>(() => LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader));
    }

    [Fact]
    public async Task Resume_rechecks_live_execution_and_keeps_original_request_and_ceiling()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "resume");
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes, new FixtureControl());
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        try
        {
            var reservation = await scheduler.ReserveAsync(Cancellation);
            var request = await scheduler.ProvisionAndPrepareAsync(reservation, Cancellation);
            await execution.AcceptAsync(Accepted(request), Cancellation);
            await scheduler.PauseAsync(Cancellation);
            var resumed = await scheduler.ResumeAsync(reservation, Cancellation);
            Assert.Equal(request.RequestIdentity, resumed.RequestIdentity);
            Assert.Equal("implement", resumed.AuthorizationCeiling);
            Assert.Contains((await store.RecoverAsync(worker.RunId, Cancellation)).Records, record => record.Kind == "resumed");
        }
        finally { Assert.True(await scheduler.StopAsync(Cancellation)); }
    }

    [Fact]
    public async Task Old_active_checkpoint_cannot_authorize_another_run_after_restart()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        await store.AppendAsync("prior-scheduler", "local-scheduler-checkpoint", new { profile = "local-single", phase = "prepared", runId = "prior", stopped = false }, Cancellation);
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var worker = Worker(repository, plan, "new-run");
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        try
        {
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReserveAsync(Cancellation));
            Assert.False(Directory.Exists(worker.Worktree));
        }
        finally { Assert.True(await scheduler.StopAsync(Cancellation)); }
    }

    [Fact]
    public async Task Observed_in_process_stop_allows_serial_next_issue_without_reusing_old_ticket()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var first = LocalSingleScheduler.Create(plan, Worker(repository, plan, "first"), Demand, Budget(), store, execution, worktrees, repository.Reader);
        var second = LocalSingleScheduler.Create(plan, Worker(repository, plan, "second"), Demand, Budget(), store, execution, worktrees, repository.Reader);
        await first.ReserveAsync(Cancellation);
        Assert.True(await first.StopAsync(Cancellation));
        try { Assert.Equal("second", (await second.ReserveAsync(Cancellation)).RunId); }
        finally { Assert.True(await second.StopAsync(Cancellation)); }
    }
    private static AgentAcceptedReceipt Accepted(AgentRequest request) => new(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity,
        request.PlanIdentity, request.BaseCommit, request.Worktree, request.AdapterId, request.AdapterVersion, "fixture-runtime", "fixture-task", DateTimeOffset.UtcNow,
        new[] { AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Pause, AgentCapability.Stop, AgentCapability.Resume }
            .Select(capability => new AgentCapabilityEvidence(capability, AgentCapabilityStatus.Supported, "fixture-control", "test-only external edge")).ToArray());

    private sealed class FixtureControl : IAgentControlPort
    {
        public Task<AgentControlResult> ControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan deadline, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentControlResult(binding, action, action == AgentControlAction.Resume ? AgentActivityState.Live : AgentActivityState.Quiescent, true, "fixture-control-receipt", "test-only external edge; no real host certification"));
    }
}
