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
            new RunStore(System.IO.Path.Combine(directories.Path, "store")), new WorktreeManager(), processes,
            new ProvenTestAgentControl());
        var plan = new FrozenWorkerPlan("run-dirty", "plan", repository.Head, "owner/repo",
            System.IO.Path.Combine(directories.Path, "parent"), repository.Path,
            System.IO.Path.Combine(directories.Path, "result"), "writer", ["tracked.txt"], [],
            "test.adapter", "1.0", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);
        var (request, _) = await coordinator.PrepareWorkerAsync(plan, cancellationToken);
        var accepted = new AgentAcceptedReceipt(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity,
            request.PlanIdentity, request.BaseCommit, request.Worktree, request.AdapterId, request.AdapterVersion,
            "runtime", "task", DateTimeOffset.UtcNow,
            request.RequiredCapabilities.Concat([AgentCapability.Pause, AgentCapability.Resume]).Select(capability =>
                new AgentCapabilityEvidence(capability, AgentCapabilityStatus.Supported, "test-interface", "test-proof")).ToArray());
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
