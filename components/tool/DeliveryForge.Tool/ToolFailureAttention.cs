using System.Globalization;
namespace DeliveryForge.Tool;
// Original marker and one nonsecret first-CAUGHT diagnostic are separate
// outgoing writes, never owner/closure proof. Each actual Task is retained.
internal sealed class ToolFailureAttention
{
    readonly ToolLifetime original; readonly TextWriter actualErrors; readonly object gate = new();
    bool entered, returned; Task? actualWrite; Exception? primary, post;
    (string Stage, Exception Error, bool CleanupAlreadyBegun, bool MarkerAlreadyEntered, Codex.CodexStartupFailureFacts? StartupContext, Codex.CodexProtocolFailureFacts? ProtocolFacts)? firstCaught;
    bool diagnosticEntered, diagnosticReturned; Task? actualDiagnosticWrite; Exception? diagnosticPrimary, diagnosticPost;
    internal ToolFailureAttention(ToolLifetime original, TextWriter actualErrors) { this.original = original; this.actualErrors = actualErrors; }
    internal void CaptureFirstCaught(string stage, Exception actual, bool cleanupAlreadyBegun)
    {
        // First catch wins, not necessarily the first internal throw or cause.
        // Cleanup and marker flags are observed under their respective gates;
        // no clock, output, wait or new cleanup entry occurs during capture.
        lock (gate)
        {
            if (firstCaught is not null) return;
            Codex.CodexStartupFailureFacts? context = null;
            try { if (IsOuterStartupTimeout(stage, actual)) context = original.ObserveActiveCodexStartup(); }
            catch (Exception) { /* Optional context cannot replace the actual first-caught failure. */ }
            Codex.CodexProtocolFailureFacts? protocol = null;
            try { protocol = Codex.CodexProtocolFailure.FindSingle(actual); }
            catch (Exception) { /* Optional code-owned facts cannot replace the actual failure. */ }
            firstCaught = (stage, actual, cleanupAlreadyBegun, entered, context, protocol);
        }
    }
    static bool IsOuterStartupTimeout(string stage, Exception actual)
    {
        if (stage is not ("standalone" or "host-implement")) return false;
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance) { actual };
        var pending = new Queue<Exception>(); pending.Enqueue(actual); var timeout = false;
        while (pending.TryDequeue(out var error))
        {
            timeout |= error is TimeoutException or OperationCanceledException;
            if (error is AggregateException wide && wide.InnerExceptions.Count > 16) return false;
            IEnumerable<Exception> causes = error is AggregateException aggregate ? aggregate.InnerExceptions :
                error.InnerException is { } inner ? [inner] : [];
            foreach (var cause in causes)
            {
                if (seen.Contains(cause)) continue;
                if (seen.Count >= 16) return false;
                seen.Add(cause); pending.Enqueue(cause);
            }
        }
        return timeout;
    }
    bool MarkerUnknownLocked => entered && !returned || actualWrite is not null && !actualWrite.IsCompleted;
    bool DiagnosticUnknownLocked => diagnosticEntered && !diagnosticReturned || actualDiagnosticWrite is not null && !actualDiagnosticWrite.IsCompleted;
    internal bool Unknown { get { lock (gate) return MarkerUnknownLocked || DiagnosticUnknownLocked; } }
    internal void EmitFirstCaughtOnce()
    {
        (string Stage, Exception Error, bool CleanupAlreadyBegun, bool MarkerAlreadyEntered, Codex.CodexStartupFailureFacts? StartupContext, Codex.CodexProtocolFailureFacts? ProtocolFacts) failure;
        lock (gate)
        {
            if (!returned || diagnosticEntered || firstCaught is not { } actual) return;
            diagnosticEntered = true; failure = actual;
        }
        try
        {
            var remaining = original.CleanupRemaining;
            static string TypeOf(Exception actual)
            {
                var name = actual.GetType().FullName ?? "Exception";
                var encodedBytes = 0;
                foreach (var character in name)
                {
                    // Default JSON encoding escapes nested/generic separators.
                    // Only admit names whose encoded value is bounded by construction.
                    if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_') encodedBytes++;
                    else if (character is '+' or '`') encodedBytes += 6;
                    else return "ExceptionTypeUnavailable";
                    if (encodedBytes > 256) return "ExceptionTypeUnavailable";
                }
                return name;
            }
            // No Message/Stack/Data/path/config/worker content is read or emitted.
            var fields = new
            {
                Stage = failure.Stage,
                Type = TypeOf(failure.Error),
                failure.Error.HResult,
                InnerType = failure.Error.InnerException is { } inner ? TypeOf(inner) : null,
                failure.CleanupAlreadyBegun,
                failure.MarkerAlreadyEntered
            };
            var codex = Codex.CodexRemoteRpcException.FindSingle(failure.Error)?.StartupFailure ?? failure.StartupContext;
            var line = "DFCLI_FIRST_CAUGHT|" + (codex is null ? System.Text.Json.JsonSerializer.Serialize(fields) :
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    fields.Stage,
                    fields.Type,
                    fields.HResult,
                    fields.InnerType,
                    fields.CleanupAlreadyBegun,
                    fields.MarkerAlreadyEntered,
                    CodexStartup = codex
                }));
            // Preserve the existing startup/default diagnostic exactly. New
            // optional facts are omitted on serialization/extent refusal, with
            // no additional output, cleanup entry or UNKNOWN transition.
            if (codex is null && failure.ProtocolFacts is { } protocol)
            {
                try
                {
                    var candidate = "DFCLI_FIRST_CAUGHT|" + System.Text.Json.JsonSerializer.Serialize(new
                    {
                        fields.Stage,
                        fields.Type,
                        fields.HResult,
                        fields.InnerType,
                        fields.CleanupAlreadyBegun,
                        fields.MarkerAlreadyEntered,
                        CodexProtocol = protocol
                    });
                    if (System.Text.Encoding.UTF8.GetByteCount(candidate) <= 1024) line = candidate;
                }
                catch (Exception) { /* Keep the original diagnostic unchanged. */ }
            }
            if (System.Text.Encoding.UTF8.GetByteCount(line) > 1024) throw new IOException("Finite first-caught attention exceeded.");
            var write = actualErrors.WriteLineAsync(line.AsMemory(), CancellationToken.None);
            lock (gate) actualDiagnosticWrite = write;
            write.WaitAsync(remaining).GetAwaiter().GetResult(); lock (gate) diagnosticReturned = true;
        }
        catch (Exception error) { lock (gate) diagnosticPrimary ??= error; }
        try { _ = original.CleanupRemaining; } catch (Exception error) { lock (gate) diagnosticPost ??= error; }
        bool failed; lock (gate) failed = diagnosticPrimary is not null || diagnosticPost is not null || DiagnosticUnknownLocked;
        if (failed) original.RetainUnknown();
    }
    internal void EmitOnce()
    {
        lock (gate) { if (entered) return; entered = true; }
        try
        {
            var remaining = original.CleanupRemaining;
            var line = "DFCLI_FAILURE|" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "|" + original.OriginalStarted.ToString(CultureInfo.InvariantCulture);
            if (line.Length > 64) throw new IOException("Finite actual caller attention identity exceeds profile.");
            var write = actualErrors.WriteLineAsync(line.AsMemory(), CancellationToken.None);
            lock (gate) actualWrite = write;
            write.WaitAsync(remaining).GetAwaiter().GetResult(); lock (gate) returned = true;
        }
        catch (Exception error) { lock (gate) primary ??= error; }
        try { _ = original.CleanupRemaining; } catch (Exception error) { lock (gate) post ??= error; }
        // Another in-flight diagnostic is not a failure of this settled marker.
        bool failed; lock (gate) failed = primary is not null || post is not null || MarkerUnknownLocked;
        if (failed) original.RetainUnknown();
    }
}
