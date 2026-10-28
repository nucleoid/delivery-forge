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
    readonly InstalledHostAdmission.RuntimeTrust? runtimeTrust;
    readonly bool conformanceTrustRoute;
    readonly bool conformanceDangerFullAccess;
    readonly Action<string>? conformanceLeafCounters; int conformanceCounterPhase;
    bool TrustProtocol => runtimeTrust is not null || conformanceTrustRoute;
    WindowsImageUse? trustedImage;
    bool trustedNativeCreationEntered, trustedNativeCreationReturned;
    HostInstructionContext? instructions;
    public void BindInstructions(HostInstructionContext context) { lock (gate) { if (startEntered || instructions is not null && !ReferenceEquals(instructions, context)) throw new WorkerEnvelopeException("Actual instructions cannot change after selection or dispatch."); instructions = context; } }
    readonly CodexAppServerSelection selection; ToolLifetime? boundOriginal; readonly WorktreeManager actualGit;
    ToolLifetime original => boundOriginal ?? throw new ToolUsageException("The actual containing original is not bound.");
    readonly Dictionary<long, string> trustedReplyMethods = [];
    readonly CodexPendingReplies pending = new(); readonly List<JsonElement> trace = []; readonly object gate = new(); CodexStartupJournal? startup;
    readonly Dictionary<string, JsonElement> terminals = new(StringComparer.Ordinal); readonly List<Task> tasks = [];
    OwnedDuplexProcess? process; CodexJsonlProtocol? protocol; Task? pump; AgentRequest? request; AgentRunBinding? binding;
    TaskCompletionSource<JsonElement>? terminalWait; long nextId; bool startEntered, shutdown; string? threadId, turnId; Exception? pumpFailure;
    public AgentAdapterProfile Profile { get; }
    public HostObservationRealm Realm => HostObservationRealm.ActualHost;
    public IReadOnlyList<Task> OriginalTasks { get { lock (tasks) return tasks.ToArray(); } }
    public OwnedDuplexProcess? OriginalProcess => process; // reference only, never a PID adoption API.
    string? observedModel, observedProvider; bool observedChatgptAccount; volatile bool accountReadRefused; long accountReadId; long configReadId; string? observedRoute; JsonElement routeProjection;
    internal string? CompletedImplementationFamily(AgentCompletionReceipt completion)
    {
        lock (gate)
        {
            var retirement = process?.OriginalRetirementTask;
            if (request is null || binding is null || completion.RunId != request.RunId || completion.RequestIdentity != request.RequestIdentity ||
                completion.RuntimeRunIdentity != binding.RuntimeRunIdentity || completion.RuntimeTaskIdentity != binding.RuntimeTaskIdentity ||
                !observedChatgptAccount || observedProvider != "openai" || observedModel is not { Length: > 4 and <= 256 } || observedModel.Any(char.IsControl) || !observedModel.StartsWith("gpt-", StringComparison.Ordinal) ||
                retirement is null || !retirement.IsCompletedSuccessfully || !retirement.Result.KnownClosed || retirement.Result.ExitCode != 0 || process!.HasUnknownIo)
                return null;
            return "gpt";
        }
    }
    public CodexAppServerPort(CodexAppServerSelection selection, WorktreeManager actualGit, AgentAdapterProfile acceptedProfile, LinuxScopeSelection? linuxScope = null)
        : this(selection, actualGit, acceptedProfile, linuxScope, null) { }
    internal CodexAppServerPort(CodexAppServerSelection selection, WorktreeManager actualGit, AgentAdapterProfile acceptedProfile, LinuxScopeSelection? linuxScope, InstalledHostAdmission.RuntimeTrust? runtimeTrust, bool conformanceTrustRoute = false, bool conformanceDangerFullAccess = false, Action<string>? conformanceLeafCounters = null)
    {
        ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(acceptedProfile);
        if (conformanceTrustRoute && ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) ||
            OperatingSystem.IsLinux() && linuxScope is null || runtimeTrust is not null || acceptedProfile.Capabilities is null ||
            acceptedProfile.Capabilities.Any(row => row is null || row.Status != AgentCapabilityStatus.Unverified)))
            throw new ToolUsageException("Trust-route conformance requires the existing Windows/Linux UNVERIFIED native producer and concrete Linux scope; it cannot grant runtime trust.");
        if (conformanceDangerFullAccess && (!OperatingSystem.IsLinux() || runtimeTrust is not null || conformanceTrustRoute ||
            linuxScope?.RestrictiveChildPolicy != "nnp-user1000-ancestor-v3" ||
            selection.Bytes != 296885960 || selection.Sha256 != "70ea33b7094a4c20c007d09b83bb50e33debb9bbf99bf066709f9eb85bad524a" ||
            selection.Version != "0.163.0-alpha.10.2" || acceptedProfile.AdapterId != "codex.cli" ||
            acceptedProfile.Capabilities is null || acceptedProfile.Capabilities.Count == 0 ||
            acceptedProfile.Capabilities.Any(row => row is null || row.Status != AgentCapabilityStatus.Unverified)))
            throw new ToolUsageException("One-job danger-full-access is only an explicit unverified Linux conformance selection for the exact admitted image and concrete restrictive scope; no runtime trust.");
        if (conformanceLeafCounters is not null && (!OperatingSystem.IsLinux() || linuxScope is null || runtimeTrust is not null || !conformanceTrustRoute && !conformanceDangerFullAccess || acceptedProfile.Capabilities is null || acceptedProfile.Capabilities.Count == 0 || acceptedProfile.Capabilities.Any(row => row is null || row.Status != AgentCapabilityStatus.Unverified)))
            throw new ToolUsageException("Leaf diagnostics require an explicit unverified Linux conformance route; production has no observer.");
        this.conformanceLeafCounters = conformanceLeafCounters;
        this.conformanceDangerFullAccess = conformanceDangerFullAccess;
        this.conformanceTrustRoute = conformanceTrustRoute; this.runtimeTrust = runtimeTrust; this.linuxScope = linuxScope;
        if (acceptedProfile.Capabilities is null || acceptedProfile.Capabilities.Count is < 1 or > 6 || acceptedProfile.Capabilities.Any(row => row is null) || string.IsNullOrWhiteSpace(selection.Version) || selection.Version.Length > 256) throw new ToolUsageException("Finite complete actual native version/profile required BEFORE capability snapshot.");
        if (acceptedProfile.AdapterId != "codex.cli" || acceptedProfile.AdapterVersion != selection.Version || acceptedProfile.Capabilities.Any(x => (x.Capability is AgentCapability.Pause or AgentCapability.Resume) && x.Status == AgentCapabilityStatus.Supported)) throw new ToolUsageException("Stable app-server has interrupt, not same-turn pause/resume; exact accepted Codex version required.");
        this.selection = selection; this.actualGit = actualGit; Profile = acceptedProfile with { Capabilities = Array.AsReadOnly(acceptedProfile.Capabilities.ToArray()) };
    }
    internal static int RequireLeafCounterPhase(int previous, string phase) => (previous, phase) switch
    {
        (0, "before-turn") => 1,
        (1, "after-terminal") => 2,
        (0, "startup-failed") => 2,
        _ => throw new IOException("Leaf diagnostics require at most two ordered phases.")
    };
    void ObserveConformanceLeafCounters(string phase)
    {
        if (conformanceLeafCounters is null) return;
        conformanceCounterPhase = RequireLeafCounterPhase(conformanceCounterPhase, phase);
        original.Check(); conformanceLeafCounters(phase); original.Check();
    }
    internal static void RequireSuccessfulTerminalWithLeafCounters(JsonElement completed, Action capture)
    {
        Exception? counterFailure = null;
        try { capture(); } catch (Exception failure) { counterFailure = failure; }
        try
        {
            var status = Text(completed.GetProperty("params").GetProperty("turn"), "status");
            if (status != "completed") throw new WorkerEnvelopeException("Actual Codex turn ended " + status + "; no successful completion inferred.");
        }
        catch (Exception primary)
        {
            if (counterFailure is not null) throw new AggregateException("Actual terminal and leaf diagnostic failure.", primary, counterFailure);
            throw;
        }
        if (counterFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(counterFailure).Throw();
    }
    public void BindOriginal(ToolLifetime actualOriginal) { lock (gate) { if (boundOriginal is not null && !ReferenceEquals(boundOriginal, actualOriginal)) throw new WorkerEnvelopeException("Codex original cannot be rebound or renewed."); actualOriginal.Check(); boundOriginal = actualOriginal; startup ??= new(actualOriginal.OriginalStarted); } }
    internal CodexStartupDiagnostics SnapshotStartupDiagnostics()
    {
        lock (gate) return (startup ?? throw new IOException("Actual Codex original not bound.")).Snapshot(pending.PendingIds, pending.Failure, pumpFailure, pump, OriginalTasks, threadId is not null, turnId is not null);
    }
    Task<T> Keep<T>(Task<T> actual) { lock (tasks) { if (tasks.Count >= 1024) throw new IOException("Finite actual Codex task ledger exhausted."); tasks.Add(actual); } return actual; }
    Task Keep(Task actual) { lock (tasks) { if (tasks.Count >= 1024) throw new IOException("Finite actual Codex task ledger exhausted."); tasks.Add(actual); } return actual; }
    async Task<JsonElement> Rpc(string method, object parameters, CancellationToken token)
    {
        original.Check(); var id = Interlocked.Increment(ref nextId); if (id > 64) throw new IOException("Fixed Codex RPC allowance exceeded.");
        runtimeTrust?.Check(original); trustedImage?.Check(token);
        if (shutdown || pump is { IsCompleted: true }) pending.Fail(pumpFailure ?? new IOException("Original Codex pump has stopped; no new RPC."));
        var reply = pending.Register(id);
        if (TrustProtocol) lock (gate) trustedReplyMethods.Add(id, method);
        if (method == "account/read") Volatile.Write(ref accountReadId, id);
        if (method == "config/read") Volatile.Write(ref configReadId, id);
        try
        {
            startup!.Client(method, id, CodexWireState.WriteEntered);
            await Keep(protocol!.WriteAsync(new { id, method, @params = parameters }, token)).ConfigureAwait(false);
            startup.Client(method, id, CodexWireState.WriteReturned);
            return await Keep(reply.Task).WaitAsync(original.Remaining, token).ConfigureAwait(false);
        }
        catch (CodexRemoteRpcException) { throw; } // A correlated server refusal is not a broken transport.
        catch (Exception error) { pending.Fail(error); throw; } // no orphaned reply or later RPC after failed wire work.
    }
    async Task Pump(CancellationToken token)
    {
        try
        {
            while (true)
            {
                JsonDocument? read;
                try { read = await protocol!.ReadAsync(token).ConfigureAwait(false); }
                catch (Exception parser) when ((TrustProtocol || pending.Contains(Volatile.Read(ref configReadId))) &&
                    parser is DeliveryForge.Contracts.Serialization.ContractJsonException or JsonException)
                {
                    // The concrete JSON parser can include property/number text
                    // in its message. Do not retain that config-bearing text or
                    // an inner exception. Owner/IO/cancellation errors are intact.
                    throw new IOException("Malformed runtime configuration/protocol frame; no parser payload is retained.");
                }
                using var doc = read; if (doc is null) { if (!shutdown) throw new IOException("Unexpected app-server EOF."); return; }
                var message = doc.RootElement; CodexJsonlProtocol.RequireEnvelope(message); string? trustedResponseMethod = null;
                // Reject unclaimed response IDs before ANY payload retention.
                // The actual configuration reply is then projected before the
                // journal, trace and pending Task result can observe it.
                if (!message.TryGetProperty("method", out _) && (message.GetProperty("id").ValueKind != JsonValueKind.Number || !message.GetProperty("id").TryGetInt64(out var pendingId) || !pending.Contains(pendingId)))
                    throw new IOException("Foreign/duplicate Codex response ID; no response payload is retained.");
                if (TrustProtocol && !message.TryGetProperty("method", out _) && message.TryGetProperty("result", out var rawResult) && rawResult.ValueKind == JsonValueKind.Object &&
                    (rawResult.TryGetProperty("config", out _) || rawResult.TryGetProperty("layers", out _) || rawResult.TryGetProperty("origins", out _)) &&
                    (!message.GetProperty("id").TryGetInt64(out var configurationId) || configurationId != Volatile.Read(ref configReadId)))
                    throw new IOException("Configuration-shaped response has a foreign correlation; no response payload is retained.");
                if (TrustProtocol)
                {
                    if (message.TryGetProperty("method", out _))
                    {
                        if (message.TryGetProperty("id", out var actualServerId))
                        {
                            // Preserve the actual opaque ID only for its wire reply;
                            // no request params, method or ID value enters retention.
                            startup!.Server(message);
                            lock (trace) { if (trace.Count >= 4096) throw new IOException("Codex event trace exceeded."); trace.Add(CodexTrustProtocolData.ServerRequestReceipt()); }
                            await protocol.WriteAsync(new { id = actualServerId.Clone(), error = new { code = -32601, message = "Unsupported client request; no approval or capability granted." } }, token).ConfigureAwait(false);
                            continue;
                        }
                        message = CodexTrustProtocolData.Notification(message);
                    }
                    else
                    {
                        var actualId = message.GetProperty("id").GetInt64(); string actualMethod;
                        lock (gate)
                            if (!trustedReplyMethods.Remove(actualId, out actualMethod!)) throw new IOException("Uncorrelated trust-protocol response method.");
                        message = CodexTrustProtocolData.Response(message, actualMethod); trustedResponseMethod = actualMethod;
                    }
                }
                else if (!message.TryGetProperty("method", out _) && message.TryGetProperty("id", out var accountId) && accountId.ValueKind == JsonValueKind.Number && accountId.TryGetInt64(out var observedId) && observedId == Volatile.Read(ref accountReadId)) message = CodexAccountReadData.Redact(message);
                startup!.Server(message);
                // This fixed private method/actual-ID receipt survives a chatty
                // startup journal without retaining any raw payload. It grants
                // no capability or runtime trust to the conformance producer.
                var retainedMessage = trustedResponseMethod is null ? message.Clone() :
                    JsonSerializer.SerializeToElement(new { TrustRpcMethod = trustedResponseMethod, Message = message });
                lock (trace) { if (trace.Count >= 4096) throw new IOException("Codex event trace exceeded."); trace.Add(retainedMessage); }
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
                if (message.TryGetProperty("error", out var error)) { if (number == Volatile.Read(ref accountReadId)) accountReadRefused = true; waiter.TrySetException(new CodexRemoteRpcException(!TrustProtocol ? "Actual app-server response error: " + error.GetRawText() : CodexTrustProtocolData.ErrorMessage(error))); continue; }
                waiter.TrySetResult(message.GetProperty("result").Clone());
            }
        }
        catch (Exception error) { pumpFailure = error; pending.Fail(error); lock (gate) terminalWait?.TrySetException(error); if (!shutdown) original.RetainUnknown(); throw; }
    }
    // Internal fixture opt-in only. Production/public defaults remain cwd-only.
    internal static object ConformanceDangerFullAccessParameters(AgentRequest actual) =>
        new { cwd = actual.Worktree, sandbox = "danger-full-access" };
    internal static void RequireConformanceDangerFullAccessThread(JsonElement result, AgentRequest actual)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("cwd", out var cwd) || cwd.ValueKind != JsonValueKind.String || cwd.GetString() != actual.Worktree ||
            !result.TryGetProperty("sandbox", out var sandbox) || sandbox.ValueKind != JsonValueKind.Object ||
            sandbox.EnumerateObject().Count() != 1 || !sandbox.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "dangerFullAccess")
            throw new WorkerEnvelopeException("Actual one-job Linux conformance cwd/dangerFullAccess policy differs; no turn is dispatched.");
    }
    public async Task<AgentAcceptedReceipt> StartAsync(AgentRequest actual, TimeSpan remaining, CancellationToken token)
    {
        original.Check(); (instructions ?? throw new WorkerEnvelopeException("Actual parent instructions required before any Codex dispatch.")).Require(actual); lock (gate) { if (startEntered) throw new WorkerEnvelopeException("App-server dispatch already entered; no retry."); startEntered = true; request = actual; }
        try
        {
            if (actual.AdapterId != "codex.cli" || actual.AdapterVersion != selection.Version || remaining <= TimeSpan.Zero || remaining > original.WorkLimit) throw new WorkerEnvelopeException("Actual request/version/deadline differs from containing original.");
            runtimeTrust?.BindWorker(original, selection, actual.Worktree, actual.ResultDirectory, linuxScope);
            startup!.SetStage(CodexStartupStage.NativeImageVerification);
            // Reserve the actual gate copy and independent sealed-fd verification
            // before pathname hashing or native birth; no ordinary Linux fallback.
            LinuxScopeImage? exactImage = null;
            if (OperatingSystem.IsLinux())
            {
                if (linuxScope is null) throw new PlatformNotSupportedException("Actual selected Linux scope required before Codex native image admission.");
                BoundedFiles.ReserveExactLinuxImageReads(new(selection.Executable, selection.Bytes, selection.Sha256), original);
                exactImage = new(selection.Executable, selection.Bytes, selection.Sha256);
            }
            // Linux retains its complete pathname verification plus the existing
            // creation-owned gate copy/sealed-fd proof; never borrow Windows handles.
            if (!TrustProtocol || !OperatingSystem.IsWindows()) BoundedFiles.VerifyNativeImage(new(selection.Executable, selection.Bytes, selection.Sha256), original);
            else trustedImage = runtimeTrust?.RetainWorkerImage(original, new(selection.Executable, selection.Bytes, selection.Sha256), token)
                ?? WindowsImageUse.Open(new(selection.Executable, selection.Bytes, selection.Sha256), original, token);
            var environment = new Dictionary<string, string> { ["DOTNET_PROCESSOR_COUNT"] = "2", ["DOTNET_GCHeapHardLimit"] = "0x100000000", ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1", ["MSBUILDDISABLENODEREUSE"] = "1" };
            runtimeTrust?.Check(original); trustedImage?.Check(token);
            startup.SetStage(CodexStartupStage.NativeCreation); trustedNativeCreationEntered = true; process = OwnedDuplexProcess.Open(new(selection.Executable, ["app-server"], actual.Worktree, environment), original, linuxScope, exactImage); trustedNativeCreationReturned = true; if (process.StartupFailure is Exception failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            if (exactImage is not null && !process.HasCreationOwnedExactLinuxImage) throw new IOException("Actual creation-owned exact Linux image proof required before Codex protocol startup.");
            protocol = new(process.Input, process.Output, original); pump = Pump(original.Token); process.AttachOriginalOutputConsumer(pump); lock (tasks) tasks.Add(pump);
            startup.SetStage(CodexStartupStage.Initialize); _ = await Rpc("initialize", new { clientInfo = new { name = "delivery_forge", title = "Delivery Forge", version = "0.1.0" }, capabilities = new { experimentalApi = false } }, token).ConfigureAwait(false);
            startup.SetStage(CodexStartupStage.InitializedNotification); startup.Client("initialized", null, CodexWireState.WriteEntered);
            await protocol.WriteAsync(new { method = "initialized", @params = new { } }, token).ConfigureAwait(false);
            startup.Client("initialized", null, CodexWireState.WriteReturned);
            if (TrustProtocol)
            {
                await ObserveRoute(token).ConfigureAwait(false);
                startup.SetStage(CodexStartupStage.AccountRead);
                var account = await Rpc("account/read", new { refreshToken = false }, token).ConfigureAwait(false);
                observedChatgptAccount = account.GetProperty("account") is { ValueKind: JsonValueKind.Object } actualAccount && actualAccount.GetProperty("type").GetString() == "chatgpt";
                if (!observedChatgptAccount) throw new IOException("Actual existing ChatGPT subscription is required before worker thread creation.");
            }
            // Omit model/provider/approval overrides: preserve actual configured
            // model, existing sign-in and host policy. No experimental fields.
            startup.SetStage(CodexStartupStage.ThreadStart);
            var threadParameters = conformanceDangerFullAccess ? ConformanceDangerFullAccessParameters(actual) : new { cwd = actual.Worktree };
            var started = await Rpc("thread/start", threadParameters, token).ConfigureAwait(false);
            if (conformanceDangerFullAccess) RequireConformanceDangerFullAccessThread(started, actual);
            threadId = Text(started.GetProperty("thread"), "id");
            if (started.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String) observedModel = model.GetString();
            if (started.TryGetProperty("modelProvider", out var provider) && provider.ValueKind == JsonValueKind.String) observedProvider = provider.GetString();
            // Optional read-only provenance. Unsupported/account-unavailable
            // metadata never becomes a family proof or changes worker dispatch.
            try
            {
                if (!TrustProtocol)
                {
                    startup.SetStage(CodexStartupStage.AccountRead); var account = await Rpc("account/read", new { refreshToken = false }, token).ConfigureAwait(false);
                    observedChatgptAccount = account.GetProperty("account") is { ValueKind: JsonValueKind.Object } actualAccount && actualAccount.GetProperty("type").GetString() == "chatgpt";
                }
            }
            catch (IOException) when (accountReadRefused && pumpFailure is null) { observedChatgptAccount = false; }
            if (TrustProtocol)
            {
                await ObserveRoute(token).ConfigureAwait(false);
                CodexConfigReadData.RequireThread(routeProjection, observedModel, observedProvider);
                CodexRuntimeRoute.RequireObserved(observedChatgptAccount, observedProvider, observedModel);
            }
            ObserveConformanceLeafCounters("before-turn");
            startup.SetStage(CodexStartupStage.TurnStart); var turn = await Rpc("turn/start", HostAssignmentPrompt.CodexTurnStart(actual, instructions!, threadId), token).ConfigureAwait(false); turnId = Text(turn.GetProperty("turn"), "id");
            lock (gate) { terminalWait = new(TaskCreationOptions.RunContinuationsAsynchronously); if (terminals.TryGetValue(turnId, out var early)) { if (Text(early.GetProperty("params"), "threadId") != threadId) throw new IOException("Early terminal has foreign actual thread."); terminalWait.TrySetResult(early); } binding = new(actual.AdapterId, actual.AdapterVersion, actual.RunId, actual.RequestIdentity, threadId, turnId); }
            original.Check(); startup.SetStage(CodexStartupStage.Accepted); return new(actual.SchemaVersion, actual.RunId, actual.RequestIdentity, actual.PlanIdentity, actual.BaseCommit, actual.Worktree, actual.AdapterId, actual.AdapterVersion, threadId, turnId, DateTimeOffset.UtcNow, Profile.Capabilities);
        }
        catch (Exception primary)
        {
            Exception? counterFailure = null;
            // Startup can fail before the before-turn hook. Sample only this
            // already-released exact Linux owner, before retirement removes its
            // cgroup. This replaces an unused sample; it cannot add a third one.
            if (conformanceLeafCounters is not null && conformanceCounterPhase == 0 && process is { HasCreationOwnedExactLinuxImage: true })
            {
                try { ObserveConformanceLeafCounters("startup-failed"); }
                catch (Exception diagnostic) { counterFailure = diagnostic; }
            }
            try
            {
                if (process is not null) { var stop = process.RetireAsync(true); lock (tasks) tasks.Add(stop); var closed = await stop.ConfigureAwait(false); CloseTrustedImage(closed.KnownClosed && !process.HasUnknownIo, true); }
                else CloseTrustedImage(!trustedNativeCreationEntered || trustedNativeCreationReturned, true);
            }
            catch (Exception cleanup)
            {
                trustedImage?.RetainUnknown();
                if (counterFailure is not null) throw new AggregateException("Codex start, leaf diagnostic and same-original retirement/image disposition failure.", primary, counterFailure, cleanup);
                throw new AggregateException("Codex start and same-original retirement/image disposition failure.", primary, cleanup);
            }
            if (counterFailure is not null) throw new AggregateException("Codex start and leaf diagnostic failure.", primary, counterFailure);
            throw;
        }
    }
    void Bound(AgentRunBinding actual) { lock (gate) if (binding is null || binding != actual) throw new WorkerEnvelopeException("No exact privately accepted Codex thread/turn binding."); }
    public async Task<AgentCompletionReceipt> WaitAsync(AgentRunBinding actual, TimeSpan remaining, CancellationToken token)
    {
        Exception? primary = null;
        try
        {
            Bound(actual); original.Check(); var completed = await Keep(terminalWait!.Task).WaitAsync(remaining < original.Remaining ? remaining : original.Remaining, token).ConfigureAwait(false);
            RequireSuccessfulTerminalWithLeafCounters(completed, () => ObserveConformanceLeafCounters("after-terminal"));
            if (TrustProtocol) await ObserveRoute(token).ConfigureAwait(false);
            shutdown = true; var retired = await Keep(process!.RetireAsync(false)).ConfigureAwait(false); HostTransportRetirement.RequireSuccessful(retired);
            if (pump is not null) await pump.WaitAsync(original.Remaining, token).ConfigureAwait(false); if (pumpFailure is not null) throw new IOException("Actual app-server protocol failed; no successful completion.", pumpFailure);
            runtimeTrust?.Check(original); trustedImage?.Check(token); CloseTrustedImage(true, false);
            var snapshotTask = actualGit.ObserveAsync(request!.Worktree, token); var snapshot = await Keep(snapshotTask).WaitAsync(original.Remaining, token).ConfigureAwait(false); original.Check();
            original.Check(); var artifact = Path.Combine(request.ResultDirectory, "codex-app-server-events.json"); JsonElement[] rows; lock (trace) rows = trace.ToArray(); var raw = conformanceTrustRoute ? JsonSerializer.SerializeToUtf8Bytes(new { Kind = "ActualCodexAppServerTrace-v1", Host = new StandaloneHostSelection(selection.Executable, selection.Bytes, selection.Sha256, selection.Version), Version = selection.Version, TrustRouteConformanceOnly = conformanceTrustRoute, ConformanceDangerFullAccess = conformanceDangerFullAccess, actual.RunId, actual.RequestIdentity, ThreadId = threadId, TurnId = turnId, Events = rows, Retirement = retired, PumpFailure = pumpFailure?.GetType().FullName, RuntimeCertified = false }, BoundedFiles.Json) : JsonSerializer.SerializeToUtf8Bytes(new { Kind = "ActualCodexAppServerTrace-v1", TrustRouteConformanceOnly = conformanceTrustRoute, ConformanceDangerFullAccess = conformanceDangerFullAccess, actual.RunId, actual.RequestIdentity, ThreadId = threadId, TurnId = turnId, Events = rows, Retirement = retired, PumpFailure = pumpFailure?.GetType().FullName, RuntimeCertified = false }, BoundedFiles.Json); BoundedFiles.WriteNew(artifact, raw, original);
            return new(request.SchemaVersion, request.RunId, request.RequestIdentity, request.AdapterId, request.AdapterVersion, actual.RuntimeRunIdentity, actual.RuntimeTaskIdentity, "success", request.Worktree, snapshot.HeadCommit, snapshot.TreeId, DateTimeOffset.UtcNow, [artifact], ["Actual transport only; independent review, real quality/current-source and installed conformance remain separate."]);
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            // A failed route/pump/postcheck cannot strand a cleanly retired image.
            // A still-live/unknown owner keeps its image for its actual retirement.
            var actualRetirement = process?.OriginalRetirementTask;
            if (trustedImage is not null && !trustedImage.IsReleased && actualRetirement is { IsCompletedSuccessfully: true } &&
                actualRetirement.Result.KnownClosed && process is { HasUnknownIo: false })
            {
                try { CloseTrustedImage(true, primary is not null); }
                catch (Exception close)
                {
                    if (primary is not null) throw new AggregateException("Codex completion and known-retired image disposition failure.", primary, close);
                    throw;
                }
            }
        }
    }
    public async Task<AgentControlResult> ControlAsync(AgentRunBinding actual, AgentControlAction action, TimeSpan remaining, CancellationToken token)
    {
        Bound(actual); if (action != AgentControlAction.Stop) return new(actual, action, AgentActivityState.Unknown, false, "", "Stable app-server interrupt is not pause or same-turn resume.");
        Exception? interruptFailure = null;
        try { if (turnId is not null && terminalWait?.Task.IsCompleted != true) _ = await Rpc("turn/interrupt", new { threadId, turnId }, token).ConfigureAwait(false); } catch (Exception error) { interruptFailure = error; }
        // WORK may already be closed by the containing stop latch. Regardless of
        // interrupt availability, only this original owner can stop its own Job.
        shutdown = true; var retired = await Keep(process!.RetireAsync(true)).ConfigureAwait(false); CloseTrustedImage(retired.KnownClosed && !process.HasUnknownIo, true); lock (gate) terminalWait?.TrySetException(new OperationCanceledException("Original owner stop settled or remains explicitly unknown.")); pending.Fail(new OperationCanceledException("Original app-server stopped; no RPC replay."));
        return new(actual, action, retired.KnownClosed ? AgentActivityState.Quiescent : AgentActivityState.Unknown, retired.KnownClosed, "actual-private-original-app-server-retirement", "Interrupt response alone never proves quiescence; original root/Job/IO/disposition joined. InterruptFailure=" + interruptFailure?.GetType().FullName);
    }
    public Task<AgentControlResult> ObserveUnstartedAsync(AgentRunBinding actual, TimeSpan remaining, CancellationToken token)
    {
        original.Check(); lock (gate) { var absent = !startEntered && process is null; return Task.FromResult(new AgentControlResult(actual, AgentControlAction.Stop, absent ? AgentActivityState.Quiescent : AgentActivityState.Unknown, absent, absent ? "actual-private-port-never-entered" : "", "No process/session adoption from DATA.")); }
    }
    async Task ObserveRoute(CancellationToken token)
    {
        try
        {
            var projected = await Rpc("config/read", new { cwd = request!.Worktree, includeLayers = true }, token).ConfigureAwait(false);
            var current = CodexConfigReadData.RequireSupported(projected);
            CodexConfigReadData.RequireUnchanged(observedRoute, current);
            observedRoute = current; routeProjection = projected;
        }
        catch (Exception primary)
        {
            try { if (runtimeTrust is not null) runtimeTrust.RevokeRoute(); else original.BeginFailureCleanup(); }
            catch (Exception cleanup) { throw new AggregateException("Runtime route refusal and same-original cleanup admission failure.", primary, cleanup); }
            throw;
        }
    }
    void CloseTrustedImage(bool knownNativeClosed, bool cleanup)
    {
        if (trustedImage is null || trustedImage.IsReleased) return;
        if (!knownNativeClosed) { trustedImage.RetainUnknown(); return; }
        try { trustedImage.Close(cleanup); }
        catch { if (!trustedImage.IsReleased) trustedImage.RetainUnknown(); throw; }
    }
    static string Text(JsonElement value, string name) { var result = value.GetProperty(name); if (result.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(result.GetString()) || result.GetString()!.Length > 4096) throw new IOException("Invalid actual app-server " + name); return result.GetString()!; }
}
