using DeliveryForge.Coordination;
using DeliveryForge.Planning;
using System.Collections.Concurrent;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
namespace DeliveryForge.Tool;

public enum HostObservationRealm { ContractFixture, ActualHost }
// Implemented by the skill/embedding host using its ACTUAL exposed tools. No argv,
// executable launch, token, paid fallback or serialized capability constructor exists here.
public interface IOriginalBoundHostPort { void BindOriginal(ToolLifetime actualOriginal); }
public interface IHostToolSessionPort
{
    AgentAdapterProfile Profile { get; }
    HostObservationRealm Realm { get; }
    Task<AgentAcceptedReceipt> StartAsync(AgentRequest request, TimeSpan remaining, CancellationToken cancellationToken);
    Task<AgentCompletionReceipt> WaitAsync(AgentRunBinding binding, TimeSpan remaining, CancellationToken cancellationToken);
    Task<AgentControlResult> ControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan remaining, CancellationToken cancellationToken);
    Task<AgentControlResult> ObserveUnstartedAsync(AgentRunBinding binding, TimeSpan remaining, CancellationToken cancellationToken) => Task.FromResult(new AgentControlResult(binding, AgentControlAction.Stop, AgentActivityState.Unknown, false, "", "INCOMPLETE: actual host has no fresh unstarted observation."));
}
public abstract class NamedHostAdapter : IHostToolSessionPort, IAgentControlPort
{
    readonly IHostToolSessionPort tools;
    readonly AgentAdapterProfile profile; readonly HostObservationRealm realm;
    readonly List<HostPortCall> calls = [];
    ICoordinatedEffectAdmission? effectAdmission;
    internal void BindEffectAdmission(ICoordinatedEffectAdmission actual)
    {
        lock (calls) { if (effectAdmission is not null || calls.Count != 0) throw new WorkerEnvelopeException("Original actual host effect admission binds once before calls."); effectAdmission = actual; }
    }
    public sealed class HostPortCall
    {
        internal HostPortCall(string runId, string kind) { RunId = runId; Kind = kind; }
        public string RunId { get; }
        public string Kind { get; }
        bool entered, returned; Task? actualTask; Exception? failure;
        public bool Entered { get => Volatile.Read(ref entered); internal set => Volatile.Write(ref entered, value); }
        public bool InvocationReturned { get => Volatile.Read(ref returned); internal set => Volatile.Write(ref returned, value); }
        public Task? ActualTask { get => Volatile.Read(ref actualTask); internal set => Volatile.Write(ref actualTask, value); }
        public Exception? InvocationFailure { get => Volatile.Read(ref failure); internal set => Volatile.Write(ref failure, value); }
        public bool Unknown => Entered && (!InvocationReturned || ActualTask is null || !ActualTask.IsCompleted);
    }
    public IReadOnlyList<HostPortCall> ActualCalls { get { lock (calls) return calls.ToArray(); } }
    public bool HasUnknownCalls(string runId) => unsettledStarts.ContainsKey(runId) || ActualCalls.Any(x => x.RunId == runId && x.Unknown);
    internal string? CompletedImplementationFamily(AgentCompletionReceipt completion) =>
        realm == HostObservationRealm.ActualHost && tools is DeliveryForge.Tool.Codex.CodexAppServerPort codex && ObserveBoundNativeQuiescence(completion.RunId)
            ? codex.CompletedImplementationFamily(completion) : null;
    internal bool ObserveBoundNativeQuiescence(string runId)
    {
        // Only this closed internal concrete fixture can supply genuine native
        // ownership in fixture realm. Arbitrary fixture ports still refuse.
        if (realm == HostObservationRealm.ContractFixture)
            return tools is ParentNativeContractPort fixture && fixture.ObserveOriginalSettled(runId);
        if (realm != HostObservationRealm.ActualHost) return false;
        DeliveryForge.Evidence.OwnedDuplexProcess? process; IReadOnlyList<Task> originals;
        switch (tools)
        {
            case DeliveryForge.Tool.Codex.CodexAppServerPort codex: process = codex.OriginalProcess; originals = codex.OriginalTasks; break;
            case DeliveryForge.Tool.Claude.ClaudeStreamPort claude: process = claude.OriginalProcess; originals = claude.OriginalTasks; break;
            case DeliveryForge.Tool.Pi.PiRpcPort pi: if (pi.ActualOperations.Any(row => row.Unknown)) return false; process = pi.OriginalProcess; originals = pi.ActualOperations.Where(row => row.Task is not null).Select(row => row.Task!).ToArray(); break;
            default: return false;
        }
        if (process is null) return !ActualCalls.Any(call => call.RunId == runId && call.Kind == "Start");
        var actual = process.OriginalRetirementTask;
        return actual is { IsCompletedSuccessfully: true } && actual.Result.KnownClosed && !process.HasUnknownIo && originals.All(task => task.IsCompleted);
    }
    internal void MarkOwnerCompleted(AgentRunBinding binding) { RequireBinding(binding); unsettledStarts.TryRemove(binding.RunId, out _); }
    static void RequireRemaining(TimeSpan remaining, CancellationToken token) { token.ThrowIfCancellationRequested(); if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromSeconds(1200)) throw new ArgumentOutOfRangeException(nameof(remaining)); }
    async Task<T> Actual<T>(string runId, string kind, TimeSpan remaining, CancellationToken token, Func<Task<T>> invoke)
    {
        RequireRemaining(remaining, token);
        var call = new HostPortCall(runId, kind);
        lock (calls) { if (calls.Count >= 512) throw new IOException("Finite actual host-call ledger exhausted."); calls.Add(call); }
        call.Entered = true;
        try { call.ActualTask = invoke(); call.InvocationReturned = true; if (call.ActualTask is not Task<T> task) throw new IOException("Actual host invocation returned no typed task."); return await task.WaitAsync(remaining, token).ConfigureAwait(false); }
        catch (Exception error) { call.InvocationFailure = error; throw; }
    }
    readonly ConcurrentDictionary<string, AgentRunBinding> actualBindings = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, AgentRequest> unsettledStarts = new(StringComparer.Ordinal);
    protected NamedHostAdapter(string id, IHostToolSessionPort tools)
    {
        this.tools = tools ?? throw new ArgumentNullException(nameof(tools));
        var observed = tools.Profile; realm = tools.Realm; if (!Enum.IsDefined(realm)) throw new ToolUsageException("Unknown host observation realm.");
        if (observed.AdapterId != id || string.IsNullOrWhiteSpace(observed.AdapterVersion) || observed.AdapterVersion.Length > 256 || observed.Capabilities.Count > 6 || observed.Capabilities.Select(x => x.Capability).Distinct().Count() != observed.Capabilities.Count) throw new ToolUsageException("Host tools do not match the selected finite adapter/version/profile.");
        foreach (var row in observed.Capabilities) if (!Enum.IsDefined(row.Capability) || !Enum.IsDefined(row.Status) || row.Interface is null || row.Interface.Length > 2048 || row.EvidenceReference is null || row.EvidenceReference.Length > 4096) throw new ToolUsageException("Host profile row exceeds exact finite profile.");
        profile = new(observed.AdapterId, observed.AdapterVersion, Array.AsReadOnly(observed.Capabilities.ToArray()));
    }
    public AgentAdapterProfile Profile => profile;
    internal void BindOriginal(ToolLifetime original) { if (tools is IOriginalBoundHostPort bounded) bounded.BindOriginal(original); }
    internal void BindInstructions(HostInstructionContext context) { if (tools is IHostInstructionPort selected) selected.BindInstructions(context); else if (realm == HostObservationRealm.ActualHost) throw new WorkerEnvelopeException("Actual host lacks explicit parent instruction binding."); }
    void FreshProfile() { var actual = tools.Profile; if (tools.Realm != realm || actual.AdapterId != profile.AdapterId || actual.AdapterVersion != profile.AdapterVersion || !actual.Capabilities.SequenceEqual(profile.Capabilities)) throw new WorkerEnvelopeException("Host profile was revoked or drifted; fresh conformance is required."); }
    public HostObservationRealm Realm => realm;
    public async Task<AgentAcceptedReceipt> StartAsync(AgentRequest request, TimeSpan remaining, CancellationToken cancellationToken)
    {
        RequireRemaining(remaining, cancellationToken); FreshProfile();
        if (request.AdapterId != Profile.AdapterId || request.AdapterVersion != Profile.AdapterVersion) throw new WorkerEnvelopeException("Actual adapter identity changed before dispatch.");
        if (!WorkerEnvelope.AssessCapabilities(Profile, request.RequiredCapabilities).Complete) throw new WorkerEnvelopeException("INCOMPLETE: actual host conformance does not support required dispatch capabilities.");
        Task<AgentAcceptedReceipt> InvokeStart()
        {
            if (!unsettledStarts.TryAdd(request.RunId, request)) throw new WorkerEnvelopeException("Actual dispatch already entered; unresolved starts cannot be replayed.");
            return Actual(request.RunId, "Start", remaining, cancellationToken, () => tools.StartAsync(request, remaining, cancellationToken));
        }
        var accepted = await (effectAdmission is null ? InvokeStart() : effectAdmission.EnterAsync(request.RunId, InvokeStart)).ConfigureAwait(false);
        WorkerEnvelope.ValidateAccepted(request, accepted);
        var binding = new AgentRunBinding(accepted.AdapterId, accepted.AdapterVersion, accepted.RunId, accepted.RequestIdentity, accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity);
        if (!actualBindings.TryAdd(request.RequestIdentity, binding)) throw new WorkerEnvelopeException("Accepted request already has an actual runtime; no retry.");
        return accepted;
    }
    void RequireBinding(AgentRunBinding binding)
    {
        if (!actualBindings.TryGetValue(binding.RequestIdentity, out var actual) || actual != binding) throw new WorkerEnvelopeException("No same-adapter actual accepted runtime binding; saved DATA cannot adopt a session.");
    }
    public Task<AgentCompletionReceipt> WaitAsync(AgentRunBinding binding, TimeSpan remaining, CancellationToken cancellationToken) { RequireBinding(binding); FreshProfile(); return Actual(binding.RunId, "Wait", remaining, cancellationToken, () => tools.WaitAsync(binding, remaining, cancellationToken)); }
    public async Task<AgentControlResult> ObserveUnstartedAsync(AgentRunBinding binding, TimeSpan remaining, CancellationToken cancellationToken = default)
    {
        FreshProfile();
        if (binding.AdapterId != profile.AdapterId || binding.AdapterVersion != profile.AdapterVersion || string.IsNullOrWhiteSpace(binding.RunId) || string.IsNullOrWhiteSpace(binding.RequestIdentity) || binding.RuntimeRunIdentity != "" || binding.RuntimeTaskIdentity != "" || actualBindings.ContainsKey(binding.RequestIdentity)) throw new WorkerEnvelopeException("Fresh unstarted observation is not an accepted-session adoption path.");
        var result = await Actual(binding.RunId, "ObserveUnstarted", remaining, cancellationToken, () => tools.ObserveUnstartedAsync(binding, remaining, cancellationToken)).ConfigureAwait(false);
        if (result.Binding != binding || result.Action != AgentControlAction.Stop || result.Activity == AgentActivityState.Live || result.CapabilitySupported && string.IsNullOrWhiteSpace(result.EvidenceReference)) throw new WorkerEnvelopeException("Unstarted actual observation has foreign identity or no actual evidence.");
        return result; // DATA only: the owner still joins its untouched store/writer/process facts.
    }
    public async Task<AgentControlResult> ControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan remaining, CancellationToken cancellationToken)
    {
        RequireBinding(binding); FreshProfile();
        var required = action switch { AgentControlAction.Pause => AgentCapability.Pause, AgentControlAction.Stop => AgentCapability.Stop, AgentControlAction.Resume => AgentCapability.Resume, _ => throw new ArgumentOutOfRangeException(nameof(action)) };
        if (!WorkerEnvelope.AssessCapabilities(Profile, [required]).Complete) return new(binding, action, AgentActivityState.Unknown, false, "", "INCOMPLETE: actual control capability unavailable.");
        Task<AgentControlResult> InvokeControl() => Actual(binding.RunId, action.ToString(), remaining, cancellationToken, () => tools.ControlAsync(binding, action, remaining, cancellationToken));
        var result = await (action == AgentControlAction.Resume && effectAdmission is not null ? effectAdmission.EnterAsync(binding.RunId, InvokeControl) : InvokeControl()).ConfigureAwait(false);
        if (result.Binding != binding || result.Action != action || !result.CapabilitySupported || string.IsNullOrWhiteSpace(result.EvidenceReference)) throw new WorkerEnvelopeException("Actual control receipt does not bind this runtime/action.");
        return result;
    }
}
public sealed class CodexHostAdapter(IHostToolSessionPort tools) : NamedHostAdapter("codex.cli", tools);
public sealed class ClaudeHostAdapter(IHostToolSessionPort tools) : NamedHostAdapter("claude-code.cli", tools);
public sealed class PiHostAdapter(IHostToolSessionPort tools) : NamedHostAdapter("pi.cli", tools);

