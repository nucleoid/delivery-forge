using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
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

    public static bool OriginalOwnedGitWindows => OperatingSystem.IsWindows();
    [Theory(Timeout = 120000, SkipUnless = nameof(OriginalOwnedGitWindows), Skip = "Actual original-owned Git requires the Windows creation-owned fixture; Linux needs a separately selected native scope.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_worker_review_failure_releases_only_settled_known_original_reservation(bool unknown)
    {
        // Preserve the actual failed/successful fixture instead of erasing its Git/record evidence.
        using var repository = new PlanningRepository(Path.Combine(Path.GetTempPath(), "forge-completed-review-failure-" + Guid.NewGuid().ToString("N")));
        TestContext.Current.TestOutputHelper!.WriteLine("Retained completed-review fixture: " + repository.Root);
        await repository.InitializeAsync(); var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "completed-review-failure-" + Guid.NewGuid().ToString("N"));
        var original = new SchedulingOriginal(); var parent = new SchedulingParent();
        var originalWorktrees = new WorktreeManager(repository.GitExecutable, new DeliveryForge.Evidence.ProcessCommandExecutor(), original);
        var worktrees = originalWorktrees.WithParentAdmission(parent, worker.RunId);
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        await using var processes = new ProcessControl(); using var execution = new ExecutionCoordinator(store, worktrees, processes, new FixtureControl());
        execution.BindCoordinationAdmission(parent);
        // Exercise the existing internal original-owner API without widening
        // Execution's production/test friend assembly surface.
        const System.Reflection.BindingFlags internalInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ExecutionCoordinator).GetMethod("BindOriginalScheduling", internalInstance)!.Invoke(execution, [original, parent, worker.RunId, worktrees]);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        var pipeline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retainedUnknown = false; var writerDispositions = 0;
        scheduler.BindOriginal(original, parent, () => pipeline.Task, () => false, () => retainedUnknown);
        var ticket = await scheduler.ReserveAsync(Cancellation);
        AgentCompletionReceipt? completion = null;
        async Task ReleaseActualWriter()
        {
            var actual = Assert.IsAssignableFrom<Task>(typeof(ExecutionCoordinator).GetMethod("ReleaseCompletedWriterAfterFailureAsync", internalInstance)!
                .Invoke(execution, [worker.RunId, completion!, (Func<bool>)(() => !retainedUnknown)]));
            await actual;
            writerDispositions++;
        }
        try
        {
            var request = await scheduler.ProvisionAndPrepareAsync(ticket, Cancellation); var accepted = Accepted(request);
            await execution.AcceptAsync(accepted, Cancellation);
            await File.WriteAllTextAsync(Path.Combine(worker.Worktree, "README.md"), "completed before actual review refusal", Cancellation);
            await repository.GitAsync(worker.Worktree, "add", "README.md");
            await repository.GitAsync(worker.Worktree, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "worker");
            var observed = await worktrees.ObserveAsync(worker.Worktree, Cancellation);
            completion = new(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity, request.AdapterId, request.AdapterVersion,
                accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity, "success", worker.Worktree, observed.HeadCommit, observed.TreeId, DateTimeOffset.UtcNow, [], []);
            await execution.CompleteAsync(completion, Cancellation);
            var failure = new IOException("Actual post-completion review Task refused.");
            pipeline.SetException(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pipeline.Task));
            original.Refuse = true; // Cleanup never reopens WORK.
            retainedUnknown = unknown;
            if (unknown)
            {
                await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReleaseCompletedFailureObservedAsync(ticket, () => true, ReleaseActualWriter));
                Assert.True(scheduler.HasRetainedReservation); Assert.Equal(0, writerDispositions);
            }
            else
            {
                await scheduler.ReleaseCompletedFailureObservedAsync(ticket, () => true, ReleaseActualWriter);
                Assert.False(scheduler.HasRetainedReservation); Assert.Equal(1, writerDispositions);
                Assert.Same(failure, pipeline.Task.Exception!.InnerException);
                var records = (await store.RecoverAsync(worker.RunId, Cancellation)).Records;
                Assert.Contains(records, row => row.Kind == "worker-completed"); Assert.DoesNotContain(records, row => row.Kind == "stopped");
                Assert.Equal("prepared", (await store.RecoverAsync(worker.RunId + "-scheduler", Cancellation)).Latest!.Payload.GetProperty("phase").GetString());
            }
        }
        finally
        {
            // This mutable uncertainty flag is fixture DATA only; Tool's sticky
            // actual UNKNOWN can never be cleared by this cleanup control.
            retainedUnknown = false;
            if (scheduler.HasRetainedReservation && completion is not null)
                await scheduler.ReleaseCompletedFailureObservedAsync(ticket, () => true, ReleaseActualWriter);
        }
    }
}
