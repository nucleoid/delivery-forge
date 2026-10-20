using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed partial class LocalSingleTests
{
    [Fact]
    public async Task Gate_only_reservation_releases_only_exact_settled_original_without_provisioning_or_cleanup_clock()
    {
        using var repository = new PlanningRepository(); await repository.InitializeAsync(); var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "historical-reservation"); var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager(); await using var processes = new ProcessControl(); using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        var original = new SchedulingOriginal(); var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = true; var unknown = false;
        scheduler.BindOriginal(original, new SchedulingParent(), () => pending.Task, () => settled, () => unknown);
        var ticket = await scheduler.ReserveAsync(Cancellation);
        try
        {
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReleaseCompletedUnprovisionedObservedAsync(ticket, () => true, Cancellation));
            Assert.True(scheduler.HasRetainedReservation);
            pending.SetResult(true);
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReleaseCompletedUnprovisionedObservedAsync(new LocalDispatchReservation(ticket.RunId), () => true, Cancellation));
            settled = false;
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReleaseCompletedUnprovisionedObservedAsync(ticket, () => true, Cancellation));
            settled = true; unknown = true;
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReleaseCompletedUnprovisionedObservedAsync(ticket, () => true, Cancellation));
            unknown = false;
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReleaseCompletedUnprovisionedObservedAsync(ticket, () => false, Cancellation));
            Assert.True(scheduler.HasRetainedReservation); Assert.False(Directory.Exists(worker.Worktree));
            await scheduler.ReleaseCompletedUnprovisionedObservedAsync(ticket, () => true, Cancellation);
            Assert.False(scheduler.HasRetainedReservation); Assert.Equal(0, original.CleanupReads); original.CheckWork();
            Assert.Empty((await store.RecoverAsync(worker.RunId, Cancellation)).Records);
            Assert.Equal("reserved", (await store.RecoverAsync(worker.RunId + "-scheduler", Cancellation)).Latest!.Payload.GetProperty("phase").GetString());
        }
        finally
        {
            pending.TrySetResult(false); settled = true; unknown = false;
            if (scheduler.CanReleaseUnprovisioned) await scheduler.ReleaseUnprovisionedObservedAsync(ticket, () => true);
        }
    }
}