// The frame, owner coordinator, every entered actual Task and primary/post remain
// reachable after timeout. No automatic retry, private owner reconstruction or
// coordinator disposal follows an unknown transport operation.
// One actual invocation identity per retained phase. This does not issue owner
// authority, start a clock, retry work or turn a terminal fault into a result.
internal sealed class RetainedHostTask<T>(ToolLifetime original)
{
    readonly object gate = new(); bool entered; Task<T>? actual; Exception? invocationFailure;
    readonly TaskCompletionSource<bool> invocationReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<T>? Actual { get { lock (gate) return actual; } }
    internal bool Started { get { lock (gate) return entered; } }
    internal bool Unknown { get { lock (gate) return entered && (actual is null || !actual.IsCompleted); } }
    internal Exception? Primary { get; private set; }
    internal Exception? Post { get; private set; }
    internal void Start(ToolLifetime life, Func<Task<T>> invoke) => Prepare(life, invoke)?.Invoke();
    internal Action? Prepare(ToolLifetime life, Func<Task<T>> invoke)
    {
        if (!ReferenceEquals(life, original)) throw new WorkerEnvelopeException("Foreign lifetime cannot enter a retained phase.");
        lock (gate) { if (entered) return null; life.Check(); entered = true; }
        var invoked = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref invoked, 1) != 0) throw new WorkerEnvelopeException("Original phase birth cannot replay.");
            // No monitor spans invocation or await. Publication only notifies
            // observers; authority and identity remain on the real returned Task.
            try { var returned = invoke() ?? throw new IOException("Original phase returned no actual Task."); lock (gate) actual = returned; }
            catch (Exception error) { lock (gate) { invocationFailure = error; Primary ??= error; } }
            finally { invocationReturned.TrySetResult(true); }
        };
    }
    internal async Task<T> ObserveAsync(ToolLifetime life)
    {
        if (!ReferenceEquals(life, original)) throw new WorkerEnvelopeException("A new lifetime cannot observe this phase as its own.");
        lock (gate) { if (!entered) throw new WorkerEnvelopeException("No original phase was entered."); }
        T value = default!; Exception? primary = null; Exception? post = null;
        try
        {
            if (!invocationReturned.Task.IsCompleted) _ = await invocationReturned.Task.WaitAsync(life.Remaining).ConfigureAwait(false);
            Task<T>? task; Exception? failure; lock (gate) { task = actual; failure = invocationFailure; }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            if (task is null) throw new IOException("Original phase invocation return is unknown.");
            value = task.IsCompleted ? await task.ConfigureAwait(false) : await task.WaitAsync(life.Remaining).ConfigureAwait(false);
        }
        catch (Exception error) { primary = error; lock (gate) Primary ??= error; }
        try { life.Check(); } catch (Exception error) { post = error; lock (gate) Post ??= error; }
        lock (gate) { primary ??= Primary; post ??= Post; }
        if (primary is not null && post is not null) throw new AggregateException("Retained actual phase and independent original endpoint.", primary, post);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
        return value;
    }
}

