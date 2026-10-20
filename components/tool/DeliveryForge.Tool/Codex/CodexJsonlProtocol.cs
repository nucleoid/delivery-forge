using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;
namespace DeliveryForge.Tool.Codex;

// Concrete stable app-server JSONL framing. It does not launch a process or
// authenticate, load a provider, infer ownership or interpret assistant prose.
public sealed class CodexJsonlProtocol(Stream input, Stream output, ToolLifetime original)
{
    // Internal deterministic observation only; the actual semaphore and original
    // ToolLifetime always keep their real timeout/token. Public callers use System.
    readonly TimeProvider outerWaitTimeProvider = TimeProvider.System;
    internal CodexJsonlProtocol(Stream input, Stream output, ToolLifetime original, TimeProvider outerWaitTimeProvider)
        : this(input, output, original)
    {
        this.outerWaitTimeProvider = outerWaitTimeProvider ?? throw new ArgumentNullException(nameof(outerWaitTimeProvider));
    }
    readonly SemaphoreSlim writer = new(1, 1); readonly byte[] readBuffer = new byte[4096]; readonly List<byte> pending = [];
    readonly List<Task> operations = []; readonly List<byte[]> originals = []; int offset, available; long readBytes, writtenBytes; int messages; bool eof; volatile bool poisoned;
    const int MaximumLine = 65536, MaximumMessages = 4096; const long MaximumRead = 16L * 1024 * 1024, MaximumWrite = 8L * 1024 * 1024;
    public IReadOnlyList<Task> EnteredTasks { get { lock (operations) return operations.ToArray(); } }
    public bool Unknown => poisoned || EnteredTasks.Any(x => !x.IsCompleted);
    public bool Eof => eof;
    public int AvailableWritePermits => writer.CurrentCount; // managed observation only.
    void Admit() { original.Check(); lock (operations) { if (poisoned) throw new IOException("Codex protocol retained UNKNOWN; no new IO may enter."); if (operations.Count >= 16384) throw new IOException("Finite protocol IO ledger exhausted before invocation."); } }
    // Only the exact SemaphoreSlim.WaitAsync(timeout, token) acquisition owns its
    // original timed wait. A second outer timer could abandon it just before a
    // successful acquisition and skip the sole permit release in WriteAsync.
    async Task<T> Enter<T>(Func<Task<T>> invoke, bool semaphoreOwnsTimeout = false)
    {
        Admit(); Task<T>? actual = null; T value = default!; Exception? primary = null; Exception? post = null;
        try { lock (operations) { Admit(); actual = invoke(); if (actual is null) throw new IOException("Protocol operation returned no Task."); operations.Add(actual); } value = semaphoreOwnsTimeout ? await actual.ConfigureAwait(false) : await actual.WaitAsync(original.Remaining, outerWaitTimeProvider).ConfigureAwait(false); } catch (Exception error) { primary = error; if (actual is null || !actual.IsCompleted) { lock (operations) { poisoned = true; original.RetainUnknown(); } } }
        try { original.Check(); } catch (Exception error) { post = error; }
        if (primary is not null && post is not null) throw new AggregateException("Protocol IO and independent original post.", primary, post); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw(); return value;
    }
    async Task Enter(Func<Task> invoke)
    {
        Admit(); Task? actual = null; Exception? primary = null; Exception? post = null;
        try { lock (operations) { Admit(); actual = invoke(); if (actual is null) throw new IOException("Protocol operation returned no Task."); operations.Add(actual); } await actual.WaitAsync(original.Remaining, outerWaitTimeProvider).ConfigureAwait(false); } catch (Exception e) { primary = e; if (actual is null || !actual.IsCompleted) { lock (operations) { poisoned = true; original.RetainUnknown(); } } }
        try { original.Check(); } catch (Exception e) { post = e; }
        if (primary is not null && post is not null) throw new AggregateException("Protocol IO and independent post.", primary, post); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
    }
    sealed class FrameBuffer : MemoryStream
    {
        void Admit(int count) { if (count < 0 || Length > MaximumLine - 1 - count) throw new IOException("Codex outgoing frame bound exceeded before write allocation."); }
        public override void Write(byte[] buffer, int offset, int count) { Admit(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Admit(buffer.Length); var copy = buffer.ToArray(); base.Write(copy, 0, copy.Length); }
        public override void WriteByte(byte value) { Admit(1); base.WriteByte(value); }
    }
    public async Task WriteAsync(object message, CancellationToken token = default)
    {
        Admit(); token.ThrowIfCancellationRequested(); byte[] bytes; using (var buffer = new FrameBuffer()) { using (var json = new Utf8JsonWriter(buffer)) { JsonSerializer.Serialize(json, message, message.GetType(), new JsonSerializerOptions { MaxDepth = 32 }); json.Flush(); } bytes = buffer.ToArray(); }
        if (bytes.Length == 0 || bytes.Length > MaximumLine - 1) throw new IOException("Codex outgoing JSONL finite profile exceeded.");
        var raw = new byte[bytes.Length + 1]; bytes.CopyTo(raw, 0); raw[^1] = 10; original.Check();
        Task<bool>? acquisition = null;
        try { var acquired = await Enter(() => acquisition = writer.WaitAsync(original.Remaining, token), semaphoreOwnsTimeout: true).ConfigureAwait(false); if (!acquired) throw new TimeoutException("Original protocol writer admission timed out without acquiring permit."); Admit(); if (writtenBytes > MaximumWrite - raw.Length) throw new IOException("Codex cumulative write bound exceeded."); lock (originals) originals.Add(raw); writtenBytes += raw.Length; await Enter(() => input.WriteAsync(raw, 0, raw.Length, token)).ConfigureAwait(false); await Enter(() => input.FlushAsync(token)).ConfigureAwait(false); }
        finally { if (acquisition is { IsCompletedSuccessfully: true } && acquisition.Result) writer.Release(); }
    }
    public async Task<JsonDocument?> ReadAsync(CancellationToken token = default)
    {
        Admit(); token.ThrowIfCancellationRequested(); if (eof) return null;
        while (true)
        {
            if (offset == available) { available = await Enter(() => output.ReadAsync(readBuffer, 0, readBuffer.Length, token)).ConfigureAwait(false); offset = 0; if (available == 0) { eof = true; if (pending.Count != 0) throw new IOException("Truncated app-server JSONL frame."); return null; } readBytes += available; if (readBytes > MaximumRead) throw new IOException("Codex complete incoming byte profile exceeded."); }
            while (offset < available) { var b = readBuffer[offset++]; if (b == 10) { if (pending.Count == 0 || ++messages > MaximumMessages) throw new IOException("Empty/excess Codex JSONL frame."); var raw = pending.ToArray(); pending.Clear(); _ = CanonicalJson.Canonicalize(raw); original.Check(); return JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 }); } if (pending.Count >= MaximumLine) throw new IOException("Codex JSONL line profile exceeded before allocation."); pending.Add(b); }
            original.Check();
        }
    }
    public static void RequireEnvelope(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) throw new IOException("Codex protocol envelope must be object.");
        if (message.TryGetProperty("jsonrpc", out var version) && (version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")) throw new IOException("Unsupported JSON-RPC version.");
        var hasId = message.TryGetProperty("id", out var id); var hasMethod = message.TryGetProperty("method", out var method);
        if (hasId && id.ValueKind != JsonValueKind.Number && id.ValueKind != JsonValueKind.String) throw new IOException("Unsupported Codex response ID type.");
        if (hasMethod && (method.ValueKind != JsonValueKind.String || method.GetString()!.Length > 256)) throw new IOException("Invalid Codex method.");
        if (hasMethod && (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _))) throw new IOException("Method message cannot carry response fields.");
        // Codex0.153.4 ServerNotificationEnvelope adds Option<i64> metadata.
        // This server event timestamp supplies no identity or deadline authority.
        var notification = hasMethod && !hasId;
        if (message.TryGetProperty("emittedAtMs", out var emitted) &&
            (!notification || emitted.ValueKind != JsonValueKind.Null &&
             (emitted.ValueKind != JsonValueKind.Number || !emitted.TryGetInt64(out _))))
            throw new IOException("Invalid Codex server notification timestamp.");
        var allowed = notification ? new[] { "jsonrpc", "method", "params", "emittedAtMs" } : hasMethod ? new[] { "jsonrpc", "id", "method", "params" } : new[] { "jsonrpc", "id", "result", "error" }; if (message.EnumerateObject().Any(x => !allowed.Contains(x.Name, StringComparer.Ordinal))) throw new IOException("Unknown Codex envelope member.");
        if (!hasId && !hasMethod) throw new IOException("Codex envelope has neither ID nor method.");
        if (!hasMethod && hasId && (message.TryGetProperty("result", out _) == message.TryGetProperty("error", out _))) throw new IOException("Codex response must contain exactly result or error.");
    }
}
