using System.Text;
using System.Text.Json;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Pi;
using DeliveryForge.Tool.Codex;
namespace DeliveryForge.Tool.Tests;
public sealed class PiRpcProtocolTests {
    static void Event(PiRpcProtocol state,string text){using var doc=JsonDocument.Parse(text);state.ObserveEvent(doc.RootElement);}
    static PiRpcProtocol Started(){var state=new PiRpcProtocol();state.AdmitPrompt();Event(state,"{\"type\":\"agent_start\"}");return state;}
    [Fact] public void Actual_protocol_sequence_requires_settled_not_agent_end() {
        var state=Started();Event(state,"{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"}}");Event(state,"{\"type\":\"agent_end\",\"willRetry\":false}");Assert.Throws<IOException>(state.RequireSuccess);
        Event(state,"{\"type\":\"agent_settled\",\"aborted\":false}");state.RequireSuccess();Assert.Throws<IOException>(()=>Event(state,"{\"type\":\"agent_start\"}"));
    }
    [Theory][InlineData("error")][InlineData("aborted")][InlineData("length")][InlineData("toolUse")][InlineData("pending")][InlineData("deferred")]
    public void Final_non_success_assistant_never_becomes_successful_worker(string stop){var state=Started();Event(state,JsonSerializer.Serialize(new {type="message_end",message=new {role="assistant",stopReason=stop}}));Event(state,"{\"type\":\"agent_settled\",\"aborted\":false}");Assert.Throws<IOException>(state.RequireSuccess);}
    [Fact] public async Task Genuine_JSONL_response_joins_exact_id_command_and_idle_session() {
        using var life=new ToolLifetime(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken);using var input=new MemoryStream();using var output=new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":\"actual-1\",\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{\"sessionId\":\"actual-session\",\"isStreaming\":false,\"isCompacting\":false,\"pendingMessageCount\":0}}\r\n"));var wire=new CodexJsonlProtocol(input,output,life);using var reply=await wire.ReadAsync(TestContext.Current.CancellationToken);Assert.NotNull(reply);var state=PiRpcProtocol.Response(reply.RootElement,"actual-1","get_state");Assert.Equal("actual-session",PiRpcProtocol.RequireIdleSession(state));Assert.Throws<IOException>(()=>PiRpcProtocol.Response(reply.RootElement,"foreign","get_state"));Assert.Throws<IOException>(()=>PiRpcProtocol.RequireIdleSession(state,"foreign"));Assert.Null(await wire.ReadAsync(TestContext.Current.CancellationToken));
    }
    [Theory][InlineData("queued")][InlineData("handled")]
    public void Echoed_prompt_without_started_run_is_not_acceptance(string disposition){using var doc=JsonDocument.Parse(JsonSerializer.Serialize(new {disposition}));Assert.Throws<IOException>(()=>PiRpcProtocol.RequireStarted(doc.RootElement));}
    [Fact] public void Premature_settlement_queued_work_and_string_booleans_refuse(){var state=new PiRpcProtocol();Assert.Throws<IOException>(()=>Event(state,"{\"type\":\"agent_start\"}"));state.AdmitPrompt();Assert.Throws<IOException>(()=>Event(state,"{\"type\":\"agent_settled\",\"aborted\":false}"));Assert.Throws<IOException>(()=>Event(state,"{\"type\":\"queue_update\",\"steering\":[\"unrequested\"],\"followUp\":[]}"));using var doc=JsonDocument.Parse("{\"sessionId\":\"a\",\"isStreaming\":\"false\",\"isCompacting\":false,\"pendingMessageCount\":0}");Assert.Throws<IOException>(()=>PiRpcProtocol.RequireIdleSession(doc.RootElement));}
}
