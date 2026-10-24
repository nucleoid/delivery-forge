using DeliveryForge.Coordination;
using DeliveryForge.Planning;
using System.Collections.Concurrent;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Scheduling;
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
    internal HostBridgeFrame(string runId, ToolLifetime life, ExecutionCoordinator owner, Func<bool> externalUnknown, bool ownsOriginal) { RunId = runId; Lifetime = life; Owner = owner; ExternalUnknown = externalUnknown; OwnsOriginal = ownsOriginal; Worker = new(life); Quality = new(life); Review = new(life); IndependentReview = new(life); LocalReal = new(life); BatchEvidenceCapture = new(life); SchedulingCompletion = new(life); }
    readonly Func<bool> ExternalUnknown; readonly bool OwnsOriginal;
    internal readonly object ReconciliationGate = new();
    internal RetainedHostTask<ToolResult> Worker { get; }
    internal RetainedHostTask<ToolResult> Quality { get; }
    internal RetainedHostTask<ToolResult> Review { get; }
    internal RetainedHostTask<ToolResult> IndependentReview { get; }
    internal RetainedHostTask<ToolResult> LocalReal { get; }
    internal RetainedHostTask<bool> BatchEvidenceCapture { get; }
    internal RetainedHostTask<bool> SchedulingCompletion { get; }
    internal LocalSingleScheduler? Scheduler;
    internal LocalDispatchReservation? Reservation;
    internal volatile bool SchedulingCleanupEntered;
    internal volatile Task? SchedulingCleanup;
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
    public bool Unknown => SchedulingCleanupEntered && (SchedulingCleanup is null || !SchedulingCleanup.IsCompleted) || Scheduler?.HasRetainedReservation == true || SchedulingCompletion.Unknown || BatchEvidenceCapture.Unknown || Worker.Unknown || Quality.Unknown || Review.Unknown || IndependentReview.Unknown || LocalReal.Unknown || ResumeControls.Any(control => control.Unknown) || RouteActive || InvocationUnknown || EnteredTasks.Any(t => !t.IsCompleted) || Owner.HasPendingControlOperations(RunId) || ExternalUnknown() || (Accepted is not null && !OwnerCompleted && !OwnerStopped);
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
public sealed partial class OwnerHostBridge(ExecutionCoordinator coordinator, NamedHostAdapter adapter, bool requireCoordination = false)
{
    ParentCoordinationHost? coordination;
    InstalledQualityHost? installedQuality;
    InstalledReviewHost? installedReview;
    LocalRealGoldenOwner? localRealGolden;
    Func<FrozenPlan, FrozenWorkerPlan, ParentCoordinationHost, LocalSingleScheduler>? localSingleFactory;
    ParentNativeContractComposition? contractFixtureProvisioning;
    FrozenPlan? schedulingPlan;
    ProductionBatchControl? batchControl;
    internal void BindBatchControl(ProductionBatchControl actual)
    {
        lock (registration) { if (batchControl is not null || frames.Count != 0) throw new ToolUsageException("Batch birth restriction binds the idle bridge once."); batchControl = actual; }
    }
    internal void BindLocalSingleFactory(Func<FrozenPlan, FrozenWorkerPlan, ParentCoordinationHost, LocalSingleScheduler> factory)
    {
        lock (registration) { if (localSingleFactory is not null || contractFixtureProvisioning is not null || frames.Count != 0) throw new ToolUsageException("LocalSingle binds once before dispatch."); localSingleFactory = factory; }
    }
    // Only the closed native fixture owner can use this provisioning route.
    // Production dispatch retains its actual LocalSingle reservation path.
    internal void BindContractFixtureProvisioning(ParentNativeContractComposition actual)
    {
        lock (registration)
        {
            if (adapter is not ParentNativeContractAdapter || adapter.Realm != HostObservationRealm.ContractFixture ||
                !ReferenceEquals(actual.Bridge, this) || contractFixtureProvisioning is not null || localSingleFactory is not null || frames.Count != 0)
                throw new ToolUsageException("The same idle concrete native fixture owner is required for provisioning.");
            contractFixtureProvisioning = actual;
        }
    }
    internal bool HasInstalledQuality => installedQuality is not null;
    // Additional forward-review restriction. ToolResult text/labels do not
    // substitute for the actual private same-frame protected comparison.
    internal static bool MayEnterIndependentReviewAfterQuality(ToolResult? actualResult, InstalledQualityHost? actualOwner, HostBridgeFrame? actualFrame) =>
        actualResult is null || actualOwner is not null && actualFrame is not null && actualOwner.HasLocalRealCompletion(actualFrame);
    internal bool HasInstalledReview => installedReview is not null;
    internal bool HasLocalRealGolden => localRealGolden is not null;
    internal void BindLocalRealGolden(LocalRealGoldenOwner actual)
    {
        lock (registration) { if (localRealGolden is not null || frames.Count != 0) throw new ToolUsageException("Local-real binds the idle actual bridge once."); localRealGolden = actual; }
    }
    internal void BindInstalledReview(InstalledReviewHost actual)
    {
        lock (registration) { if (installedReview is not null || frames.Count != 0) throw new ToolUsageException("Installed review binds the idle actual bridge once."); installedReview = actual; }
    }
    internal bool InstalledReviewDispositionEntered => installedReview?.DispositionEntered == true;
    internal bool InstalledReviewDispositionReturned => installedReview is null || installedReview.DispositionReturned;
    internal void DisposeInstalledReviewWhenSettled(bool cleanup = true)
    {
        if (frames.Values.Any(frame => frame.Unknown)) throw new IOException("Actual worker/review frames are not settled; retain the review object view.");
        if (cleanup) installedReview?.Dispose(); else installedReview?.DisposeDuringWork();
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
        if (config.LocalSingle is not null && localSingleFactory is null) throw new ToolUsageException("Actual LocalSingle composition required; selection cannot be ignored.");
        if (localSingleFactory is not null) schedulingPlan = plan;
        installedQuality?.Bind(plan, assignment, config, original);
        installedReview?.Bind(plan, assignment, config, original);
        localRealGolden?.Bind(plan, assignment, config, original);
        historicalQuality?.Bind(plan, assignment, config, original);
    }
    internal bool ObserveCoordinationWorkSettled(string runId)
        => frames.TryGetValue(runId, out var frame) && !frame.SchedulingCompletion.Unknown && frame.Scheduler?.HasRetainedReservation != true && ObservePipelineSettled(runId);
    bool ObservePipelineSettled(string runId)
    {
        if (!frames.TryGetValue(runId, out var actual)) return false;
        // This is the synchronous parent quiescence sample, not quality's
        // CurrentAsync/EnterAsync admission. Every actual containing phase and
        // child/control remains an obligation; only this observation route is
        // excluded. In particular an unreturned phase invocation is not quiet.
        if (actual.BatchEvidenceCapture.Unknown || actual.Worker.Unknown || actual.Quality.Unknown || actual.Review.Unknown || actual.IndependentReview.Unknown || actual.LocalReal.Unknown || historicalQuality?.Unknown == true || actual.ResumeControls.Any(control => control.Unknown) || actual.InvocationUnknown || actual.EnteredTasks.Any(task => !task.IsCompleted) || adapter.HasUnknownCalls(runId) || !adapter.ObserveBoundNativeQuiescence(runId) || !coordinator.ObserveOriginalProcessesSettled(runId)) return false;
        return actual.OwnerCompleted || actual.OwnerStopped || actual.Accepted is null && !adapter.ActualCalls.Any(call => call.RunId == runId && call.Kind == "Start");
    }
    readonly ConcurrentDictionary<string, HostBridgeFrame> frames = new(StringComparer.Ordinal);
    public IReadOnlyList<HostBridgeFrame> RetainedFrames => frames.Values.ToArray();
    public string StoreRootPath => coordinator.StoreRootPath;
    public AgentAdapterProfile AdapterProfile => adapter.Profile;
    public HostObservationRealm Realm => adapter.Realm;
    // Issued only from this concrete bridge's exact settled original frame.
    // It is never serialized, imported or accepted as a substitute for that frame.
    internal sealed class BatchNodeCompletion(OwnerHostBridge issuer, HostBridgeFrame frame, string phase)
    {
        internal OwnerHostBridge Issuer { get; } = issuer;
        internal HostBridgeFrame Frame { get; } = frame;
        internal string RunId => Frame.RunId;
        internal string HeadCommit => Frame.Completion!.HeadCommit;
        internal string Phase { get; } = phase;
    }
    // Captured only by the actual post-worker/pre-writer-disposition route.
    // This private object is not serialized and cannot be reconstructed from
    // Pass labels, receipt DATA, or a completed boolean Task.
    sealed class BatchNodeEvidence
    {
        internal readonly OwnerHostBridge Issuer;
        internal readonly HostBridgeFrame Frame;
        internal readonly AgentCompletionReceipt Completion;
        internal readonly InstalledQualityHost? QualityOwner;
        internal readonly InstalledReviewHost? ReviewOwner;
        internal readonly DeliveryForge.Execution.Git.WorktreeManager.OwnedGitBinding WriterBinding;
        internal readonly DeliveryForge.Evidence.GateRunResult[] GateResults;
        internal readonly IReadOnlyList<DeliveryForge.Contracts.Validation.ValidatedContract>? GateReceipts;
        internal readonly DeliveryForge.Tool.Claude.ClaudeIndependentReviewer.AcceptedLocalRevision? AcceptedReview;
        internal readonly DeliveryForge.Contracts.Validation.ValidatedContract? ReviewPolicy;
        internal readonly (RetainedHostTask<ToolResult> Phase, Task<ToolResult> Actual)[] Phases;
        internal readonly string Phase;
        internal BatchNodeEvidence(OwnerHostBridge issuer, HostBridgeFrame frame,
            DeliveryForge.Execution.Git.WorktreeManager.OwnedGitBinding writerBinding,
            InstalledQualityHost? qualityOwner, InstalledReviewHost? reviewOwner,
            IReadOnlyList<DeliveryForge.Contracts.Validation.ValidatedContract>? quality,
            DeliveryForge.Tool.Claude.ClaudeIndependentReviewer.AcceptedLocalRevision? review,
            DeliveryForge.Contracts.Validation.ValidatedContract? policy,
            (RetainedHostTask<ToolResult> Phase, Task<ToolResult> Actual)[] phases, string phase)
        {
            Issuer = issuer; Frame = frame; Completion = frame.Completion!; WriterBinding = writerBinding;
            QualityOwner = qualityOwner; ReviewOwner = reviewOwner;
            GateResults = qualityOwner?.RetainedResults.ToArray() ?? [];
            GateReceipts = quality; AcceptedReview = review; ReviewPolicy = policy;
            Phases = phases; Phase = phase;
        }
    }
    BatchNodeEvidence? batchEvidence;
    Task<bool>? batchEvidenceTask;
    BatchNodeCompletion? batchCompletion;
    static RetainedHostTask<ToolResult>[] BatchPhases(HostBridgeFrame frame) =>
        [frame.Worker, frame.Quality, frame.Review, frame.IndependentReview, frame.LocalReal];
    void RequireWorkerSettledBeforeBatchEvidence(HostBridgeFrame frame, ToolLifetime original)
    {
        original.Check();
        if (batchControl is null || !frames.TryGetValue(frame.RunId, out var actual) || !ReferenceEquals(actual, frame) ||
            !ReferenceEquals(frame.Lifetime, original) || original.HasExplicitUnknown || !NativeCompletedAndQuiet(frame) ||
            frame.Scheduler is not { HasRetainedReservation: true } || frame.Reservation is null ||
            frame.InvocationUnknown || frame.EnteredTasks.Any(task => !task.IsCompleted) ||
            frame.ResumeControls.Any(control => control.Unknown))
            throw new IOException("Batch evidence requires its actual settled worker and still-owned scheduling reservation.");
        foreach (var phase in BatchPhases(frame))
            if (phase.Started && (phase.Actual is not { IsCompletedSuccessfully: true } || phase.Primary is not null || phase.Post is not null))
                throw new IOException("Actual worker/gate/reviewer phase has not settled before evidence capture.");
    }
    async Task<bool> CaptureActualBatchEvidenceAsync(HostBridgeFrame frame, ToolLifetime original)
    {
        try
        {
            RequireWorkerSettledBeforeBatchEvidence(frame, original);
            if (batchEvidence is not null) throw new IOException("Actual batch evidence cannot be recaptured.");
            // Both calls below may read current Git/metadata. They MUST finish
            // while the exact original writer is still owned.
            var writer = coordinator.RequireOriginalWorktreeBinding(frame.RunId, original);
            var phases = BatchPhases(frame).Where(phase => phase.Started).Select(phase => (Phase: phase, Actual: phase.Actual!)).ToArray();
            var results = phases.Where(row => !ReferenceEquals(row.Phase, frame.Review) || !frame.IndependentReview.Started).Select(row => row.Actual.Result).ToArray();
            var quality = installedQuality is null || coordination is null ? null : await installedQuality.LocalRealEvidenceAsync(frame, coordination).ConfigureAwait(false);
            var review = installedReview is null || coordination is null ? null : await installedReview.LocalRealEvidenceAsync(frame, coordination).ConfigureAwait(false);
            var requiredGateIds = new HashSet<string>(StringComparer.Ordinal);
            if (schedulingPlan is not null)
            {
                using var plan = System.Text.Json.JsonDocument.Parse(schedulingPlan.CanonicalBytes);
                foreach (var gate in plan.RootElement.GetProperty("plan").GetProperty("gates").EnumerateArray()) requiredGateIds.Add(gate.GetProperty("id").GetString()!);
            }
            var actualGateIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var receipt in quality ?? Array.Empty<DeliveryForge.Contracts.Validation.ValidatedContract>())
            {
                using var document = System.Text.Json.JsonDocument.Parse(receipt.CanonicalBytes);
                actualGateIds.Add(document.RootElement.GetProperty("gateId").GetString()!);
            }
            // Worker-local COMPLETE and source-preparation COMPLETE are never a
            // substitute for protected quality, exact accepted independent review,
            // and complete coverage of the frozen plan's required gate identities.
            var gatesPassed = quality is not null && review is not null && requiredGateIds.Count != 0 && requiredGateIds.IsSubsetOf(actualGateIds);
            var phase = results.Any(result => result.Outcome is "FAIL" or "BLOCKED") ? "Fail" : gatesPassed ? "Pass" :
                installedQuality is null && installedReview is null ? "LocalComplete" : "Incomplete";
            RequireWorkerSettledBeforeBatchEvidence(frame, original);
            if (!ReferenceEquals(writer, coordinator.RequireOriginalWorktreeBinding(frame.RunId, original)))
                throw new IOException("Actual writer changed during pre-disposition batch evidence capture.");
            batchEvidence = new(this, frame, writer, installedQuality, installedReview, quality, review?.Accepted, review?.Policy, phases, phase);
            return true;
        }
        catch (Exception error) { frame.Primary ??= error; throw; }
    }
    internal void RequireCapturedBatchEvidence(HostBridgeFrame frame, ToolLifetime original)
    {
        original.Check();
        var proof = batchEvidence ?? throw new IOException("Private pre-disposition batch evidence was not captured.");
        if (!ReferenceEquals(proof.Issuer, this) || !ReferenceEquals(proof.Frame, frame) || !ReferenceEquals(frame.Lifetime, original) ||
            !ReferenceEquals(proof.Completion, frame.Completion) || !ReferenceEquals(proof.QualityOwner, installedQuality) ||
            !ReferenceEquals(proof.ReviewOwner, installedReview) || original.HasExplicitUnknown ||
            frame.BatchEvidenceCapture.Actual is not { IsCompletedSuccessfully: true } capture || !ReferenceEquals(capture, batchEvidenceTask) || !capture.Result ||
            frame.BatchEvidenceCapture.Primary is not null || frame.BatchEvidenceCapture.Post is not null ||
            proof.Phases.Any(row => !ReferenceEquals(row.Phase.Actual, row.Actual) || !row.Actual.IsCompletedSuccessfully || row.Phase.Primary is not null || row.Phase.Post is not null))
            throw new IOException("Actual original/frame/phase references changed after batch evidence capture.");
        var current = installedQuality?.RetainedResults ?? Array.Empty<DeliveryForge.Evidence.GateRunResult>();
        if (current.Count != proof.GateResults.Length || current.Where((row, index) => !ReferenceEquals(row, proof.GateResults[index])).Any())
            throw new IOException("Actual protected gate result references changed after capture.");
        // No current Git, metadata/writer check, or new acceptance is performed
        // here. The writer may now be disposed; the genuine proof remains on
        // this same original frame and completed capture Task.
    }
    internal async Task<BatchNodeCompletion> ReadActualBatchCompletionAsync(ToolLifetime original)
    {
        original.Check();
        if (frames.Count != 1) throw new IOException("Batch node requires exactly one actual original frame.");
        var frame = frames.Values.Single();
        RequireCapturedBatchEvidence(frame, original);
        _ = await frame.BatchEvidenceCapture.ObserveAsync(original).ConfigureAwait(false);
        RequireSettledBatchFrame(frame, original);
        if (batchCompletion is not null) { RequireActualBatchCompletion(batchCompletion, original); return batchCompletion; }
        return batchCompletion = new(this, frame, batchEvidence!.Phase);
    }
    internal void RequireActualBatchCompletion(BatchNodeCompletion actual, ToolLifetime original)
    {
        if (!ReferenceEquals(actual, batchCompletion) || !ReferenceEquals(actual.Issuer, this) ||
            !frames.TryGetValue(actual.RunId, out var retained) || !ReferenceEquals(retained, actual.Frame))
            throw new IOException("Foreign or reconstructed batch completion cannot retire this original.");
        RequireCapturedBatchEvidence(retained, original);
        RequireSettledBatchFrame(retained, original);
    }
    void RequireSettledBatchFrame(HostBridgeFrame frame, ToolLifetime original)
    {
        original.Check();
        if (!ReferenceEquals(frame.Lifetime, original) || original.HasExplicitUnknown || frame.Unknown ||
            !NativeCompletedAndQuiet(frame) || !ObserveCoordinationWorkSettled(frame.RunId) ||
            frame.Scheduler is null || frame.Scheduler.HasRetainedReservation ||
            frame.SchedulingCompletion.Actual is not { IsCompletedSuccessfully: true } settled || !settled.Result)
            throw new IOException("Actual worker, writer, phases and scheduling reservation have not settled under this original.");
        foreach (var phase in new[] { frame.Worker, frame.Quality, frame.Review, frame.IndependentReview, frame.LocalReal })
            if (phase.Started && (phase.Actual is not { IsCompletedSuccessfully: true } || phase.Primary is not null || phase.Post is not null))
                throw new IOException("An actual node phase remains failed or unsettled.");
    }
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
        ToolResult actualResult;
        Exception? pendingFailure = null;
        try
        {
            frame.Worker.Start(original, () => ImplementRetainedCoreAsync(plan, frame));
            actualResult = await frame.Worker.ObserveAsync(original).ConfigureAwait(false);
            if (frame.Scheduler is { } scheduler && frame.Reservation is { } reservation && frame.Primary is null && frame.Post is null && NativeCompletedAndQuiet(frame))
            {
                Exception? evidenceFailure = null;
                if (batchControl is not null)
                {
                    frame.BatchEvidenceCapture.Start(original, () => CaptureActualBatchEvidenceAsync(frame, original));
                    try
                    {
                        _ = await frame.BatchEvidenceCapture.ObserveAsync(original).ConfigureAwait(false);
                        batchEvidenceTask = frame.BatchEvidenceCapture.Actual;
                    }
                    catch (Exception error)
                    {
                        // A completed DATA capture failure cannot issue evidence,
                        // but does not itself make the actual native child live.
                        // Pending calls, an expired endpoint or native uncertainty
                        // still retain every original owner and reservation.
                        if (!SettledBatchEvidenceFailure(frame)) throw;
                        // A known synchronous refusal may already have entered
                        // this parent's own loss-stop. Observe that exact Task;
                        // never issue a second stop or race its writer handling.
                        if (coordination is null) throw;
                        try { _ = await frame.EnterCleanup(_ => coordination.ObserveExistingLossStopAsync(plan.RunId, original)).ConfigureAwait(false); }
                        catch (Exception observation)
                        {
                            throw new AggregateException("Completed evidence capture failed and its original loss-stop observation also failed.", error, observation);
                        }
                        if (!SettledBatchEvidenceFailure(frame) || !ObservePipelineSettled(plan.RunId) ||
                            !CompletedNativeOwnedResourcesClosed(frame)) throw;
                        evidenceFailure = error;
                    }
                }
                if (evidenceFailure is not null)
                {
                    // A failure closes forward WORK. Only the actual completed
                    // worker's retained writer and reservation may retire here.
                    // No failed capture becomes accepted evidence or a batch Pass.
                    lock (frame.ReconciliationGate)
                    {
                        if (frame.SchedulingCleanupEntered) throw new IOException("Original scheduling cleanup already entered.", evidenceFailure);
                        frame.SchedulingCleanupEntered = true;
                        frame.SchedulingCleanup = scheduler.ReleaseCompletedFailureObservedAsync(reservation,
                            () => SettledBatchEvidenceFailure(frame) && ObservePipelineSettled(plan.RunId) && CompletedNativeOwnedResourcesClosed(frame),
                            () => coordinator.ReleaseCompletedWriterAfterFailureAsync(plan.RunId, frame.Completion!,
                                () => SettledBatchEvidenceFailure(frame) && ObservePipelineSettled(plan.RunId) && CompletedNativeOwnedResourcesClosed(frame)));
                    }
                    try { await frame.SchedulingCleanup.WaitAsync(original.CleanupRemaining).ConfigureAwait(false); _ = original.CleanupRemaining; }
                    catch (Exception cleanup) { throw new AggregateException("Original batch evidence failure and same-original cleanup.", evidenceFailure, cleanup); }
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(evidenceFailure).Throw();
                }
                frame.SchedulingCompletion.Start(original, async () =>
                {
                    await scheduler.CompleteObservedAsync(reservation,
                        () => coordinator.ReleaseCompletedWriterAsync(plan.RunId, frame.Completion!, () => ObservePipelineSettled(plan.RunId) && NativeCompletedAndQuiet(frame), original.Token), original.Token).ConfigureAwait(false);
                    return true;
                });
                await frame.SchedulingCompletion.ObserveAsync(original).ConfigureAwait(false);
            }
        }
        catch (Exception error) { pendingFailure = error; throw; }
        finally
        {
            if (!frame.Worker.Started) frame.LeaveRoute();
            var priorFailure = frame.Primary;
            try
            {
                // The existing parent stop can still be finishing its checkpoint
                // after native retirement wakes the worker. Join that SAME task
                // before the root decides that retained bridge work is UNKNOWN.
                if (coordination is { HasEnteredLossStop: true })
                    _ = await frame.EnterCleanup(_ => coordination.ObserveExistingLossStopAsync(plan.RunId, original)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var primary = pendingFailure ?? priorFailure;
                frame.Primary ??= primary ?? error;
                original.RetainUnknown();
                if (primary is not null && !ReferenceEquals(primary, error))
                    throw new AggregateException("Original host operation and existing parent loss-stop observation failed.", primary, error);
                throw;
            }
            finally { await FinishStoppedSchedulingAsync(frame).ConfigureAwait(false); frame.CloseWhenSettled(); }
        }
        return RetainedResult(frame, actualResult);
    }
    async Task FinishStoppedSchedulingAsync(HostBridgeFrame frame)
    {
        if (frame.Scheduler is not { HasRetainedReservation: true } scheduler || frame.Reservation is not { } ticket || !ObservePipelineSettled(frame.RunId)) return;
        bool NoWorkerStart() => frame.Accepted is null && frame.Request is null && !frame.Lifetime.HasExplicitUnknown && !adapter.ActualCalls.Any(call => call.RunId == frame.RunId && call.Kind == "Start") && ObservePipelineSettled(frame.RunId);
        var unprovisioned = !frame.OwnerStopped && scheduler.CanReleaseUnprovisioned && NoWorkerStart();
        if (!frame.OwnerStopped && !unprovisioned) return;
        try
        {
            bool enter;
            lock (frame.ReconciliationGate) { enter = !frame.SchedulingCleanupEntered; frame.SchedulingCleanupEntered = true; }
            if (enter) frame.SchedulingCleanup = unprovisioned ? scheduler.ReleaseUnprovisionedObservedAsync(ticket, NoWorkerStart) : scheduler.ReleaseStoppedObservedAsync(ticket,
                () => frame.OwnerStopped && ObservePipelineSettled(frame.RunId),
                () => coordinator.ReleaseStoppedWriterAsync(frame.RunId, () => frame.OwnerStopped && ObservePipelineSettled(frame.RunId)));
            var actual = frame.SchedulingCleanup ?? throw new IOException("Original scheduler cleanup invocation has not returned its task.");
            await actual.WaitAsync(frame.Lifetime.CleanupRemaining).ConfigureAwait(false); _ = frame.Lifetime.CleanupRemaining;
        }
        catch (Exception error) { frame.Primary ??= error; frame.Lifetime.RetainBridgeUnknown(frame); }
    }
    static string ValidateWorkerResultDirectory(FrozenWorkerPlan plan, ToolLifetime original)
    {
        original.Check();
        var path = BoundedFiles.Plain(plan.ResultDirectory, true);
        BoundedFiles.Separate(plan.ParentCheckout, path);
        BoundedFiles.Separate(plan.Worktree, path);
        original.Check();
        if (Directory.Exists(path))
        {
            var populated = Directory.EnumerateFileSystemEntries(path).Take(1).Any();
            original.Check();
            if (populated) throw new IOException("Worker result directory must be absent or empty; retained evidence cannot be adopted.");
        }
        original.Check();
        return path;
    }
    Task<bool> PrepareWorkerResultDirectoryAsync(HostBridgeFrame frame, AgentRequest request, FrozenWorkerPlan plan)
    {
        return frame.Enter(() =>
        {
            var creationEntered = false;
            try
            {
                bool Prepare()
                {
                    try
                    {
                        var path = ValidateWorkerResultDirectory(plan, frame.Lifetime);
                        if (!Directory.Exists(path))
                        {
                            creationEntered = true;
                            Directory.CreateDirectory(path);
                        }
                        frame.Lifetime.Check();
                        _ = BoundedFiles.Plain(path, true);
                        return true;
                    }
                    catch
                    {
                        // The actual parent's finally must observe uncertainty
                        // before its synchronous effect invocation returns.
                        if (creationEntered) frame.Lifetime.RetainUnknown();
                        throw;
                    }
                }
                return Task.FromResult(coordination is null ? Prepare() :
                    coordination.EnterSynchronous(request.RunId, Prepare));
            }
            catch (Exception error)
            {
                // Also retain a failed parent postcheck after Prepare returned.
                if (creationEntered)
                {
                    frame.Lifetime.RetainUnknown();
                    try { coordination?.RetainUncertain(request.RunId, "Worker result directory creation or its original postcheck did not settle."); }
                    catch (Exception retention) { return Task.FromException<bool>(new AggregateException("Result directory preparation and parent uncertainty retention failed.", error, retention)); }
                }
                return Task.FromException<bool>(error);
            }
        });
    }
    async Task<ToolResult> ImplementRetainedCoreAsync(FrozenWorkerPlan plan, HostBridgeFrame frame)
    {
        try
        {
            // Known path refusals precede lease, reservation, provisioning and writer effects.
            await frame.Enter(() =>
            {
                try { _ = ValidateWorkerResultDirectory(plan, frame.Lifetime); return Task.FromResult(true); }
                catch (Exception error) { return Task.FromException<bool>(error); }
            }).ConfigureAwait(false);
            if (coordination is not null) await coordination.AcquireAsync(plan.RunId).ConfigureAwait(false);
            if (contractFixtureProvisioning is { } fixture)
                await frame.Enter(() => fixture.ProvisionAsync(plan, coordination ?? throw new ToolUsageException("Actual acquired fixture parent required."))).ConfigureAwait(false);
            AgentRequest request;
            if (localSingleFactory is not null)
            {
                var scheduler = localSingleFactory(schedulingPlan ?? throw new ToolUsageException("Actual frozen scheduling plan required."), plan, coordination ?? throw new ToolUsageException("Actual acquired parent required."));
                frame.Scheduler = scheduler;
                scheduler.BindOriginal(frame.Lifetime, coordination, () => frame.Worker.Actual, () => ObservePipelineSettled(plan.RunId) && NativeCompletedAndQuiet(frame), () => frame.Lifetime.HasExplicitUnknown);
                frame.Reservation = await frame.Enter(() => scheduler.ReserveAsync(frame.Lifetime.Token)).ConfigureAwait(false);
                request = await frame.Enter(() => scheduler.ProvisionAndPrepareAsync(frame.Reservation, frame.Lifetime.Token)).ConfigureAwait(false);
            }
            else request = (await frame.Enter(() => coordinator.PrepareWorkerAsync(plan, frame.Lifetime.Token)).ConfigureAwait(false)).Request;
            frame.Request = request;
            await PrepareWorkerResultDirectoryAsync(frame, request, plan).ConfigureAwait(false);
            frame.Accepted = await frame.Enter(() => batchControl is null
                ? adapter.StartAsync(request, frame.Lifetime.Remaining, frame.Lifetime.Token)
                : batchControl.EnterNodeStart(() => adapter.StartAsync(request, frame.Lifetime.Remaining, frame.Lifetime.Token))).ConfigureAwait(false);
            await frame.Enter(() => coordinator.AcceptAsync(frame.Accepted ?? throw new WorkerEnvelopeException("Missing actual acceptance."), frame.Lifetime.Token)).ConfigureAwait(false);
            var a = frame.Accepted ?? throw new WorkerEnvelopeException("Missing actual acceptance."); var binding = new AgentRunBinding(a.AdapterId, a.AdapterVersion, a.RunId, a.RequestIdentity, a.RuntimeRunIdentity, a.RuntimeTaskIdentity);
            frame.Completion = await frame.Enter(() => adapter.WaitAsync(binding, frame.Lifetime.Remaining, frame.Lifetime.Token)).ConfigureAwait(false);
            WorkerEnvelope.ValidateCompletion(request, a, frame.Completion ?? throw new WorkerEnvelopeException("Missing actual completion."));
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
            if (localRealGolden is not null && installedQuality?.HasLocalRealCompletion(frame) != true)
                return qualityResult ?? ToolResult.Incomplete("LOCAL_REAL_PROTECTED_BASELINE_REQUIRED", "Actual protected quality and independently approved baseline completion are required before local-real review/publication.");
            if (!MayEnterIndependentReviewAfterQuality(qualityResult, installedQuality, frame)) return qualityResult!;
            if (installedReview?.HasIndependentReviewer == true)
            {
                if (coordination is null || !NativeCompletedAndQuiet(frame) || frame.Quality.Unknown || installedQuality?.Unknown == true || frame.EnteredTasks.Any(task => !task.IsCompleted)) throw new WorkerEnvelopeException("Actual independent review requires its same original completed author and settled quality originals.");
                var completion = frame.Completion ?? throw new WorkerEnvelopeException("Actual original completion is absent.");
                frame.IndependentReview.Start(frame.Lifetime, () => installedReview.RunIndependentAsync(frame, coordination, adapter.CompletedImplementationFamily(completion),
                    () => NativeCompletedAndQuiet(frame) && !frame.Quality.Unknown && installedQuality?.Unknown != true, installedQuality?.RetainedResults ?? Array.Empty<DeliveryForge.Evidence.GateRunResult>()));
                var reviewed = await frame.IndependentReview.ObserveAsync(frame.Lifetime).ConfigureAwait(false);
                if (localRealGolden is not null)
                {
                    frame.LocalReal.Start(frame.Lifetime, () => localRealGolden.RunAsync(frame, coordination, installedQuality, installedReview, () => NativeCompletedAndQuiet(frame), coordinator.StoreRootPath));
                    return await frame.LocalReal.ObserveAsync(frame.Lifetime).ConfigureAwait(false);
                }
                if (qualityResult is null) return reviewed;
                return qualityResult with { Data = new { Quality = qualityResult.Data, Review = reviewed, ReviewPreparation = reviewPreparation } };
            }
            if (localRealGolden is not null) return ToolResult.Incomplete("LOCAL_REAL_ACCEPTED_REVIEW_REQUIRED", "Explicit local-real selection requires the actual same-original independent reviewer and protected quality; no fixture fallback.");
            if (qualityResult is not null) return reviewPreparation is null ? qualityResult : qualityResult with { Data = new { Quality = qualityResult.Data, ReviewPreparation = reviewPreparation } };
            if (reviewPreparation is not null) return reviewPreparation;
            return new("COMPLETE", adapter.Realm == HostObservationRealm.ContractFixture ? "CONTRACT_FIXTURE_COMPLETE" : "OWNER_COMPLETED_REQUIRES_PROFILE_AUDIT", "Actual owner completion recorded; quality/review/publication and installed certification remain separate.", new { committed.RunId, committed.Sequence, committed.Sha256, Realm = adapter.Realm.ToString() });
        }
        catch (Exception error) { frame.Primary ??= error; return ToolResult.Incomplete("HOST_OPERATION_RETAINED", "Actual host/owner operation failed or remains unknown; no retry or invented quiescence.", new { frame.RunId, frame.Unknown, Type = error.GetType().FullName, OwnerCompleted = frame.OwnerCompleted }); }
        finally { frame.LeaveRoute(); frame.CloseWhenSettled(); }
    }
    internal static bool SettledBatchEvidenceFailure(HostBridgeFrame frame) =>
        frame.BatchEvidenceCapture.Actual is { IsCompleted: true, IsCompletedSuccessfully: false } &&
        frame.BatchEvidenceCapture.Primary is not null &&
        !frame.BatchEvidenceCapture.Unknown && !frame.Lifetime.HasExplicitUnknown;
    bool NativeCompletedAndQuiet(HostBridgeFrame frame) => !frame.OwnerStopped && frame.Primary is null && frame.Post is null && CompletedNativeOwnedResourcesClosed(frame);
    bool CompletedNativeOwnedResourcesClosed(HostBridgeFrame frame) => frame.OwnerCompleted &&
        frame.Completion is not null &&
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
        finally { frame.LeaveRoute(); await FinishStoppedSchedulingAsync(frame).ConfigureAwait(false); frame.CloseWhenSettled(); }
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
