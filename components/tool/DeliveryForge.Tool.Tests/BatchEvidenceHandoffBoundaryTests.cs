using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Tool.Tests;

// These controls exercise actual containing Task retention and private-proof
// refusal. They do not create an installed gate/reviewer issuer or certify a
// production batch; the real installed two-node acceptance remains required.
public sealed class BatchEvidenceHandoffBoundaryTests
{
    sealed class UncalledPort : IHostToolSessionPort
    {
        public AgentAdapterProfile Profile => new("codex.cli", "managed-boundary-only", []);
        public HostObservationRealm Realm => HostObservationRealm.ContractFixture;
        public Task<AgentAcceptedReceipt> StartAsync(AgentRequest request, TimeSpan remaining, CancellationToken token) => throw new InvalidOperationException("No native start belongs to this boundary control.");
        public Task<AgentCompletionReceipt> WaitAsync(AgentRunBinding binding, TimeSpan remaining, CancellationToken token) => throw new InvalidOperationException("No native wait belongs to this boundary control.");
        public Task<AgentControlResult> ControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan remaining, CancellationToken token) => throw new InvalidOperationException("No native control belongs to this boundary control.");
    }
    static ExecutionCoordinator IdleCoordinator(ProcessControl processes) => new(
        new RunStore(Path.Combine(Path.GetTempPath(), "forge-batch-evidence-boundary-" + Guid.NewGuid().ToString("N"))),
        new WorktreeManager(), processes);

    [Theory]
    [InlineData("missing")]
    [InlineData("copied-pass-phases")]
    [InlineData("completed-boolean-task")]
    public async Task Result_labels_and_completed_boolean_task_cannot_issue_private_pre_disposition_evidence(string supplied)
    {
        await using var processes = new ProcessControl();
        using var coordinator = IdleCoordinator(processes);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var bridge = new OwnerHostBridge(coordinator, new CodexHostAdapter(new UncalledPort()));
        var frame = new HostBridgeFrame("unissued-batch-evidence", original, coordinator, () => false, false);
        if (supplied == "copied-pass-phases")
        {
            frame.Worker.Start(original, () => Task.FromResult(new ToolResult("COMPLETE", "DATA_ONLY", "Copied success is not an issuer.")));
            frame.Quality.Start(original, () => Task.FromResult(new ToolResult("PASS", "DATA_ONLY", "Copied quality is not a protected comparison.")));
            frame.IndependentReview.Start(original, () => Task.FromResult(new ToolResult("PASS", "DATA_ONLY", "Copied review is not an accepted native revision.")));
            _ = await frame.Worker.ObserveAsync(original);
            _ = await frame.Quality.ObserveAsync(original);
            _ = await frame.IndependentReview.ObserveAsync(original);
        }
        if (supplied == "completed-boolean-task")
        {
            var actual = Task.FromResult(true);
            frame.BatchEvidenceCapture.Start(original, () => actual);
            Assert.True(await frame.BatchEvidenceCapture.ObserveAsync(original));
            Assert.Same(actual, frame.BatchEvidenceCapture.Actual);
        }
        var error = Assert.Throws<IOException>(() => bridge.RequireCapturedBatchEvidence(frame, original));
        Assert.Contains("Private pre-disposition batch evidence was not captured", error.Message, StringComparison.Ordinal);
        Assert.True(frame.CloseWhenSettled());
    }

    [Fact]
    public async Task Actual_pending_capture_keeps_same_frame_and_original_retained_until_its_task_settles()
    {
        await using var processes = new ProcessControl();
        using var coordinator = IdleCoordinator(processes);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("pending-batch-evidence", original, coordinator, () => false, false);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        frame.BatchEvidenceCapture.Start(original, () => pending.Task);
        Assert.True(frame.Unknown);
        Assert.False(OwnerHostBridge.SettledBatchEvidenceFailure(frame));
        Assert.False(frame.CloseWhenSettled());
        Assert.Same(pending.Task, frame.BatchEvidenceCapture.Actual);
        pending.SetResult(true);
        Assert.True(await frame.BatchEvidenceCapture.ObserveAsync(original));
        Assert.True(frame.CloseWhenSettled());
        // Settlement permits retirement of this inert control, but cannot mint
        // the bridge's private same-frame actual installed evidence object.
        var bridge = new OwnerHostBridge(coordinator, new CodexHostAdapter(new UncalledPort()));
        Assert.Throws<IOException>(() => bridge.RequireCapturedBatchEvidence(frame, original));
        original.Check();
    }

    [Fact]
    public async Task Actual_capture_failure_retains_its_task_and_primary_without_creating_success()
    {
        await using var processes = new ProcessControl();
        using var coordinator = IdleCoordinator(processes);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("failed-batch-evidence", original, coordinator, () => false, false);
        var failure = new IOException("Actual evidence capture failed.");
        var actual = Task.FromException<bool>(failure);
        frame.BatchEvidenceCapture.Start(original, () => actual);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => frame.BatchEvidenceCapture.ObserveAsync(original)));
        Assert.Same(actual, frame.BatchEvidenceCapture.Actual);
        Assert.Same(failure, frame.BatchEvidenceCapture.Primary);
        Assert.True(OwnerHostBridge.SettledBatchEvidenceFailure(frame));
        var bridge = new OwnerHostBridge(coordinator, new CodexHostAdapter(new UncalledPort()));
        Assert.Throws<IOException>(() => bridge.RequireCapturedBatchEvidence(frame, original));
        Assert.True(frame.CloseWhenSettled());
    }

    [Theory]
    [InlineData("settled")]
    [InlineData("pending-review")]
    [InlineData("explicit-unknown")]
    public async Task Completed_review_failure_requires_actual_phase_settlement_without_erasing_primary(string state)
    {
        await using var processes = new ProcessControl();
        using var coordinator = IdleCoordinator(processes);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("completed-review-failure", original, coordinator, () => false, false);
        var failure = new IOException("Actual review preparation fixture failure.");
        frame.Completion = new(WorkerEnvelope.SchemaVersion, frame.RunId, new string('a', 64), "codex.cli", "fixture", "run", "task", "success",
            Path.GetFullPath("."), new string('b', 40), new string('c', 40), DateTimeOffset.UtcNow, [], []);
        frame.OwnerCompleted = true;
        frame.Primary = failure;
        frame.Worker.Start(original, () => Task.FromResult(ToolResult.Incomplete("HOST_OPERATION_RETAINED", "Fixture actual failed pipeline.")));
        _ = await frame.Worker.ObserveAsync(original);
        var pending = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actual = state == "pending-review" ? pending.Task : Task.FromException<ToolResult>(failure);
        frame.Review.Start(original, () => actual);
        if (state != "pending-review")
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => frame.Review.ObserveAsync(original)));
        if (state == "explicit-unknown") original.RetainUnknown();
        Assert.Equal(state == "settled", OwnerHostBridge.SettledCompletedPipelineFailure(frame));
        Assert.Same(failure, frame.Primary); Assert.Same(actual, frame.Review.Actual);
        // DATA/task guard only: no native quiescence, writer, reservation or authority is supplied.
        if (state == "pending-review")
        {
            Assert.False(frame.CloseWhenSettled());
            pending.SetException(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => frame.Review.ObserveAsync(original)));
            Assert.True(frame.CloseWhenSettled());
        }
    }
}
