using System.Collections.Concurrent;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Tool.Codex;
namespace DeliveryForge.Tool.Pi;

public sealed record PiRpcSelection(string Executable, long Bytes, string Sha256, string Version);
// Actual runtime conformance must admit this installed version before construction
// by the privileged containing composition. A configuration file cannot certify it.
public sealed class PiRpcPort : IHostToolSessionPort, IOriginalBoundHostPort, IHostInstructionPort
{
    readonly LinuxScopeSelection? linuxScope;
    HostInstructionContext? instructions;
    public Exception? Primary { get; private set; }
    public Exception? CleanupFailure { get; private set; }
    public Exception? StopRequestFailure { get; private set; }
    public void BindInstructions(HostInstructionContext context) { lock (gate) { if (entered || instructions is not null && !ReferenceEquals(instructions, context)) throw new WorkerEnvelopeException("Pi instructions cannot change after binding or dispatch."); instructions = context; } }
    readonly PiRpcSelection selection; readonly WorktreeManager git; readonly object gate = new();
    ToolLifetime? bound; ToolLifetime Original => bound ?? throw new ToolUsageException("Actual containing original is not bound.");
    readonly ConcurrentDictionary<string, (string Command, TaskCompletionSource<JsonElement> Reply)> pending = new(StringComparer.Ordinal);
    readonly List<JsonElement> trace = []; readonly List<Operation> operations = []; readonly PiRpcProtocol state = new();
    readonly TaskCompletionSource<bool> settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    OwnedDuplexProcess? process; CodexJsonlProtocol? framing; Task? pump; Exception? pumpFailure;
    AgentRequest? request; AgentRunBinding? binding; string? sessionId, promptId; long nextId; bool entered, shutdown, stopEntered;
    public sealed class Operation { public bool Entered { get; internal set; } public bool Returned { get; internal set; } public Task? Task { get; internal set; } public Exception? Failure { get; internal set; } public bool Unknown => Entered && (!Returned || Task is null || !Task.IsCompleted); }
    public IReadOnlyList<Operation> ActualOperations { get { lock (operations) return operations.ToArray(); } }
    public OwnedDuplexProcess? OriginalProcess => process;
    public AgentAdapterProfile Profile { get; }
    public HostObservationRealm Realm => HostObservationRealm.ActualHost;
    public PiRpcPort(PiRpcSelection selection, WorktreeManager actualGit, AgentAdapterProfile acceptedProfile, LinuxScopeSelection? linuxScope = null)
    {
        this.linuxScope = linuxScope;
        ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(acceptedProfile);
        if (acceptedProfile.Capabilities is null || acceptedProfile.Capabilities.Count is < 1 or > 6 || acceptedProfile.Capabilities.Any(row => row is null) || string.IsNullOrWhiteSpace(selection.Version) || selection.Version.Length > 256) throw new ToolUsageException("Finite complete actual native version/profile required BEFORE capability snapshot.");
        if (acceptedProfile.AdapterId != "pi.cli" || acceptedProfile.AdapterVersion != selection.Version || acceptedProfile.Capabilities.Any(x => (x.Capability is AgentCapability.Pause or AgentCapability.Resume) && x.Status == AgentCapabilityStatus.Supported)) throw new ToolUsageException("Exact Pi profile required; abort is not pause or same-run resume.");
        this.selection = selection; git = actualGit; Profile = acceptedProfile with { Capabilities = Array.AsReadOnly(acceptedProfile.Capabilities.ToArray()) };
    }
    public void BindOriginal(ToolLifetime original) { lock (gate) { if (bound is not null && !ReferenceEquals(bound, original)) throw new WorkerEnvelopeException("Pi original cannot be renewed or rebound."); original.Check(); bound = original; } }
    Operation Admit() { lock (operations) { if (operations.Count >= 128) throw new IOException("Finite Pi actual-task ledger exhausted before invocation."); var row = new Operation { Entered = true }; operations.Add(row); return row; } }
    Task<T> Begin<T>(Func<Task<T>> invoke) { var row = Admit(); try { var actual = invoke(); row.Task = actual; row.Returned = true; return actual ?? throw new IOException("Actual Pi call returned no task."); } catch (Exception error) { row.Failure = error; throw; } }
    Task Begin(Func<Task> invoke) { var row = Admit(); try { var actual = invoke(); row.Task = actual; row.Returned = true; return actual ?? throw new IOException("Actual Pi call returned no task."); } catch (Exception error) { row.Failure = error; throw; } }
    async Task<JsonElement> Command(string command, Func<string, object> payload, CancellationToken token)
    {
        Original.Check(); var index = Interlocked.Increment(ref nextId); if (index > 16) throw new IOException("Fixed Pi command allowance exceeded."); var id = "delivery-forge-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); if (!pending.TryAdd(id, (command, completion))) throw new IOException("Duplicate Pi command ID.");
        await Begin(() => framing!.WriteAsync(payload(id), token)).ConfigureAwait(false);
        return await Begin(() => completion.Task).WaitAsync(Original.Remaining, token).ConfigureAwait(false);
    }
    async Task Pump(CancellationToken token)
    {
        try
        {
            while (true)
            {
                using var document = await framing!.ReadAsync(token).ConfigureAwait(false); if (document is null) { if (!shutdown) throw new IOException("Unexpected actual Pi EOF."); return; }
                var row = document.RootElement; var type = PiRpcProtocol.Text(row, "type"); lock (trace) { if (trace.Count >= 4096) throw new IOException("Pi complete event trace exceeded."); trace.Add(row.Clone()); }
                if (type == "response") { var id = PiRpcProtocol.Text(row, "id"); if (!pending.TryRemove(id, out var actual)) throw new IOException("Foreign or duplicate Pi command response."); try { actual.Reply.TrySetResult(PiRpcProtocol.Response(row, id, actual.Command)); } catch (Exception failure) { actual.Reply.TrySetException(failure); } continue; }
                if (type == "extension_ui_request") { lock (gate) state.ObserveEvent(row); var id = PiRpcProtocol.Text(row, "id"); var method = PiRpcProtocol.Text(row, "method"); if (method is "select" or "confirm" or "input" or "editor") await framing.WriteAsync(new { type = "extension_ui_response", id, cancelled = true }, token).ConfigureAwait(false); continue; }
                lock (gate) { state.ObserveEvent(row); if (state.Settled) settled.TrySetResult(true); }
            }
        }
        catch (Exception failure) { pumpFailure = failure; settled.TrySetException(failure); foreach (var call in pending.Values) call.Reply.TrySetException(failure); if (!shutdown) Original.RetainUnknown(); throw; }
    }
    public async Task<AgentAcceptedReceipt> StartAsync(AgentRequest actual, TimeSpan remaining, CancellationToken token)
    {
        Original.Check(); (instructions ?? throw new WorkerEnvelopeException("Actual parent instructions required before Pi dispatch.")).Require(actual); lock (gate) { if (entered) throw new WorkerEnvelopeException("Actual Pi dispatch entered; no retry."); entered = true; request = actual; }
        try
        {
            if (actual.AdapterId != "pi.cli" || actual.AdapterVersion != selection.Version || remaining <= TimeSpan.Zero || remaining > Original.WorkLimit) throw new WorkerEnvelopeException("Pi request/version/deadline differs from containing original.");
            // Reserve both actual exact-image reads before pathname hashing or birth.
            LinuxScopeImage? exactImage = null;
            if (OperatingSystem.IsLinux())
            {
                if (linuxScope is null) throw new PlatformNotSupportedException("Actual selected Linux scope required before Pi native image admission.");
                BoundedFiles.ReserveExactLinuxImageReads(new(selection.Executable, selection.Bytes, selection.Sha256), Original);
                exactImage = new(selection.Executable, selection.Bytes, selection.Sha256);
            }
            BoundedFiles.VerifyNativeImage(new(selection.Executable, selection.Bytes, selection.Sha256), Original);
            var environment = new Dictionary<string, string> { ["DOTNET_PROCESSOR_COUNT"] = "2", ["DOTNET_GCHeapHardLimit"] = "0x100000000", ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1", ["MSBUILDDISABLENODEREUSE"] = "1" };
            process = OwnedDuplexProcess.Open(new(selection.Executable, ["--mode", "rpc", "--no-session"], actual.Worktree, environment), Original, linuxScope, exactImage); if (process.StartupFailure is Exception failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            if (exactImage is not null && !process.HasCreationOwnedExactLinuxImage) throw new IOException("Actual creation-owned exact Linux image proof required before Pi protocol startup.");
            framing = new(process.Input, process.Output, Original); pump = Begin(() => Pump(Original.Token)); process.AttachOriginalOutputConsumer(pump);
            var before = await Command("get_state", id => new { id, type = "get_state" }, token).ConfigureAwait(false); sessionId = PiRpcProtocol.RequireIdleSession(before);
            var acknowledgement = await Command("prompt", id => { promptId = id; lock (gate) state.AdmitPrompt(); return HostAssignmentPrompt.PiPrompt(actual, instructions!, id); }, token).ConfigureAwait(false); PiRpcProtocol.RequireStarted(acknowledgement);
            lock (gate) binding = new(actual.AdapterId, actual.AdapterVersion, actual.RunId, actual.RequestIdentity, sessionId, promptId!);
            Original.Check(); return new(actual.SchemaVersion, actual.RunId, actual.RequestIdentity, actual.PlanIdentity, actual.BaseCommit, actual.Worktree, actual.AdapterId, actual.AdapterVersion, sessionId, promptId!, DateTimeOffset.UtcNow, Profile.Capabilities);
        }
        catch (Exception primary) { Primary = primary; if (process is not null) { shutdown = true; try { var retired = await Begin(() => process.RetireAsync(true)).ConfigureAwait(false); if (!retired.KnownClosed) Original.RetainUnknown(); } catch (Exception cleanup) { CleanupFailure = cleanup; throw new AggregateException("Pi start primary and independent original retirement failure.", primary, cleanup); } } throw; }
    }
    void Bound(AgentRunBinding actual) { lock (gate) if (binding is null || binding != actual) throw new WorkerEnvelopeException("No privately accepted Pi session/prompt binding."); }
    public async Task<AgentCompletionReceipt> WaitAsync(AgentRunBinding actual, TimeSpan remaining, CancellationToken token)
    {
        Bound(actual); Original.Check(); if (remaining <= TimeSpan.Zero || remaining > Original.WorkLimit) throw new ArgumentOutOfRangeException(nameof(remaining));
        await Begin(() => settled.Task).WaitAsync(remaining < Original.Remaining ? remaining : Original.Remaining, token).ConfigureAwait(false); lock (gate) state.RequireSuccess();
        var after = await Command("get_state", id => new { id, type = "get_state" }, token).ConfigureAwait(false); _ = PiRpcProtocol.RequireIdleSession(after, sessionId);
        shutdown = true; var retirement = await Begin(() => process!.RetireAsync(false)).ConfigureAwait(false); HostTransportRetirement.RequireSuccessful(retirement);
        if (pump is not null) await pump.WaitAsync(Original.Remaining, token).ConfigureAwait(false); if (pumpFailure is not null) throw new IOException("Pi stream failed.", pumpFailure); Original.Check(); lock (gate) state.RequireSuccess();
        var snapshot = await Begin(() => git.ObserveAsync(request!.Worktree, token)).WaitAsync(Original.Remaining, token).ConfigureAwait(false); Original.Check();
        JsonElement[] rows; lock (trace) rows = trace.ToArray(); var artifact = Path.Combine(request!.ResultDirectory, "pi-rpc-events.json"); BoundedFiles.WriteNew(artifact, JsonSerializer.SerializeToUtf8Bytes(new { Kind = "ActualPiRpcTrace-v1", actual.RunId, actual.RequestIdentity, SessionId = sessionId, PromptCommandId = promptId, Events = rows, Retirement = retirement, RuntimeCertified = false }, BoundedFiles.Json), Original);
        return new(request.SchemaVersion, request.RunId, request.RequestIdentity, request.AdapterId, request.AdapterVersion, actual.RuntimeRunIdentity, actual.RuntimeTaskIdentity, "success", request.Worktree, snapshot.HeadCommit, snapshot.TreeId, DateTimeOffset.UtcNow, [artifact], ["Actual transport only; separate installed conformance/current quality and independent review remain required."]);
    }
    public async Task<AgentControlResult> ControlAsync(AgentRunBinding actual, AgentControlAction action, TimeSpan remaining, CancellationToken token)
    {
        Bound(actual); if (action != AgentControlAction.Stop) return new(actual, action, AgentActivityState.Unknown, false, "", "Pi abort cannot pause or resume the same accepted run.");
        lock (gate) { if (stopEntered) throw new WorkerEnvelopeException("Pi original stop already entered; no retry."); stopEntered = true; }
        Exception? abortFailure = null; try { _ = await Command("abort", id => new { id, type = "abort" }, token).ConfigureAwait(false); } catch (Exception failure) { abortFailure = failure; StopRequestFailure = failure; }
        shutdown = true; var retirement = await Begin(() => process!.RetireAsync(true)).ConfigureAwait(false); settled.TrySetException(new OperationCanceledException("Actual original Pi stop.")); foreach (var row in pending.Values) row.Reply.TrySetException(new OperationCanceledException("Original Pi stopped; no replay."));
        return new(actual, action, retirement.KnownClosed ? AgentActivityState.Quiescent : AgentActivityState.Unknown, retirement.KnownClosed, "actual-private-original-pi-retirement", "Abort acknowledgement alone is not quiescence. Original native/IO retirement joined; abort failure=" + abortFailure?.GetType().FullName);
    }
    public Task<AgentControlResult> ObserveUnstartedAsync(AgentRunBinding actual, TimeSpan remaining, CancellationToken token) { Original.Check(); lock (gate) { var absent = !entered && process is null; return Task.FromResult(new AgentControlResult(actual, AgentControlAction.Stop, absent ? AgentActivityState.Quiescent : AgentActivityState.Unknown, absent, absent ? "actual-private-pi-never-entered" : "", "No DATA/PID adoption.")); } }
}
