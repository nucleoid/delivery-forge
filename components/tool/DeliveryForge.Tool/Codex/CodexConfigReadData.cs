using System.Text.Json;

namespace DeliveryForge.Tool.Codex;

// Pure bounded response projection. Never persist raw config/layers/origins,
// URLs, headers, credentials, errors or hashes of those secret-bearing values.
// Absence of known overrides is not an exhaustive resolved-endpoint proof.
internal enum CodexConfigReadDiagnostic
{
    NotObserved, Unrecognized, Supported, MissingResult, ConfigNotObject, OriginsNotObject, LayersMissing, LayersNull, LayersNotArray, LayersEmpty, LayersTooMany, LayerNotObject, LayerConfigNotObject, LayerNameNotObject, LayerVersionNotString, KnownDestinationOverride, CustomProviderDefinitions, UnsupportedModelIdentity, UnsupportedProviderIdentity, InspectionBoundExceeded
}
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
        var diagnostic = EnvelopeDiagnostic(message);
        if (message.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("origins", out var origins) && origins.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array && layers.GetArrayLength() is >= 1 and <= 32)
        {
            var nodes = 0; diagnostic = CodexConfigReadDiagnostic.Supported; var safe = Inspect(config, ref nodes, 0, ref diagnostic);
            foreach (var layer in layers.EnumerateArray())
            {
                var shape = LayerDiagnostic(layer);
                if (shape != CodexConfigReadDiagnostic.Supported)
                { if (diagnostic == CodexConfigReadDiagnostic.Supported) diagnostic = shape; safe = false; break; }
                safe &= Inspect(layer.GetProperty("config"), ref nodes, 0, ref diagnostic);
            }
            if (config.TryGetProperty("model", out var selectedModel) && selectedModel.ValueKind != JsonValueKind.Null)
            {
                if (selectedModel.ValueKind == JsonValueKind.String && selectedModel.GetString() is { Length: > 4 and <= 256 } value &&
                    value.StartsWith("gpt-", StringComparison.Ordinal) && !value.Any(char.IsControl)) model = value;
                else { if (diagnostic == CodexConfigReadDiagnostic.Supported) diagnostic = CodexConfigReadDiagnostic.UnsupportedModelIdentity; safe = false; }
            }
            if (config.TryGetProperty("model_provider", out var selectedProvider) && selectedProvider.ValueKind != JsonValueKind.Null)
            {
                if (selectedProvider.ValueKind == JsonValueKind.String && selectedProvider.GetString() == "openai") provider = "openai";
                else { if (diagnostic == CodexConfigReadDiagnostic.Supported) diagnostic = CodexConfigReadDiagnostic.UnsupportedProviderIdentity; safe = false; }
            }
            status = safe ? "owner-trusted-no-known-override" : "unsupported";
        }
        // Only fixed status/diagnostic enums and permitted model/provider identities cross the
        // projection. Opaque original bytes never enter trace or pending replies.
        return JsonSerializer.SerializeToElement(new { id = expectedId, result = new { routeStatus = status, routeDiagnostic = diagnostic.ToString(), model, provider } });
    }
    static bool Inspect(JsonElement value, ref int nodes, int depth, ref CodexConfigReadDiagnostic diagnostic)
    {
        if (++nodes > 4096 || depth > 16) { if (diagnostic == CodexConfigReadDiagnostic.Supported) diagnostic = CodexConfigReadDiagnostic.InspectionBoundExceeded; return false; }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray()) if (!Inspect(child, ref nodes, depth + 1, ref diagnostic)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "openai_base_url" or "chatgpt_base_url")
                {
                    if (property.Value.ValueKind != JsonValueKind.Null) { if (diagnostic == CodexConfigReadDiagnostic.Supported) diagnostic = CodexConfigReadDiagnostic.KnownDestinationOverride; return false; }
                }
                else if (property.Name == "model_providers")
                {
                    if (property.Value.ValueKind != JsonValueKind.Null &&
                        (property.Value.ValueKind != JsonValueKind.Object || property.Value.EnumerateObject().Any())) { if (diagnostic == CodexConfigReadDiagnostic.Supported) diagnostic = CodexConfigReadDiagnostic.CustomProviderDefinitions; return false; }
                }
                if (!Inspect(property.Value, ref nodes, depth + 1, ref diagnostic)) return false;
            }
        }
        return true;
    }
    static CodexConfigReadDiagnostic EnvelopeDiagnostic(JsonElement message)
    {
        if (!message.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return CodexConfigReadDiagnostic.MissingResult;
        if (!result.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object) return CodexConfigReadDiagnostic.ConfigNotObject;
        if (!result.TryGetProperty("origins", out var origins) || origins.ValueKind != JsonValueKind.Object) return CodexConfigReadDiagnostic.OriginsNotObject;
        if (!result.TryGetProperty("layers", out var layers)) return CodexConfigReadDiagnostic.LayersMissing;
        if (layers.ValueKind == JsonValueKind.Null) return CodexConfigReadDiagnostic.LayersNull;
        if (layers.ValueKind != JsonValueKind.Array) return CodexConfigReadDiagnostic.LayersNotArray;
        if (layers.GetArrayLength() == 0) return CodexConfigReadDiagnostic.LayersEmpty;
        if (layers.GetArrayLength() > 32) return CodexConfigReadDiagnostic.LayersTooMany;
        return CodexConfigReadDiagnostic.Supported;
    }
    static CodexConfigReadDiagnostic LayerDiagnostic(JsonElement layer)
    {
        if (layer.ValueKind != JsonValueKind.Object) return CodexConfigReadDiagnostic.LayerNotObject;
        if (!layer.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object) return CodexConfigReadDiagnostic.LayerConfigNotObject;
        if (!layer.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.Object) return CodexConfigReadDiagnostic.LayerNameNotObject;
        if (!layer.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String) return CodexConfigReadDiagnostic.LayerVersionNotString;
        return CodexConfigReadDiagnostic.Supported;
    }
    // Never copy an arbitrary diagnostic string from a message into a snapshot
    // or exception. Only these fixed enum labels can cross the boundary.
    internal static CodexConfigReadDiagnostic Diagnostic(JsonElement projected)
    {
        if (projected.ValueKind != JsonValueKind.Object || !projected.TryGetProperty("routeDiagnostic", out var value) || value.ValueKind != JsonValueKind.String)
            return CodexConfigReadDiagnostic.Unrecognized;
        return value.GetString() switch
        {
            nameof(CodexConfigReadDiagnostic.NotObserved) => CodexConfigReadDiagnostic.NotObserved,
            nameof(CodexConfigReadDiagnostic.Unrecognized) => CodexConfigReadDiagnostic.Unrecognized,
            nameof(CodexConfigReadDiagnostic.Supported) => CodexConfigReadDiagnostic.Supported,
            nameof(CodexConfigReadDiagnostic.MissingResult) => CodexConfigReadDiagnostic.MissingResult,
            nameof(CodexConfigReadDiagnostic.ConfigNotObject) => CodexConfigReadDiagnostic.ConfigNotObject,
            nameof(CodexConfigReadDiagnostic.OriginsNotObject) => CodexConfigReadDiagnostic.OriginsNotObject,
            nameof(CodexConfigReadDiagnostic.LayersMissing) => CodexConfigReadDiagnostic.LayersMissing,
            nameof(CodexConfigReadDiagnostic.LayersNull) => CodexConfigReadDiagnostic.LayersNull,
            nameof(CodexConfigReadDiagnostic.LayersNotArray) => CodexConfigReadDiagnostic.LayersNotArray,
            nameof(CodexConfigReadDiagnostic.LayersEmpty) => CodexConfigReadDiagnostic.LayersEmpty,
            nameof(CodexConfigReadDiagnostic.LayersTooMany) => CodexConfigReadDiagnostic.LayersTooMany,
            nameof(CodexConfigReadDiagnostic.LayerNotObject) => CodexConfigReadDiagnostic.LayerNotObject,
            nameof(CodexConfigReadDiagnostic.LayerConfigNotObject) => CodexConfigReadDiagnostic.LayerConfigNotObject,
            nameof(CodexConfigReadDiagnostic.LayerNameNotObject) => CodexConfigReadDiagnostic.LayerNameNotObject,
            nameof(CodexConfigReadDiagnostic.LayerVersionNotString) => CodexConfigReadDiagnostic.LayerVersionNotString,
            nameof(CodexConfigReadDiagnostic.KnownDestinationOverride) => CodexConfigReadDiagnostic.KnownDestinationOverride,
            nameof(CodexConfigReadDiagnostic.CustomProviderDefinitions) => CodexConfigReadDiagnostic.CustomProviderDefinitions,
            nameof(CodexConfigReadDiagnostic.UnsupportedModelIdentity) => CodexConfigReadDiagnostic.UnsupportedModelIdentity,
            nameof(CodexConfigReadDiagnostic.UnsupportedProviderIdentity) => CodexConfigReadDiagnostic.UnsupportedProviderIdentity,
            nameof(CodexConfigReadDiagnostic.InspectionBoundExceeded) => CodexConfigReadDiagnostic.InspectionBoundExceeded,
            _ => CodexConfigReadDiagnostic.Unrecognized
        };
    }
    internal static string RequireSupported(JsonElement projected)
    {
        if (projected.ValueKind != JsonValueKind.Object || !projected.TryGetProperty("routeStatus", out var status) ||
            status.ValueKind != JsonValueKind.String || status.GetString() != "owner-trusted-no-known-override")
            throw new ToolUsageException("Actual runtime configuration is unavailable, malformed or contains unsupported route overrides; no worker turn is dispatched. Diagnostic=" + Diagnostic(projected));
        // This bounded identity contains ONLY the already projected permitted
        // fields/fixed diagnostic; it is not a digest of secret-bearing configuration values.
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
