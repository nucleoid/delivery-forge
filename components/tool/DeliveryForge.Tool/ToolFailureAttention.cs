using System.Globalization;
namespace DeliveryForge.Tool;
// Original marker and one nonsecret first-CAUGHT diagnostic are separate
// outgoing writes, never owner/closure proof. Each actual Task is retained.
internal sealed class ToolFailureAttention
{
    readonly ToolLifetime original; readonly TextWriter actualErrors; readonly object gate = new();
    bool entered, returned; Task? actualWrite; Exception? primary, post;
    (string Stage, Exception Error, bool CleanupAlreadyBegun, bool MarkerAlreadyEntered)? firstCaught;
    bool diagnosticEntered, diagnosticReturned; Task? actualDiagnosticWrite; Exception? diagnosticPrimary, diagnosticPost;
    internal ToolFailureAttention(ToolLifetime original, TextWriter actualErrors) { this.original = original; this.actualErrors = actualErrors; }
    internal void CaptureFirstCaught(string stage, Exception actual, bool cleanupAlreadyBegun)
    {
        // First catch wins, not necessarily the first internal throw or cause.
        // Cleanup and marker flags are observed under their respective gates;
        // no clock, output, wait or new cleanup entry occurs during capture.
        lock (gate) firstCaught ??= (stage, actual, cleanupAlreadyBegun, entered);
    }
    bool MarkerUnknownLocked => entered && !returned || actualWrite is not null && !actualWrite.IsCompleted;
    bool DiagnosticUnknownLocked => diagnosticEntered && !diagnosticReturned || actualDiagnosticWrite is not null && !actualDiagnosticWrite.IsCompleted;
    internal bool Unknown { get { lock (gate) return MarkerUnknownLocked || DiagnosticUnknownLocked; } }
    internal void EmitFirstCaughtOnce()
    {
        (string Stage, Exception Error, bool CleanupAlreadyBegun, bool MarkerAlreadyEntered) failure;
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
                return name.Length <= 256 ? name : "ExceptionTypeUnavailable";
            }
            // No Message/Stack/Data/path/config/worker content is read or emitted.
            var line = "DFCLI_FIRST_CAUGHT|" + System.Text.Json.JsonSerializer.Serialize(new
            {
                Stage = failure.Stage, Type = TypeOf(failure.Error), failure.Error.HResult,
                InnerType = failure.Error.InnerException is { } inner ? TypeOf(inner) : null,
                failure.CleanupAlreadyBegun, failure.MarkerAlreadyEntered
            });
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
