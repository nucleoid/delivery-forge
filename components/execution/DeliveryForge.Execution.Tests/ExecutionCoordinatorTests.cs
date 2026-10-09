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
        var accepted = Accepted(request);
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
        await coordinator.AcceptAsync(Accepted(request), cancellationToken);
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
        await coordinator.AcceptAsync(Accepted(request), cancellationToken);

        var paused = await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);
        var resumed = await coordinator.ResumeAsync(request.RunId, cancellationToken);

        Assert.True(paused.Quiescent);
        Assert.Empty(paused.Processes);
        Assert.Equal(ReconciliationAction.ResumeDispatch, resumed.Reconciliation.Action);
        Assert.Equal("resumed", resumed.Record!.Kind);
    }

    [Fact]
    public async Task Process_registered_before_restart_is_unknown_and_blocks_pause()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var originalProcesses = new ProcessControl();
        using var original = CreateCoordinator(directories.Path, originalProcesses);
        var (request, _) = await original.PrepareWorkerAsync(Plan(repository, directories.Path, "run-restart"), cancellationToken);
        await original.AcceptAsync(Accepted(request), cancellationToken);
        var launch = OperatingSystem.IsWindows()
            ? new ProcessLaunch("ping.exe", ["127.0.0.1", "-n", "30"])
            : new ProcessLaunch("/bin/sleep", ["30"]);
        var child = await original.StartTaskProcessAsync(request.RunId, launch, cancellationToken);

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
    public void Host_receipts_must_bind_schema_capability_and_immutable_request_identity()
    {
        var request = Request("run-bind");
        var accepted = Accepted(request);
        WorkerEnvelope.ValidateAccepted(request, accepted);

        Assert.Throws<WorkerEnvelopeException>(() =>
            WorkerEnvelope.ValidateAccepted(request, accepted with { HostCapability = "fake" }));
        var wrong = Completion(request, accepted, (new string('b', 40), new string('c', 40))) with { RequestIdentity = new string('0', 64) };
        Assert.Throws<WorkerEnvelopeException>(() => WorkerEnvelope.ValidateCompletion(request, accepted, wrong));
    }

    [Fact]
    public void Request_rejects_absolute_and_traversing_allowed_paths()
    {
        var plan = new FrozenWorkerPlan("run", "plan", new string('a', 40), "owner/repo", "/parent", "/worker", "/result", "token", ["../escape"], []);
        Assert.Throws<WorkerEnvelopeException>(() => WorkerEnvelope.PrepareWorker(plan));
    }

    private static ExecutionCoordinator CreateCoordinator(string root, ProcessControl processes, string store = "store") =>
        new(new RunStore(System.IO.Path.Combine(root, store)), new WorktreeManager(), processes);

    private static FrozenWorkerPlan Plan(GitFixture repository, string root, string runId) =>
        new(runId, "plan-v1", repository.Head, "owner/repo", System.IO.Path.Combine(root, "parent"), repository.Path,
            System.IO.Path.Combine(root, "result"), $"writer-{runId}", ["tracked.txt", "allowed"], ["allowed/excluded.txt"]);

    private static AgentRequest Request(string runId)
    {
        var plan = new FrozenWorkerPlan(runId, "plan-v1", new string('a', 40), "owner/repo", "/parent", "/worker", "/result", "token", ["src"], []);
        return WorkerEnvelope.PrepareWorker(plan);
    }

    private static AgentAcceptedReceipt Accepted(AgentRequest request) =>
        new(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity, request.PlanIdentity, request.BaseCommit,
            request.Worktree, $"host-{request.RunId}", "child", DateTimeOffset.UtcNow, WorkerEnvelope.SupportedHostCapability);

    private static AgentCompletionReceipt Completion(
        AgentRequest request, AgentAcceptedReceipt accepted, (string Head, string Tree) revision) =>
        new(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity, accepted.HostRunId, accepted.ChildIdentity,
            "success", request.Worktree, revision.Head, revision.Tree, DateTimeOffset.UtcNow, [], []);

    private static async Task<WorkflowStateName> StateNameAsync(string root, string runId, CancellationToken cancellationToken)
    {
        var recovery = await new RunStore(System.IO.Path.Combine(root, "store")).RecoverAsync(runId, cancellationToken);
        return recovery.Latest?.Kind == "worker-completed" ? WorkflowStateName.LocalComplete : WorkflowStateName.Other;
    }

    private enum WorkflowStateName { Other, LocalComplete }
}
