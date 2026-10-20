using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
namespace DeliveryForge.Tool.Tests;

public sealed class IndependentReviewerBoundaryTests
{
    [Fact]
    public async Task Pending_independent_reviewer_task_cannot_be_hidden_by_completed_source_preparation()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "forge-independent-review-boundary-" + Guid.NewGuid().ToString("N"))), new WorktreeManager(), processes);
        var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("managed-review-boundary", life, coordinator, () => false, false);
        var pending = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        frame.EnterRoute();
        frame.Review.Start(life, () => Task.FromResult(ToolResult.Incomplete("SOURCE_ONLY", "No reviewer authority.")));
        frame.IndependentReview.Start(life, () => pending.Task); frame.LeaveRoute();
        life.Dispose();
        Assert.False(frame.CloseWhenSettled()); Assert.Same(pending.Task, frame.IndependentReview.Actual);
        var observed = frame.IndependentReview.ObserveAsync(life);
        pending.SetResult(ToolResult.Incomplete("MANAGED_ONLY", "No native reviewer was launched."));
        await observed;
        Assert.True(frame.CloseWhenSettled()); Assert.Throws<ObjectDisposedException>(life.Check);
    }
}
