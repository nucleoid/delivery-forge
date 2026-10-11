using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed partial class LocalSingleTests
{
    [Theory(Timeout = 60_000)]
    [InlineData(AgentControlAction.Pause)]
    [InlineData(AgentControlAction.Stop)]
    [InlineData(AgentControlAction.Resume)]
    public async Task RemainingHostControls_ignore_port_deadline_without_hanging_or_releasing_capacity(AgentControlAction action)
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "bounded-" + action);
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        var control = new DelayedFixtureControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes, control);
        var slots = new ResourceSlots(1, 1, 1024, 0, 0, 0);
        var budget = ResourceBudget.Measure(slots, new("fixture-bounded", true, slots, TimeSpan.FromMinutes(20), 10000, 0));
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, budget, store, execution, worktrees, repository.Reader,
            controlTimeout: TimeSpan.FromMilliseconds(50));
        Task? operation = null;
        try
        {
            var reservation = await scheduler.ReserveAsync(Cancellation);
            var request = await scheduler.ProvisionAndPrepareAsync(reservation, Cancellation);
            await execution.AcceptAsync(Accepted(request), Cancellation);
            if (action == AgentControlAction.Resume) await scheduler.PauseAsync(Cancellation);
            control.Block(action);
            operation = action switch
            {
                AgentControlAction.Pause => scheduler.PauseAsync(Cancellation),
                AgentControlAction.Stop => scheduler.StopAsync(Cancellation),
                _ => scheduler.ResumeAsync(reservation, Cancellation)
            };
            await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
            try { await operation.WaitAsync(TimeSpan.FromSeconds(3), Cancellation); }
            catch (SchedulingException) { }
            Assert.True(operation.IsCompleted, "Host control must settle despite a port ignoring its deadline and cancellation.");
            Assert.False(budget.CanAllocate(Demand));
            Assert.False(await scheduler.StopAsync(Cancellation));
            Assert.False(await scheduler.ReconcileCleanupAsync(Cancellation));
            control.Release();
            Assert.True(await scheduler.ReconcileCleanupAsync(Cancellation));
            Assert.True(budget.CanAllocate(Demand));
            Assert.True(control.StopObservations > (action == AgentControlAction.Stop ? 1 : 0),
                "Cleanup must obtain a fresh bound Stop observation; a late response alone cannot release capacity.");
        }
        finally
        {
            control.Release();
            if (operation is not null)
            {
                try { await operation.WaitAsync(TimeSpan.FromSeconds(10), Cancellation); }
                catch (SchedulingException) { }
            }
            if (!await scheduler.StopAsync(Cancellation))
                Assert.True(await scheduler.ReconcileCleanupAsync(Cancellation));
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ColdRecovery_paused_run_reobserves_writer_base_and_host_before_reusing_original_request(bool changedBase, bool writerStillHeld)
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "cold-recovery");
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes, new FixtureControl());
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        LocalSingleScheduler? receiver = null;
        ExecutionCoordinator? coldExecution = null;
        var relinquished = false;
        try
        {
            var originalTicket = await scheduler.ReserveAsync(Cancellation);
            var original = await scheduler.ProvisionAndPrepareAsync(originalTicket, Cancellation);
            await execution.AcceptAsync(Accepted(original), Cancellation);
            await scheduler.PauseAsync(Cancellation);
            await scheduler.RelinquishPausedAsync(Cancellation);
            relinquished = true;
            if (!writerStillHeld) execution.Dispose();
            if (changedBase) await repository.CommitAsync("changed after checkpoint");
            coldExecution = new ExecutionCoordinator(store, worktrees, processes, new FixtureControl());
            receiver = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, coldExecution, worktrees, repository.Reader);
            if (changedBase || writerStillHeld)
            {
                await Assert.ThrowsAsync<SchedulingException>(() => receiver.RecoverPausedAsync(Cancellation));
                Assert.DoesNotContain((await store.RecoverAsync(worker.RunId, Cancellation)).Records, record => record.Kind == "resumed");
            }
            else
            {
                var recoveredTicket = await receiver.RecoverPausedAsync(Cancellation);
                Assert.NotSame(originalTicket, recoveredTicket);
                await Assert.ThrowsAsync<SchedulingException>(() => receiver.ProvisionAndPrepareAsync(originalTicket, Cancellation));
                var resumed = await receiver.ResumeAsync(recoveredTicket, Cancellation);
                Assert.Equal(original.RequestIdentity, resumed.RequestIdentity);
                Assert.Equal(original.WriterToken, resumed.WriterToken);
                Assert.Equal(original.AuthorizationCeiling, resumed.AuthorizationCeiling);
                Assert.Single((await store.RecoverAsync(worker.RunId, Cancellation)).Records, record => record.Kind == "worker-prepared");
                Assert.True(await receiver.StopAsync(Cancellation));
            }
        }
        finally
        {
            if (!relinquished) Assert.True(await scheduler.StopAsync(Cancellation));
            execution.Dispose(); // Settle the former writer before fresh cleanup of a failed adoption.
            if (receiver is not null && !await receiver.StopAsync(Cancellation))
                Assert.True(await receiver.ReconcileCleanupAsync(Cancellation));
            coldExecution?.Dispose();
        }
    }

    private sealed class DelayedFixtureControl : IAgentControlPort
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AgentControlAction? _blocked;
        public int StopObservations { get; private set; }
        public void Block(AgentControlAction action) => _blocked = action;
        public void Release() => _release.TrySetResult();
        public async Task<AgentControlResult> ControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan deadline, CancellationToken cancellationToken = default)
        {
            if (action == AgentControlAction.Stop) StopObservations++;
            if (_blocked == action && !_release.Task.IsCompleted)
            {
                Entered.TrySetResult();
                await _release.Task; // Deliberately ignores both supplied deadline and cancellation.
            }
            return new(binding, action, action == AgentControlAction.Resume ? AgentActivityState.Live : AgentActivityState.Quiescent,
                true, "fixture-fresh-control", "Marked external fixture; no production host proof.");
        }
    }

    [Fact(Timeout = 600_000)]
    public async Task ResumingIntent_is_durable_before_host_send_so_cold_paused_claims_cannot_outrun_it()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "resume-intent");
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        var control = new DelayedFixtureControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes, control);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand with { TimeLimit = TimeSpan.FromMinutes(5) }, Budget(), store, execution, worktrees, repository.Reader,
            controlTimeout: TimeSpan.FromSeconds(30));
        Task<AgentRequest>? resume = null;
        Exception? primaryFailure = null;
        try
        {
            var ticket = await scheduler.ReserveAsync(Cancellation);
            var original = await scheduler.ProvisionAndPrepareAsync(ticket, Cancellation);
            await execution.AcceptAsync(Accepted(original), Cancellation);
            await scheduler.PauseAsync(Cancellation);
            control.Block(AgentControlAction.Resume);
            resume = scheduler.ResumeAsync(ticket, Cancellation);
            await AwaitEnteredOrFailureAsync(control.Entered.Task, resume);
            var journal = await store.RecoverAsync(worker.RunId + "-scheduler", Cancellation);
            Assert.Equal("resuming", journal.Latest!.Payload.GetProperty("phase").GetString());
            control.Release();
            Assert.Equal(original.RequestIdentity, (await resume).RequestIdentity);
        }
        catch (Exception exception) { primaryFailure = exception; throw; }
        finally { await SettleAndCloseFixtureAsync(scheduler, control, primaryFailure, resume); }
    }

    [Fact(Timeout = 600_000)]
    public async Task ClosedNamespace_case_distinct_run_claim_cannot_reuse_another_run_closure_proof()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var demand = Demand with { TimeLimit = TimeSpan.FromMinutes(5) };
        var closed = LocalSingleScheduler.Create(plan, Worker(repository, plan, "case-run"), demand, Budget(), store, execution, worktrees, repository.Reader);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await closed.ReserveAsync(Cancellation);
            Assert.True(await closed.StopAsync(cleanup.Token));
        }
        finally { Assert.True(await closed.StopAsync(cleanup.Token), closed.CleanupLimitation); }
        await store.AppendAsync("CASE-RUN-scheduler", "local-scheduler-checkpoint", new { profile = "local-single", phase = "prepared", runId = "CASE-RUN" }, Cancellation);
        var fresh = LocalSingleScheduler.Create(plan, Worker(repository, plan, "new-after-case"), demand, Budget(), store, execution, worktrees, repository.Reader);
        try { await Assert.ThrowsAsync<SchedulingException>(() => fresh.ReserveAsync(Cancellation)); }
        finally { Assert.True(await fresh.StopAsync(cleanup.Token), fresh.CleanupLimitation); }
    }

    [Fact(Timeout = 600_000)]
    public async Task ConcurrentPause_during_resume_never_returns_a_dispatchable_request()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "pause-during-resume");
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        var control = new DelayedFixtureControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes, control);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand with { TimeLimit = TimeSpan.FromMinutes(5) }, Budget(), store, execution, worktrees, repository.Reader,
            controlTimeout: TimeSpan.FromSeconds(30));
        Task<AgentRequest>? resume = null;
        Task? pause = null;
        Exception? primaryFailure = null;
        try
        {
            var ticket = await scheduler.ReserveAsync(Cancellation);
            var request = await scheduler.ProvisionAndPrepareAsync(ticket, Cancellation);
            await execution.AcceptAsync(Accepted(request), Cancellation);
            await scheduler.PauseAsync(Cancellation);
            control.Block(AgentControlAction.Resume);
            resume = scheduler.ResumeAsync(ticket, Cancellation);
            await AwaitEnteredOrFailureAsync(control.Entered.Task, resume);
            pause = scheduler.PauseAsync(Cancellation); // Public ingress is synchronous before its first awaited serial gate.
            control.Release();
            await Assert.ThrowsAsync<SchedulingException>(async () => await resume);
            await pause;
        }
        catch (Exception exception) { primaryFailure = exception; throw; }
        finally { await SettleAndCloseFixtureAsync(scheduler, control, primaryFailure, resume, pause); }
    }

    private static async Task AwaitEnteredOrFailureAsync(Task entered, Task operation)
    {
        var first = await Task.WhenAny(entered, operation).WaitAsync(TimeSpan.FromMinutes(2), Cancellation);
        if (ReferenceEquals(first, operation))
        {
            await operation; // Preserve a real preflight/control failure instead of disguising it as a barrier timeout.
            Assert.Fail("The control operation completed before the fixture port was entered.");
        }
        await entered;
    }

    private static async Task SettleAndCloseFixtureAsync(LocalSingleScheduler scheduler, DelayedFixtureControl control, Exception? primaryFailure, params Task?[] operations)
    {
        control.Release();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var cleanupFailures = new List<Exception>();
        foreach (var operation in operations.Where(operation => operation is not null).Cast<Task>())
        {
            try { await operation.WaitAsync(cleanup.Token); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            Assert.True(operation.IsCompleted, $"Fixture operation did not positively settle before cleanup. Primary: {primaryFailure}; cleanup: {string.Join(';', cleanupFailures)}");
        }
        var stopped = await scheduler.StopAsync(cleanup.Token).WaitAsync(cleanup.Token);
        if (!stopped) stopped = await scheduler.ReconcileCleanupAsync(cleanup.Token).WaitAsync(cleanup.Token);
        Assert.True(stopped, $"Fresh bounded uncancelled cleanup failed: {scheduler.CleanupLimitation}; primary: {primaryFailure}; cleanup: {string.Join(';', cleanupFailures)}");
    }
}
