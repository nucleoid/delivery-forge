using System.Text.Json;
using DeliveryForge.Tool.Codex;

namespace DeliveryForge.Tool.Tests;

// Pure private route DATA projection. No process, login, endpoint, model or
// production authority is supplied by these fixtures.
public sealed class CodexConfigReadDataTests
{
    static JsonElement Project(string config = "{}", string layers = "[{\"config\":{},\"name\":{\"type\":\"user\"},\"version\":\"1\"}]", string origins = "{}")
    {
        using var doc = JsonDocument.Parse("{\"id\":7,\"result\":{\"config\":" + config + ",\"origins\":" + origins + ",\"layers\":" + layers + "}}");
        return CodexConfigReadData.Redact(doc.RootElement, 7).GetProperty("result");
    }
    [Theory]
    [InlineData("openai_base_url")]
    [InlineData("chatgpt_base_url")]
    public void Builtin_provider_label_does_not_allow_known_custom_destination(string key)
    {
        var result = Project("{\"model_provider\":\"openai\",\"model\":\"gpt-observed\",\"" + key + "\":\"https://private.invalid/?api=secret-sentinel\"}");
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(result));
        Assert.DoesNotContain("private.invalid", result.GetRawText()); Assert.DoesNotContain("secret-sentinel", result.GetRawText());
    }
    [Theory]
    [InlineData("{\"model_providers\":{\"openai\":{\"base_url\":\"secret-sentinel\",\"http_headers\":{\"Authorization\":\"Bearer secret-sentinel\"}}}}")]
    [InlineData("{\"model_provider\":\"custom-secret-sentinel\"}")]
    [InlineData("{\"model\":\"other-secret-sentinel\"}")]
    public void Unsupported_provider_definition_or_identity_is_not_retained(string config)
    {
        var result = Project(config); Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(result));
        Assert.DoesNotContain("secret-sentinel", result.GetRawText());
    }
    [Fact]
    public void Layer_override_is_refused_even_if_effective_object_omits_extra_key()
    {
        var result = Project("{}", "[{\"config\":{\"profiles\":{\"selected\":{\"openai_base_url\":\"secret-sentinel\"}}},\"name\":{\"type\":\"system\"},\"version\":\"1\"}]");
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(result)); Assert.DoesNotContain("secret-sentinel", result.GetRawText());
    }
    [Theory]
    [InlineData("{\"id\":7,\"result\":{\"config\":{},\"origins\":{}}}")]
    [InlineData("{\"id\":7,\"result\":{\"config\":[],\"origins\":{},\"layers\":[]}}")]
    [InlineData("{\"id\":7,\"result\":{\"config\":{},\"origins\":{},\"layers\":[null]}}")]
    public void Missing_or_malformed_metadata_refuses_before_dispatch(string raw)
    {
        using var doc = JsonDocument.Parse(raw); var result = CodexConfigReadData.Redact(doc.RootElement, 7).GetProperty("result");
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(result));
    }
    [Fact]
    public void Error_projection_preserves_only_fixed_correlated_refusal()
    {
        using var doc = JsonDocument.Parse("{\"id\":7,\"error\":{\"code\":5,\"message\":\"secret-sentinel\",\"data\":{\"api_key\":\"secret-sentinel\"}}}");
        var result = CodexConfigReadData.Redact(doc.RootElement, 7); Assert.True(result.TryGetProperty("error", out _)); Assert.DoesNotContain("secret-sentinel", result.GetRawText());
    }
    [Theory]
    [InlineData(6)]
    [InlineData(0)]
    [InlineData(65)]
    public void Foreign_response_cannot_enter_correlated_projection(long id)
    {
        using var doc = JsonDocument.Parse("{\"id\":" + id + ",\"result\":{\"config\":{\"secret\":\"secret-sentinel\"}}}");
        var refusal = Assert.Throws<IOException>(() => CodexConfigReadData.Redact(doc.RootElement, 7)); Assert.DoesNotContain("secret-sentinel", refusal.ToString());
    }
    [Theory]
    [InlineData("{\"id\":\"secret-sentinel\",\"result\":{}}")]
    [InlineData("{\"id\":null,\"result\":{}}")]
    [InlineData("[]")]
    public void Malformed_response_identity_has_only_fixed_refusal(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var refusal = Assert.Throws<IOException>(() => CodexConfigReadData.Redact(doc.RootElement, 7)); Assert.DoesNotContain("secret-sentinel", refusal.ToString());
    }
    [Fact]
    public void Unknown_response_is_not_a_registered_reply_or_a_trace_candidate()
    {
        var pending = new CodexPendingReplies(); var actual = pending.Register(7);
        Assert.True(pending.Contains(7)); Assert.False(pending.Contains(6));
        pending.Fail(new IOException("existing failure")); Assert.False(pending.Contains(7)); Assert.True(actual.Task.IsFaulted);
    }
    [Fact]
    public void Nonrouting_secrets_never_cross_projection_and_drift_signature_is_not_raw_configuration_hash()
    {
        var first = Project("{\"model\":\"gpt-observed\",\"model_provider\":\"openai\",\"developer_instructions\":\"first-secret\"}");
        var second = Project("{\"model\":\"gpt-observed\",\"model_provider\":\"openai\",\"developer_instructions\":\"different-secret\"}");
        Assert.DoesNotContain("secret", first.GetRawText()); Assert.Equal(CodexConfigReadData.RequireSupported(first), CodexConfigReadData.RequireSupported(second));
    }
    [Fact]
    public void Changed_projected_route_or_actual_thread_identity_refuses()
    {
        var first = Project("{\"model\":\"gpt-first\",\"model_provider\":\"openai\"}");
        var second = Project("{\"model\":\"gpt-second\",\"model_provider\":\"openai\"}");
        var a = CodexConfigReadData.RequireSupported(first); CodexConfigReadData.RequireUnchanged(a, a);
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireUnchanged(a, CodexConfigReadData.RequireSupported(second)));
        CodexConfigReadData.RequireThread(first, "gpt-first", "openai");
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireThread(first, "gpt-second", "openai"));
    }
    [Fact]
    public void Existing_empty_defaults_remain_owner_trust_data_not_resolved_endpoint_proof()
    {
        var result = Project("{\"openai_base_url\":null,\"model_providers\":{}}");
        Assert.Equal(64, CodexConfigReadData.RequireSupported(result).Length);
        Assert.Equal(CodexConfigReadDiagnostic.Supported, CodexConfigReadData.Diagnostic(result));
        Assert.Equal(CodexConfigOriginClass.NotObserved, CodexConfigReadData.DestinationOrigin(result));
        Assert.False(result.TryGetProperty("endpoint", out _));
    }
    [Fact]
    public void Exact_binary_known_chatgpt_endpoint_environment_override_is_unsupported()
    {
        Assert.Throws<ToolUsageException>(() => CodexRuntimeRoute.RequireSubscriptionEnvironment(name => name == "CODEX_APP_SERVER_CHATGPT_BASE_URL" ? "https://private.invalid" : null));
    }

    [Theory]
    [InlineData("{\"id\":7}", "MissingResult")]
    [InlineData("{\"id\":7,\"result\":{\"config\":null}}", "ConfigNotObject")]
    [InlineData("{\"id\":7,\"result\":{\"config\":{},\"origins\":null}}", "OriginsNotObject")]
    [InlineData("{\"id\":7,\"result\":{\"config\":{},\"origins\":{}}}", "LayersMissing")]
    [InlineData("{\"id\":7,\"result\":{\"config\":{},\"origins\":{},\"layers\":null}}", "LayersNull")]
    [InlineData("{\"id\":7,\"result\":{\"config\":{},\"origins\":{},\"layers\":{}}}", "LayersNotArray")]
    [InlineData("{\"id\":7,\"result\":{\"config\":{},\"origins\":{},\"layers\":[]}}", "LayersEmpty")]
    [InlineData("{\"id\":7,\"result\":{\"config\":{},\"origins\":{},\"layers\":[null]}}", "LayerNotObject")]
    [InlineData("{\"id\":7,\"result\":{\"config\":{},\"origins\":{},\"layers\":[{\"config\":null,\"name\":{\"type\":\"sessionFlags\"},\"version\":\"1\"}]}}", "LayerConfigNotObject")]
    [InlineData("{\"id\":7,\"result\":{\"config\":{},\"origins\":{},\"layers\":[{\"config\":{},\"name\":null,\"version\":\"1\"}]}}", "LayerNameNotObject")]
    [InlineData("{\"id\":7,\"result\":{\"config\":{},\"origins\":{},\"layers\":[{\"config\":{},\"name\":{},\"version\":null}]}}", "LayerVersionNotString")]
    public void Fixed_shape_diagnostic_does_not_turn_schema_optional_metadata_into_admission(string raw, string expected)
    {
        using var doc = JsonDocument.Parse(raw);
        var projected = CodexConfigReadData.Redact(doc.RootElement, 7).GetProperty("result");
        Assert.Equal(expected, CodexConfigReadData.Diagnostic(projected).ToString());
        var refusal = Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(projected));
        Assert.EndsWith("Diagnostic=" + expected, refusal.Message, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("{\"openai_base_url\":\"secret-sentinel\"}", "EffectiveOpenAiStringDestination")]
    [InlineData("{\"model_providers\":{\"private-provider\":{\"base_url\":\"secret-sentinel\"}}}", "CustomProviderDefinitions")]
    [InlineData("{\"model\":\"secret-sentinel\"}", "UnsupportedModelIdentity")]
    [InlineData("{\"model_provider\":\"secret-sentinel\"}", "UnsupportedProviderIdentity")]
    public void Fixed_route_diagnostic_never_retains_the_rejected_value(string config, string expected)
    {
        var projected = Project(config);
        Assert.Equal(expected, CodexConfigReadData.Diagnostic(projected).ToString());
        var refusal = Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(projected));
        Assert.DoesNotContain("secret-sentinel", projected.GetRawText());
        Assert.DoesNotContain("secret-sentinel", refusal.ToString());
    }
    [Fact]
    public void Traversal_bound_remains_a_refusal_with_only_a_fixed_diagnostic()
    {
        var config = "{\"values\":[" + string.Join(",", Enumerable.Repeat("null", 4096)) + "]}";
        var projected = Project(config);
        Assert.Equal(CodexConfigReadDiagnostic.InspectionBoundExceeded, CodexConfigReadData.Diagnostic(projected));
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(projected));
    }
    [Fact]
    public void Too_many_layers_remains_a_refusal_with_only_a_fixed_diagnostic()
    {
        var layers = "[" + string.Join(",", Enumerable.Repeat("{\"config\":{},\"name\":{},\"version\":\"1\"}", 33)) + "]";
        var projected = Project("{}", layers);
        Assert.Equal(CodexConfigReadDiagnostic.LayersTooMany, CodexConfigReadData.Diagnostic(projected));
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(projected));
    }
    [Fact]
    public void Existing_snapshot_retains_only_the_closed_diagnostic_enum()
    {
        var journal = new CodexStartupJournal(System.Diagnostics.Stopwatch.GetTimestamp());
        using var raw = JsonDocument.Parse("{\"id\":2,\"result\":{\"config\":{},\"origins\":{},\"layers\":null}}");
        journal.Server(CodexConfigReadData.Redact(raw.RootElement, 2));
        var snapshot = journal.Snapshot([], null, null, null, [], false, false);
        Assert.Equal(CodexConfigReadDiagnostic.LayersNull, snapshot.ConfigurationDiagnostic);
        using var forged = JsonDocument.Parse("{\"id\":2,\"result\":{\"routeStatus\":\"unsupported\",\"routeDiagnostic\":\"secret-sentinel\"}}");
        journal.Server(forged.RootElement);
        snapshot = journal.Snapshot([], null, null, null, [], false, false);
        Assert.Equal(CodexConfigReadDiagnostic.Unrecognized, snapshot.ConfigurationDiagnostic);
        Assert.DoesNotContain("secret-sentinel", JsonSerializer.Serialize(snapshot, BoundedFiles.Json));
        var refusal = Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(forged.RootElement.GetProperty("result")));
        Assert.DoesNotContain("secret-sentinel", refusal.ToString());
    }

    static JsonElement DestinationProjection(bool layer, string key, string rawValue)
    {
        using var value = JsonDocument.Parse(rawValue);
        var configuration = new Dictionary<string, JsonElement> { [key] = value.RootElement.Clone() };
        object empty = new { };
        var source = new { type = layer ? "user" : "packagedDefaults", file = "private-path-sentinel" };
        var raw = JsonSerializer.SerializeToElement(new
        {
            id = 7,
            result = new
            {
                config = layer ? empty : configuration,
                origins = layer ? new Dictionary<string, object>() : new Dictionary<string, object> { [key] = new { name = source, version = "private-version-sentinel" } },
                layers = new[] { new { config = layer ? (object)configuration : empty, name = source, version = "private-version-sentinel" } }
            }
        });
        return CodexConfigReadData.Redact(raw, 7).GetProperty("result");
    }
    [Theory]
    [InlineData(false, "openai_base_url", "\"private-value-sentinel\"", "EffectiveOpenAiStringDestination")]
    [InlineData(true, "openai_base_url", "\"private-value-sentinel\"", "LayerOpenAiStringDestination")]
    [InlineData(false, "openai_base_url", "false", "EffectiveOpenAiNonStringDestination")]
    [InlineData(true, "openai_base_url", "{}", "LayerOpenAiNonStringDestination")]
    [InlineData(false, "chatgpt_base_url", "\"https://chatgpt.com/backend-api/\"", "EffectiveChatGptDocumentedDefaultDestination")]
    [InlineData(true, "chatgpt_base_url", "\"https://chatgpt.com/backend-api/\"", "LayerChatGptDocumentedDefaultDestination")]
    [InlineData(false, "chatgpt_base_url", "\"private-value-sentinel\"", "EffectiveChatGptOtherStringDestination")]
    [InlineData(true, "chatgpt_base_url", "\"private-value-sentinel\"", "LayerChatGptOtherStringDestination")]
    [InlineData(false, "chatgpt_base_url", "false", "EffectiveChatGptNonStringDestination")]
    [InlineData(true, "chatgpt_base_url", "[]", "LayerChatGptNonStringDestination")]
    public void Explicit_origin_or_layer_destinations_remain_refused(bool layer, string key, string value, string expected)
    {
        var projected = DestinationProjection(layer, key, value);
        Assert.Equal(expected, CodexConfigReadData.Diagnostic(projected).ToString());
        Assert.Equal(layer ? CodexConfigOriginClass.User : CodexConfigOriginClass.PackagedDefaults, CodexConfigReadData.DestinationOrigin(projected));
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(projected));
        Assert.DoesNotContain("https://", projected.GetRawText());
        Assert.DoesNotContain("private-", projected.GetRawText());
        var journal = new CodexStartupJournal(System.Diagnostics.Stopwatch.GetTimestamp());
        journal.Server(JsonSerializer.SerializeToElement(new { id = 7, result = projected }));
        var snapshot = journal.Snapshot([], null, null, null, [], false, false);
        Assert.Equal(CodexConfigReadData.Diagnostic(projected), snapshot.ConfigurationDiagnostic);
        Assert.Equal(CodexConfigReadData.DestinationOrigin(projected), snapshot.ConfigurationDestinationOrigin);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(snapshot, BoundedFiles.Json));
    }
    [Theory]
    [InlineData("https://chatgpt.com.evil.invalid/backend-api/")]
    [InlineData("https://chatgpt.com/backend-api/?private-sentinel")]
    [InlineData("https://chatgpt.com/backend-api")]
    public void Documented_literal_classification_does_not_normalize_or_allow_hostnames_prefixes_or_suffixes(string value)
    {
        var projected = DestinationProjection(false, "chatgpt_base_url", JsonSerializer.Serialize(value));
        Assert.Equal(CodexConfigReadDiagnostic.EffectiveChatGptOtherStringDestination, CodexConfigReadData.Diagnostic(projected));
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(projected));
        Assert.DoesNotContain(value, projected.GetRawText());
    }
    [Fact]
    public void Nested_effective_destination_does_not_guess_a_top_level_origin()
    {
        var projected = Project("{\"profiles\":{\"private-profile-sentinel\":{\"chatgpt_base_url\":\"https://chatgpt.com/backend-api/\"}}}");
        Assert.Equal(CodexConfigReadDiagnostic.EffectiveChatGptDocumentedDefaultDestination, CodexConfigReadData.Diagnostic(projected));
        Assert.Equal(CodexConfigOriginClass.NestedNotResolved, CodexConfigReadData.DestinationOrigin(projected));
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(projected));
        Assert.DoesNotContain("private-profile-sentinel", projected.GetRawText());
    }
    [Fact]
    public void Unknown_origin_labels_are_not_promoted_or_retained()
    {
        using var forged = JsonDocument.Parse("{\"routeDiagnostic\":\"EffectiveChatGptDocumentedDefaultDestination\",\"routeStatus\":\"unsupported\",\"destinationOrigin\":\"private-origin-sentinel\"}");
        Assert.Equal(CodexConfigOriginClass.Unrecognized, CodexConfigReadData.DestinationOrigin(forged.RootElement));
        var journal = new CodexStartupJournal(System.Diagnostics.Stopwatch.GetTimestamp());
        journal.Server(JsonSerializer.SerializeToElement(new { id = 7, result = forged.RootElement }));
        var snapshot = journal.Snapshot([], null, null, null, [], false, false);
        Assert.Equal(CodexConfigOriginClass.Unrecognized, snapshot.ConfigurationDestinationOrigin);
        Assert.DoesNotContain("private-origin-sentinel", JsonSerializer.Serialize(snapshot, BoundedFiles.Json));
    }

    const string MaterializedDefault = "\"chatgpt_base_url\":\"https://chatgpt.com/backend-api/\"";
    [Fact]
    public void Observed_root_documented_default_without_origin_passes_only_the_private_route_projection()
    {
        var projected = Project("{" + MaterializedDefault + ",\"model\":\"gpt-observed\",\"model_provider\":\"openai\"}");
        Assert.Equal("owner-trusted-no-known-override", projected.GetProperty("routeStatus").GetString());
        Assert.Equal(CodexConfigReadDiagnostic.Supported, CodexConfigReadData.Diagnostic(projected));
        Assert.Equal(64, CodexConfigReadData.RequireSupported(projected).Length);
        CodexConfigReadData.RequireThread(projected, "gpt-observed", "openai");
        Assert.DoesNotContain("https://", projected.GetRawText());
        Assert.False(projected.TryGetProperty("endpoint", out _)); // No native capability or endpoint attestation.
    }
    [Theory]
    [InlineData("{\"chatgpt_base_url\":\"https://chatgpt.com/backend-api/\"}")]
    [InlineData("{\"openai_base_url\":\"private-destination-sentinel\"}")]
    [InlineData("{\"model_providers\":{\"private-provider\":{\"base_url\":\"private-destination-sentinel\"}}}")]
    public void Accepted_materialized_default_does_not_skip_any_later_layer(string secondConfig)
    {
        var layers = "[{\"config\":{},\"name\":{\"type\":\"sessionFlags\"},\"version\":\"1\"},{\"config\":" + secondConfig + ",\"name\":{\"type\":\"user\",\"file\":\"private-path-sentinel\"},\"version\":\"1\"}]";
        var projected = Project("{" + MaterializedDefault + "}", layers);
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(projected));
        Assert.DoesNotContain("private-", projected.GetRawText());
    }
    [Theory]
    [InlineData("\"openai_base_url\":\"private-destination-sentinel\"")]
    [InlineData("\"model_providers\":{\"private-provider\":{\"base_url\":\"private-destination-sentinel\"}}")]
    [InlineData("\"profiles\":{\"private-profile\":{\"chatgpt_base_url\":\"https://chatgpt.com/backend-api/\"}}")]
    public void Accepted_materialized_default_does_not_skip_other_root_or_nested_checks(string other)
    {
        var projected = Project("{" + MaterializedDefault + "," + other + "}");
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(projected));
        Assert.DoesNotContain("private-", projected.GetRawText());
    }
    [Theory]
    [InlineData("https://chatgpt.com.evil.invalid/backend-api/")]
    [InlineData("https://chatgpt.com/backend-api/?private-sentinel")]
    [InlineData("https://chatgpt.com/backend-api")]
    public void Missing_origin_does_not_admit_any_other_literal(string value)
    {
        var projected = Project("{\"chatgpt_base_url\":" + JsonSerializer.Serialize(value) + "}");
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(projected));
        Assert.DoesNotContain(value, projected.GetRawText());
    }
    [Theory]
    [InlineData("{\"name\":{\"type\":\"user\",\"file\":\"private-path-sentinel\"},\"version\":\"1\"}")]
    [InlineData("null")]
    public void Explicit_user_or_malformed_origin_is_not_the_observed_missing_origin(string origin)
    {
        var projected = Project("{" + MaterializedDefault + "}", origins: "{\"chatgpt_base_url\":" + origin + "}");
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(projected));
        Assert.DoesNotContain("private-path-sentinel", projected.GetRawText());
    }
    [Fact]
    public void Materialized_default_value_still_consumes_the_original_shared_node_credit()
    {
        var values = "\"values\":[" + string.Join(",", Enumerable.Repeat("null", 4093)) + "]";
        Assert.Equal(64, CodexConfigReadData.RequireSupported(Project("{" + values + "}")).Length);
        var withDefault = Project("{" + MaterializedDefault + "," + values + "}");
        Assert.Equal(CodexConfigReadDiagnostic.InspectionBoundExceeded, CodexConfigReadData.Diagnostic(withDefault));
        Assert.Throws<ToolUsageException>(() => CodexConfigReadData.RequireSupported(withDefault));
    }
}
