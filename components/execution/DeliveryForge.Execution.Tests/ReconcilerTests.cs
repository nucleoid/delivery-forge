using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Execution.Tests;

public sealed class ReconcilerTests
{
    [Fact]
    public async Task Resume_blocks_new_dirty_or_user_files_observed_after_pause()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        using var directories = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(
            new RunStore(System.IO.Path.Combine(directories.Path, "store")), new WorktreeManager(), processes);
        var plan = new FrozenWorkerPlan("run-dirty", "plan", repository.Head, "owner/repo",
            System.IO.Path.Combine(directories.Path, "parent"), repository.Path,
            System.IO.Path.Combine(directories.Path, "result"), "writer", ["tracked.txt"], []);
        var (request, _) = await coordinator.PrepareWorkerAsync(plan, cancellationToken);
        var accepted = new AgentAcceptedReceipt(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity,
            request.PlanIdentity, request.BaseCommit, request.Worktree, "host", "child", DateTimeOffset.UtcNow,
            WorkerEnvelope.SupportedHostCapability);
        await coordinator.AcceptAsync(accepted, cancellationToken);
        await coordinator.PauseAsync(request.RunId, TimeSpan.FromSeconds(1), cancellationToken);
        await File.WriteAllTextAsync(System.IO.Path.Combine(repository.Path, "user-file.txt"), "preserve", cancellationToken);

        var result = await coordinator.ResumeAsync(request.RunId, cancellationToken);

        Assert.Equal(ReconciliationAction.Blocked, result.Reconciliation.Action);
        Assert.Contains(result.Reconciliation.Reasons, reason => reason.Contains("state", StringComparison.Ordinal) || reason.Contains("files", StringComparison.Ordinal));
        Assert.DoesNotContain(result.GetType().GetProperties(),
            property => property.Name.Contains("Command", StringComparison.OrdinalIgnoreCase));
    }
}
