using System.Text.Json;
namespace DeliveryForge.Tool.Claude;

// Protocol DATA correspondence only. Kernel/Job ownership is never inferred
// from any Claude message, terminal result, idle event or task identifier.
public sealed class ClaudeStreamState(string promptId, string exactPrompt)
{
    readonly HashSet<string> started = new(StringComparer.Ordinal), finished = new(StringComparer.Ordinal);
    string? session, provisionalSession; bool replayed, initialized, admitted, poisoned; JsonElement? result; string? activity;
    public string Session => session ?? throw new IOException("Actual Claude init/session not received.");
    public bool Acknowledged => initialized && replayed;
    public bool Settled => !poisoned && Acknowledged && result is not null && activity == "idle" && started.SetEquals(finished);
    public void AdmitPrompt() { if (admitted) throw new IOException("Original Claude prompt already entered."); admitted = true; activity = null; }
    internal static JsonElement Member(JsonElement row, string key) { if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out var value)) throw new IOException("Actual Claude member required: " + key); return value; }
    public static string Text(JsonElement row, string key) { var value = Member(row, key); if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()) || value.GetString()!.Length > 4096) throw new IOException("Actual Claude string identity invalid: " + key); return value.GetString()!; }
    void SameSession(JsonElement row) { if (Text(row, "session_id") != Session) throw new IOException("Foreign Claude session."); }
    public void Observe(JsonElement row) { if (poisoned) throw new IOException("Original Claude correspondence already failed; no retry."); try { ObserveCore(row); } catch { poisoned = true; throw; } }
    void ObserveCore(JsonElement row)
    {
        var type = Text(row, "type");
        if (type == "system")
        {
            var subtype = Text(row, "subtype");
            if (subtype == "init")
            {
                if (initialized) throw new IOException("Duplicate actual Claude init.");
                var initSession = Text(row, "session_id");
                if (provisionalSession is not null && initSession != provisionalSession)
                    throw new IOException("Actual Claude init differs from its provisional SDK session.");
                session = initSession; initialized = true; return;
            }
            // CLI 2.1.306 may report its SDK-only running state before init.
            // Retain only this one provisional correlation, never an accepted
            // Session, acknowledgement, idle state or completion authority.
            if (!initialized)
            {
                if (provisionalSession is not null || subtype != "session_state_changed" ||
                    Text(row, "state") != "running" || Member(row, "sdk_host_only").ValueKind != JsonValueKind.True)
                    throw new IOException("Unsupported or duplicate actual Claude pre-init state.");
                _ = Text(row, "uuid");
                provisionalSession = Text(row, "session_id"); return;
            }
            // Selected SDK permits task_updated without session_id. Only a
            // task ID already entered on THIS actual stream can be updated.
            if (subtype != "task_updated" || row.TryGetProperty("session_id", out var actualSession) && actualSession.ValueKind != JsonValueKind.Null) SameSession(row);
            if (subtype == "session_state_changed")
            {
                var state = Text(row, "state"); if (state is not ("running" or "idle" or "requires_action")) throw new IOException("Unsupported actual Claude session state."); activity = state; return;
            }
            if (subtype == "task_started") { var id = Text(row, "task_id"); if (started.Count >= 128 || !started.Add(id)) throw new IOException("Duplicate/excess Claude task."); activity = "running"; return; }
            if (subtype is "task_notification" or "task_updated")
            {
                var id = Text(row, "task_id"); if (!started.Contains(id)) throw new IOException("Foreign Claude task completion.");
                var status = subtype == "task_updated" ? Text(Member(row, "patch"), "status") : Text(row, "status");
                if (status is "completed" or "failed" or "stopped") finished.Add(id); return;
            }
            throw new IOException("Unsupported actual Claude system subtype; no settlement inferred.");
        }
        SameSession(row);
        if (type == "user")
        {
            if (!admitted || replayed || Text(row, "uuid") != promptId || Member(row, "parent_tool_use_id").ValueKind != JsonValueKind.Null) throw new IOException("Foreign/duplicate actual Claude user acknowledgement.");
            var message = Member(row, "message"); var content = Member(message, "content"); if (Text(message, "role") != "user" || content.ValueKind != JsonValueKind.String || content.GetString() != exactPrompt) throw new IOException("Actual replay differs from complete parent-selected assignment.");
            if (Text(Member(row, "origin"), "kind") != "human") throw new IOException("Actual prompt origin differs."); replayed = true; return;
        }
        if (type == "rate_limit_event")
        {
            _ = Text(row, "uuid"); var status = Text(Member(row, "rate_limit_info"), "status"); if (status is not ("allowed" or "allowed_warning")) throw new IOException("Actual Claude rate limit refused work."); return;
        }
        if (type == "result")
        {
            if (!Acknowledged || result is not null || Text(Member(row, "origin"), "kind") != "human" || Text(row, "subtype") != "success" || Text(row, "terminal_reason") != "completed" || Member(row, "is_error").ValueKind != JsonValueKind.False) throw new IOException("Actual Claude result is foreign, duplicate, incomplete or unsuccessful.");
            _ = Text(row, "uuid"); if (row.TryGetProperty("permission_denials", out var denials) && (denials.ValueKind != JsonValueKind.Array || denials.GetArrayLength() != 0)) throw new IOException("Actual Claude permission denial prevents success.");
            if (row.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null && (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0)) throw new IOException("Actual Claude errors prevent success."); result = row.Clone(); return;
        }
        if (type is "assistant" or "stream_event") { activity = "running"; return; }
        throw new IOException("Unsupported actual Claude stream message; no success inferred.");
    }
    public void RequireSuccess() { if (!Settled) throw new IOException("Actual Claude prompt/result/idle/task correspondence incomplete."); }
}
