using System.Text.Json;
namespace DeliveryForge.Tool.Pi;

// Provider-specific schema/state; only byte framing is shared with the Codex codec.
// One actual process, one prompt, no queue/steer/model/auth mutation route.
public sealed class PiRpcProtocol {
    bool prompt,entered,settled;string? finalAssistantStop;
    public void AdmitPrompt(){if(prompt)throw new IOException("One actual selected Pi prompt only.");prompt=true;}
    public bool Settled=>settled;
    public bool Aborted {get;private set;}
    public void ObserveEvent(JsonElement row) {
        var type=Text(row,"type");
        if(type=="agent_start") {if(!prompt)throw new IOException("Unrequested Pi run before actual prompt admission.");if(settled)throw new IOException("Pi restarted after settled; no automatic second dispatch admitted.");entered=true;}
        if(type=="message_end") {var message=row.GetProperty("message");if(Text(message,"role")=="assistant")finalAssistantStop=Text(message,"stopReason");}
        if(type=="agent_settled") {if(!entered||settled)throw new IOException("Pi settled without actual run or repeated settlement.");Aborted=Boolean(row,"aborted");settled=true;}
        if(type=="extension_error")throw new IOException("Actual Pi extension failed; no successful worker result inferred.");
        if(type=="queue_update"&&(row.GetProperty("steering").GetArrayLength()!=0||row.GetProperty("followUp").GetArrayLength()!=0))throw new IOException("Pi acquired unrequested queued work.");
    }
    public void RequireSuccess() {if(!settled||Aborted||finalAssistantStop!="stop")throw new IOException("Actual Pi run is not settled with a successful final assistant message.");}
    public static JsonElement Response(JsonElement row,string expectedId,string expectedCommand) {
        if(Text(row,"type")!="response"||Text(row,"id")!=expectedId||Text(row,"command")!=expectedCommand)throw new IOException("Pi response has foreign ID or command.");
        var success=Boolean(row,"success");
        var allowed=success?new[]{"id","type","command","success","data"}:new[]{"id","type","command","success","error"};
        if(row.EnumerateObject().Any(x=>!allowed.Contains(x.Name,StringComparer.Ordinal)))throw new IOException("Unknown Pi response envelope member.");
        if(!success)throw new IOException("Actual Pi command refused: "+Text(row,"error"));
        return row.TryGetProperty("data",out var data)?data.Clone():default;
    }
    public static string RequireIdleSession(JsonElement state,string? expected=null) {
        var session=Text(state,"sessionId");if(expected is not null&&session!=expected)throw new IOException("Actual Pi session changed.");
        if(Boolean(state,"isStreaming")||Boolean(state,"isCompacting")||!state.GetProperty("pendingMessageCount").TryGetInt32(out var count)||count!=0)throw new IOException("Actual Pi session is not idle without pending work.");
        return session;
    }
    public static void RequireStarted(JsonElement response){if(Text(response,"disposition")!="started")throw new IOException("Pi prompt was queued or handled without starting the assigned run.");}
    public static string Text(JsonElement row,string key){if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty(key,out var value)||value.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(value.GetString())||value.GetString()!.Length>4096)throw new IOException("Invalid Pi "+key);return value.GetString()!;}
    static bool Boolean(JsonElement row,string key){var value=row.GetProperty(key);if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new IOException("Pi boolean required: "+key);return value.GetBoolean();}
}
