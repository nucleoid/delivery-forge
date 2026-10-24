using System.Globalization;
namespace DeliveryForge.Tool;
// One concrete outgoing failure notice, not a callback/native-owner issuer. The
// actual output Task is retained on timeout; no fallback write or retry occurs.
internal sealed class ToolFailureAttention
{
    readonly ToolLifetime original; readonly TextWriter actualErrors; readonly object gate = new();
    bool entered, returned; Task? actualWrite; Exception? primary, post;
    internal ToolFailureAttention(ToolLifetime original, TextWriter actualErrors) { this.original = original; this.actualErrors = actualErrors; }
    internal bool Unknown { get { lock (gate) return entered && !returned || actualWrite is not null && !actualWrite.IsCompleted; } }
    internal void EmitOnce()
    {
        lock (gate) { if (entered) return; entered = true; }
        try
        {
            var remaining = original.CleanupRemaining;
            var line = "DFCLI_FAILURE|" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "|" + original.OriginalStarted.ToString(CultureInfo.InvariantCulture);
            if (line.Length > 64) throw new IOException("Finite actual caller attention identity exceeds profile.");
            // Actual write reference is captured before waiting. No serialized
            // marker establishes ownership or authorizes administrative closure.
            actualWrite = actualErrors.WriteLineAsync(line.AsMemory(), CancellationToken.None);
            actualWrite.WaitAsync(remaining).GetAwaiter().GetResult(); returned = true;
        }
        catch (Exception error) { primary = error; }
        try { _ = original.CleanupRemaining; } catch (Exception error) { post = error; }
        if (primary is not null || post is not null || Unknown) original.RetainUnknown();
    }
}
