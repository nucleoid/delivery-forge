using System.Diagnostics;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed partial class LocalSingleTests
{
    [Fact]
    public async Task Completed_worker_keeps_reservation_until_exact_pipeline_task_settles_without_stop()
    {
        using var repository = new PlanningRepository(); await repository.InitializeAsync(); var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "original-terminal"); var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager(); await using var processes = new ProcessControl(); using var execution = new ExecutionCoordinator(store, worktrees, processes, new FixtureControl());
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        var original = new SchedulingOriginal(); var pendingPipeline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.BindOriginal(original, new SchedulingParent(), () => pendingPipeline.Task, () => true, () => false);
        var ticket = await scheduler.ReserveAsync(Cancellation);
        try
        {
            var request = await scheduler.ProvisionAndPrepareAsync(ticket, Cancellation); var accepted = Accepted(request); await execution.AcceptAsync(accepted, Cancellation);
            await File.WriteAllTextAsync(Path.Combine(worker.Worktree, "README.md"), "completed actual Git fixture", Cancellation);
            await repository.GitAsync(worker.Worktree, "add", "README.md");
            await repository.GitAsync(worker.Worktree, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "worker");
            var observed = await worktrees.ObserveAsync(worker.Worktree, Cancellation);
            await execution.CompleteAsync(new(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity, request.AdapterId, request.AdapterVersion,
                accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity, "success", worker.Worktree, observed.HeadCommit, observed.TreeId, DateTimeOffset.UtcNow, [], []), Cancellation);
            var dispositions = 0;
            Task FixtureDisposition() { dispositions++; return Task.CompletedTask; }
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.CompleteObservedAsync(ticket, FixtureDisposition, Cancellation));
            Assert.True(scheduler.HasRetainedReservation); Assert.Equal(0, dispositions);
            pendingPipeline.SetResult(true);
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.CompleteObservedAsync(new LocalDispatchReservation(ticket.RunId), FixtureDisposition, Cancellation));
            Assert.True(scheduler.HasRetainedReservation); Assert.Equal(0, dispositions);
            // This callback is a fixture boundary only. Tool uses the actual
            // same-coordinator writer disposition and native closure checks.
            await scheduler.CompleteObservedAsync(ticket, FixtureDisposition, Cancellation);
            Assert.False(scheduler.HasRetainedReservation); Assert.Equal(1, dispositions);
            Assert.DoesNotContain((await store.RecoverAsync(worker.RunId, Cancellation)).Records, record => record.Kind == "stopped");
            Assert.Equal("completed", (await store.RecoverAsync(worker.RunId + "-scheduler", Cancellation)).Latest!.Payload.GetProperty("phase").GetString());
        }
        finally { if (scheduler.HasRetainedReservation) await scheduler.ReconcileCleanupAsync(Cancellation); }
    }

    [Fact]
    public async Task Fresh_base_refusal_before_provisioning_releases_only_exact_settled_unstarted_ticket_under_original_cleanup()
    {
        using var repository = new PlanningRepository(); await repository.InitializeAsync(); var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "unprovisioned-original"); var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager(); await using var processes = new ProcessControl(); using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader);
        var original = new SchedulingOriginal(); var pipeline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.BindOriginal(original, new SchedulingParent(), () => pipeline.Task, () => false, () => false);
        var ticket = await scheduler.ReserveAsync(Cancellation);
        try
        {
            await File.AppendAllTextAsync(Path.Combine(repository.Checkout, "README.md"), "actual source drift", Cancellation);
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ProvisionAndPrepareAsync(ticket, Cancellation));
            Assert.True(scheduler.CanReleaseUnprovisioned); Assert.False(Directory.Exists(worker.Worktree));
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReleaseUnprovisionedObservedAsync(ticket, () => true));
            Assert.True(scheduler.HasRetainedReservation);
            pipeline.SetResult(false); original.Refuse = true; // WORK stays closed; cleanup does not reopen it.
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReleaseUnprovisionedObservedAsync(new LocalDispatchReservation(ticket.RunId), () => true));
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ReleaseUnprovisionedObservedAsync(ticket, () => false));
            Assert.True(scheduler.HasRetainedReservation);
            await scheduler.ReleaseUnprovisionedObservedAsync(ticket, () => true);
            Assert.False(scheduler.HasRetainedReservation);
            Assert.Empty((await store.RecoverAsync(worker.RunId, Cancellation)).Records);
            Assert.Equal("reserved", (await store.RecoverAsync(worker.RunId + "-scheduler", Cancellation)).Latest!.Payload.GetProperty("phase").GetString());
        }
        finally
        {
            pipeline.TrySetResult(false);
            if (scheduler.CanReleaseUnprovisioned) await scheduler.ReleaseUnprovisionedObservedAsync(ticket, () => true);
        }
    }
    [Fact]
    public async Task Original_work_refusal_precedes_reservation_store_or_provisioning()
    {
        using var repository = new PlanningRepository(); await repository.InitializeAsync(); var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "expired-original"); var storeRoot = Path.Combine(repository.Root, "absent-store"); var store = new RunStore(storeRoot);
        var worktrees = new WorktreeManager(); await using var processes = new ProcessControl(); using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), store, execution, worktrees, repository.Reader); var original = new SchedulingOriginal();
        scheduler.BindOriginal(original, new SchedulingParent(), () => Task.CompletedTask, () => true, () => false); original.Refuse = true;
        await Assert.ThrowsAsync<TimeoutException>(() => scheduler.ReserveAsync(Cancellation));
        Assert.False(scheduler.HasRetainedReservation); Assert.False(Directory.Exists(storeRoot)); Assert.False(Directory.Exists(worker.Worktree));
    }
    [Fact(Timeout = 120000, SkipUnless = nameof(OriginalOwnedGitWindows), Skip = "Actual original-owned Git requires the Windows creation-owned fixture; Linux needs a separately selected native scope.")]
    public async Task Completed_pipeline_after_local_deadline_cannot_record_completion_or_release_before_actual_cleanup()
    {
        // A supplied root is retained by PlanningRepository, including on failure.
        using var repository = new PlanningRepository(Path.Combine(Path.GetTempPath(), "forge-local-deadline-" + Guid.NewGuid().ToString("N")));
        TestContext.Current.TestOutputHelper!.WriteLine("Retained local-deadline fixture: " + repository.Root);
        await repository.InitializeAsync(); var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "late-pipeline-" + Guid.NewGuid().ToString("N"));
        var original = new SchedulingOriginal(); var parent = new SchedulingParent();
        var originalWorktrees = new WorktreeManager(repository.GitExecutable, new ProcessCommandExecutor(), original);
        var worktrees = originalWorktrees.WithParentAdmission(parent, worker.RunId);
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        await using var processes = new ProcessControl(); using var execution = new ExecutionCoordinator(store, worktrees, processes, new FixtureControl());
        execution.BindCoordinationAdmission(parent);
        // Reuse the existing safety fixture's exact internal API calls. No clock,
        // ticket, owner or private scheduler field is reflected or reconstructed.
        const System.Reflection.BindingFlags internalInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ExecutionCoordinator).GetMethod("BindOriginalScheduling", internalInstance)!.Invoke(execution, [original, parent, worker.RunId, worktrees]);
        var scheduler = LocalSingleScheduler.Create(plan, worker, Demand with { TimeLimit = TimeSpan.FromSeconds(30) }, Budget(), store, execution, worktrees, repository.Reader);
        var pipeline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentCompletionReceipt? completion = null;
        bool ActualClosed() => completion is not null && pipeline.Task.IsCompletedSuccessfully && execution.ObserveOriginalProcessesSettled(worker.RunId);
        scheduler.BindOriginal(original, parent, () => pipeline.Task, ActualClosed, () => false);
        var ticket = await scheduler.ReserveAsync(Cancellation);
        var reserved = (await store.RecoverAsync(worker.RunId + "-scheduler", Cancellation)).Latest!.Payload;
        var expires = reserved.GetProperty("expiresAtUtc").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromSeconds(30), expires - reserved.GetProperty("startedAtUtc").GetDateTimeOffset());
        var forwardEntries = 0; var cleanupReturned = 0; var cleanupEntered = false;
        async Task ForwardWriter()
        {
            forwardEntries++;
            var actual = Assert.IsAssignableFrom<Task>(typeof(ExecutionCoordinator).GetMethod("ReleaseCompletedWriterAsync", internalInstance)!
                .Invoke(execution, [worker.RunId, completion!, (Func<bool>)ActualClosed, Cancellation]));
            await actual;
        }
        async Task CleanupWriter()
        {
            var actual = Assert.IsAssignableFrom<Task>(typeof(ExecutionCoordinator).GetMethod("ReleaseCompletedWriterAfterFailureAsync", internalInstance)!
                .Invoke(execution, [worker.RunId, completion!, (Func<bool>)ActualClosed]));
            await actual; cleanupReturned++;
        }
        async Task CleanupOnce()
        {
            Assert.False(cleanupEntered); cleanupEntered = true;
            original.Refuse = true; // Existing cleanup cannot reopen original WORK.
            await scheduler.ReleaseCompletedFailureObservedAsync(ticket, ActualClosed, CleanupWriter);
        }
        try
        {
            var request = await scheduler.ProvisionAndPrepareAsync(ticket, Cancellation); var accepted = Accepted(request);
            await execution.AcceptAsync(accepted, Cancellation);
            await File.WriteAllTextAsync(Path.Combine(worker.Worktree, "README.md"), "completed before local pipeline expiry", Cancellation);
            await repository.GitAsync(worker.Worktree, "add", "README.md");
            await repository.GitAsync(worker.Worktree, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "worker");
            var observed = await worktrees.ObserveAsync(worker.Worktree, Cancellation);
            completion = new(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity, request.AdapterId, request.AdapterVersion,
                accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity, "success", worker.Worktree, observed.HeadCommit, observed.TreeId, DateTimeOffset.UtcNow, [], []);
            await execution.CompleteAsync(completion, Cancellation);
            Assert.False(pipeline.Task.IsCompleted); Assert.True(scheduler.HasRetainedReservation);
            Assert.True(DateTimeOffset.UtcNow < expires, "Retained fixture setup exceeded its 30-second local demand before the intended expiry control.");
            // Wait for the actual immutable journal deadline, without changing a clock.
            while (true)
            {
                var remaining = expires - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining, Cancellation);
            }
            pipeline.SetResult(true);
            var failure = await Assert.ThrowsAsync<SchedulingException>(() => scheduler.CompleteObservedAsync(ticket, ForwardWriter, Cancellation));
            Assert.Contains("finite local run time budget is exhausted", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, forwardEntries); Assert.True(scheduler.HasRetainedReservation);
            Assert.DoesNotContain((await store.RecoverAsync(worker.RunId + "-scheduler", Cancellation)).Records,
                row => row.Payload.GetProperty("phase").GetString() == "completed");
            await CleanupOnce();
            Assert.Equal(1, cleanupReturned); Assert.Equal(0, forwardEntries); Assert.False(scheduler.HasRetainedReservation);
            var retained = (await store.RecoverAsync(worker.RunId, Cancellation)).Records;
            Assert.Contains(retained, row => row.Kind == "worker-completed"); Assert.DoesNotContain(retained, row => row.Kind == "stopped");
        }
        finally
        {
            // Never retry an entered failed cleanup or erase the retained Git/record evidence.
            pipeline.TrySetResult(true);
            if (!cleanupEntered && scheduler.HasRetainedReservation && completion is not null && ActualClosed()) await CleanupOnce();
        }
    }

    sealed class SchedulingOriginal : IOwnedDuplexBudget
    {
        internal bool Refuse;
        internal int CleanupReads;
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(1200);
        public TimeSpan WorkLimit => TimeSpan.FromSeconds(1100);
        public TimeSpan CleanupRemaining { get { CleanupReads++; return TimeSpan.FromSeconds(60); } }
        public CancellationToken Token => CancellationToken.None;
        public void CheckWork() { if (Refuse) throw new TimeoutException("Fixture original refused WORK."); }
    }
    sealed class SchedulingParent : ICoordinatedSynchronousEffectAdmission
    {
        public void CheckCurrent(string runId) { }
        public T EnterSynchronous<T>(string runId, Func<T> invoke) => invoke();
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) => invoke();
        public void RetainUncertain(string runId, string reason) => throw new IOException(reason);
    }
}
