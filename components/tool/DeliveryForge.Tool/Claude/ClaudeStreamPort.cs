using System.Collections.Concurrent;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Tool.Codex;
namespace DeliveryForge.Tool.Claude;

public sealed record ClaudeStreamSelection(string Executable, long Bytes, string Sha256, string Version);
// Public stream-json transport, no dynamic tools, bypass permissions, API key,
// hardcoded model or replacement auth. A trusted real conformance profile must
// admit this exact CLI version BEFORE construction; configuration is not proof.
public sealed class ClaudeStreamPort : IHostToolSessionPort, IOriginalBoundHostPort, IHostInstructionPort
{
    readonly ClaudeStreamSelection selection; readonly WorktreeManager git; readonly LinuxScopeSelection? linuxScope; readonly object gate = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new(StringComparer.Ordinal);
    readonly List<Task> tasks = []; readonly List<JsonElement> trace = [];
    readonly TaskCompletionSource<bool> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously), settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    ToolLifetime? owner; ToolLifetime Original => owner ?? throw new ToolUsageException("Actual containing original not bound.");
    HostInstructionContext? instructions; OwnedDuplexProcess? process; CodexJsonlProtocol? framing; Task? pump; Exception? pumpFailure;
    AgentRequest? request; AgentRunBinding? binding; ClaudeStreamState? state; string? promptId; long requests; bool entered, shutdown, stopEntered;
    public AgentAdapterProfile Profile { get; }
    public HostObservationRealm Realm => HostObservationRealm.ActualHost;
    public OwnedDuplexProcess? OriginalProcess => process;
    public IReadOnlyList<Task> OriginalTasks { get { lock (tasks) return tasks.ToArray(); } }
    public ClaudeStreamPort(ClaudeStreamSelection selection, WorktreeManager actualGit, AgentAdapterProfile acceptedProfile, LinuxScopeSelection? linuxScope = null)
    {
        ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(acceptedProfile);
        if (acceptedProfile.Capabilities is null || acceptedProfile.Capabilities.Count is < 1 or > 6 || acceptedProfile.Capabilities.Any(row => row is null) || string.IsNullOrWhiteSpace(selection.Version) || selection.Version.Length > 256) throw new ToolUsageException("Finite complete actual native version/profile required BEFORE capability snapshot.");
        if (acceptedProfile.AdapterId != "claude-code.cli" || acceptedProfile.AdapterVersion != selection.Version || acceptedProfile.Capabilities.Any(x => (x.Capability is AgentCapability.Pause or AgentCapability.Resume) && x.Status == AgentCapabilityStatus.Supported)) throw new ToolUsageException("Exact accepted Claude CLI version required; interrupt is not pause/resume.");
        this.selection = selection; git = actualGit; this.linuxScope = linuxScope; Profile = acceptedProfile with { Capabilities = Array.AsReadOnly(acceptedProfile.Capabilities.ToArray()) };
    }
    public void BindOriginal(ToolLifetime actual) { lock (gate) { if (owner is not null && !ReferenceEquals(owner, actual)) throw new WorkerEnvelopeException("Original cannot be rebound."); actual.Check(); owner = actual; } }
    public void BindInstructions(HostInstructionContext actual) { lock (gate) { if (entered || instructions is not null && !ReferenceEquals(instructions, actual)) throw new WorkerEnvelopeException("Actual parent instructions cannot change."); instructions = actual; } }
    Task Keep(Task actual) { lock (tasks) { if (tasks.Count >= 1024) throw new IOException("Actual Claude task ledger exceeded."); tasks.Add(actual); } return actual; }
    async Task<JsonElement> Control(string subtype, CancellationToken token)
    {
        Original.Check(); var ordinal = Interlocked.Increment(ref requests); if (ordinal > 8) throw new IOException("Finite Claude control request allowance exceeded."); var id = "forge-" + Guid.NewGuid().ToString("N"); var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); if (!pending.TryAdd(id, reply)) throw new IOException("Duplicate private control request.");
        object payload = subtype == "initialize" ? new { subtype, hooks = (object?)null } : new { subtype }; await Keep(framing!.WriteAsync(new { type = "control_request", request_id = id, request = payload }, token)).ConfigureAwait(false); _ = Keep(reply.Task); return await reply.Task.WaitAsync(Original.Remaining, token).ConfigureAwait(false);
    }
    async Task Pump(CancellationToken token)
    {
        try
        {
            while (true)
            {
                using var doc = await framing!.ReadAsync(token).ConfigureAwait(false); if (doc is null) { if (!shutdown) throw new IOException("Unexpected actual Claude EOF."); return; }
                var row = doc.RootElement; var type = ClaudeStreamState.Text(row, "type"); lock (trace) { if (trace.Count >= 4096) throw new IOException("Actual complete Claude event ledger exceeded."); trace.Add(row.Clone()); }
                if (type == "control_response") { var response = row.GetProperty("response"); var id = ClaudeStreamState.Text(response, "request_id"); if (!pending.TryRemove(id, out var waiter)) throw new IOException("Foreign/duplicate Claude control response."); if (ClaudeStreamState.Text(response, "subtype") != "success") { var failure = new IOException("Actual Claude control refused."); waiter.TrySetException(failure); throw failure; } waiter.TrySetResult(response.Clone()); continue; }
                if (type == "control_request") { var id = ClaudeStreamState.Text(row, "request_id"); await framing.WriteAsync(new { type = "control_response", response = new { subtype = "error", request_id = id, error = "Unsupported host control; no approval, auth, tool or capability granted." } }, token).ConfigureAwait(false); throw new IOException("Actual host requested unsupported privileged control."); }
                lock (gate) { state!.Observe(row); if (state.Acknowledged) accepted.TrySetResult(true); if (state.Settled) settled.TrySetResult(true); }
            }
        }
        catch (Exception failure) { pumpFailure = failure; accepted.TrySetException(failure); settled.TrySetException(failure); foreach (var reply in pending.Values) reply.TrySetException(failure); if (!shutdown) Original.RetainUnknown(); throw; }
    }
    public async Task<AgentAcceptedReceipt> StartAsync(AgentRequest actual, TimeSpan remaining, CancellationToken token)
    {
        Original.Check(); (instructions ?? throw new WorkerEnvelopeException("Actual parent-selected instructions required.")).Require(actual); lock (gate) { if (entered) throw new WorkerEnvelopeException("Claude dispatch already entered; no retry."); entered = true; request = actual; }
        try
        {
            if (actual.AdapterId != "claude-code.cli" || actual.AdapterVersion != selection.Version || remaining <= TimeSpan.Zero || remaining > Original.WorkLimit) throw new WorkerEnvelopeException("Actual Claude request/version/deadline differs.");
            if (selection.Bytes is <= 0 or > 268435456) throw new IOException("Selected Claude image exceeds finite profile."); var bytes = BoundedFiles.Read(selection.Executable, Original, 268435456); if (bytes.LongLength != selection.Bytes || BoundedFiles.Sha(bytes) != selection.Sha256) throw new IOException("Actual selected Claude image changed.");
            var prompt = HostAssignmentPrompt.Create(actual, instructions); promptId = Guid.NewGuid().ToString(); state = new(promptId, prompt);
            var inherited = Environment.GetEnvironmentVariable("CLAUDE_CODE_SDK_READS_SESSION_STATE"); if (inherited is not null && inherited != "1") throw new ToolUsageException("Existing session-state selection differs; no configuration override.");
            var env = new Dictionary<string, string> { ["DOTNET_PROCESSOR_COUNT"] = "2", ["DOTNET_GCHeapHardLimit"] = "0x100000000", ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1", ["MSBUILDDISABLENODEREUSE"] = "1", ["CLAUDE_CODE_SDK_READS_SESSION_STATE"] = "1" };
            process = OwnedDuplexProcess.Open(new(selection.Executable, ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--replay-user-messages"], actual.Worktree, env), Original, linuxScope); if (process.StartupFailure is Exception failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            framing = new(process.Input, process.Output, Original); pump = Pump(Original.Token); _ = Keep(pump); process.AttachOriginalOutputConsumer(pump);
            _ = await Control("initialize", token).ConfigureAwait(false); lock (gate) state.AdmitPrompt();
            await Keep(framing.WriteAsync(new { type = "user", session_id = "", message = new { role = "user", content = prompt }, parent_tool_use_id = (string?)null, uuid = promptId, origin = new { kind = "human" }, client_composed = true }, token)).ConfigureAwait(false);
            _ = Keep(accepted.Task); await accepted.Task.WaitAsync(remaining < Original.Remaining ? remaining : Original.Remaining, token).ConfigureAwait(false);
            lock (gate) binding = new(actual.AdapterId, actual.AdapterVersion, actual.RunId, actual.RequestIdentity, state.Session, promptId); Original.Check();
            return new(actual.SchemaVersion, actual.RunId, actual.RequestIdentity, actual.PlanIdentity, actual.BaseCommit, actual.Worktree, actual.AdapterId, actual.AdapterVersion, binding.RuntimeRunIdentity, binding.RuntimeTaskIdentity, DateTimeOffset.UtcNow, Profile.Capabilities);
        }
        catch (Exception primary) { if (process is not null) { shutdown = true; try { var stop = process.RetireAsync(true); _ = Keep(stop); var retired = await stop.ConfigureAwait(false); if (!retired.KnownClosed) Original.RetainUnknown(); } catch (Exception post) { throw new AggregateException("Actual Claude start and independent original retirement failure.", primary, post); } } throw; }
    }
    void Bound(AgentRunBinding actual) { lock (gate) if (binding is null || binding != actual) throw new WorkerEnvelopeException("No exact private Claude session/prompt binding."); }
    public async Task<AgentCompletionReceipt> WaitAsync(AgentRunBinding actual, TimeSpan remaining, CancellationToken token)
    {
        Bound(actual); Original.Check(); if (remaining <= TimeSpan.Zero || remaining > Original.WorkLimit) throw new ArgumentOutOfRangeException(nameof(remaining)); _ = Keep(settled.Task); await settled.Task.WaitAsync(remaining < Original.Remaining ? remaining : Original.Remaining, token).ConfigureAwait(false); lock (gate) state!.RequireSuccess();
        shutdown = true; var close = process!.RetireAsync(false); _ = Keep(close); var retirement = await close.ConfigureAwait(false); HostTransportRetirement.RequireSuccessful(retirement); if (pump is not null) await pump.WaitAsync(Original.Remaining, token).ConfigureAwait(false); if (pumpFailure is not null) throw new IOException("Actual Claude stream failed.", pumpFailure); lock (gate) state!.RequireSuccess();
        var snapshotTask = git.ObserveAsync(request!.Worktree, token); _ = Keep(snapshotTask); var snapshot = await snapshotTask.WaitAsync(Original.Remaining, token).ConfigureAwait(false); Original.Check(); JsonElement[] rows; lock (trace) rows = trace.ToArray();
        var artifact = Path.Combine(request.ResultDirectory, "claude-stream-events.json"); BoundedFiles.WriteNew(artifact, JsonSerializer.SerializeToUtf8Bytes(new { Kind = "ActualClaudeStreamTrace-v1", actual.RunId, actual.RequestIdentity, SessionId = actual.RuntimeRunIdentity, PromptId = promptId, Events = rows, Retirement = retirement, RuntimeCertified = false }, BoundedFiles.Json), Original);
        return new(request.SchemaVersion, request.RunId, request.RequestIdentity, request.AdapterId, request.AdapterVersion, actual.RuntimeRunIdentity, actual.RuntimeTaskIdentity, "success", request.Worktree, snapshot.HeadCommit, snapshot.TreeId, DateTimeOffset.UtcNow, [artifact], ["Actual transport source; independent installed conformance, review and real quality/current-source gates remain mandatory."]);
    }
    public async Task<AgentControlResult> ControlAsync(AgentRunBinding actual, AgentControlAction action, TimeSpan remaining, CancellationToken token)
    {
        Bound(actual); if (action != AgentControlAction.Stop) return new(actual, action, AgentActivityState.Unknown, false, "", "Claude interrupt cannot pause/resume the same actual turn."); lock (gate) { if (stopEntered) throw new WorkerEnvelopeException("Actual stop already entered; no retry."); stopEntered = true; }
        Exception? interrupt = null; try { _ = await Control("interrupt", token).ConfigureAwait(false); } catch (Exception error) { interrupt = error; }
        shutdown = true; var stop = process!.RetireAsync(true); _ = Keep(stop); var retirement = await stop.ConfigureAwait(false); settled.TrySetException(new OperationCanceledException("Actual original Claude stop.")); foreach (var reply in pending.Values) reply.TrySetException(new OperationCanceledException("Actual original stopped; no replay."));
        return new(actual, action, retirement.KnownClosed ? AgentActivityState.Quiescent : AgentActivityState.Unknown, retirement.KnownClosed, "actual-private-original-claude-retirement", "Control acknowledgement alone is not quiescence; original group/IO/disposition joined. InterruptFailure=" + interrupt?.GetType().FullName);
    }
    public Task<AgentControlResult> ObserveUnstartedAsync(AgentRunBinding actual, TimeSpan remaining, CancellationToken token) { Original.Check(); lock (gate) { var absent = !entered && process is null; return Task.FromResult(new AgentControlResult(actual, AgentControlAction.Stop, absent ? AgentActivityState.Quiescent : AgentActivityState.Unknown, absent, absent ? "actual-private-claude-never-entered" : "", "No session/process adoption from DATA.")); } }
}
