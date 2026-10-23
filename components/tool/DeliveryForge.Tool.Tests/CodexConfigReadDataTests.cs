using System.Text.Json;
using DeliveryForge.Tool.Codex;

namespace DeliveryForge.Tool.Tests;

// Pure private route DATA projection. No process, login, endpoint, model or
// production authority is supplied by these fixtures.
public sealed class CodexConfigReadDataTests
{
    static JsonElement Project(string config = "{}", string layers = "[{\"config\":{},\"name\":{\"type\":\"user\"},\"version\":\"1\"}]")
    {
        using var doc = JsonDocument.Parse("{\"id\":7,\"result\":{\"config\":" + config + ",\"origins\":{},\"layers\":" + layers + "}}");
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
        Assert.False(result.TryGetProperty("endpoint", out _));
    }
    [Fact]
    public void Exact_binary_known_chatgpt_endpoint_environment_override_is_unsupported()
    {
        Assert.Throws<ToolUsageException>(() => CodexRuntimeRoute.RequireSubscriptionEnvironment(name => name == "CODEX_APP_SERVER_CHATGPT_BASE_URL" ? "https://private.invalid" : null));
    }
}
