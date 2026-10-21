using System.Text;
using System.Text.Json;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Pi;
using DeliveryForge.Tool.Codex;
namespace DeliveryForge.Tool.Tests;

public sealed class PiRpcProtocolTests
{
    static void Event(PiRpcProtocol state, string text) { using var doc = JsonDocument.Parse(text); state.ObserveEvent(doc.RootElement); }
    static PiRpcProtocol Started() { var state = new PiRpcProtocol(); state.AdmitPrompt(); Event(state, "{\"type\":\"agent_start\"}"); return state; }
    [Fact]
    public void Actual_protocol_sequence_requires_settled_not_agent_end()
    {
        var state = Started(); Event(state, "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"}}"); Event(state, "{\"type\":\"agent_end\",\"willRetry\":false}"); Assert.Throws<IOException>(state.RequireSuccess);
        Event(state, "{\"type\":\"agent_settled\",\"aborted\":false}"); state.RequireSuccess(); Assert.Throws<IOException>(() => Event(state, "{\"type\":\"agent_start\"}"));
    }
    [Theory]
    [InlineData("error")]
    [InlineData("aborted")]
    [InlineData("length")]
    [InlineData("toolUse")]
    [InlineData("pending")]
    [InlineData("deferred")]
    public void Final_non_success_assistant_never_becomes_successful_worker(string stop) { var state = Started(); Event(state, JsonSerializer.Serialize(new { type = "message_end", message = new { role = "assistant", stopReason = stop } })); Event(state, "{\"type\":\"agent_settled\",\"aborted\":false}"); Assert.Throws<IOException>(state.RequireSuccess); }
    [Fact]
    public async Task Genuine_JSONL_response_joins_exact_id_command_and_idle_session()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); using var input = new MemoryStream(); using var output = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":\"actual-1\",\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{\"sessionId\":\"actual-session\",\"isStreaming\":false,\"isCompacting\":false,\"pendingMessageCount\":0}}\r\n")); var wire = new CodexJsonlProtocol(input, output, life); using var reply = await wire.ReadAsync(TestContext.Current.CancellationToken); Assert.NotNull(reply); var state = PiRpcProtocol.Response(reply.RootElement, "actual-1", "get_state"); Assert.Equal("actual-session", PiRpcProtocol.RequireIdleSession(state)); Assert.Throws<IOException>(() => PiRpcProtocol.Response(reply.RootElement, "foreign", "get_state")); Assert.Throws<IOException>(() => PiRpcProtocol.RequireIdleSession(state, "foreign")); Assert.Null(await wire.ReadAsync(TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData("queued")]
    [InlineData("handled")]
    public void Echoed_prompt_without_started_run_is_not_acceptance(string disposition) { using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { disposition })); Assert.Throws<IOException>(() => PiRpcProtocol.RequireStarted(doc.RootElement)); }
    [Fact] public void Premature_settlement_queued_work_and_string_booleans_refuse() { var state = new PiRpcProtocol(); Assert.Throws<IOException>(() => Event(state, "{\"type\":\"agent_start\"}")); state.AdmitPrompt(); Assert.Throws<IOException>(() => Event(state, "{\"type\":\"agent_settled\",\"aborted\":false}")); Assert.Throws<IOException>(() => Event(state, "{\"type\":\"queue_update\",\"steering\":[\"unrequested\"],\"followUp\":[]}")); using var doc = JsonDocument.Parse("{\"sessionId\":\"a\",\"isStreaming\":\"false\",\"isCompacting\":false,\"pendingMessageCount\":0}"); Assert.Throws<IOException>(() => PiRpcProtocol.RequireIdleSession(doc.RootElement)); }
    [Theory]
    [InlineData("message-stop")]
    [InlineData("message-error")]
    [InlineData("agent-start")]
    [InlineData("repeated-settlement")]
    [InlineData("extension-error")]
    [InlineData("queued-work")]
    [InlineData("tool-start")]
    [InlineData("tool-end")]
    [InlineData("turn-start")]
    [InlineData("message-start")]
    [InlineData("unknown")]
    [InlineData("empty-queue")]
    [InlineData("extension-ui")]
    public void Late_state_event_permanently_refuses_previously_successful_settlement(string late)
    {
        var state = Started();
        const string successfulMessage = "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"}}";
        Event(state, successfulMessage); Event(state, "{\"type\":\"agent_settled\",\"aborted\":false}"); state.RequireSuccess();
        var actual = late switch
        {
            "message-stop" => successfulMessage,
            "message-error" => "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"error\"}}",
            "agent-start" => "{\"type\":\"agent_start\"}",
            "repeated-settlement" => "{\"type\":\"agent_settled\",\"aborted\":false}",
            "extension-error" => "{\"type\":\"extension_error\"}",
            "queued-work" => "{\"type\":\"queue_update\",\"steering\":[\"unrequested\"],\"followUp\":[]}",
            "tool-start" => "{\"type\":\"tool_execution_start\"}",
            "tool-end" => "{\"type\":\"tool_execution_end\"}",
            "turn-start" => "{\"type\":\"turn_start\"}",
            "message-start" => "{\"type\":\"message_start\"}",
            "unknown" => "{\"type\":\"unqualified-event\"}",
            "empty-queue" => "{\"type\":\"queue_update\",\"steering\":[],\"followUp\":[]}",
            "extension-ui" => "{\"type\":\"extension_ui_request\",\"id\":\"ui\",\"method\":\"confirm\"}",
            _ => throw new ArgumentOutOfRangeException(nameof(late)),
        };
        Assert.Throws<IOException>(() => Event(state, actual));
        Assert.Throws<IOException>(state.RequireSuccess);
        Assert.Throws<IOException>(() => Event(state, successfulMessage));
        Assert.Throws<IOException>(state.RequireSuccess);
    }
    [Fact]
    public void Assistant_completion_before_run_entry_cannot_be_reused_by_a_later_start()
    {
        var state = new PiRpcProtocol(); state.AdmitPrompt();
        Assert.Throws<IOException>(() => Event(state, "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"}}"));
        Assert.Throws<IOException>(() => Event(state, "{\"type\":\"agent_start\"}"));
        Assert.Throws<IOException>(() => Event(state, "{\"type\":\"agent_settled\",\"aborted\":false}"));
        Assert.Throws<IOException>(state.RequireSuccess);
    }
    [Fact]
    public void Repeated_run_entry_refuses_before_reusing_an_earlier_assistant_completion()
    {
        var state = Started(); Event(state, "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"}}");
        Assert.Throws<IOException>(() => Event(state, "{\"type\":\"agent_start\"}"));
        Assert.Throws<IOException>(state.RequireSuccess);
    }
    [Theory]
    [InlineData("{\"type\":\"message_end\"}")]
    [InlineData("{\"type\":\"message_end\",\"message\":[]}")]
    [InlineData("{\"type\":\"queue_update\",\"steering\":false,\"followUp\":[]}")]
    [InlineData("{\"type\":\"queue_update\",\"steering\":[]}")]
    [InlineData("{\"type\":\"agent_settled\"}")]
    public void Malformed_event_members_are_typed_IO_failures_and_permanently_poison_success(string text)
    {
        var state = Started(); Assert.Throws<IOException>(() => Event(state, text));
        Assert.Throws<IOException>(state.RequireSuccess);
    }
    [Theory]
    [InlineData("{\"id\":\"r\",\"type\":\"response\",\"command\":\"get_state\"}", true)]
    [InlineData("{\"id\":\"r\",\"type\":\"response\",\"command\":\"get_state\",\"success\":null}", true)]
    [InlineData("{\"sessionId\":\"s\",\"isStreaming\":false,\"isCompacting\":false}", false)]
    [InlineData("{\"sessionId\":\"s\",\"isStreaming\":false,\"isCompacting\":false,\"pendingMessageCount\":\"0\"}", false)]
    public void Malformed_response_and_idle_members_are_typed_IO_failures(string text, bool response)
    {
        using var doc = JsonDocument.Parse(text);
        if (response) Assert.Throws<IOException>(() => PiRpcProtocol.Response(doc.RootElement, "r", "get_state"));
        else Assert.Throws<IOException>(() => PiRpcProtocol.RequireIdleSession(doc.RootElement));
    }
}