public sealed class HostBridgeFrame
{
    internal HostBridgeFrame(string runId, ToolLifetime life, ExecutionCoordinator owner, Func<bool> externalUnknown, bool ownsOriginal) { RunId = runId; Lifetime = life; Owner = owner; ExternalUnknown = externalUnknown; OwnsOriginal = ownsOriginal; Worker = new(life); Quality = new(life); Review = new(life); IndependentReview = new(life); }
    readonly Func<bool> ExternalUnknown; readonly bool OwnsOriginal;
    internal readonly object ReconciliationGate = new();
    internal RetainedHostTask<ToolResult> Worker { get; }
    internal RetainedHostTask<ToolResult> Quality { get; }
    internal RetainedHostTask<ToolResult> Review { get; }
    internal RetainedHostTask<ToolResult> IndependentReview { get; }
    readonly List<(QuiescenceResult Pause, RetainedHostTask<ResumeResult> Control)> resumeControls = [];
    QuiescenceResult? actualPause;
    internal QuiescenceResult? ActualPause { get { lock (ReconciliationGate) return actualPause; } }
    internal void RetainActualPause(QuiescenceResult value)
    {
        lock (operationGate)
        {
            if (!operations.Any(row => row.Task is Task<QuiescenceResult> { IsCompletedSuccessfully: true } actual && ReferenceEquals(actual.Result, value)))
                throw new WorkerEnvelopeException("Pause DATA did not come from this frame's actual completed owner Task.");
        }
        lock (ReconciliationGate) actualPause = value;
    }
    internal IReadOnlyList<RetainedHostTask<ResumeResult>> ResumeControls { get { lock (ReconciliationGate) return resumeControls.Select(row => row.Control).ToArray(); } }
    internal RetainedHostTask<ResumeResult> ControlForActualPause(QuiescenceResult pause)
    {
        if (!Monitor.IsEntered(ReconciliationGate) || !ReferenceEquals(pause, ActualPause) || !pause.Quiescent)
            throw new WorkerEnvelopeException("Resume requires the actual retained quiescent owner pause, never copied DATA.");
        var prior = resumeControls.FirstOrDefault(row => ReferenceEquals(row.Pause, pause));
        if (prior.Control is not null) return prior.Control;
        if (resumeControls.Count >= 1024) throw new IOException("Finite original control ledger exhausted.");
        var control = new RetainedHostTask<ResumeResult>(Lifetime); resumeControls.Add((pause, control)); return control;
    }
    public string RunId { get; }
    internal ToolLifetime Lifetime { get; }
    internal ExecutionCoordinator Owner { get; }
    sealed class Operation { public bool Entered, Returned; public Task? Task; public Exception? Failure; }
    readonly List<Operation> operations = []; readonly object operationGate = new(); int activeRoutes;
    public IReadOnlyList<Task> EnteredTasks { get { lock (operationGate) return operations.Where(x => x.Task is not null).Select(x => x.Task!).ToArray(); } }
    public bool InvocationUnknown { get { lock (operationGate) return operations.Any(x => x.Entered && (!x.Returned || x.Task is null)); } }
    internal void EnterRoute() { lock (operationGate) { Lifetime.RetainBridgeUnknown(this); activeRoutes++; } }
    internal void LeaveRoute() { lock (operationGate) activeRoutes--; }
    bool RouteActive { get { lock (operationGate) return activeRoutes != 0; } }
    Operation AdmitOperation() { lock (operationGate) { if (operations.Count >= 1024) throw new IOException("Finite original operation ledger exhausted."); Lifetime.RetainBridgeUnknown(this); var row = new Operation { Entered = true }; operations.Add(row); return row; } }
    Exception? primary, post;
    public Exception? Primary { get => Volatile.Read(ref primary); internal set { if (value is not null) Interlocked.CompareExchange(ref primary, value, null); } }
    public Exception? Post { get => Volatile.Read(ref post); internal set { if (value is not null) Interlocked.CompareExchange(ref post, value, null); } }
    public AgentRequest? Request { get; internal set; }
    public AgentAcceptedReceipt? Accepted { get; internal set; }
    public AgentCompletionReceipt? Completion { get; internal set; }
    public bool OwnerCompleted { get; internal set; }
    public bool OwnerStopped { get; internal set; }
    public bool Unknown => Worker.Unknown || Quality.Unknown || Review.Unknown || IndependentReview.Unknown || ResumeControls.Any(control => control.Unknown) || RouteActive || InvocationUnknown || EnteredTasks.Any(t => !t.IsCompleted) || Owner.HasPendingControlOperations(RunId) || ExternalUnknown() || (Accepted is not null && !OwnerCompleted && !OwnerStopped);
    internal async Task<T> Enter<T>(Func<Task<T>> invoke)
    {
        Lifetime.Check(); T value = default!; Exception? primary = null; Exception? post = null; var operation = AdmitOperation();
        try { var task = invoke(); if (task is null) throw new IOException("Actual operation returned no task."); lock (operationGate) { operation.Task = task; operation.Returned = true; } value = task.IsCompleted ? await task.ConfigureAwait(false) : await task.WaitAsync(Lifetime.Remaining).ConfigureAwait(false); }
        catch (Exception error) { primary = error; operation.Failure = error; }
        try { Lifetime.Check(); } catch (Exception error) { post = error; Post ??= error; }
        if (primary is not null && post is not null) throw new AggregateException("Actual operation and independent endpoint post.", primary, post);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
        return value;
    }
    internal async Task<T> EnterCleanup<T>(Func<TimeSpan, Task<T>> invoke)
    {
        var left = Lifetime.CleanupRemaining; T value = default!; Exception? primary = null; Exception? post = null; var operation = AdmitOperation();
        try { var actual = invoke(left); if (actual is null) throw new IOException("Actual cleanup invocation returned no task."); lock (operationGate) { operation.Task = actual; operation.Returned = true; } value = await actual.WaitAsync(left).ConfigureAwait(false); }
        catch (Exception error) { primary = error; operation.Failure = error; Primary ??= error; }
        try { _ = Lifetime.CleanupRemaining; } catch (Exception error) { post = error; Post ??= error; }
        if (primary is not null && post is not null) throw new AggregateException("Original cleanup and independent post.", primary, post);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw(); return value;
    }
    public bool CloseWhenSettled() { lock (operationGate) { if (Unknown) { Lifetime.RetainBridgeUnknown(this); return false; } Lifetime.ReleaseSettledBridge(this); if (OwnsOriginal) Lifetime.Dispose(); return true; } }
}
public sealed class OwnerHostBridge(ExecutionCoordinator coordinator, NamedHostAdapter adapter, bool requireCoordination = false)
{
    ParentCoordinationHost? coordination;
    InstalledQualityHost? installedQuality;
    InstalledReviewHost? installedReview;
    internal bool HasInstalledQuality => installedQuality is not null;
    internal bool HasInstalledReview => installedReview is not null;
    internal void BindInstalledReview(InstalledReviewHost actual)
    {
        lock (registration) { if (installedReview is not null || frames.Count != 0) throw new ToolUsageException("Installed review binds the idle actual bridge once."); installedReview = actual; }
    }
    internal void DisposeInstalledReviewWhenSettled()
    {
        if (frames.Values.Any(frame => frame.Unknown)) throw new IOException("Actual worker/review frames are not settled; retain the review object view.");
        installedReview?.Dispose();
    }
    internal void BindInstalledQuality(InstalledQualityHost actual)
    {
        lock (registration) { if (installedQuality is not null || frames.Count != 0) throw new ToolUsageException("Installed quality binds the idle actual bridge once."); installedQuality = actual; }
    }
    internal void BindCoordination(ParentCoordinationHost actual)
    {
        lock (registration) { if (coordination is not null || frames.Count != 0) throw new WorkerEnvelopeException("Parent coordination binds the idle original bridge once."); coordination = actual; actual.BindBridge(this); adapter.BindEffectAdmission(actual); coordinator.BindCoordinationAdmission(actual); }
    }
    internal void BindCoordinationSelection(FrozenPlan plan, FrozenWorkerPlan assignment, TrustedConfiguration config, ToolLifetime original)
    {
        if (coordination is null) { if (config.Coordination is not null || requireCoordination) throw new ToolUsageException("Configured production coordination owner is unavailable."); return; }
        coordination.BindSelection(plan, assignment, config, original);
        installedQuality?.Bind(plan, assignment, config, original);
        installedReview?.Bind(plan, assignment, config, original);
    }
    internal bool ObserveCoordinationWorkSettled(string runId)
    {
        if (!frames.TryGetValue(runId, out var actual)) return false;
        // This is the synchronous parent quiescence sample, not quality's
        // CurrentAsync/EnterAsync admission. Every actual containing phase and
        // child/control remains an obligation; only this observation route is
        // excluded. In particular an unreturned phase invocation is not quiet.
        if (actual.Worker.Unknown || actual.Quality.Unknown || actual.Review.Unknown || actual.IndependentReview.Unknown || actual.ResumeControls.Any(control => control.Unknown) || actual.InvocationUnknown || actual.EnteredTasks.Any(task => !task.IsCompleted) || adapter.HasUnknownCalls(runId) || !adapter.ObserveBoundNativeQuiescence(runId) || !coordinator.ObserveOriginalProcessesSettled(runId)) return false;
        return actual.OwnerCompleted || actual.OwnerStopped || actual.Accepted is null && !adapter.ActualCalls.Any(call => call.RunId == runId && call.Kind == "Start");
    }
    readonly ConcurrentDictionary<string, HostBridgeFrame> frames = new(StringComparer.Ordinal);
    public IReadOnlyList<HostBridgeFrame> RetainedFrames => frames.Values.ToArray();
    public string StoreRootPath => coordinator.StoreRootPath;
    public AgentAdapterProfile AdapterProfile => adapter.Profile;
    public HostObservationRealm Realm => adapter.Realm;
    readonly object registration = new();
    bool Register(string runId, HostBridgeFrame frame) { lock (registration) { return frames.Count < 128 && frames.TryAdd(runId, frame); } }
    public Task<ToolResult> ImplementAsync(FrozenWorkerPlan plan, TimeSpan total, CancellationToken cancellationToken = default) => Task.FromResult(ToolResult.Incomplete("PARENT_INSTRUCTIONS_REQUIRED", "A taskless worker envelope cannot dispatch; supply the actual frozen plan or parent-selected immutable instruction context."));
    public Task<ToolResult> ImplementAsync(FrozenWorkerPlan plan, HostInstructionContext instructions, TimeSpan total, CancellationToken cancellationToken = default) => ImplementAsync(plan, instructions, new ToolLifetime(total, cancellationToken), cancellationToken, true);
    internal async Task<ToolResult> ImplementAsync(FrozenWorkerPlan plan, HostInstructionContext instructions, ToolLifetime original, CancellationToken cancellationToken = default, bool ownsOriginal = false)
    {
        if (requireCoordination && coordination is null) return ToolResult.Incomplete("CONTINUUM_PARENT_REQUIRED", "Actual standalone dispatch requires the explicitly configured original Continuum parent.");
        try { instructions.Require(WorkerEnvelope.PrepareWorker(plan)); adapter.BindInstructions(instructions); adapter.BindOriginal(original); } catch { if (ownsOriginal) original.Dispose(); throw; }
        var frame = new HostBridgeFrame(plan.RunId, original, coordinator, () => adapter.HasUnknownCalls(plan.RunId) || installedQuality?.Unknown == true || installedReview?.Unknown == true, ownsOriginal);
        if (!Register(plan.RunId, frame)) { if (ownsOriginal) original.Dispose(); throw new WorkerEnvelopeException("Run already entered or finite host frame allowance exhausted; no replay."); }
        frame.EnterRoute(); // Retain the actual original before phase factory entry.
        try
        {
            frame.Worker.Start(original, () => ImplementRetainedCoreAsync(plan, frame));
            var actualResult = await frame.Worker.ObserveAsync(original).ConfigureAwait(false);
            return RetainedResult(frame, actualResult);
        }
        finally { if (!frame.Worker.Started) frame.LeaveRoute(); frame.CloseWhenSettled(); }
    }
    async Task<ToolResult> ImplementRetainedCoreAsync(FrozenWorkerPlan plan, HostBridgeFrame frame)
    {
        try
        {
            if (coordination is not null) await coordination.AcquireAsync(plan.RunId).ConfigureAwait(false);
            var prepared = await frame.Enter(() => coordinator.PrepareWorkerAsync(plan, frame.Lifetime.Token)).ConfigureAwait(false); frame.Request = prepared.Request;
            frame.Accepted = await frame.Enter(() => adapter.StartAsync(prepared.Request, frame.Lifetime.Remaining, frame.Lifetime.Token)).ConfigureAwait(false);
            await frame.Enter(() => coordinator.AcceptAsync(frame.Accepted ?? throw new WorkerEnvelopeException("Missing actual acceptance."), frame.Lifetime.Token)).ConfigureAwait(false);
            var a = frame.Accepted ?? throw new WorkerEnvelopeException("Missing actual acceptance."); var binding = new AgentRunBinding(a.AdapterId, a.AdapterVersion, a.RunId, a.RequestIdentity, a.RuntimeRunIdentity, a.RuntimeTaskIdentity);
            frame.Completion = await frame.Enter(() => adapter.WaitAsync(binding, frame.Lifetime.Remaining, frame.Lifetime.Token)).ConfigureAwait(false);
            WorkerEnvelope.ValidateCompletion(prepared.Request, a, frame.Completion ?? throw new WorkerEnvelopeException("Missing actual completion."));
            if (frame.OwnerStopped) throw new WorkerEnvelopeException("Stopped original cannot enter completion or installed quality.");
            var committed = await frame.Enter(() => coordinator.CompleteAsync(frame.Completion ?? throw new WorkerEnvelopeException("Missing actual completion."), frame.Lifetime.Token)).ConfigureAwait(false); lock (frame.ReconciliationGate) frame.OwnerCompleted = true; adapter.MarkOwnerCompleted(binding);
            ToolResult? reviewPreparation = null;
            if (installedReview is not null)
            {
                foreach (var enteredControl in frame.ResumeControls)
                {
                    var actualResume = await enteredControl.ObserveAsync(frame.Lifetime).ConfigureAwait(false);
                    if (actualResume.Reconciliation.Action != ReconciliationAction.ResumeDispatch || actualResume.Record is null)
                        throw new WorkerEnvelopeException("Actual owner resume did not authorize immutable review preparation.");
                }
                if (coordination is null || frame.Primary is not null || frame.Post is not null || frame.InvocationUnknown ||
                    frame.EnteredTasks.Any(task => !task.IsCompleted) || !NativeCompletedAndQuiet(frame))
                    throw new WorkerEnvelopeException("Immutable review preparation requires the actual completed native worker and current parent owner.");
                frame.Review.Start(frame.Lifetime, () => installedReview.PrepareCompletedAsync(frame, coordination,
                    () => NativeCompletedAndQuiet(frame)));
                reviewPreparation = await frame.Review.ObserveAsync(frame.Lifetime).ConfigureAwait(false);
            }
            ToolResult? qualityResult = null;
            if (installedQuality is not null)
            {
                foreach (var enteredControl in frame.ResumeControls)
                {
                    var actualResume = await enteredControl.ObserveAsync(frame.Lifetime).ConfigureAwait(false);
                    if (actualResume.Reconciliation.Action != ReconciliationAction.ResumeDispatch || actualResume.Record is null)
                        throw new WorkerEnvelopeException("Actual owner resume did not authorize forward quality work.");
                }
                if (frame.OwnerStopped) throw new WorkerEnvelopeException("Stopped original cannot enter installed quality.");
                if (frame.Primary is not null || frame.Post is not null || coordination is null || adapter.Realm == HostObservationRealm.ContractFixture || frame.InvocationUnknown || frame.EnteredTasks.Any(task => !task.IsCompleted) || adapter.HasUnknownCalls(plan.RunId) || !adapter.ObserveBoundNativeQuiescence(plan.RunId) || !coordinator.ObserveOriginalProcessesSettled(plan.RunId))
                    throw new WorkerEnvelopeException("Installed quality requires the genuine original completed native worker and current parent owner.");
                frame.Quality.Start(frame.Lifetime, () => installedQuality.RunCompletedAsync(frame, coordination));
                qualityResult = await frame.Quality.ObserveAsync(frame.Lifetime).ConfigureAwait(false);
            }
            if (installedReview?.HasIndependentReviewer == true)
            {
                if (coordination is null || !NativeCompletedAndQuiet(frame) || frame.Quality.Unknown || installedQuality?.Unknown == true || frame.EnteredTasks.Any(task => !task.IsCompleted)) throw new WorkerEnvelopeException("Actual independent review requires its same original completed author and settled quality originals.");
                var completion = frame.Completion ?? throw new WorkerEnvelopeException("Actual original completion is absent.");
                frame.IndependentReview.Start(frame.Lifetime, () => installedReview.RunIndependentAsync(frame, coordination, adapter.CompletedImplementationFamily(completion),
                    () => NativeCompletedAndQuiet(frame) && !frame.Quality.Unknown && installedQuality?.Unknown != true, installedQuality?.RetainedResults ?? Array.Empty<DeliveryForge.Evidence.GateRunResult>()));
                var reviewed = await frame.IndependentReview.ObserveAsync(frame.Lifetime).ConfigureAwait(false);
                if (qualityResult is null) return reviewed;
                return qualityResult with { Data = new { Quality = qualityResult.Data, Review = reviewed, ReviewPreparation = reviewPreparation } };
            }
            if (qualityResult is not null) return reviewPreparation is null ? qualityResult : qualityResult with { Data = new { Quality = qualityResult.Data, ReviewPreparation = reviewPreparation } };
            if (reviewPreparation is not null) return reviewPreparation;
            return new("COMPLETE", adapter.Realm == HostObservationRealm.ContractFixture ? "CONTRACT_FIXTURE_COMPLETE" : "OWNER_COMPLETED_REQUIRES_PROFILE_AUDIT", "Actual owner completion recorded; quality/review/publication and installed certification remain separate.", new { committed.RunId, committed.Sequence, committed.Sha256, Realm = adapter.Realm.ToString() });
        }
        catch (Exception error) { frame.Primary ??= error; return ToolResult.Incomplete("HOST_OPERATION_RETAINED", "Actual host/owner operation failed or remains unknown; no retry or invented quiescence.", new { frame.RunId, frame.Unknown, Type = error.GetType().FullName, OwnerCompleted = frame.OwnerCompleted }); }
        finally { frame.LeaveRoute(); frame.CloseWhenSettled(); }
    }
    bool NativeCompletedAndQuiet(HostBridgeFrame frame) => frame.OwnerCompleted && !frame.OwnerStopped &&
        frame.Primary is null && frame.Post is null && frame.Completion is not null &&
        adapter.Realm != HostObservationRealm.ContractFixture && !adapter.HasUnknownCalls(frame.RunId) &&
        adapter.ObserveBoundNativeQuiescence(frame.RunId) && coordinator.ObserveOriginalProcessesSettled(frame.RunId);
    public async Task<ToolResult> StopRetainedAsync(string runId)
    {
        if (!frames.TryGetValue(runId, out var frame)) return ToolResult.Incomplete("ORIGINAL_OWNER_REQUIRED", "No retained same-original owner frame exists; saved DATA is not a session.");
        frame.EnterRoute();
        try
        {
            // Stop is the existing owner's actual control, not a new agent kill or
            // timeout refund. It ignores caller cancellation only within cleanup.
            var reviewerClosed = installedReview is null || await frame.EnterCleanup(_ => installedReview.StopIndependentAsync(frame)).ConfigureAwait(false);
            var result = await frame.EnterCleanup(left => coordinator.StopAsync(runId, left, CancellationToken.None)).ConfigureAwait(false);
            if (result.Quiescent)
            {
                lock (frame.ReconciliationGate) frame.OwnerStopped = true;
                if (frame.Accepted is AgentAcceptedReceipt a) adapter.MarkOwnerCompleted(new(a.AdapterId, a.AdapterVersion, a.RunId, a.RequestIdentity, a.RuntimeRunIdentity, a.RuntimeTaskIdentity));
            }
            return new(result.Quiescent && reviewerClosed ? "COMPLETE" : "INCOMPLETE", "OWNER_STOP", "Actual same-owner stop observation recorded; unknown original tasks still prevent frame disposal.", new { runId, result.Quiescent, ReviewerClosed = reviewerClosed, frame.Unknown });
        }
        catch (Exception error) { frame.Primary ??= error; return ToolResult.Incomplete("STOP_RETAINED", "Stop failed or exceeded the SAME original cleanup endpoint; originals retained.", new { runId, frame.Unknown, Type = error.GetType().FullName }); }
        finally { frame.LeaveRoute(); frame.CloseWhenSettled(); }
    }
    public async Task<ToolResult> PauseAsync(string runId)
    {
        if (!frames.TryGetValue(runId, out var frame)) return ToolResult.Incomplete("ORIGINAL_OWNER_REQUIRED", "No retained same-original owner frame exists.");
        frame.EnterRoute();
        try { var result = await frame.Enter(() => coordinator.PauseAsync(runId, frame.Lifetime.Remaining, frame.Lifetime.Token)).ConfigureAwait(false); frame.RetainActualPause(result); return new(result.Quiescent ? "COMPLETE" : "INCOMPLETE", "OWNER_PAUSE", "Actual owner checkpoint/control observation; no new original.", new { runId, result.Quiescent }); }
        catch (Exception error) { frame.Primary ??= error; return ToolResult.Incomplete("PAUSE_RETAINED", "Actual owner/control frame retained.", new { runId, frame.Unknown, Type = error.GetType().FullName }); }
        finally { frame.LeaveRoute(); frame.CloseWhenSettled(); }
    }
    static ToolResult RetainedResult(HostBridgeFrame frame, ToolResult actualResult) =>
        frame.Primary is not null || frame.Post is not null
            ? ToolResult.Incomplete("HOST_OPERATION_RETAINED", "A retained original failure/post cannot become a successful resume.", new { frame.RunId, ActualResult = actualResult, frame.Unknown })
            : actualResult;
    public Task<ToolResult> ResumeRetainedAsync(string runId) => ResumeRetainedAsync(runId, null, null);
    internal async Task<ToolResult> ResumeRetainedAsync(string runId, ToolLifetime? actualOriginal, TrustedConfiguration? actualConfiguration)
    {
        if (!frames.TryGetValue(runId, out var frame) || !frame.Worker.Started)
            return ToolResult.Incomplete("ORIGINAL_OWNER_REQUIRED", "No retained actual worker pipeline exists; stored JSON is not an owner.");
        if (actualOriginal is not null && !ReferenceEquals(actualOriginal, frame.Lifetime))
            return ToolResult.Incomplete("ORIGINAL_OWNER_REQUIRED", "Resume cannot replace or renew the actual original.");
        if (installedQuality is not null && !installedQuality.MatchesRetained(frame, actualConfiguration, actualOriginal))
            return ToolResult.Incomplete("ORIGINAL_QUALITY_BINDING_REQUIRED", "Same retained original/configuration/plan/assignment is required.");
        if (installedReview is not null && !installedReview.MatchesRetained(frame, actualConfiguration, actualOriginal))
            return ToolResult.Incomplete("ORIGINAL_REVIEW_BINDING_REQUIRED", "Same retained original/configuration/plan/assignment is required for review.");
        frame.EnterRoute();
        try
        {
            // Terminal actual tasks are observed, never restarted. Only the real
            // still-live worker may need the existing owner's resume control.
            Action? enterControl = null; RetainedHostTask<ResumeResult>? selectedControl = null;
            lock (frame.ReconciliationGate)
            {
                if (!frame.OwnerCompleted && !frame.OwnerStopped && frame.Accepted is not null && frame.Worker.Actual?.IsCompleted == false && frame.ActualPause is { Quiescent: true } actualPause)
                {
                    selectedControl = frame.ControlForActualPause(actualPause);
                    enterControl = selectedControl.Prepare(frame.Lifetime, () => frame.Enter(() => coordinator.ResumeAsync(runId, frame.Lifetime.Remaining, frame.Lifetime.Token)));
                }
            }
            enterControl?.Invoke();
            if (selectedControl is not null)
            {
                var control = await selectedControl.ObserveAsync(frame.Lifetime).ConfigureAwait(false);
                if (control.Reconciliation.Action != ReconciliationAction.ResumeDispatch || control.Record is null)
                    return ToolResult.Incomplete("OWNER_RESUME_RETAINED", "Actual parent/worker reconciliation did not authorize resume; no quality replay.");
            }
            var actualResult = await frame.Worker.ObserveAsync(frame.Lifetime).ConfigureAwait(false);
            return RetainedResult(frame, actualResult);
        }
        catch (Exception error) { frame.Primary ??= error; return ToolResult.Incomplete("RESUME_RETAINED", "Same actual worker/quality/control task failed, cancelled or remains unknown; no retry.", new { runId, frame.Unknown, Type = error.GetType().FullName }); }
        finally { frame.LeaveRoute(); frame.CloseWhenSettled(); }
    }
    public Task<ToolResult> ResumeAsync(string runId, TimeSpan total, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolResult.Incomplete("ORIGINAL_OWNER_REQUIRED", "A new CLI/lifetime cannot reconstruct retained worker or quality authority."));
    internal Task<ToolResult> ResumeAsync(string runId, ToolLifetime original, CancellationToken cancellationToken = default, bool ownsOriginal = false) =>
        ResumeRetainedAsync(runId, original, null);
}
