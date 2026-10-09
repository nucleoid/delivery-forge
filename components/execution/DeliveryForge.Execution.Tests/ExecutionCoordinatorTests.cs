using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Execution.Tests;

public sealed class ExecutionCoordinatorTests
{
    [Fact]
    public async Task Prepare_accept_complete_uses_the_durable_request_and_exact_committed_revision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var plan = Plan(repository, directories.Path, "run-positive");

        var (request, _) = await coordinator.PrepareWorkerAsync(plan, cancellationToken);
        var accepted = Accepted(request);
        await coordinator.AcceptAsync(accepted, cancellationToken);
        var revision = await repository.CommitAsync("tracked.txt", "completed", cancellationToken);

        var completed = await coordinator.CompleteAsync(Completion(request, accepted, revision), cancellationToken);

        Assert.Equal("worker-completed", completed.Kind);
        Assert.Equal(WorkflowStateName.LocalComplete, await StateNameAsync(directories.Path, request.RunId, cancellationToken));
    }

    [Fact]
    public async Task Accept_without_durable_prepare_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directory.Path, processes);
        var request = Request("run");

        await Assert.ThrowsAsync<WorkerEnvelopeException>(() => coordinator.AcceptAsync(Accepted(request), cancellationToken));
    }

    [Fact]
    public async Task Duplicate_accept_is_rejected_by_durable_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(Plan(repository, directories.Path, "run-duplicate"), cancellationToken);
        var receipt = Accepted(request);
        await coordinator.AcceptAsync(receipt, cancellationToken);

        await Assert.ThrowsAsync<WorkerEnvelopeException>(() => coordinator.AcceptAsync(receipt, cancellationToken));
    }

    [Fact]
    public async Task Concurrent_duplicate_accepts_publish_exactly_one_transition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(Plan(repository, directories.Path, "run-concurrent"), cancellationToken);
        var receipt = Accepted(request);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await coordinator.AcceptAsync(receipt, cancellationToken); return true; }
            catch (WorkerEnvelopeException) { return false; }
        }));
        var recovery = await new RunStore(System.IO.Path.Combine(directories.Path, "store"))
            .RecoverAsync(request.RunId, cancellationToken);

        Assert.Single(outcomes, accepted => accepted);
        Assert.Single(recovery.Records, record => record.Kind == "worker-accepted");
    }

    [Fact]
    public async Task Complete_before_accept_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(Plan(repository, directories.Path, "run-order"), cancellationToken);
        var accepted = Accepted(request);
        var revision = await repository.RevisionAsync(cancellationToken);

        await Assert.ThrowsAsync<WorkerEnvelopeException>(() =>
            coordinator.CompleteAsync(Completion(request, accepted, revision), cancellationToken));
    }

    [Fact]
    public async Task Non_success_completion_is_rejected_without_advancing_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(Plan(repository, directories.Path, "run-failed"), cancellationToken);
        var accepted = Accepted(request);
        await coordinator.AcceptAsync(accepted, cancellationToken);
        var revision = await repository.RevisionAsync(cancellationToken);
        var failed = Completion(request, accepted, revision) with { Outcome = "failed" };

        await Assert.ThrowsAsync<WorkerEnvelopeException>(() => coordinator.CompleteAsync(failed, cancellationToken));
        var recovery = await new RunStore(System.IO.Path.Combine(directories.Path, "store"))
            .RecoverAsync(request.RunId, cancellationToken);
        Assert.Equal("worker-accepted", recovery.Latest!.Kind);
    }

    [Theory]
    [InlineData("outside.txt", false)]
    [InlineData("allowed/excluded.txt", true)]
    public async Task Completion_rejects_out_of_scope_and_excluded_changes(string path, bool excluded)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(Plan(repository, directories.Path, $"run-scope-{excluded}"), cancellationToken);
        var accepted = Accepted(request);
        await coordinator.AcceptAsync(accepted, cancellationToken);
        var revision = await repository.CommitAsync(path, "forbidden", cancellationToken);

        await Assert.ThrowsAsync<WorktreeBoundaryException>(() =>
            coordinator.CompleteAsync(Completion(request, accepted, revision), cancellationToken));
    }

    [Fact]
    public async Task Completion_rejects_wrong_worktree_even_when_head_and_tree_match()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(Plan(repository, directories.Path, "run-worktree"), cancellationToken);
        var accepted = Accepted(request);
        await coordinator.AcceptAsync(accepted, cancellationToken);
        var revision = await repository.CommitAsync("tracked.txt", "completed", cancellationToken);
        var wrong = Completion(request, accepted, revision) with { Worktree = System.IO.Path.Combine(directories.Path, "other") };

        await Assert.ThrowsAsync<WorkerEnvelopeException>(() => coordinator.CompleteAsync(wrong, cancellationToken));
    }

    [Fact]
    public async Task Completion_rejects_a_clean_unrelated_head()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(Plan(repository, directories.Path, "run-base"), cancellationToken);
        var accepted = Accepted(request);
        await coordinator.AcceptAsync(accepted, cancellationToken);
        await GitFixture.RunAsync(repository.Path, cancellationToken, "checkout", "--orphan", "unrelated");
        await GitFixture.RunAsync(repository.Path, cancellationToken, "rm", "-rf", ".");
        await File.WriteAllTextAsync(System.IO.Path.Combine(repository.Path, "tracked.txt"), "unrelated", cancellationToken);
        await GitFixture.RunAsync(repository.Path, cancellationToken, "add", "tracked.txt");
        await GitFixture.RunAsync(repository.Path, cancellationToken, "commit", "-m", "unrelated");
        var revision = await repository.RevisionAsync(cancellationToken);

        await Assert.ThrowsAsync<WorktreeBoundaryException>(() =>
            coordinator.CompleteAsync(Completion(request, accepted, revision), cancellationToken));
    }

    [Fact]
    public async Task Stop_prevents_late_completion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(Plan(repository, directories.Path, "run-stop"), cancellationToken);
        var accepted = AcceptedWithControls(request);
        await coordinator.AcceptAsync(accepted, cancellationToken);
        var stopped = await coordinator.StopAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);
        var revision = await repository.RevisionAsync(cancellationToken);

        Assert.True(stopped.Quiescent);
        await Assert.ThrowsAsync<WorkerEnvelopeException>(() =>
            coordinator.CompleteAsync(Completion(request, accepted, revision), cancellationToken));
    }

    [Fact]
    public async Task Resume_reobserves_and_blocks_an_exact_head_mismatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(Plan(repository, directories.Path, "run-resume"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);
        await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);
        await repository.CommitAsync("tracked.txt", "moved", cancellationToken);

        var result = await coordinator.ResumeAsync(request.RunId, cancellationToken);

        Assert.Equal(ReconciliationAction.Blocked, result.Reconciliation.Action);
        Assert.Contains(result.Reconciliation.Reasons, reason => reason.Contains("HEAD", StringComparison.Ordinal));
        Assert.Null(result.Record);
    }

    [Fact]
    public async Task Pause_then_resume_dispatches_only_when_fresh_observations_match_the_checkpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(Plan(repository, directories.Path, "run-resume-positive"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);

        var paused = await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);
        var resumed = await coordinator.ResumeAsync(request.RunId, cancellationToken);

        Assert.True(paused.Quiescent);
        Assert.Empty(paused.Processes);
        Assert.Equal(ReconciliationAction.ResumeDispatch, resumed.Reconciliation.Action);
        Assert.Equal("resumed", resumed.Record!.Kind);
    }

    [Fact]
    public async Task Orderly_dispose_preserves_durable_writer_identity_for_restart_resume()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        string runId;
        using (var original = CreateCoordinator(directories.Path, processes))
        {
            var (request, _) = await original.PrepareWorkerAsync(
                Plan(repository, directories.Path, "run-orderly-restart"), cancellationToken);
            runId = request.RunId;
            await original.AcceptAsync(AcceptedWithControls(request), cancellationToken);
            await original.PauseAsync(runId, TimeSpan.FromSeconds(1), cancellationToken);
        }

        using var restarted = CreateCoordinator(directories.Path, processes);
        var resumed = await restarted.ResumeAsync(runId, cancellationToken);

        Assert.Equal(ReconciliationAction.ResumeDispatch, resumed.Reconciliation.Action);
        Assert.Equal("resumed", resumed.Record!.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Non_owner_cannot_pause_or_stop_a_run(bool pause)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var ownerProcesses = new ProcessControl();
        await using var intruderProcesses = new ProcessControl();
        using var owner = CreateCoordinator(directories.Path, ownerProcesses);
        using var intruder = CreateCoordinator(directories.Path, intruderProcesses);
        var (request, _) = await owner.PrepareWorkerAsync(
            Plan(repository, directories.Path, $"run-non-owner-{pause}"), cancellationToken);
        await owner.AcceptAsync(Accepted(request), cancellationToken);

        var operation = pause
            ? intruder.PauseAsync(request.RunId, TimeSpan.FromMilliseconds(50), cancellationToken)
            : intruder.StopAsync(request.RunId, TimeSpan.FromMilliseconds(50), cancellationToken);

        await Assert.ThrowsAsync<WorktreeBoundaryException>(() => operation);
    }

    [Fact]
    public async Task Accepted_external_activity_cannot_be_reported_quiescent_without_adapter_control_evidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes, controlled: false);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-uncontrolled-adapter"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);

        var paused = await coordinator.PauseAsync(request.RunId, TimeSpan.FromMilliseconds(50), cancellationToken);

        Assert.False(paused.Quiescent);
        Assert.Equal("blocked-quiescence", paused.Record.Kind);
    }

    [Fact]
    public async Task Pause_requires_supported_capability_evidence_from_the_accepted_receipt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-pause-capability"), cancellationToken);
        await coordinator.AcceptAsync(Accepted(request), cancellationToken);

        var paused = await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);

        Assert.False(paused.Quiescent);
        Assert.Equal("blocked-quiescence", paused.Record.Kind);
    }

    [Fact]
    public async Task Mismatched_control_action_cannot_prove_adapter_quiescence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        var port = new FixedTestAgentControl((binding, _) => new AgentControlResult(
            binding, AgentControlAction.Stop, AgentActivityState.Quiescent, true, "wrong-action-proof", ""));
        using var coordinator = new ExecutionCoordinator(
            new RunStore(System.IO.Path.Combine(directories.Path, "store")), new WorktreeManager(), processes, port);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-mismatched-action"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);

        var paused = await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);

        Assert.False(paused.Quiescent);
        Assert.Equal("blocked-quiescence", paused.Record.Kind);
    }

    [Fact]
    public async Task Control_result_for_another_runtime_identity_cannot_prove_quiescence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        var port = new FixedTestAgentControl((binding, action) => new AgentControlResult(
            binding with { RuntimeTaskIdentity = "another-task" }, action,
            AgentActivityState.Quiescent, true, "wrong-runtime-proof", ""));
        using var coordinator = new ExecutionCoordinator(
            new RunStore(System.IO.Path.Combine(directories.Path, "store")), new WorktreeManager(), processes, port);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-mismatched-binding"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);

        var stopped = await coordinator.StopAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);

        Assert.False(stopped.Quiescent);
        Assert.Equal("blocked-quiescence", stopped.Record.Kind);
    }

    [Fact]
    public async Task Resume_without_supported_resume_evidence_is_blocked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-resume-capability"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request, includeResume: false), cancellationToken);
        await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);

        var resumed = await coordinator.ResumeAsync(request.RunId, cancellationToken);

        Assert.Equal(ReconciliationAction.Blocked, resumed.Reconciliation.Action);
        Assert.Null(resumed.Record);
        Assert.Contains(resumed.Reconciliation.Reasons,
            reason => reason.Contains("Resume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resume_requires_a_fresh_bound_live_adapter_observation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        AgentControlResult? invalidResult = null;
        var port = new FixedTestAgentControl((binding, action) =>
        {
            var result = action == AgentControlAction.Resume
                ? new AgentControlResult(binding with { RunId = "another-run" }, action,
                    AgentActivityState.Live, true, "wrong-run-resume-proof", "raw-adapter-limitation")
                : new AgentControlResult(binding, action, AgentActivityState.Quiescent, true, "pause-proof", "");
            if (action == AgentControlAction.Resume) invalidResult = result;
            return result;
        });
        var store = new RunStore(System.IO.Path.Combine(directories.Path, "store"));
        using var coordinator = new ExecutionCoordinator(
            store, new WorktreeManager(), processes, port);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-resume-binding"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);
        await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);

        var resumed = await coordinator.ResumeAsync(request.RunId, cancellationToken);

        Assert.Equal(ReconciliationAction.Blocked, resumed.Reconciliation.Action);
        Assert.Equal("blocked-quiescence", resumed.Record!.Kind);
        Assert.Contains(resumed.Reconciliation.Reasons,
            reason => reason.Contains("unbound", StringComparison.Ordinal));
        var payload = store.ReadPayload<QuiescenceRecord>(resumed.Record);
        Assert.Equal(invalidResult, payload.AdapterControl);
        Assert.False(payload.Quiescent);
    }

    [Fact]
    public async Task Resume_adapter_exception_is_durably_recorded_as_blocked_quiescence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        var port = new FixedTestAgentControl((binding, action) => action == AgentControlAction.Resume
            ? throw new InvalidOperationException("resume adapter failed")
            : new AgentControlResult(binding, action, AgentActivityState.Quiescent, true, "pause-proof", ""));
        var store = new RunStore(System.IO.Path.Combine(directories.Path, "store"));
        using var coordinator = new ExecutionCoordinator(store, new WorktreeManager(), processes, port);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-resume-exception"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);
        await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);

        var resumed = await coordinator.ResumeAsync(request.RunId, cancellationToken);

        Assert.Equal(ReconciliationAction.Blocked, resumed.Reconciliation.Action);
        Assert.Equal("blocked-quiescence", resumed.Record!.Kind);
        var payload = store.ReadPayload<QuiescenceRecord>(resumed.Record);
        Assert.Null(payload.AdapterControl);
        Assert.Contains(payload.Limitations,
            limitation => limitation.Contains("InvalidOperationException: resume adapter failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Caller_cancellation_after_successful_resume_cannot_lose_the_durable_outcome()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var resumeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var port = new FixedTestAgentControl((binding, action) =>
        {
            if (action == AgentControlAction.Resume) resumeCancellation.Cancel();
            return new AgentControlResult(binding, action,
                action == AgentControlAction.Resume ? AgentActivityState.Live : AgentActivityState.Quiescent,
                true, $"proof-{action}", "");
        });
        var store = new RunStore(System.IO.Path.Combine(directories.Path, "store"));
        using var coordinator = new ExecutionCoordinator(store, new WorktreeManager(), processes, port);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-resume-cancelled-after-control"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);
        await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);

        var resumed = await coordinator.ResumeAsync(request.RunId, resumeCancellation.Token);

        Assert.True(resumeCancellation.IsCancellationRequested);
        Assert.Equal(ReconciliationAction.ResumeDispatch, resumed.Reconciliation.Action);
        Assert.Equal("resumed", resumed.Record!.Kind);
        var payload = store.ReadPayload<AgentResumeRecord>(resumed.Record);
        Assert.Equal(AgentActivityState.Live, payload.AdapterObservation.Activity);
    }

    [Fact]
    public async Task Stop_before_acceptance_records_unknown_remote_dispatch_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-stop-before-accept"), cancellationToken);

        var stopped = await coordinator.StopAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);

        Assert.False(stopped.Quiescent);
        Assert.Equal("blocked-quiescence", stopped.Record.Kind);
    }

    [Fact]
    public async Task Post_control_fact_failure_is_durably_recorded_as_blocked_quiescence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(
            new RunStore(System.IO.Path.Combine(directories.Path, "store")), new WorktreeManager(), processes,
            new ProvenTestAgentControl(), new ThrowingExecutionFactSource());
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-fact-observation-failure"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);

        var paused = await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);

        Assert.False(paused.Quiescent);
        Assert.Equal("blocked-quiescence", paused.Record.Kind);
        var payload = new RunStore(System.IO.Path.Combine(directories.Path, "store"))
            .ReadPayload<QuiescenceRecord>(paused.Record);
        Assert.NotNull(payload.AdapterControl);
        Assert.Contains(payload.Limitations, limitation => limitation.Contains("fact observation failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adapter_cancellation_after_control_is_durably_recorded_as_blocked_quiescence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        var port = new FixedTestAgentControl((_, _) =>
            throw new OperationCanceledException("adapter cancelled after dispatching control"));
        using var coordinator = new ExecutionCoordinator(
            new RunStore(System.IO.Path.Combine(directories.Path, "store")), new WorktreeManager(), processes, port);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-control-cancelled"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);
        var launch = OperatingSystem.IsWindows()
            ? new ProcessLaunch("ping.exe", ["127.0.0.1", "-n", "30"])
            : new ProcessLaunch("/bin/sleep", ["30"]);
        await coordinator.StartTaskProcessAsync(request.RunId, launch, cancellationToken);

        var stopped = await coordinator.StopAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);

        Assert.False(stopped.Quiescent);
        Assert.Equal("blocked-quiescence", stopped.Record.Kind);
        Assert.Equal(ProcessControlOutcome.Quiesced, Assert.Single(stopped.Processes).Outcome);
        var payload = new RunStore(System.IO.Path.Combine(directories.Path, "store"))
            .ReadPayload<QuiescenceRecord>(stopped.Record);
        Assert.Contains(payload.Limitations,
            limitation => limitation.Contains("cancelled after dispatching control", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Completion_rejects_a_live_registered_process()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-live-completion"), cancellationToken);
        var accepted = Accepted(request);
        await coordinator.AcceptAsync(accepted, cancellationToken);
        var launch = OperatingSystem.IsWindows()
            ? new ProcessLaunch("ping.exe", ["127.0.0.1", "-n", "30"])
            : new ProcessLaunch("/bin/sleep", ["30"]);
        var child = await coordinator.StartTaskProcessAsync(request.RunId, launch, cancellationToken);
        var revision = await repository.CommitAsync("tracked.txt", "completed", cancellationToken);

        await Assert.ThrowsAsync<WorkerEnvelopeException>(() =>
            coordinator.CompleteAsync(Completion(request, accepted, revision), cancellationToken));

        await processes.QuiesceAsync(child.Identity, TimeSpan.FromMilliseconds(50), CancellationToken.None);
    }

    [Fact]
    public async Task Completion_rejects_a_live_owned_process_without_durable_registration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-undurable-live-completion"), cancellationToken);
        var accepted = Accepted(request);
        await coordinator.AcceptAsync(accepted, cancellationToken);
        var launch = OperatingSystem.IsWindows()
            ? new ProcessLaunch("ping.exe", ["127.0.0.1", "-n", "30"])
            : new ProcessLaunch("/bin/sleep", ["30"]);
        var child = processes.StartOwned(launch, request.RunId);
        var revision = await repository.CommitAsync("tracked.txt", "completed", cancellationToken);

        await Assert.ThrowsAsync<WorkerEnvelopeException>(() =>
            coordinator.CompleteAsync(Completion(request, accepted, revision), cancellationToken));

        await processes.QuiesceAsync(child.Identity, TimeSpan.FromMilliseconds(50), CancellationToken.None);
    }

    [Fact]
    public async Task Stop_quiesces_a_live_owned_process_without_durable_registration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = CreateCoordinator(directories.Path, processes);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-undurable-live-stop"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);
        var launch = OperatingSystem.IsWindows()
            ? new ProcessLaunch("ping.exe", ["127.0.0.1", "-n", "30"])
            : new ProcessLaunch("/bin/sleep", ["30"]);
        var child = processes.StartOwned(launch, request.RunId);

        var stopped = await coordinator.StopAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);

        Assert.True(stopped.Quiescent);
        Assert.Equal(ProcessControlOutcome.Quiesced, Assert.Single(stopped.Processes).Outcome);
        Assert.Equal(ProcessControlOutcome.AlreadyExited, processes.Observe(child.Identity).Outcome);
    }

    [Fact]
    public async Task Completion_rejects_an_unknown_registered_process_after_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var originalProcesses = new ProcessControl();
        var launch = OperatingSystem.IsWindows()
            ? new ProcessLaunch("ping.exe", ["127.0.0.1", "-n", "30"])
            : new ProcessLaunch("/bin/sleep", ["30"]);
        AgentRequest request;
        AgentAcceptedReceipt accepted;
        OwnedProcess child;
        using (var original = CreateCoordinator(directories.Path, originalProcesses))
        {
            (request, _) = await original.PrepareWorkerAsync(
                Plan(repository, directories.Path, "run-unknown-completion"), cancellationToken);
            accepted = Accepted(request);
            await original.AcceptAsync(accepted, cancellationToken);
            child = await original.StartTaskProcessAsync(request.RunId, launch, cancellationToken);
        }
        var revision = await repository.CommitAsync("tracked.txt", "completed", cancellationToken);
        await using var restartedProcesses = new ProcessControl();
        using var restarted = CreateCoordinator(directories.Path, restartedProcesses);

        await Assert.ThrowsAsync<WorkerEnvelopeException>(() =>
            restarted.CompleteAsync(Completion(request, accepted, revision), cancellationToken));

        await originalProcesses.QuiesceAsync(child.Identity, TimeSpan.FromMilliseconds(50), CancellationToken.None);
    }

    [Fact]
    public async Task Resume_blocks_when_fresh_issue_owned_authorization_facts_change()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        var facts = new MutableExecutionFactSource();
        using var coordinator = new ExecutionCoordinator(
            new RunStore(System.IO.Path.Combine(directories.Path, "store")), new WorktreeManager(), processes,
            new ProvenTestAgentControl(), facts);
        var (request, _) = await coordinator.PrepareWorkerAsync(
            Plan(repository, directories.Path, "run-fact-change"), cancellationToken);
        await coordinator.AcceptAsync(AcceptedWithControls(request), cancellationToken);
        await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);
        facts.AuthorizationCeiling = "plan";

        var resumed = await coordinator.ResumeAsync(request.RunId, cancellationToken);

        Assert.Equal(ReconciliationAction.Blocked, resumed.Reconciliation.Action);
        Assert.Contains(resumed.Reconciliation.Reasons,
            reason => reason.Contains("authorization", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Process_registered_before_restart_is_unknown_and_blocks_pause()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var originalProcesses = new ProcessControl();
        var launch = OperatingSystem.IsWindows()
            ? new ProcessLaunch("ping.exe", ["127.0.0.1", "-n", "30"])
            : new ProcessLaunch("/bin/sleep", ["30"]);
        AgentRequest request;
        OwnedProcess child;
        using (var original = CreateCoordinator(directories.Path, originalProcesses))
        {
            (request, _) = await original.PrepareWorkerAsync(
                Plan(repository, directories.Path, "run-restart"), cancellationToken);
            await original.AcceptAsync(AcceptedWithControls(request), cancellationToken);
            child = await original.StartTaskProcessAsync(request.RunId, launch, cancellationToken);
        }

        await using var restartedProcesses = new ProcessControl();
        using var restarted = CreateCoordinator(directories.Path, restartedProcesses);
        var paused = await restarted.PauseAsync(request.RunId, TimeSpan.FromMilliseconds(50), cancellationToken);

        Assert.False(paused.Quiescent);
        Assert.Equal(ProcessControlOutcome.IdentityUnknown, Assert.Single(paused.Processes).Outcome);
        await originalProcesses.QuiesceAsync(child.Identity, TimeSpan.FromMilliseconds(50), CancellationToken.None);
    }

    [Fact]
    public async Task A_second_coordinator_cannot_acquire_the_same_writer_boundary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var firstProcesses = new ProcessControl();
        await using var secondProcesses = new ProcessControl();
        using var first = CreateCoordinator(directories.Path, firstProcesses, "store-one");
        using var second = CreateCoordinator(directories.Path, secondProcesses, "store-two");
        await first.PrepareWorkerAsync(Plan(repository, directories.Path, "run-writer-one"), cancellationToken);

        await Assert.ThrowsAsync<WorktreeBoundaryException>(() =>
            second.PrepareWorkerAsync(Plan(repository, directories.Path, "run-writer-two"), cancellationToken));
    }

    [Fact]
    public void Adapter_receipts_must_bind_version_capabilities_and_immutable_request_identity()
    {
        var request = Request("run-bind");
        var accepted = Accepted(request);
        WorkerEnvelope.ValidateAccepted(request, accepted);

        Assert.Throws<WorkerEnvelopeException>(() =>
            WorkerEnvelope.ValidateAccepted(request, accepted with { AdapterVersion = "other" }));
        Assert.Throws<AgentCapabilityIncompleteException>(() =>
            WorkerEnvelope.ValidateAccepted(request, accepted with
            {
                Capabilities = accepted.Capabilities.Select(item =>
                    item.Capability == AgentCapability.Launch
                        ? item with { Status = AgentCapabilityStatus.Unverified }
                        : item).ToArray()
            }));
        var wrong = Completion(request, accepted, (new string('b', 40), new string('c', 40))) with { RequestIdentity = new string('0', 64) };
        Assert.Throws<WorkerEnvelopeException>(() => WorkerEnvelope.ValidateCompletion(request, accepted, wrong));
    }

    [Fact]
    public void Named_runtime_profiles_do_not_claim_live_proof_from_discovered_interfaces()
    {
        var profiles = new[]
        {
            KnownAgentProfiles.CodexCli("0.160.0"),
            KnownAgentProfiles.ClaudeCodeCli("2.1.292"),
            KnownAgentProfiles.PiCliUnavailable(),
            KnownAgentProfiles.OpenClawOptional("optional")
        };

        Assert.Equal(["codex.cli", "claude-code.cli", "pi.cli", "openclaw.sessions"],
            profiles.Select(profile => profile.AdapterId));
        Assert.All(profiles.SelectMany(profile => profile.Capabilities),
            capability => Assert.NotEqual(AgentCapabilityStatus.Supported, capability.Status));
        Assert.All(profiles, profile => Assert.Contains(profile.Capabilities,
            capability => capability.Capability == AgentCapability.Launch &&
                          !string.IsNullOrWhiteSpace(capability.Interface)));
        Assert.All(profiles, profile => Assert.Equal("INCOMPLETE",
            WorkerEnvelope.AssessCapabilities(profile,
                [AgentCapability.Launch, AgentCapability.CompletionEvidence]).Outcome));
    }

    [Fact]
    public void Request_rejects_absolute_and_traversing_allowed_paths()
    {
        var plan = new FrozenWorkerPlan("run", "plan", new string('a', 40), "owner/repo", "/parent", "/worker", "/result", "token", ["../escape"], [],
            "test.adapter", "1.0", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);
        Assert.Throws<WorkerEnvelopeException>(() => WorkerEnvelope.PrepareWorker(plan));
    }

    private static ExecutionCoordinator CreateCoordinator(
        string root, ProcessControl processes, string store = "store", bool controlled = true) =>
        new(new RunStore(System.IO.Path.Combine(root, store)), new WorktreeManager(), processes,
            controlled ? new ProvenTestAgentControl() : null);

    private static FrozenWorkerPlan Plan(GitFixture repository, string root, string runId) =>
        new(runId, "plan-v1", repository.Head, "owner/repo", System.IO.Path.Combine(root, "parent"), repository.Path,
            System.IO.Path.Combine(root, "result"), $"writer-{runId}", ["tracked.txt", "allowed"], ["allowed/excluded.txt"],
            "test.adapter", "1.0", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);

    [Fact]
    public void Request_fixture_uses_platform_local_absolute_paths()
    {
        var request = Request("portable-paths");

        Assert.True(System.IO.Path.IsPathFullyQualified(request.Worktree));
        Assert.True(System.IO.Path.IsPathFullyQualified(request.ResultDirectory));
    }

    private static AgentRequest Request(string runId)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "delivery-forge-request-fixtures", runId);
        var plan = new FrozenWorkerPlan(runId, "plan-v1", new string('a', 40), "owner/repo",
            System.IO.Path.Combine(root, "parent"), System.IO.Path.Combine(root, "worker"),
            System.IO.Path.Combine(root, "result"), "token", ["src"], [],
            "test.adapter", "1.0", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);
        return WorkerEnvelope.PrepareWorker(plan);
    }

    private static AgentAcceptedReceipt Accepted(AgentRequest request) =>
        new(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity, request.PlanIdentity, request.BaseCommit,
            request.Worktree, request.AdapterId, request.AdapterVersion, $"runtime-{request.RunId}", "opaque-task", DateTimeOffset.UtcNow,
            request.RequiredCapabilities.Select(capability =>
                new AgentCapabilityEvidence(capability, AgentCapabilityStatus.Supported, "test-interface", "test-proof")).ToArray());

    private static AgentAcceptedReceipt AcceptedWithControls(AgentRequest request, bool includeResume = true)
    {
        var capabilities = request.RequiredCapabilities
            .Concat([AgentCapability.Pause, AgentCapability.Stop])
            .Concat(includeResume ? [AgentCapability.Resume] : [])
            .Distinct()
            .Select(capability => new AgentCapabilityEvidence(
                capability, AgentCapabilityStatus.Supported, "test-interface", "test-proof"))
            .ToArray();
        return Accepted(request) with { Capabilities = capabilities };
    }

    private static AgentCompletionReceipt Completion(
        AgentRequest request, AgentAcceptedReceipt accepted, (string Head, string Tree) revision) =>
        new(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity, accepted.AdapterId, accepted.AdapterVersion,
            accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity, "success", request.Worktree,
            revision.Head, revision.Tree, DateTimeOffset.UtcNow, [], []);

    private static async Task<WorkflowStateName> StateNameAsync(string root, string runId, CancellationToken cancellationToken)
    {
        var recovery = await new RunStore(System.IO.Path.Combine(root, "store")).RecoverAsync(runId, cancellationToken);
        return recovery.Latest?.Kind == "worker-completed" ? WorkflowStateName.LocalComplete : WorkflowStateName.Other;
    }

    private enum WorkflowStateName { Other, LocalComplete }
}
