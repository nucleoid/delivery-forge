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

    [Fact(Timeout = 150000)]
    public async Task Gate_only_completion_after_local_deadline_retains_ticket_until_same_original_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "delivery-forge-historical-local-expiry-" + Guid.NewGuid().ToString("N"));
        TestContext.Current.TestOutputHelper!.WriteLine("Retained historical reservation fixture: " + root);
        using var repository = new PlanningRepository(root); await repository.InitializeAsync(); var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "historical-local-expiry"); var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager(); await using var processes = new ProcessControl(); using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand with { TimeLimit = TimeSpan.FromSeconds(30) }, Budget(), store, execution, worktrees, repository.Reader);
        var original = new SchedulingOriginal(); var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.BindOriginal(original, new SchedulingParent(), () => pending.Task, () => true, () => false);
        var ticket = await scheduler.ReserveAsync(Cancellation);
        try
        {
            var reserved = (await store.RecoverAsync(worker.RunId + "-scheduler", Cancellation)).Latest!.Payload;
            var expires = reserved.GetProperty("expiresAtUtc").GetDateTimeOffset();
            Assert.True(DateTimeOffset.UtcNow < expires, "Fixture setup must precede the actual 30-second immutable expiry.");
            while (true)
            {
                var remaining = expires - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining, Cancellation);
            }
            pending.SetResult(true);
            var failure = await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReleaseCompletedUnprovisionedObservedAsync(ticket, () => true, Cancellation));
            Assert.Contains("finite local run time budget is exhausted", failure.Message, StringComparison.Ordinal);
            Assert.True(scheduler.HasRetainedReservation); Assert.True(scheduler.CanReleaseUnprovisioned);
            Assert.Equal(0, original.CleanupReads); Assert.False(Directory.Exists(worker.Worktree));
            Assert.Empty((await store.RecoverAsync(worker.RunId, Cancellation)).Records);
            original.Refuse = true;
            await scheduler.ReleaseUnprovisionedObservedAsync(ticket, () => true);
            Assert.False(scheduler.HasRetainedReservation); Assert.True(original.CleanupReads > 0);
            Assert.Equal("reserved", (await store.RecoverAsync(worker.RunId + "-scheduler", Cancellation)).Latest!.Payload.GetProperty("phase").GetString());
        }
        finally
        {
            pending.TrySetResult(false);
            if (scheduler.CanReleaseUnprovisioned) await scheduler.ReleaseUnprovisionedObservedAsync(ticket, () => true);
        }
    }
}
