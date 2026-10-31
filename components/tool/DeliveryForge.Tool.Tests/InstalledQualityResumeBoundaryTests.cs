using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;

public sealed class InstalledQualityResumeBoundaryTests
{
    sealed class IdlePort : IHostToolSessionPort
    {
        public AgentAdapterProfile Profile { get; } = new("codex.cli", "resume-unit", []);
        public HostObservationRealm Realm => HostObservationRealm.ContractFixture;
        public Task<AgentAcceptedReceipt> StartAsync(AgentRequest request, TimeSpan remaining, CancellationToken token) => throw new InvalidOperationException("No actual launch belongs to this boundary test.");
        public Task<AgentCompletionReceipt> WaitAsync(AgentRunBinding binding, TimeSpan remaining, CancellationToken token) => throw new InvalidOperationException("No actual worker belongs to this boundary test.");
        public Task<AgentControlResult> ControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan remaining, CancellationToken token) => throw new InvalidOperationException("No actual control belongs to this boundary test.");
        public Task<AgentControlResult> ObserveUnstartedAsync(AgentRunBinding binding, TimeSpan remaining, CancellationToken token) => throw new InvalidOperationException("No actual owner belongs to this boundary test.");
    }
    [Fact]
    public async Task New_lifetime_resume_cannot_create_an_owner_from_a_run_id()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "forge-quality-resume-" + Guid.NewGuid().ToString("N"))), new DeliveryForge.Execution.Git.WorktreeManager(), processes);
        var bridge = new OwnerHostBridge(coordinator, new CodexHostAdapter(new IdlePort()));
        var result = await bridge.ResumeAsync("run-" + new string('a', 32), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal("ORIGINAL_OWNER_REQUIRED", result.Code);
        Assert.Empty(bridge.RetainedFrames);
    }
    [Fact]
    public async Task Pause_binding_joins_actual_task_result_and_rejects_equal_copies()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "forge-pause-unit-" + Guid.NewGuid().ToString("N"))), new DeliveryForge.Execution.Git.WorktreeManager(), processes);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("unit-pause", life, coordinator, () => false, false);
        var record = new StoredRunRecord("unit-pause", 1, "paused", DateTimeOffset.UtcNow, System.Text.Json.JsonSerializer.SerializeToElement(new { UnitOnly = true }), "not-an-owner-receipt", new string('a', 64));
        var pause = new QuiescenceResult(true, [], record);
        Assert.Throws<WorkerEnvelopeException>(() => frame.RetainActualPause(pause));
        var actual = Task.FromResult(pause); Assert.Same(pause, await frame.Enter(() => actual));
        Assert.Throws<WorkerEnvelopeException>(() => frame.RetainActualPause(pause with { }));
        frame.RetainActualPause(pause);
        lock (frame.ReconciliationGate)
        {
            var first = frame.ControlForActualPause(pause);
            Assert.Same(first, frame.ControlForActualPause(pause));
            Assert.Throws<WorkerEnvelopeException>(() => frame.ControlForActualPause(pause with { }));
        }
        Assert.True(frame.CloseWhenSettled());
        // Deliberately DATA-only: this frame never launches a worker, quality
        // producer or native owner. It tests retained Task result identity only.
    }
    [Fact]
    public async Task Pending_phase_prevents_original_disposal_until_actual_settlement()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "forge-phase-unit-" + Guid.NewGuid().ToString("N"))), new DeliveryForge.Execution.Git.WorktreeManager(), processes);
        var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("unit-phase", life, coordinator, () => false, false);
        var pending = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        frame.EnterRoute(); frame.Worker.Start(life, () => pending.Task); frame.LeaveRoute();
        life.Dispose(); Assert.False(frame.CloseWhenSettled()); Assert.True(life.HasRetainedUnknown);
        Assert.Same(pending.Task, frame.Worker.Actual); Assert.False(pending.Task.IsCompleted);
        var observed = frame.Worker.ObserveAsync(life); var nonAuthority = ToolResult.Incomplete("UNIT_ONLY", "No runtime authority.");
        pending.SetResult(nonAuthority); Assert.Same(nonAuthority, await observed);
        Assert.True(frame.CloseWhenSettled()); Assert.Throws<ObjectDisposedException>(life.Check);
    }
    [Fact]
    public async Task Actual_inner_frame_fault_is_consumed_before_cancelled_original_post()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "forge-frame-fault-" + Guid.NewGuid().ToString("N"))), new DeliveryForge.Execution.Git.WorktreeManager(), processes);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), cancel.Token);
        var frame = new HostBridgeFrame("unit-terminal-fault", life, coordinator, () => false, false);
        var primary = new IOException("actual inner operation fault"); var actual = Task.FromException<int>(primary);
        var error = await Assert.ThrowsAsync<AggregateException>(() => frame.Enter(() => { cancel.Cancel(); return actual; }));
        Assert.Contains(primary, error.InnerExceptions); Assert.Contains(error.InnerExceptions, e => e is OperationCanceledException);
        Assert.Same(actual, Assert.Single(frame.EnteredTasks)); Assert.True(actual.IsFaulted);
    }
    [Fact]
    public async Task Actual_inner_task_cancellation_keeps_its_token_before_original_post()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "forge-frame-cancel-" + Guid.NewGuid().ToString("N"))), new DeliveryForge.Execution.Git.WorktreeManager(), processes);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var child = new CancellationTokenSource(); child.Cancel();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), cancel.Token);
        var frame = new HostBridgeFrame("unit-terminal-cancel", life, coordinator, () => false, false);
        var actual = Task.FromCanceled<int>(child.Token);
        var error = await Assert.ThrowsAsync<AggregateException>(() => frame.Enter(() => { cancel.Cancel(); return actual; }));
        Assert.Contains(error.InnerExceptions, e => e is OperationCanceledException canceled && canceled.CancellationToken == child.Token);
        Assert.Contains(error.InnerExceptions, e => e is OperationCanceledException canceled && canceled.CancellationToken == life.Token);
        Assert.Same(actual, Assert.Single(frame.EnteredTasks));
    }
    [Fact]
    public async Task Actual_inner_fault_is_consumed_before_genuinely_expired_restricted_work_post()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "forge-frame-expiry-" + Guid.NewGuid().ToString("N"))), new DeliveryForge.Execution.Git.WorktreeManager(), processes);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("unit-terminal-expiry", life, coordinator, () => false, false);
        var primary = new IOException("actual terminal failure at original endpoint"); var actual = Task.FromException<int>(primary);
        var error = await Assert.ThrowsAsync<AggregateException>(() => frame.Enter(() =>
        {
            // Restrict the real original, never inject/advance/reset a clock.
            // The existing API commits the shorter endpoint before refusing it.
            Assert.Throws<TimeoutException>(() => life.Restrict(TimeSpan.FromTicks(1)));
            return actual;
        }));
        Assert.Contains(primary, error.InnerExceptions); Assert.Contains(error.InnerExceptions, e => e is TimeoutException);
        Assert.Same(actual, Assert.Single(frame.EnteredTasks));
    }
}
