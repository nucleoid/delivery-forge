using System.Text.Json;
namespace DeliveryForge.Tool.Pi;

// Provider-specific schema/state; only byte framing is shared with the Codex codec.
// One actual process, one prompt, no queue/steer/model/auth mutation route.
public sealed class PiRpcProtocol
{
    bool prompt, entered, settled; string? finalAssistantStop; Exception? protocolFailure;
    public void AdmitPrompt() { if (prompt) throw new IOException("One actual selected Pi prompt only."); prompt = true; }
    public bool Settled => settled;
    public bool Aborted { get; private set; }
    public void ObserveEvent(JsonElement row)
    {
        try
        {
            if (protocolFailure is not null) throw new IOException("Pi event stream previously refused.", protocolFailure);
            var type = Text(row, "type");
            if (settled) throw new IOException("Pi event after settlement; success permanently refused.");
            if (type == "agent_start") { if (!prompt || entered) throw new IOException("Unrequested or repeated Pi run; one actual admitted prompt only."); entered = true; finalAssistantStop = null; }
            if (type == "message_end") { if (!entered) throw new IOException("Pi message completed before the actual admitted run entered."); var message = Member(row, "message"); if (Text(message, "role") == "assistant") finalAssistantStop = Text(message, "stopReason"); }
            if (type == "agent_settled") { if (!entered || settled) throw new IOException("Pi settled without actual run or repeated settlement."); Aborted = Boolean(row, "aborted"); settled = true; }
            if (type == "extension_error") throw new IOException("Actual Pi extension failed; no successful worker result inferred.");
            if (type == "queue_update" && (ArrayCount(row, "steering") != 0 || ArrayCount(row, "followUp") != 0)) throw new IOException("Pi acquired unrequested queued work.");
        }
        catch (Exception failure) { protocolFailure ??= failure; throw; }
    }
    public void RequireSuccess() { if (protocolFailure is not null) throw new IOException("Pi event stream remains failed.", protocolFailure); if (!settled || Aborted || finalAssistantStop != "stop") throw new IOException("Actual Pi run is not settled with a successful final assistant message."); }
    public static JsonElement Response(JsonElement row, string expectedId, string expectedCommand)
    {
        if (Text(row, "type") != "response" || Text(row, "id") != expectedId || Text(row, "command") != expectedCommand) throw new IOException("Pi response has foreign ID or command.");
        var success = Boolean(row, "success");
        var allowed = success ? new[] { "id", "type", "command", "success", "data" } : new[] { "id", "type", "command", "success", "error" };
        if (row.EnumerateObject().Any(x => !allowed.Contains(x.Name, StringComparer.Ordinal))) throw new IOException("Unknown Pi response envelope member.");
        if (!success) throw new IOException("Actual Pi command refused: " + Text(row, "error"));
        return row.TryGetProperty("data", out var data) ? data.Clone() : default;
    }
    public static string RequireIdleSession(JsonElement state, string? expected = null)
    {
        var session = Text(state, "sessionId"); if (expected is not null && session != expected) throw new IOException("Actual Pi session changed.");
        var pending = Member(state, "pendingMessageCount");
        if (Boolean(state, "isStreaming") || Boolean(state, "isCompacting") || pending.ValueKind != JsonValueKind.Number || !pending.TryGetInt32(out var count) || count != 0) throw new IOException("Actual Pi session is not idle without pending work.");
        return session;
    }
    public static void RequireStarted(JsonElement response) { if (Text(response, "disposition") != "started") throw new IOException("Pi prompt was queued or handled without starting the assigned run."); }
    public static string Text(JsonElement row, string key) { if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 4096) throw new IOException("Invalid Pi " + key); return value.GetString()!; }
    static JsonElement Member(JsonElement row, string key) { if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out var value)) throw new IOException("Pi member required: " + key); return value; }
    static int ArrayCount(JsonElement row, string key) { var value = Member(row, key); if (value.ValueKind != JsonValueKind.Array) throw new IOException("Pi array required: " + key); return value.GetArrayLength(); }
    static bool Boolean(JsonElement row, string key) { var value = Member(row, key); if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new IOException("Pi boolean required: " + key); return value.GetBoolean(); }
}
