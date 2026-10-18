using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
namespace DeliveryForge.Tool.Codex;

public sealed record CodexAppServerSelection(string Executable, long Bytes, string Sha256, string Version);
// The accepted profile is supplied by the actual privileged containing composition
// after independent version-specific conformance. Configuration does not issue it.
public sealed class CodexAppServerPort : IHostToolSessionPort, IOriginalBoundHostPort, IHostInstructionPort
{
    readonly LinuxScopeSelection? linuxScope;
    HostInstructionContext? instructions;
    public void BindInstructions(HostInstructionContext context) { lock (gate) { if (startEntered || instructions is not null && !ReferenceEquals(instructions, context)) throw new WorkerEnvelopeException("Actual instructions cannot change after selection or dispatch."); instructions = context; } }
    readonly CodexAppServerSelection selection; ToolLifetime? boundOriginal; readonly WorktreeManager actualGit;
    ToolLifetime original => boundOriginal ?? throw new ToolUsageException("The actual containing original is not bound.");
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new(); readonly List<JsonElement> trace = []; readonly object gate = new();
    readonly Dictionary<string, JsonElement> terminals = new(StringComparer.Ordinal); readonly List<Task> tasks = [];
    OwnedDuplexProcess? process; CodexJsonlProtocol? protocol; Task? pump; AgentRequest? request; AgentRunBinding? binding;
    TaskCompletionSource<JsonElement>? terminalWait; long nextId; bool startEntered, shutdown; string? threadId, turnId; Exception? pumpFailure;
    public AgentAdapterProfile Profile { get; }
    public HostObservationRealm Realm => HostObservationRealm.ActualHost;
    public IReadOnlyList<Task> OriginalTasks { get { lock (tasks) return tasks.ToArray(); } }
    public OwnedDuplexProcess? OriginalProcess => process; // reference only, never a PID adoption API.
    public CodexAppServerPort(CodexAppServerSelection selection, WorktreeManager actualGit, AgentAdapterProfile acceptedProfile, LinuxScopeSelection? linuxScope = null)
    {
        this.linuxScope = linuxScope;
        ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(acceptedProfile);
        if (acceptedProfile.Capabilities is null || acceptedProfile.Capabilities.Count is < 1 or > 6 || acceptedProfile.Capabilities.Any(row => row is null) || string.IsNullOrWhiteSpace(selection.Version) || selection.Version.Length > 256) throw new ToolUsageException("Finite complete actual native version/profile required BEFORE capability snapshot.");
        if (acceptedProfile.AdapterId != "codex.cli" || acceptedProfile.AdapterVersion != selection.Version || acceptedProfile.Capabilities.Any(x => (x.Capability is AgentCapability.Pause or AgentCapability.Resume) && x.Status == AgentCapabilityStatus.Supported)) throw new ToolUsageException("Stable app-server has interrupt, not same-turn pause/resume; exact accepted Codex version required.");
        this.selection = selection; this.actualGit = actualGit; Profile = acceptedProfile with { Capabilities = Array.AsReadOnly(acceptedProfile.Capabilities.ToArray()) };
    }
    public void BindOriginal(ToolLifetime actualOriginal) { lock (gate) { if (boundOriginal is not null && !ReferenceEquals(boundOriginal, actualOriginal)) throw new WorkerEnvelopeException("Codex original cannot be rebound or renewed."); actualOriginal.Check(); boundOriginal = actualOriginal; } }
    Task<T> Keep<T>(Task<T> actual) { lock (tasks) { if (tasks.Count >= 1024) throw new IOException("Finite actual Codex task ledger exhausted."); tasks.Add(actual); } return actual; }
    Task Keep(Task actual) { lock (tasks) { if (tasks.Count >= 1024) throw new IOException("Finite actual Codex task ledger exhausted."); tasks.Add(actual); } return actual; }
    async Task<JsonElement> Rpc(string method, object parameters, CancellationToken token)
    {
        original.Check(); var id = Interlocked.Increment(ref nextId); if (id > 64) throw new IOException("Fixed Codex RPC allowance exceeded."); var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); if (!pending.TryAdd(id, reply)) throw new IOException("Codex duplicate private request ID.");
        await Keep(protocol!.WriteAsync(new { id, method, @params = parameters }, token)).ConfigureAwait(false);
        return await Keep(reply.Task).WaitAsync(original.Remaining, token).ConfigureAwait(false);
    }
    async Task Pump(CancellationToken token)
    {
        try
        {
            while (true)
            {
                using var doc = await protocol!.ReadAsync(token).ConfigureAwait(false); if (doc is null) { if (!shutdown) throw new IOException("Unexpected app-server EOF."); return; }
                var message = doc.RootElement; CodexJsonlProtocol.RequireEnvelope(message); lock (trace) { if (trace.Count >= 4096) throw new IOException("Codex event trace exceeded."); trace.Add(message.Clone()); }
                if (message.TryGetProperty("method", out var method))
                {
                    if (message.TryGetProperty("id", out var serverId))
                    {
                        // No invented user approval, experimental tool dispatch or
                        // auth/settings mutation. Unsupported client request fails.
                        await protocol.WriteAsync(new { id = serverId.Clone(), error = new { code = -32601, message = "Unsupported client request; no approval or capability granted." } }, token).ConfigureAwait(false); continue;
                    }
                    if (method.GetString() == "turn/completed")
                    {
                        var parameters = message.GetProperty("params"); var actualThread = Text(parameters, "threadId"); var turn = parameters.GetProperty("turn"); var actualTurn = Text(turn, "id"); var status = Text(turn, "status"); if (status is not ("completed" or "interrupted" or "failed")) throw new IOException("Invalid terminal Codex status.");
                        lock (gate) { if (threadId is not null && actualThread != threadId) throw new IOException("Foreign Codex terminal thread."); if (terminals.Count >= 8 || terminals.ContainsKey(actualTurn)) throw new IOException("Duplicate/excess terminal event."); terminals.Add(actualTurn, message.Clone()); if (turnId == actualTurn) terminalWait?.TrySetResult(message.Clone()); }
                    }
                    continue;
                }
                var responseId = message.GetProperty("id"); if (!responseId.TryGetInt64(out var number) || !pending.TryRemove(number, out var waiter)) throw new IOException("Foreign/duplicate Codex response ID.");
                if (message.TryGetProperty("error", out var error)) { waiter.TrySetException(new IOException("Actual app-server response error: " + error.GetRawText())); continue; }
                waiter.TrySetResult(message.GetProperty("result").Clone());
            }
        }
        catch (Exception error) { pumpFailure = error; foreach (var waiter in pending.Values) waiter.TrySetException(error); lock (gate) terminalWait?.TrySetException(error); if (!shutdown) original.RetainUnknown(); throw; }
    }
    public async Task<AgentAcceptedReceipt> StartAsync(AgentRequest actual, TimeSpan remaining, CancellationToken token)
    {
        original.Check(); (instructions ?? throw new WorkerEnvelopeException("Actual parent instructions required before any Codex dispatch.")).Require(actual); lock (gate) { if (startEntered) throw new WorkerEnvelopeException("App-server dispatch already entered; no retry."); startEntered = true; request = actual; }
        try
        {
            if (actual.AdapterId != "codex.cli" || actual.AdapterVersion != selection.Version || remaining <= TimeSpan.Zero || remaining > original.WorkLimit) throw new WorkerEnvelopeException("Actual request/version/deadline differs from containing original.");
            if (selection.Bytes is <= 0 or > 268435456) throw new IOException("Explicit Codex image profile exceeds256MiB."); var image = BoundedFiles.Read(selection.Executable, original, 268435456); if (image.LongLength != selection.Bytes || BoundedFiles.Sha(image) != selection.Sha256) throw new IOException("Selected actual Codex image changed.");
            var environment = new Dictionary<string, string> { ["DOTNET_PROCESSOR_COUNT"] = "2", ["DOTNET_GCHeapHardLimit"] = "0x100000000", ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1", ["MSBUILDDISABLENODEREUSE"] = "1" };
            process = OwnedDuplexProcess.Open(new(selection.Executable, ["app-server"], actual.Worktree, environment), original, linuxScope); if (process.StartupFailure is Exception failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            protocol = new(process.Input, process.Output, original); pump = Pump(original.Token); process.AttachOriginalOutputConsumer(pump); lock (tasks) tasks.Add(pump);
            _ = await Rpc("initialize", new { clientInfo = new { name = "delivery_forge", title = "Delivery Forge", version = "0.1.0" }, capabilities = new { experimentalApi = false } }, token).ConfigureAwait(false);
            await protocol.WriteAsync(new { method = "initialized", @params = new { } }, token).ConfigureAwait(false);
            // Omit model/provider/approval overrides: preserve actual configured
            // model, existing sign-in and host policy. No experimental fields.
            var started = await Rpc("thread/start", new { cwd = actual.Worktree }, token).ConfigureAwait(false); threadId = Text(started.GetProperty("thread"), "id");
            var turn = await Rpc("turn/start", HostAssignmentPrompt.CodexTurnStart(actual, instructions!, threadId), token).ConfigureAwait(false); turnId = Text(turn.GetProperty("turn"), "id");
            lock (gate) { terminalWait = new(TaskCreationOptions.RunContinuationsAsynchronously); if (terminals.TryGetValue(turnId, out var early)) { if (Text(early.GetProperty("params"), "threadId") != threadId) throw new IOException("Early terminal has foreign actual thread."); terminalWait.TrySetResult(early); } binding = new(actual.AdapterId, actual.AdapterVersion, actual.RunId, actual.RequestIdentity, threadId, turnId); }
            original.Check(); return new(actual.SchemaVersion, actual.RunId, actual.RequestIdentity, actual.PlanIdentity, actual.BaseCommit, actual.Worktree, actual.AdapterId, actual.AdapterVersion, threadId, turnId, DateTimeOffset.UtcNow, Profile.Capabilities);
        }
        catch { if (process is not null) { var stop = process.RetireAsync(true); lock (tasks) tasks.Add(stop); await stop.ConfigureAwait(false); } throw; }
    }
    void Bound(AgentRunBinding actual) { lock (gate) if (binding is null || binding != actual) throw new WorkerEnvelopeException("No exact privately accepted Codex thread/turn binding."); }
    public async Task<AgentCompletionReceipt> WaitAsync(AgentRunBinding actual, TimeSpan remaining, CancellationToken token)
    {
        Bound(actual); original.Check(); var completed = await Keep(terminalWait!.Task).WaitAsync(remaining < original.Remaining ? remaining : original.Remaining, token).ConfigureAwait(false); var status = Text(completed.GetProperty("params").GetProperty("turn"), "status"); if (status != "completed") throw new WorkerEnvelopeException("Actual Codex turn ended " + status + "; no successful completion inferred.");
        shutdown = true; var retired = await Keep(process!.RetireAsync(false)).ConfigureAwait(false); HostTransportRetirement.RequireSuccessful(retired);
        if (pump is not null) await pump.WaitAsync(original.Remaining, token).ConfigureAwait(false); if (pumpFailure is not null) throw new IOException("Actual app-server protocol failed; no successful completion.", pumpFailure);
        var snapshotTask = actualGit.ObserveAsync(request!.Worktree, token); var snapshot = await Keep(snapshotTask).WaitAsync(original.Remaining, token).ConfigureAwait(false); original.Check();
        original.Check(); var artifact = Path.Combine(request.ResultDirectory, "codex-app-server-events.json"); JsonElement[] rows; lock (trace) rows = trace.ToArray(); var raw = JsonSerializer.SerializeToUtf8Bytes(new { Kind = "ActualCodexAppServerTrace-v1", actual.RunId, actual.RequestIdentity, ThreadId = threadId, TurnId = turnId, Events = rows, Retirement = retired, PumpFailure = pumpFailure?.GetType().FullName, RuntimeCertified = false }, BoundedFiles.Json); BoundedFiles.WriteNew(artifact, raw, original);
        return new(request.SchemaVersion, request.RunId, request.RequestIdentity, request.AdapterId, request.AdapterVersion, actual.RuntimeRunIdentity, actual.RuntimeTaskIdentity, "success", request.Worktree, snapshot.HeadCommit, snapshot.TreeId, DateTimeOffset.UtcNow, [artifact], ["Actual transport only; independent review, real quality/current-source and installed conformance remain separate."]);
    }
    public async Task<AgentControlResult> ControlAsync(AgentRunBinding actual, AgentControlAction action, TimeSpan remaining, CancellationToken token)
    {
        Bound(actual); if (action != AgentControlAction.Stop) return new(actual, action, AgentActivityState.Unknown, false, "", "Stable app-server interrupt is not pause or same-turn resume.");
        Exception? interruptFailure = null;
        try { if (turnId is not null && terminalWait?.Task.IsCompleted != true) _ = await Rpc("turn/interrupt", new { threadId, turnId }, token).ConfigureAwait(false); } catch (Exception error) { interruptFailure = error; }
        // WORK may already be closed by the containing stop latch. Regardless of
        // interrupt availability, only this original owner can stop its own Job.
        shutdown = true; var retired = await Keep(process!.RetireAsync(true)).ConfigureAwait(false); lock (gate) terminalWait?.TrySetException(new OperationCanceledException("Original owner stop settled or remains explicitly unknown.")); foreach (var reply in pending.Values) reply.TrySetException(new OperationCanceledException("Original app-server stopped; no RPC replay."));
        return new(actual, action, retired.KnownClosed ? AgentActivityState.Quiescent : AgentActivityState.Unknown, retired.KnownClosed, "actual-private-original-app-server-retirement", "Interrupt response alone never proves quiescence; original root/Job/IO/disposition joined. InterruptFailure=" + interruptFailure?.GetType().FullName);
    }
    public Task<AgentControlResult> ObserveUnstartedAsync(AgentRunBinding actual, TimeSpan remaining, CancellationToken token)
    {
        original.Check(); lock (gate) { var absent = !startEntered && process is null; return Task.FromResult(new AgentControlResult(actual, AgentControlAction.Stop, absent ? AgentActivityState.Quiescent : AgentActivityState.Unknown, absent, absent ? "actual-private-port-never-entered" : "", "No process/session adoption from DATA.")); }
    }
    static string Text(JsonElement value, string name) { var result = value.GetProperty(name); if (result.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(result.GetString()) || result.GetString()!.Length > 4096) throw new IOException("Invalid actual app-server " + name); return result.GetString()!; }
}
