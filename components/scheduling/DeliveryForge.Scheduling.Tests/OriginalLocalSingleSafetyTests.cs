using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed partial class LocalSingleTests
{
    [Fact]
    public async Task Stopped_original_uncertainty_retains_actual_reservation_and_never_enters_writer_disposition()
    {
        using var repository = new PlanningRepository(); await repository.InitializeAsync(); var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "stopped-original-uncertainty");
        var store = new RunStore(Path.Combine(repository.Root, "results")); var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl(); using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var slots = new ResourceSlots(1, 1, 1024, 0, 0, 0);
        var budget = ResourceBudget.Measure(slots, new("retained-original-test", true, slots, TimeSpan.FromMinutes(20), 2000, 0));
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, budget, store, execution, worktrees, repository.Reader);
        var original = new SchedulingOriginal(); var unknown = false; var dispositions = 0;
        scheduler.BindOriginal(original, new SchedulingParent(), () => Task.CompletedTask, () => false, () => unknown);
        var ticket = await scheduler.ReserveAsync(Cancellation);
        Task FixtureDisposition() { dispositions++; return Task.CompletedTask; }
        try
        {
            unknown = true; original.Refuse = true;
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReleaseStoppedObservedAsync(ticket, () => true, FixtureDisposition));
            Assert.True(scheduler.HasRetainedReservation); Assert.False(budget.CanAllocate(Demand)); Assert.Equal(0, dispositions);
            var second = LocalSingleScheduler.Create(plan, Worker(repository, plan, "must-not-start"), Demand, Budget(), store, execution, worktrees, repository.Reader);
            await Assert.ThrowsAsync<SchedulingException>(() => second.ReserveAsync(Cancellation));
            Assert.False(second.HasRetainedReservation); Assert.False(Directory.Exists(worker.Worktree));
        }
        finally
        {
            // Contract-fixture reconciliation only: no child/provisioning/writer
            // was entered. Tool's real original-unknown latch cannot be cleared
            // this way; its actual stopped callback is independently evidenced.
            unknown = false;
            if (scheduler.HasRetainedReservation) await scheduler.ReleaseStoppedObservedAsync(ticket, () => true, FixtureDisposition);
        }
        Assert.False(scheduler.HasRetainedReservation); Assert.Equal(1, dispositions);
        Assert.False(budget.CanAllocate(Demand with { TokenLimit = 2000 })); // Never refund original reserved spend.
    }

    [Fact]
    public async Task Bound_generic_cleanup_controls_refuse_explicitly_after_WORK_without_rechecking_parent_or_releasing_capacity()
    {
        using var repository = new PlanningRepository(); await repository.InitializeAsync(); var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "bound-cleanup-control"); var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager(); await using var processes = new ProcessControl(); using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        var original = new SchedulingOriginal(); scheduler.BindOriginal(original, new SchedulingParent(), () => Task.CompletedTask, () => false, () => false);
        original.Refuse = true;
        Assert.False(await scheduler.StopAsync(Cancellation));
        Assert.False(await scheduler.ReconcileCleanupAsync(Cancellation));
        Assert.False(await scheduler.ReconcileColdTerminalAsync(Cancellation));
        await Assert.ThrowsAsync<SchedulingException>(() => scheduler.PauseAsync(Cancellation));
        await Assert.ThrowsAsync<SchedulingException>(() => scheduler.RelinquishPausedAsync(Cancellation));
        await Assert.ThrowsAsync<SchedulingException>(() => scheduler.RecoverPausedAsync(Cancellation));
        Assert.Equal(0, original.CleanupReads); Assert.False(scheduler.HasRetainedReservation);
        Assert.Contains("actual containing host", scheduler.CleanupLimitation, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("stop")]
    [InlineData("cleanup")]
    [InlineData("cold-terminal")]
    [InlineData("pause")]
    public async Task Refused_bound_generic_control_does_not_latch_or_prevent_actual_owner_reservation(string control)
    {
        using var repository = new PlanningRepository(); await repository.InitializeAsync(); var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "refused-control-" + control);
        var storeRoot = Path.Combine(repository.Root, "control-store"); var store = new RunStore(storeRoot);
        var worktrees = new WorktreeManager(); await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        var original = new SchedulingOriginal();
        scheduler.BindOriginal(original, new SchedulingParent(), () => Task.CompletedTask, () => true, () => false);
        if (control == "pause") await Assert.ThrowsAsync<SchedulingException>(() => scheduler.PauseAsync(Cancellation));
        else Assert.False(await (control switch
        {
            "stop" => scheduler.StopAsync(Cancellation),
            "cleanup" => scheduler.ReconcileCleanupAsync(Cancellation),
            _ => scheduler.ReconcileColdTerminalAsync(Cancellation)
        }));
        Assert.Equal(0, original.CleanupReads); Assert.False(scheduler.HasRetainedReservation);
        Assert.False(Directory.Exists(worker.Worktree));
        var ticket = await scheduler.ReserveAsync(Cancellation);
        try { Assert.True(scheduler.HasRetainedReservation); original.CheckWork(); }
        finally
        {
            // Same fixture owner, exact unused ticket, completed bound pipeline;
            // no native worker/provisioning or production issuer is claimed.
            await scheduler.ReleaseCompletedUnprovisionedObservedAsync(ticket, () => true, Cancellation);
        }
        Assert.False(scheduler.HasRetainedReservation); Assert.Equal(0, original.CleanupReads);
    }
}
