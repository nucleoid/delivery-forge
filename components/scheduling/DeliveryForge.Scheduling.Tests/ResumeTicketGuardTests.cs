using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed partial class LocalSingleTests
{
    [Fact(Timeout = 120_000)]
    public async Task GuardOnlyOldTicketDenial_keeps_the_correct_recovered_ticket_usable_on_same_receiver()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "guard-only-ticket");
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        var host = new ResumeTicketFixtureControl();
        var demand = Demand with { TimeLimit = TimeSpan.FromSeconds(90) };
        using var original = new ExecutionCoordinator(store, worktrees, processes, host);
        var parent = LocalSingleScheduler.Create(plan, worker, demand, Budget(), store, original, worktrees, repository.Reader);
        ExecutionCoordinator? receiverExecution = null;
        LocalSingleScheduler? receiver = null;
        try
        {
            var oldTicket = await parent.ReserveAsync(Cancellation);
            var request = await parent.ProvisionAndPrepareAsync(oldTicket, Cancellation);
            await original.AcceptAsync(Accepted(request), Cancellation);
            await parent.PauseAsync(Cancellation);
            await parent.RelinquishPausedAsync(Cancellation);
            original.Dispose(); // Settle the actual former writer; no data-derived takeover claim.
            var freshStore = new RunStore(store.RootPath);
            receiverExecution = new(freshStore, worktrees, processes, host);
            receiver = LocalSingleScheduler.Create(plan, worker, demand, Budget(), freshStore, receiverExecution, worktrees, repository.Reader);
            var correctTicket = await receiver.RecoverPausedAsync(Cancellation);
            Assert.NotSame(oldTicket, correctTicket);
            var before = (await freshStore.RecoverAsync(worker.RunId, Cancellation)).Records.Select(item => item.Sha256).ToArray();
            await Assert.ThrowsAsync<SchedulingException>(() => receiver.ResumeAsync(oldTicket, Cancellation));
            Assert.Equal(0, host.ResumeCalls);
            Assert.Equal(before, (await freshStore.RecoverAsync(worker.RunId, Cancellation)).Records.Select(item => item.Sha256).ToArray());
            // The exact same receiver must still accept its own ticket after a guard-only refusal.
            var resumed = await receiver.ResumeAsync(correctTicket, Cancellation);
            Assert.Equal(request.RequestIdentity, resumed.RequestIdentity);
            Assert.Equal(request.WriterToken, resumed.WriterToken);
            Assert.Equal(request.AuthorizationCeiling, resumed.AuthorizationCeiling);
            Assert.Equal(1, host.ResumeCalls);
            Assert.Single((await freshStore.RecoverAsync(worker.RunId, Cancellation)).Records, item => item.Kind == "resumed");
        }
        finally
        {
            original.Dispose();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (receiver is not null) Assert.True(await receiver.StopAsync(cleanup.Token) || await receiver.ReconcileCleanupAsync(cleanup.Token), receiver.CleanupLimitation);
            Assert.True(await parent.StopAsync(cleanup.Token) || await parent.ReconcileCleanupAsync(cleanup.Token), parent.CleanupLimitation);
            receiverExecution?.Dispose();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task PostSendResumeFailure_keeps_uncertainty_until_fresh_public_cleanup()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "post-send-ticket");
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        var host = new ResumeTicketFixtureControl { FailAfterResumeEntry = true };
        var demand = Demand with { TimeLimit = TimeSpan.FromSeconds(90) };
        using var execution = new ExecutionCoordinator(store, worktrees, processes, host);
        var scheduler = LocalSingleScheduler.Create(plan, worker, demand, Budget(), store, execution, worktrees, repository.Reader);
        try
        {
            var ticket = await scheduler.ReserveAsync(Cancellation);
            var request = await scheduler.ProvisionAndPrepareAsync(ticket, Cancellation);
            await execution.AcceptAsync(Accepted(request), Cancellation);
            await scheduler.PauseAsync(Cancellation);
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ResumeAsync(ticket, Cancellation));
            Assert.Equal(1, host.ResumeCalls);
            Assert.Contains((await store.RecoverAsync(worker.RunId, Cancellation)).Records, item => item.Kind == "blocked-quiescence");
            Assert.Contains((await store.RecoverAsync(worker.RunId + "-scheduler", Cancellation)).Records,
                item => item.Kind == "local-scheduler-checkpoint" && item.Payload.GetProperty("phase").GetString() == "resuming");
            host.FailAfterResumeEntry = false;
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ResumeAsync(ticket, Cancellation));
            Assert.Equal(1, host.ResumeCalls); // A possible sent operation cannot be retried by a guard-only fix.
            Assert.False(await scheduler.StopAsync(Cancellation));
            Assert.True(await scheduler.ReconcileCleanupAsync(Cancellation));
            Assert.DoesNotContain((await store.RecoverAsync(worker.RunId, Cancellation)).Records, item => item.Kind == "resumed");
        }
        finally
        {
            host.FailAfterResumeEntry = false;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Assert.True(await scheduler.StopAsync(cleanup.Token) || await scheduler.ReconcileCleanupAsync(cleanup.Token), scheduler.CleanupLimitation);
        }
    }

    private sealed class ResumeTicketFixtureControl : IAgentControlPort
    {
        public bool FailAfterResumeEntry { get; set; }
        public int ResumeCalls { get; private set; }
        public Task<AgentControlResult> ControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan deadline, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action == AgentControlAction.Resume)
            {
                ResumeCalls++;
                if (FailAfterResumeEntry) throw new IOException("FIXTURE host Resume entry reached; effects cannot be ruled out.");
            }
            return Task.FromResult(new AgentControlResult(binding, action, action == AgentControlAction.Resume ? AgentActivityState.Live : AgentActivityState.Quiescent,
                true, "fixture-ticket-control", "FIXTURE external edge only; no native quiescence/worker/production proof."));
        }
    }
}
