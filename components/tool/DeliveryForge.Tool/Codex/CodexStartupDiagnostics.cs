using System.Diagnostics;
using System.Text.Json;

namespace DeliveryForge.Tool.Codex;

internal enum CodexStartupStage { NotStarted, NativeImageVerification, NativeCreation, Initialize, InitializedNotification, ThreadStart, AccountRead, TurnStart, Accepted }
internal enum CodexWireDirection { Client, Server }
internal enum CodexWireMethod { None, Initialize, Initialized, ThreadStart, ThreadStarted, AccountRead, TurnStart, TurnStarted, TurnCompleted, TurnInterrupt, Other, ConfigRead }
internal enum CodexWireState { WriteEntered, WriteReturned, Received }
internal enum CodexWireIdKind { None, ClientNumber, OtherNumber, String }
internal sealed record CodexWireObservation(long ElapsedTicks, CodexStartupStage Stage, CodexWireDirection Direction, CodexWireMethod Method, CodexWireState State, CodexWireIdKind IdKind, long? ClientId, bool HasError, int? ErrorCode);
internal sealed record CodexStartupDiagnostics(CodexStartupStage Stage, long OriginalStarted, long ElapsedTicks, IReadOnlyList<CodexWireObservation> Wire, bool WireBoundExceeded, int ReceivedMessages, int SentMessages, IReadOnlyList<long> PendingIds, string? ReplyFailureType, string? PumpFailureType, TaskStatus? PumpStatus, IReadOnlyList<TaskStatus> OriginalTasks, bool ThreadReceived, bool TurnReceived, CodexConfigReadDiagnostic ConfigurationDiagnostic = CodexConfigReadDiagnostic.NotObserved, CodexConfigOriginClass ConfigurationDestinationOrigin = CodexConfigOriginClass.NotObserved);

// Fixed Codex-only metadata. No prompts, params/results, account values, arbitrary
// methods or string IDs enter this ledger. It issues no process/capability proof.
internal sealed class CodexStartupJournal(long originalStarted)
{
    readonly object gate = new(); readonly List<CodexWireObservation> wire = []; CodexStartupStage stage; bool exceeded; int received, sent; CodexConfigReadDiagnostic configurationDiagnostic; CodexConfigOriginClass configurationDestinationOrigin;
    internal void SetStage(CodexStartupStage value) { lock (gate) stage = value; }
    static CodexWireMethod Method(string? method) => method switch
    {
        "initialize" => CodexWireMethod.Initialize,
        "initialized" => CodexWireMethod.Initialized,
        "account/read" => CodexWireMethod.AccountRead,
        "config/read" => CodexWireMethod.ConfigRead,
        "thread/start" => CodexWireMethod.ThreadStart,
        "thread/started" => CodexWireMethod.ThreadStarted,
        "turn/start" => CodexWireMethod.TurnStart,
        "turn/started" => CodexWireMethod.TurnStarted,
        "turn/completed" => CodexWireMethod.TurnCompleted,
        "turn/interrupt" => CodexWireMethod.TurnInterrupt,
        null => CodexWireMethod.None,
        _ => CodexWireMethod.Other
    };
    internal void Client(string method, long? id, CodexWireState state) => Add(CodexWireDirection.Client, Method(method), state, id is null ? CodexWireIdKind.None : CodexWireIdKind.ClientNumber, id, false, null);
    internal void Server(JsonElement message)
    {
        var method = message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var kind = CodexWireIdKind.None; long? number = null;
        if (message.TryGetProperty("id", out var id))
        {
            if (id.ValueKind == JsonValueKind.String) kind = CodexWireIdKind.String;
            else if (id.TryGetInt64(out var actual) && actual is >= 1 and <= 64) { kind = CodexWireIdKind.ClientNumber; number = actual; }
            else kind = CodexWireIdKind.OtherNumber;
        }
        var hasError = message.TryGetProperty("error", out var error); int? code = null;
        if (hasError && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var wireCode) && wireCode.ValueKind == JsonValueKind.Number && wireCode.TryGetInt32(out var actualCode)) code = actualCode;
        if (message.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("routeDiagnostic", out _))
            lock (gate) { configurationDiagnostic = CodexConfigReadData.Diagnostic(result); configurationDestinationOrigin = CodexConfigReadData.DestinationOrigin(result); }
        Add(CodexWireDirection.Server, Method(method), CodexWireState.Received, kind, number, hasError, code);
    }
    void Add(CodexWireDirection direction, CodexWireMethod method, CodexWireState state, CodexWireIdKind kind, long? id, bool error, int? errorCode)
    {
        lock (gate)
        {
            if (direction == CodexWireDirection.Server) received++; else if (state == CodexWireState.WriteReturned) sent++;
            if (wire.Count >= 128) { exceeded = true; return; }
            wire.Add(new(Stopwatch.GetTimestamp() - originalStarted, stage, direction, method, state, kind, id, error, errorCode));
        }
    }
    internal CodexStartupDiagnostics Snapshot(IReadOnlyList<long> pending, Exception? replyFailure, Exception? pumpFailure, Task? pump, IReadOnlyList<Task> tasks, bool thread, bool turn)
    {
        lock (gate) return new(stage, originalStarted, Stopwatch.GetTimestamp() - originalStarted, wire.ToArray(), exceeded, received, sent, pending, replyFailure?.GetType().FullName, pumpFailure?.GetType().FullName, pump?.Status, tasks.Select(t => t.Status).ToArray(), thread, turn, configurationDiagnostic, configurationDestinationOrigin);
    }
}

// Register and failure-latch share ONE gate. A pump that has already failed can
// never leave a subsequently registered reply waiting until the whole endpoint.
internal sealed class CodexPendingReplies
{
    readonly object gate = new(); readonly Dictionary<long, TaskCompletionSource<JsonElement>> pending = []; Exception? failure;
    internal TaskCompletionSource<JsonElement> Register(long id)
    {
        lock (gate)
        {
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            if (id is < 1 or > 64 || pending.Count >= 64 || pending.ContainsKey(id)) throw new IOException("Finite distinct Codex private request ID required.");
            var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); pending.Add(id, reply); return reply;
        }
    }
    internal bool Contains(long id) { lock (gate) return pending.ContainsKey(id); }
    internal bool TryRemove(long id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TaskCompletionSource<JsonElement>? reply) { lock (gate) return pending.Remove(id, out reply); }
    internal void Fail(Exception error)
    {
        lock (gate)
        {
            failure ??= error;
            foreach (var reply in pending.Values) reply.TrySetException(failure);
            pending.Clear();
        }
    }
    internal Exception? Failure { get { lock (gate) return failure; } }
    internal long[] PendingIds { get { lock (gate) return pending.Keys.Order().ToArray(); } }
}

// A correlated protocol error leaves the transport usable for optional metadata.
internal sealed class CodexRemoteRpcException(string message) : IOException(message) { }
