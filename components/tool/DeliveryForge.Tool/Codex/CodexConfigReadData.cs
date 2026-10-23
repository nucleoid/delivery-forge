using System.Text.Json;

namespace DeliveryForge.Tool.Codex;

// Pure bounded response projection. Never persist raw config/layers/origins,
// URLs, headers, credentials, errors or hashes of those secret-bearing values.
// Absence of known overrides is not an exhaustive resolved-endpoint proof.
internal static class CodexConfigReadData
{
    internal static JsonElement Redact(JsonElement message, long expectedId)
    {
        if (expectedId is < 1 or > 64 || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var actualId) || actualId != expectedId)
            throw new IOException("Uncorrelated configuration response; no response payload is retained.");
        if (message.TryGetProperty("error", out _))
            return JsonSerializer.SerializeToElement(new { id = expectedId, error = new { code = -32000, message = "Selected runtime configuration metadata is unavailable." } });
        var status = "malformed"; string? model = null; string? provider = null;
        if (message.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("origins", out var origins) && origins.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array && layers.GetArrayLength() is >= 1 and <= 32)
        {
            var nodes = 0; var safe = Inspect(config, ref nodes, 0);
            foreach (var layer in layers.EnumerateArray())
            {
                if (layer.ValueKind != JsonValueKind.Object || !layer.TryGetProperty("config", out var data) || data.ValueKind != JsonValueKind.Object ||
                    !layer.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.Object ||
                    !layer.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
                { safe = false; break; }
                safe &= Inspect(data, ref nodes, 0);
            }
            if (config.TryGetProperty("model", out var selectedModel) && selectedModel.ValueKind != JsonValueKind.Null)
            {
                if (selectedModel.ValueKind == JsonValueKind.String && selectedModel.GetString() is { Length: > 4 and <= 256 } value &&
                    value.StartsWith("gpt-", StringComparison.Ordinal) && !value.Any(char.IsControl)) model = value;
                else safe = false;
            }
            if (config.TryGetProperty("model_provider", out var selectedProvider) && selectedProvider.ValueKind != JsonValueKind.Null)
            {
                if (selectedProvider.ValueKind == JsonValueKind.String && selectedProvider.GetString() == "openai") provider = "openai";
                else safe = false;
            }
            status = safe ? "owner-trusted-no-known-override" : "unsupported";
        }
        // Only a fixed status and permitted model/provider identities cross the
        // projection. Opaque original bytes never enter trace or pending replies.
        return JsonSerializer.SerializeToElement(new { id = expectedId, result = new { routeStatus = status, model, provider } });
    }
    static bool Inspect(JsonElement value, ref int nodes, int depth)
    {
        if (++nodes > 4096 || depth > 16) return false;
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray()) if (!Inspect(child, ref nodes, depth + 1)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "openai_base_url" or "chatgpt_base_url")
                {
                    if (property.Value.ValueKind != JsonValueKind.Null) return false;
                }
                else if (property.Name == "model_providers")
                {
                    if (property.Value.ValueKind != JsonValueKind.Null &&
                        (property.Value.ValueKind != JsonValueKind.Object || property.Value.EnumerateObject().Any())) return false;
                }
                if (!Inspect(property.Value, ref nodes, depth + 1)) return false;
            }
        }
        return true;
    }
    internal static string RequireSupported(JsonElement projected)
    {
        if (projected.ValueKind != JsonValueKind.Object || !projected.TryGetProperty("routeStatus", out var status) ||
            status.ValueKind != JsonValueKind.String || status.GetString() != "owner-trusted-no-known-override")
            throw new ToolUsageException("Actual runtime configuration is unavailable, malformed or contains unsupported route overrides; no worker turn is dispatched.");
        // This bounded identity contains ONLY the already projected permitted
        // fields; it is not a digest of secret-bearing configuration values.
        return BoundedFiles.Sha(JsonSerializer.SerializeToUtf8Bytes(projected));
    }
    internal static void RequireUnchanged(string? existing, string current)
    {
        if (existing is not null && existing != current)
            throw new ToolUsageException("Observed runtime route changed; the original worker admission is revoked.");
    }
    internal static void RequireThread(JsonElement projected, string? actualModel, string? actualProvider)
    {
        _ = RequireSupported(projected);
        if (projected.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String && model.GetString() != actualModel ||
            projected.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.String && provider.GetString() != actualProvider)
            throw new ToolUsageException("Actual thread model/provider differs from its observed runtime configuration; no worker turn is dispatched.");
    }
}
