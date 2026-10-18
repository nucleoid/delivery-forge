using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;

public sealed class HostBridgeTests
{
    sealed class Port : IHostToolSessionPort
    {
        public AgentAdapterProfile Profile { get; set; } = new("codex.cli", "fixture-v1", new[] { AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Stop }.Select(x => new AgentCapabilityEvidence(x, AgentCapabilityStatus.Supported, "contract fixture port", "fixture:explicit-only")).ToArray());
        public HostObservationRealm Realm { get; set; } = HostObservationRealm.ContractFixture;
        public int Starts, Controls, Unstarted; public bool ThrowOnStart, ForeignAccepted; public TaskCompletionSource<AgentAcceptedReceipt>? PendingStart;
        public Task<AgentAcceptedReceipt> StartAsync(AgentRequest r, TimeSpan left, CancellationToken token) { Starts++; if (PendingStart is not null) return PendingStart.Task; if (ThrowOnStart) throw new IOException("sync original invocation failure"); return Task.FromResult(new AgentAcceptedReceipt(r.SchemaVersion, r.RunId, ForeignAccepted ? new string('0', 64) : r.RequestIdentity, r.PlanIdentity, r.BaseCommit, r.Worktree, r.AdapterId, r.AdapterVersion, "fixture-run", "fixture-task", DateTimeOffset.UtcNow, Profile.Capabilities)); }
        public Task<AgentCompletionReceipt> WaitAsync(AgentRunBinding b, TimeSpan left, CancellationToken token) => throw new IOException("No completion fixture supplied; must remain incomplete.");
        public Task<AgentControlResult> ObserveUnstartedAsync(AgentRunBinding b, TimeSpan left, CancellationToken token) { Unstarted++; return Task.FromResult(new AgentControlResult(b, AgentControlAction.Stop, AgentActivityState.Quiescent, true, "fixture:actual-observation", "DATA fixture only")); }
        public Task<AgentControlResult> ControlAsync(AgentRunBinding b, AgentControlAction a, TimeSpan left, CancellationToken token) { Controls++; return Task.FromResult(new AgentControlResult(b, a, AgentActivityState.Quiescent, true, "fixture:control", "contract fixture only")); }
    }
    static AgentRequest Request() => WorkerEnvelope.PrepareWorker(new("fixture-run", "sha256:" + new string('1', 64), new string('a', 40), "fixture/repo", Path.GetFullPath("."), Path.Combine(Path.GetTempPath(), "assigned-fixture"), Path.Combine(Path.GetTempPath(), "result-fixture"), "fixture-token", ["source.cs"], [], "codex.cli", "fixture-v1", [AgentCapability.Launch, AgentCapability.CompletionEvidence]));
    [Fact]
    public async Task Genuine_port_start_then_exact_control_retains_actual_task_and_binding()
    {
        var port = new Port(); var adapter = new CodexHostAdapter(port); var r = Request(); var a = await adapter.StartAsync(r, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); Assert.Equal(r.RequestIdentity, a.RequestIdentity); Assert.Single(adapter.ActualCalls);
        var b = new AgentRunBinding(a.AdapterId, a.AdapterVersion, a.RunId, a.RequestIdentity, a.RuntimeRunIdentity, a.RuntimeTaskIdentity); var c = await adapter.ControlAsync(b, AgentControlAction.Stop, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); Assert.Equal(b, c.Binding); Assert.Equal(1, port.Controls); Assert.Equal(HostObservationRealm.ContractFixture, adapter.Realm);
        await Assert.ThrowsAsync<WorkerEnvelopeException>(() => adapter.ControlAsync(b with { RuntimeTaskIdentity = "foreign" }, AgentControlAction.Stop, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)); Assert.Equal(1, port.Controls);
    }
    [Fact] public async Task Sync_throw_does_not_become_known_quiescence() { var port = new Port { ThrowOnStart = true }; var adapter = new CodexHostAdapter(port); await Assert.ThrowsAsync<IOException>(() => adapter.StartAsync(Request(), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)); Assert.True(adapter.HasUnknownCalls("fixture-run")); Assert.False(adapter.ActualCalls.Single().InvocationReturned); }
    [Fact] public async Task Foreign_accepted_receipt_is_rejected_and_original_start_retained() { var port = new Port { ForeignAccepted = true }; var adapter = new CodexHostAdapter(port); await Assert.ThrowsAsync<WorkerEnvelopeException>(() => adapter.StartAsync(Request(), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)); Assert.True(adapter.HasUnknownCalls("fixture-run")); Assert.Equal(1, port.Starts); }
    [Fact] public async Task Revoked_profile_refuses_before_host_call() { var port = new Port(); var adapter = new CodexHostAdapter(port); port.Profile = port.Profile with { AdapterVersion = "changed" }; await Assert.ThrowsAsync<WorkerEnvelopeException>(() => adapter.StartAsync(Request(), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)); Assert.Equal(0, port.Starts); }
    [Fact]
    public async Task Genuine_fresh_unstarted_port_observation_then_accepted_identity_refuses()
    {
        var port = new Port(); var adapter = new CodexHostAdapter(port); var r = Request(); var b = new AgentRunBinding(r.AdapterId, r.AdapterVersion, r.RunId, r.RequestIdentity, "", "");
        var observed = await adapter.ObserveUnstartedAsync(b, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); Assert.Equal(AgentActivityState.Quiescent, observed.Activity); Assert.Equal(1, port.Unstarted);
        _ = await adapter.StartAsync(r, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); await Assert.ThrowsAsync<WorkerEnvelopeException>(() => adapter.ObserveUnstartedAsync(b, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)); Assert.Equal(1, port.Unstarted);
    }
    [Fact]
    public async Task Genuine_controlled_pending_host_task_is_retained_after_cancellation_without_retry()
    {
        var port = new Port(); var adapter = new CodexHostAdapter(port); var r = Request(); var a = await adapter.StartAsync(r, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); Assert.Equal(r.RequestIdentity, a.RequestIdentity);
        var pending = new TaskCompletionSource<AgentAcceptedReceipt>(TaskCreationOptions.RunContinuationsAsynchronously); var nextPort = new Port { PendingStart = pending }; var nextAdapter = new CodexHostAdapter(nextPort); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var actual = nextAdapter.StartAsync(r, TimeSpan.FromSeconds(5), cancel.Token); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actual);
        Assert.Same(pending.Task, nextAdapter.ActualCalls.Single().ActualTask); Assert.True(nextAdapter.HasUnknownCalls(r.RunId)); Assert.False(pending.Task.IsCompleted);
        await Assert.ThrowsAsync<WorkerEnvelopeException>(() => nextAdapter.StartAsync(r, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)); Assert.Equal(1, nextPort.Starts);
        pending.SetResult(a); Assert.True(nextAdapter.HasUnknownCalls(r.RunId)); // late receipt cannot fabricate owner acceptance.
    }
    [Fact]
    public async Task Invalid_deadline_or_precancel_refuses_before_actual_start()
    {
        var port = new Port(); var adapter = new CodexHostAdapter(port); await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => adapter.StartAsync(Request(), TimeSpan.Zero, TestContext.Current.CancellationToken)); using var c = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); c.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.StartAsync(Request(), TimeSpan.FromSeconds(1), c.Token)); Assert.Equal(0, port.Starts); Assert.Empty(adapter.ActualCalls);
    }
    [Theory]
    [InlineData("claude-code.cli")]
    [InlineData("pi.cli")]
    public async Task Each_named_adapter_calls_its_actual_typed_port_and_keeps_fixture_realm(string id)
    {
        var port = new Port(); port.Profile = port.Profile with { AdapterId = id }; NamedHostAdapter adapter = id == "pi.cli" ? new PiHostAdapter(port) : new ClaudeHostAdapter(port);
        var plan = new FrozenWorkerPlan("fixture-run", "sha256:" + new string('1', 64), new string('a', 40), "fixture/repo", Path.GetFullPath("."), Path.Combine(Path.GetTempPath(), "assigned-fixture"), Path.Combine(Path.GetTempPath(), "result-fixture"), "fixture-token", ["source.cs"], [], id, "fixture-v1", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);
        var r = WorkerEnvelope.PrepareWorker(plan); var accepted = await adapter.StartAsync(r, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); Assert.Equal(id, accepted.AdapterId); Assert.Equal(1, port.Starts); Assert.Equal(HostObservationRealm.ContractFixture, adapter.Realm);
    }
    [Fact]
    public async Task Realm_drift_and_unsupported_control_refuse_without_fallback()
    {
        var port = new Port(); var adapter = new CodexHostAdapter(port); port.Realm = HostObservationRealm.ActualHost; await Assert.ThrowsAsync<WorkerEnvelopeException>(() => adapter.StartAsync(Request(), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)); Assert.Equal(0, port.Starts);
        port.Realm = HostObservationRealm.ContractFixture; var a = await adapter.StartAsync(Request(), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); var b = new AgentRunBinding(a.AdapterId, a.AdapterVersion, a.RunId, a.RequestIdentity, a.RuntimeRunIdentity, a.RuntimeTaskIdentity);
        var result = await adapter.ControlAsync(b, AgentControlAction.Pause, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); Assert.False(result.CapabilitySupported); Assert.Equal(AgentActivityState.Unknown, result.Activity); Assert.Equal(0, port.Controls);
    }
    [Fact] public void Wrong_named_adapter_cannot_borrow_port() { var port = new Port(); Assert.Throws<ToolUsageException>(() => new PiHostAdapter(port)); Assert.Throws<ToolUsageException>(() => new ClaudeHostAdapter(port)); }
    [Fact]
    public async Task Bound_parent_refusal_precedes_actual_start_enrollment_and_native_port()
    {
        var port = new Port(); var adapter = new CodexHostAdapter(port); var gate = new ParentEffectAdmission(new object(), _ => false, _ => { }); adapter.BindEffectAdmission(gate);
        await Assert.ThrowsAsync<DeliveryForge.Coordination.CoordinationUncertainException>(() => adapter.StartAsync(Request(), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(0, port.Starts); Assert.Empty(adapter.ActualCalls); Assert.Equal(0, gate.EnteredCount); Assert.False(adapter.HasUnknownCalls(Request().RunId));
    }
    [Fact]
    public async Task Bound_parent_revocation_stops_resume_before_port_but_preserves_original_stop_control()
    {
        var current = true; var port = new Port(); port.Profile = port.Profile with { Capabilities = port.Profile.Capabilities.Concat([new AgentCapabilityEvidence(AgentCapability.Resume, AgentCapabilityStatus.Supported, "explicit fixture resume", "fixture:control")]).ToArray() };
        var adapter = new CodexHostAdapter(port); var gate = new ParentEffectAdmission(new object(), _ => current, _ => current = false); adapter.BindEffectAdmission(gate);
        var request = Request(); var accepted = await adapter.StartAsync(request, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var binding = new AgentRunBinding(accepted.AdapterId, accepted.AdapterVersion, accepted.RunId, accepted.RequestIdentity, accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity);
        current = false;
        await Assert.ThrowsAsync<DeliveryForge.Coordination.CoordinationUncertainException>(() => adapter.ControlAsync(binding, AgentControlAction.Resume, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(0, port.Controls);
        var stopped = await adapter.ControlAsync(binding, AgentControlAction.Stop, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(AgentActivityState.Quiescent, stopped.Activity); Assert.Equal(1, port.Controls); Assert.Equal(1, gate.EnteredCount);
        adapter.MarkOwnerCompleted(binding); Assert.False(adapter.HasUnknownCalls(request.RunId));
    }

}
