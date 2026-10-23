using System.Text.Json;

namespace DeliveryForge.Tool.Codex;

// Pure bounded response projection. Never persist raw config/layers/origins,
// URLs, headers, credentials, errors or hashes of those secret-bearing values.
// Absence of known overrides is not an exhaustive resolved-endpoint proof.
internal enum CodexConfigReadDiagnostic
{
    NotObserved, Unrecognized, Supported, MissingResult, ConfigNotObject, OriginsNotObject, LayersMissing, LayersNull, LayersNotArray, LayersEmpty, LayersTooMany, LayerNotObject, LayerConfigNotObject, LayerNameNotObject, LayerVersionNotString, KnownDestinationOverride, EffectiveOpenAiStringDestination, EffectiveOpenAiNonStringDestination, LayerOpenAiStringDestination, LayerOpenAiNonStringDestination, EffectiveChatGptDocumentedDefaultDestination, EffectiveChatGptOtherStringDestination, EffectiveChatGptNonStringDestination, LayerChatGptDocumentedDefaultDestination, LayerChatGptOtherStringDestination, LayerChatGptNonStringDestination, CustomProviderDefinitions, UnsupportedModelIdentity, UnsupportedProviderIdentity, InspectionBoundExceeded
}
internal enum CodexConfigOriginClass { NotObserved, Missing, Unrecognized, NestedNotResolved, PackagedDefaults, Mdm, System, EnterpriseManaged, User, Project, SessionFlags, LegacyManagedFile, LegacyManagedMdm }
internal static class CodexConfigReadData
{
    // Exact public sample literal; only the root/missing-origin case below admits it.
    // Never normalize URLs or match hosts/prefixes.
    // https://learn.chatgpt.com/docs/config-file/config-sample
    const string DocumentedStandardChatGptBaseUrl = "https://chatgpt.com/backend-api/";
    internal static JsonElement Redact(JsonElement message, long expectedId)
    {
        if (expectedId is < 1 or > 64 || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var actualId) || actualId != expectedId)
            throw new IOException("Uncorrelated configuration response; no response payload is retained.");
        if (message.TryGetProperty("error", out _))
            return JsonSerializer.SerializeToElement(new { id = expectedId, error = new { code = -32000, message = "Selected runtime configuration metadata is unavailable." } });
        var status = "malformed"; string? model = null; string? provider = null;
        var diagnostic = EnvelopeDiagnostic(message); var destinationOrigin = CodexConfigOriginClass.NotObserved;
        if (message.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("origins", out var origins) && origins.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array && layers.GetArrayLength() is >= 1 and <= 32)
        {
            var nodes = 0; diagnostic = CodexConfigReadDiagnostic.Supported; var safe = Inspect(config, ref nodes, 0, ref diagnostic, ref destinationOrigin, false, origins);
            foreach (var layer in layers.EnumerateArray())
            {
                var shape = LayerDiagnostic(layer);
                if (shape != CodexConfigReadDiagnostic.Supported)
                { if (diagnostic == CodexConfigReadDiagnostic.Supported) diagnostic = shape; safe = false; break; }
                safe &= Inspect(layer.GetProperty("config"), ref nodes, 0, ref diagnostic, ref destinationOrigin, true, layer.GetProperty("name"));
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
        return JsonSerializer.SerializeToElement(new { id = expectedId, result = new { routeStatus = status, routeDiagnostic = diagnostic.ToString(), destinationOrigin = destinationOrigin.ToString(), model, provider } });
    }
    static bool Inspect(JsonElement value, ref int nodes, int depth, ref CodexConfigReadDiagnostic diagnostic,
        ref CodexConfigOriginClass destinationOrigin, bool inLayer, JsonElement originContext)
    {
        if (++nodes > 4096 || depth > 16) { if (diagnostic == CodexConfigReadDiagnostic.Supported) diagnostic = CodexConfigReadDiagnostic.InspectionBoundExceeded; return false; }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray()) if (!Inspect(child, ref nodes, depth + 1, ref diagnostic, ref destinationOrigin, inLayer, originContext)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "openai_base_url" or "chatgpt_base_url")
                {
                    // The observed first-party default is materialized without an
                    // origin entry. No layer, nested value or explicit origin is exempt.
                    var materializedDefault = !inLayer && depth == 0 && property.Name == "chatgpt_base_url" &&
                        property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == DocumentedStandardChatGptBaseUrl &&
                        EffectiveOrigin(originContext, property.Name) == CodexConfigOriginClass.Missing;
                    if (property.Value.ValueKind != JsonValueKind.Null && !materializedDefault)
                    {
                        if (diagnostic == CodexConfigReadDiagnostic.Supported)
                        {
                            diagnostic = DestinationDiagnostic(property.Name, property.Value, inLayer);
                            destinationOrigin = inLayer ? SourceClass(originContext) : depth == 0 ? EffectiveOrigin(originContext, property.Name) : CodexConfigOriginClass.NestedNotResolved;
                        }
                        return false;
                    }
                }
                else if (property.Name == "model_providers")
                {
                    if (property.Value.ValueKind != JsonValueKind.Null &&
                        (property.Value.ValueKind != JsonValueKind.Object || property.Value.EnumerateObject().Any())) { if (diagnostic == CodexConfigReadDiagnostic.Supported) diagnostic = CodexConfigReadDiagnostic.CustomProviderDefinitions; return false; }
                }
                // An allowed default still consumes the same value/node/depth credit.
                if (!Inspect(property.Value, ref nodes, depth + 1, ref diagnostic, ref destinationOrigin, inLayer, originContext)) return false;
            }
        }
        return true;
    }
    static CodexConfigReadDiagnostic DestinationDiagnostic(string key, JsonElement value, bool layer)
    {
        var text = value.ValueKind == JsonValueKind.String;
        if (key == "openai_base_url") return layer
            ? text ? CodexConfigReadDiagnostic.LayerOpenAiStringDestination : CodexConfigReadDiagnostic.LayerOpenAiNonStringDestination
            : text ? CodexConfigReadDiagnostic.EffectiveOpenAiStringDestination : CodexConfigReadDiagnostic.EffectiveOpenAiNonStringDestination;
        if (!text) return layer ? CodexConfigReadDiagnostic.LayerChatGptNonStringDestination : CodexConfigReadDiagnostic.EffectiveChatGptNonStringDestination;
        var documented = value.GetString() == DocumentedStandardChatGptBaseUrl;
        return layer
            ? documented ? CodexConfigReadDiagnostic.LayerChatGptDocumentedDefaultDestination : CodexConfigReadDiagnostic.LayerChatGptOtherStringDestination
            : documented ? CodexConfigReadDiagnostic.EffectiveChatGptDocumentedDefaultDestination : CodexConfigReadDiagnostic.EffectiveChatGptOtherStringDestination;
    }
    static CodexConfigOriginClass EffectiveOrigin(JsonElement origins, string key)
    {
        if (!origins.TryGetProperty(key, out var metadata)) return CodexConfigOriginClass.Missing;
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
            !metadata.TryGetProperty("name", out var name)) return CodexConfigOriginClass.Unrecognized;
        return SourceClass(name);
    }
    // Exact fixed source variants from installed ConfigLayerSource schema. Names,
    // paths, versions, IDs, domains and keys are never copied or opened here.
    static CodexConfigOriginClass SourceClass(JsonElement name)
    {
        static bool Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String;
        if (name.ValueKind != JsonValueKind.Object || !name.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return CodexConfigOriginClass.Unrecognized;
        return type.GetString() switch
        {
            "packagedDefaults" when Text(name, "file") => CodexConfigOriginClass.PackagedDefaults,
            "mdm" when Text(name, "domain") && Text(name, "key") => CodexConfigOriginClass.Mdm,
            "system" when Text(name, "file") => CodexConfigOriginClass.System,
            "enterpriseManaged" when Text(name, "id") && Text(name, "name") => CodexConfigOriginClass.EnterpriseManaged,
            "user" when Text(name, "file") => CodexConfigOriginClass.User,
            "project" when Text(name, "dotCodexFolder") => CodexConfigOriginClass.Project,
            "sessionFlags" => CodexConfigOriginClass.SessionFlags,
            "legacyManagedConfigTomlFromFile" when Text(name, "file") => CodexConfigOriginClass.LegacyManagedFile,
            "legacyManagedConfigTomlFromMdm" => CodexConfigOriginClass.LegacyManagedMdm,
            _ => CodexConfigOriginClass.Unrecognized
        };
    }
    internal static CodexConfigOriginClass DestinationOrigin(JsonElement projected)
    {
        if (projected.ValueKind != JsonValueKind.Object || !projected.TryGetProperty("destinationOrigin", out var value)) return CodexConfigOriginClass.NotObserved;
        if (value.ValueKind != JsonValueKind.String) return CodexConfigOriginClass.Unrecognized;
        return value.GetString() switch
        {
            nameof(CodexConfigOriginClass.NotObserved) => CodexConfigOriginClass.NotObserved,
            nameof(CodexConfigOriginClass.Missing) => CodexConfigOriginClass.Missing,
            nameof(CodexConfigOriginClass.Unrecognized) => CodexConfigOriginClass.Unrecognized,
            nameof(CodexConfigOriginClass.NestedNotResolved) => CodexConfigOriginClass.NestedNotResolved,
            nameof(CodexConfigOriginClass.PackagedDefaults) => CodexConfigOriginClass.PackagedDefaults,
            nameof(CodexConfigOriginClass.Mdm) => CodexConfigOriginClass.Mdm,
            nameof(CodexConfigOriginClass.System) => CodexConfigOriginClass.System,
            nameof(CodexConfigOriginClass.EnterpriseManaged) => CodexConfigOriginClass.EnterpriseManaged,
            nameof(CodexConfigOriginClass.User) => CodexConfigOriginClass.User,
            nameof(CodexConfigOriginClass.Project) => CodexConfigOriginClass.Project,
            nameof(CodexConfigOriginClass.SessionFlags) => CodexConfigOriginClass.SessionFlags,
            nameof(CodexConfigOriginClass.LegacyManagedFile) => CodexConfigOriginClass.LegacyManagedFile,
            nameof(CodexConfigOriginClass.LegacyManagedMdm) => CodexConfigOriginClass.LegacyManagedMdm,
            _ => CodexConfigOriginClass.Unrecognized
        };
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
            nameof(CodexConfigReadDiagnostic.EffectiveOpenAiStringDestination) => CodexConfigReadDiagnostic.EffectiveOpenAiStringDestination,
            nameof(CodexConfigReadDiagnostic.EffectiveOpenAiNonStringDestination) => CodexConfigReadDiagnostic.EffectiveOpenAiNonStringDestination,
            nameof(CodexConfigReadDiagnostic.LayerOpenAiStringDestination) => CodexConfigReadDiagnostic.LayerOpenAiStringDestination,
            nameof(CodexConfigReadDiagnostic.LayerOpenAiNonStringDestination) => CodexConfigReadDiagnostic.LayerOpenAiNonStringDestination,
            nameof(CodexConfigReadDiagnostic.EffectiveChatGptDocumentedDefaultDestination) => CodexConfigReadDiagnostic.EffectiveChatGptDocumentedDefaultDestination,
            nameof(CodexConfigReadDiagnostic.EffectiveChatGptOtherStringDestination) => CodexConfigReadDiagnostic.EffectiveChatGptOtherStringDestination,
            nameof(CodexConfigReadDiagnostic.EffectiveChatGptNonStringDestination) => CodexConfigReadDiagnostic.EffectiveChatGptNonStringDestination,
            nameof(CodexConfigReadDiagnostic.LayerChatGptDocumentedDefaultDestination) => CodexConfigReadDiagnostic.LayerChatGptDocumentedDefaultDestination,
            nameof(CodexConfigReadDiagnostic.LayerChatGptOtherStringDestination) => CodexConfigReadDiagnostic.LayerChatGptOtherStringDestination,
            nameof(CodexConfigReadDiagnostic.LayerChatGptNonStringDestination) => CodexConfigReadDiagnostic.LayerChatGptNonStringDestination,
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
