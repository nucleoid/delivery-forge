using System.Text.Json;
using DeliveryForge.Tool.Codex;
namespace DeliveryForge.Tool.Tests;

// Genuine serializer/parser behavior over adversarial wire DATA only. These
// tests do not stand in for the pinned binary's real route conformance case.
public sealed class CodexTrustProtocolDataTests
{
    const string Secret = "secret-sentinel@example.invalid/?token=private";
    [Theory]
    [InlineData("initialize")]
    [InlineData("thread/start")]
    [InlineData("turn/start")]
    [InlineData("account/read")]
    [InlineData("config/read")]
    public void Every_trust_RPC_error_keeps_only_numeric_code_and_fixed_message(string method)
    {
        var message = JsonSerializer.SerializeToElement(new { id = 3, error = new { code = -32602, message = Secret, data = new { config = Secret, headers = Secret } } });
        var safe = CodexTrustProtocolData.Response(message, method);
        Assert.Equal(-32602, safe.GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain(Secret, safe.GetRawText());
        Assert.DoesNotContain(Secret, CodexTrustProtocolData.ErrorMessage(safe.GetProperty("error")));
    }
    [Fact]
    public void Thread_reply_preserves_only_actual_wire_binding_and_admitted_runtime_labels()
    {
        var message = JsonSerializer.SerializeToElement(new
        {
            id = 4,
            result = new
            {
                thread = new { id = "actual-thread", cwd = Secret },
                model = "gpt-observed",
                modelProvider = "openai",
                providerOptions = Secret
            }
        });
        var safe = CodexTrustProtocolData.Response(message, "thread/start").GetProperty("result");
        Assert.Equal("actual-thread", safe.GetProperty("thread").GetProperty("id").GetString());
        Assert.Equal("gpt-observed", safe.GetProperty("model").GetString());
        Assert.Equal("openai", safe.GetProperty("modelProvider").GetString());
        Assert.DoesNotContain(Secret, safe.GetRawText());
    }
    [Theory]
    [InlineData("completed")]
    [InlineData("interrupted")]
    [InlineData("failed")]
    public void Terminal_projection_preserves_required_status_without_error_text(string status)
    {
        var wire = JsonSerializer.SerializeToElement(new
        {
            method = "turn/completed",
            @params = new
            {
                threadId = "actual-thread",
                turn = new { id = "actual-turn", status, error = new { message = Secret }, items = new[] { Secret } }
            }
        });
        CodexJsonlProtocol.RequireEnvelope(wire);
        var safe = CodexTrustProtocolData.Notification(wire);
        Assert.Equal(status, safe.GetProperty("params").GetProperty("turn").GetProperty("status").GetString());
        Assert.Equal("actual-turn", safe.GetProperty("params").GetProperty("turn").GetProperty("id").GetString());
        Assert.DoesNotContain(Secret, safe.GetRawText());
    }
    [Theory]
    [InlineData("error")]
    [InlineData("warning")]
    [InlineData("codex/event/config-warning")]
    public void Unknown_notifications_have_fixed_metadata_without_arbitrary_method_or_params(string method)
    {
        var wire = JsonSerializer.SerializeToElement(new { method, @params = new { message = Secret, config = Secret } });
        CodexJsonlProtocol.RequireEnvelope(wire); var safe = CodexTrustProtocolData.Notification(wire);
        Assert.Equal("trust/other-notification", safe.GetProperty("method").GetString());
        Assert.DoesNotContain(Secret, safe.GetRawText());
        Assert.False(safe.GetProperty("params").TryGetProperty("message", out _));
    }
    [Fact]
    public void Server_request_diagnostics_keep_no_actual_string_id_method_or_payload()
    {
        var request = JsonSerializer.SerializeToElement(new { id = Secret, method = Secret, @params = new { config = Secret } });
        CodexJsonlProtocol.RequireEnvelope(request);
        var journal = new CodexStartupJournal(System.Diagnostics.Stopwatch.GetTimestamp()); journal.Server(request);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(journal.Snapshot([], null, null, null, [], false, false)));
        Assert.DoesNotContain(Secret, CodexTrustProtocolData.ServerRequestReceipt().GetRawText());
        // The real pump replies to the original ID before retaining this receipt;
        // these DATA checks do not fabricate any actual native request or reply.
    }
    [Fact]
    public void Malformed_thread_identity_has_a_fixed_refusal_without_its_value()
    {
        var message = JsonSerializer.SerializeToElement(new { id = 4, result = new { thread = new { id = new[] { Secret } } } });
        var failure = Assert.Throws<IOException>(() => CodexTrustProtocolData.Response(message, "thread/start"));
        Assert.DoesNotContain(Secret, failure.ToString());
    }
    [Fact]
    public void Ignored_initialize_and_interrupt_results_do_not_add_payload_requirements()
    {
        using var wire = JsonDocument.Parse("{\"id\":1,\"result\":null}");
        foreach (var method in new[] { "initialize", "turn/interrupt" })
            Assert.Empty(CodexTrustProtocolData.Response(wire.RootElement, method).GetProperty("result").EnumerateObject());
    }
}
