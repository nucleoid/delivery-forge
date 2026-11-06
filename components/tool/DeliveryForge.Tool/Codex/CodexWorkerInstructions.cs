using System.Text.Json;

namespace DeliveryForge.Tool.Codex;

// Only the already-correlated config/read response enters this capsule. Policy
// refusal settles through the existing projected RPC, then the containing route
// raises it; a known instruction refusal must not kill the protocol pump.
internal sealed class CodexWorkerInstructions
{
    string? selected;
    bool observed;
    IOException? refusal;

    internal JsonElement ObserveAndProject(JsonElement raw, long id)
    {
        if (refusal is null)
        {
            try
            {
                if (id is < 1 or > 64 || !raw.TryGetProperty("id", out var actualId) || actualId.ValueKind != JsonValueKind.Number ||
                    !actualId.TryGetInt64(out var actual) || actual != id)
                    throw new IOException("Selected developer instructions have a foreign response identity.");
                if (raw.TryGetProperty("error", out _))
                    throw new IOException("Selected developer instructions are unavailable.");
                var current = HostAssignmentPrompt.ConfiguredDeveloperInstructions(raw);
                if (observed && !string.Equals(current, selected, StringComparison.Ordinal))
                    throw new IOException("Selected developer instructions changed during the worker original.");
                selected = current; observed = true;
            }
            catch (IOException known) { refusal = known; }
        }
        return CodexTrustProtocolData.Response(raw, "config/read");
    }
    internal string? RequireObserved()
    {
        if (refusal is not null) throw refusal;
        if (!observed) throw new IOException("Selected developer instructions were not observed before worker dispatch.");
        return selected;
    }
}