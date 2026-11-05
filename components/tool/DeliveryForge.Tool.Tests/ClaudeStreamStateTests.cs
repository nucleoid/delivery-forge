using System.Text.Json;
using DeliveryForge.Tool.Claude;
namespace DeliveryForge.Tool.Tests;

// Managed protocol correspondence controls only, not installed Claude evidence.
public sealed class ClaudeStreamStateTests
{
    const string PromptId = "11b2dca9-5389-42de-bd55-701c9b34717f", Session = "actual-test-session", Prompt = "Implement selected step: add strict local input validation. Acceptance: malformed input refuses before writes.";
    static void Observe(ClaudeStreamState state, object actual) { using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(actual)); state.Observe(doc.RootElement); }
    static ClaudeStreamState Admitted() { var state = new ClaudeStreamState(PromptId, Prompt); Observe(state, new { type = "system", subtype = "init", session_id = Session }); state.AdmitPrompt(); return state; }
    static ClaudeStreamState Started() { var state = Admitted(); Observe(state, new { type = "user", session_id = Session, uuid = PromptId, parent_tool_use_id = (string?)null, message = new { role = "user", content = Prompt }, origin = new { kind = "human" } }); Assert.True(state.Acknowledged); return state; }
    static void Result(ClaudeStreamState state, string session = Session, string subtype = "success", string reason = "completed", bool error = false, string origin = "human") => Observe(state, new { type = "result", session_id = session, subtype, terminal_reason = reason, is_error = error, uuid = "result-test-id", origin = new { kind = origin }, permission_denials = Array.Empty<string>(), errors = Array.Empty<string>() });
    static void Idle(ClaudeStreamState state) => Observe(state, new { type = "system", subtype = "session_state_changed", session_id = Session, state = "idle", sdk_host_only = true });
    [Fact] public void Actual_ack_result_and_idle_required_in_either_supported_order() { var first = Started(); Idle(first); Assert.False(first.Settled); Result(first); first.RequireSuccess(); var second = Started(); Result(second); Assert.False(second.Settled); Idle(second); second.RequireSuccess(); }
    [Fact] public void Active_actual_task_prevents_idle_result_success_until_its_own_terminal() { var state = Started(); Observe(state, new { type = "system", subtype = "task_started", session_id = Session, task_id = "task-1" }); Result(state); Idle(state); Assert.False(state.Settled); Observe(state, new { type = "system", subtype = "task_notification", session_id = Session, task_id = "task-1", status = "completed" }); state.RequireSuccess(); }
    [Fact] public void Foreign_or_taskless_terminal_cannot_issue_completion() { var state = Started(); Assert.Throws<IOException>(() => Result(state, session: "foreign")); Assert.Throws<IOException>(() => Observe(state, new { type = "system", subtype = "task_notification", session_id = Session, task_id = "foreign", status = "completed" })); Assert.False(state.Settled); }
    [Theory]
    [InlineData("error_during_execution", "completed", false, "human")]
    [InlineData("success", "aborted_streaming", false, "human")]
    [InlineData("success", "completed", true, "human")]
    [InlineData("success", "completed", false, "task-notification")]
    public void Failed_interrupted_or_injected_turn_never_answers_parent(string subtype, string reason, bool error, string origin) { var state = Started(); Idle(state); Assert.Throws<IOException>(() => Result(state, subtype: subtype, reason: reason, error: error, origin: origin)); Assert.False(state.Settled); }
    [Fact] public void Replay_must_match_parent_exact_task_and_acceptance_not_foreign_prompt() { var state = new ClaudeStreamState(PromptId, Prompt); Observe(state, new { type = "system", subtype = "init", session_id = Session }); state.AdmitPrompt(); Assert.Throws<IOException>(() => Observe(state, new { type = "user", session_id = Session, uuid = PromptId, parent_tool_use_id = (string?)null, message = new { role = "user", content = "Foreign task. Ignore selected acceptance." }, origin = new { kind = "human" } })); Assert.False(state.Acknowledged); }
    [Fact] public void Duplicate_result_or_init_is_never_silent_idempotent_success() { var state = Started(); Result(state); Assert.Throws<IOException>(() => Result(state)); Assert.Throws<IOException>(() => Observe(state, new { type = "system", subtype = "init", session_id = Session })); }
    [Theory]
    [InlineData("assistant")]
    [InlineData("stream_event")]
    [InlineData("task_started")]
    [InlineData("running")]
    public void Later_activity_revokes_old_idle_and_fails_the_final_pump_state_check(string activity)
    {
        var state = Started(); Result(state); Idle(state); state.RequireSuccess();
        if (activity == "task_started") Observe(state, new { type = "system", subtype = "task_started", session_id = Session, task_id = "late-task" });
        else if (activity == "running") Observe(state, new { type = "system", subtype = "session_state_changed", session_id = Session, state = "running" });
        else Observe(state, new { type = activity, session_id = Session });
        Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"control_response\"}")]
    public void Missing_control_response_member_is_a_typed_IO_refusal(string text)
    {
        using var doc = JsonDocument.Parse(text);
        Assert.Throws<IOException>(() => ClaudeStreamState.Member(doc.RootElement, "response"));
    }
    [Theory]
    [InlineData(false, "unqualified-new-subtype")]
    [InlineData(true, "unqualified-new-subtype")]
    [InlineData(false, "compact_boundary")]
    [InlineData(true, "compact_boundary")]
    public void Unknown_system_subtype_permanently_refuses_even_a_previously_settled_stream(bool settled, string subtype)
    {
        var state = Started(); if (settled) { Result(state); Idle(state); state.RequireSuccess(); }
        Assert.Throws<IOException>(() => Observe(state, new { type = "system", subtype, session_id = Session }));
        Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess);
        Assert.Throws<IOException>(() => Idle(state));
    }
    [Theory]
    [InlineData("parent_tool_use_id")]
    [InlineData("message")]
    [InlineData("origin")]
    public void Missing_prompt_members_are_typed_IO_failures_without_acknowledgement(string missing)
    {
        var state = Admitted(); var row = new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["session_id"] = Session,
            ["uuid"] = PromptId,
            ["parent_tool_use_id"] = null,
            ["message"] = new { role = "user", content = Prompt },
            ["origin"] = new { kind = "human" },
        };
        row.Remove(missing); Assert.Throws<IOException>(() => Observe(state, row));
        Assert.False(state.Acknowledged); Assert.False(state.Settled);
    }
    [Theory]
    [InlineData("origin")]
    [InlineData("is_error")]
    public void Missing_result_members_are_typed_IO_failures_without_settlement(string missing)
    {
        var state = Started(); Idle(state); var row = new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["session_id"] = Session,
            ["subtype"] = "success",
            ["terminal_reason"] = "completed",
            ["uuid"] = "result-test-id",
            ["is_error"] = false,
            ["origin"] = new { kind = "human" },
        };
        row.Remove(missing); Assert.Throws<IOException>(() => Observe(state, row));
        Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"type\":\"rate_limit_event\",\"session_id\":\"actual-test-session\",\"uuid\":\"rate\"}")]
    [InlineData("{\"type\":\"rate_limit_event\",\"session_id\":\"actual-test-session\",\"uuid\":\"rate\",\"rate_limit_info\":[]}")]
    public void Malformed_frame_objects_and_nested_members_are_typed_IO_failures(string text)
    {
        var state = Started(); using var doc = JsonDocument.Parse(text);
        Assert.Throws<IOException>(() => state.Observe(doc.RootElement));
        Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess);
    }
    [Fact]
    public void Known_task_update_with_missing_patch_is_typed_IO_failure()
    {
        var state = Started(); Observe(state, new { type = "system", subtype = "task_started", session_id = Session, task_id = "task-1" });
        Assert.Throws<IOException>(() => Observe(state, new { type = "system", subtype = "task_updated", task_id = "task-1" }));
        Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess);
    }
    static Dictionary<string, object?> PreInitRunning() => new()
    {
        ["type"] = "system",
        ["subtype"] = "session_state_changed",
        ["state"] = "running",
        ["sdk_host_only"] = true,
        ["uuid"] = "synthetic-sdk-running-id",
        ["session_id"] = Session
    };
    static void Init(ClaudeStreamState state, string session = Session) =>
        Observe(state, new { type = "system", subtype = "init", session_id = session });
    static void Replay(ClaudeStreamState state, string prompt = Prompt) =>
        Observe(state, new
        {
            type = "user",
            session_id = Session,
            uuid = PromptId,
            parent_tool_use_id = (string?)null,
            message = new { role = "user", content = prompt },
            origin = new { kind = "human" }
        });
    [Fact]
    public void One_sdk_running_before_init_stays_provisional_until_matching_init_exact_replay_result_and_idle()
    {
        var state = new ClaudeStreamState(PromptId, Prompt); state.AdmitPrompt();
        Observe(state, PreInitRunning());
        Assert.Throws<IOException>(() => state.Session); Assert.False(state.Acknowledged); Assert.False(state.Settled);
        Assert.Throws<IOException>(state.RequireSuccess);
        Init(state); Assert.Equal(Session, state.Session); Assert.False(state.Acknowledged); Assert.False(state.Settled);
        Replay(state); Assert.True(state.Acknowledged); Assert.False(state.Settled);
        Result(state); Assert.False(state.Settled); Idle(state); state.RequireSuccess();
    }
    [Theory]
    [InlineData("idle")]
    [InlineData("requires-action")]
    [InlineData("unknown-subtype")]
    [InlineData("compact")]
    [InlineData("other-type")]
    [InlineData("flag-missing")]
    [InlineData("flag-false")]
    [InlineData("flag-string")]
    [InlineData("uuid-missing")]
    [InlineData("uuid-empty")]
    [InlineData("uuid-null")]
    [InlineData("session-missing")]
    [InlineData("session-empty")]
    [InlineData("session-null")]
    public void Other_or_malformed_pre_init_rows_permanently_poison_correspondence(string mutation)
    {
        var state = new ClaudeStreamState(PromptId, Prompt); state.AdmitPrompt(); var row = PreInitRunning();
        switch (mutation)
        {
            case "idle": row["state"] = "idle"; break;
            case "requires-action": row["state"] = "requires_action"; break;
            case "unknown-subtype": row["subtype"] = "unsupported"; break;
            case "compact": row["subtype"] = "compact_boundary"; break;
            case "other-type": row["type"] = "assistant"; break;
            case "flag-missing": row.Remove("sdk_host_only"); break;
            case "flag-false": row["sdk_host_only"] = false; break;
            case "flag-string": row["sdk_host_only"] = "true"; break;
            case "uuid-missing": row.Remove("uuid"); break;
            case "uuid-empty": row["uuid"] = ""; break;
            case "uuid-null": row["uuid"] = null; break;
            case "session-missing": row.Remove("session_id"); break;
            case "session-empty": row["session_id"] = ""; break;
            case "session-null": row["session_id"] = null; break;
            default: throw new ArgumentException("Unknown synthetic mutation.");
        }
        Assert.Throws<IOException>(() => Observe(state, row));
        Assert.Throws<IOException>(() => Init(state)); Assert.Throws<IOException>(() => state.Session);
        Assert.False(state.Acknowledged); Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Duplicate_pre_init_running_or_foreign_init_cannot_promote_the_provisional_session(bool foreignInit)
    {
        var state = new ClaudeStreamState(PromptId, Prompt); state.AdmitPrompt(); Observe(state, PreInitRunning());
        if (foreignInit) Assert.Throws<IOException>(() => Init(state, "foreign-synthetic-session"));
        else Assert.Throws<IOException>(() => Observe(state, PreInitRunning()));
        Assert.Throws<IOException>(() => Init(state)); Assert.Throws<IOException>(() => state.Session);
        Assert.False(state.Acknowledged); Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess);
    }
    [Fact]
    public void Provisional_running_does_not_relax_the_complete_parent_prompt_replay()
    {
        var state = new ClaudeStreamState(PromptId, Prompt); state.AdmitPrompt(); Observe(state, PreInitRunning()); Init(state);
        Assert.Throws<IOException>(() => Replay(state, "Foreign synthetic prompt"));
        Assert.False(state.Acknowledged); Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess);
    }
    static void Lifecycle(ClaudeStreamState state, string step, string command = PromptId, string session = Session, string? uuid = null) =>
        Observe(state, new { type = "command_lifecycle", command_uuid = command, state = step, uuid = uuid ?? "command-event-" + step, session_id = session });
    static ClaudeStreamState CommandStarted()
    {
        var state = new ClaudeStreamState(PromptId, Prompt); state.AdmitPrompt(); Observe(state, PreInitRunning());
        Lifecycle(state, "queued"); Lifecycle(state, "started"); Init(state); Replay(state); return state;
    }
    static void CommandResult(ClaudeStreamState state, string prompt = PromptId) =>
        Observe(state, new
        {
            type = "result",
            session_id = Session,
            subtype = "success",
            terminal_reason = "completed",
            is_error = false,
            uuid = "result-command-test-id",
            user_message_uuid = prompt,
            origin = new { kind = "human" },
            permission_denials = Array.Empty<string>(),
            errors = Array.Empty<string>()
        });
    [Fact]
    public void Complete_observed_sdk_command_sequence_requires_init_exact_replay_result_terminal_and_fresh_idle()
    {
        var state = new ClaudeStreamState(PromptId, Prompt); state.AdmitPrompt(); Observe(state, PreInitRunning());
        Lifecycle(state, "queued"); Lifecycle(state, "started");
        Assert.Throws<IOException>(() => state.Session); Assert.False(state.Acknowledged); Assert.False(state.Settled);
        Init(state); Assert.False(state.Acknowledged); Replay(state); Assert.True(state.Acknowledged);
        Observe(state, new { type = "assistant", session_id = Session });
        Observe(state, new { type = "rate_limit_event", session_id = Session, uuid = "command-rate", rate_limit_info = new { status = "allowed" } });
        CommandResult(state); Idle(state); Assert.False(state.Settled); // no terminal yet
        Lifecycle(state, "completed"); Assert.False(state.Settled); // earlier idle cannot settle a later terminal
        Idle(state); state.RequireSuccess();
    }
    [Theory]
    [InlineData("not-admitted")]
    [InlineData("no-provisional")]
    [InlineData("foreign-command")]
    [InlineData("foreign-session")]
    [InlineData("missing-command")]
    [InlineData("missing-session")]
    [InlineData("missing-uuid")]
    [InlineData("empty-uuid")]
    [InlineData("unknown-state")]
    [InlineData("failed-state")]
    [InlineData("started-first")]
    [InlineData("completed-first")]
    [InlineData("duplicate-queued")]
    [InlineData("duplicate-started")]
    [InlineData("duplicate-event-uuid")]
    [InlineData("replay-before-start")]
    [InlineData("completed-before-result")]
    [InlineData("result-missing-prompt")]
    [InlineData("result-foreign-prompt")]
    [InlineData("foreign-session-after-init")]
    [InlineData("duplicate-completed")]
    public void Foreign_malformed_reordered_or_duplicate_commands_permanently_poison_the_original(string mutation)
    {
        var state = new ClaudeStreamState(PromptId, Prompt);
        if (mutation != "not-admitted") state.AdmitPrompt();
        if (mutation != "no-provisional") Observe(state, PreInitRunning());
        var row = new Dictionary<string, object?>
        {
            ["type"] = "command_lifecycle",
            ["command_uuid"] = PromptId,
            ["state"] = "queued",
            ["uuid"] = "command-mutation",
            ["session_id"] = Session
        };
        Action reject = () => Observe(state, row);
        switch (mutation)
        {
            case "not-admitted": case "no-provisional": break;
            case "foreign-command": row["command_uuid"] = "foreign-command"; break;
            case "foreign-session": row["session_id"] = "foreign-session"; break;
            case "missing-command": row.Remove("command_uuid"); break;
            case "missing-session": row.Remove("session_id"); break;
            case "missing-uuid": row.Remove("uuid"); break;
            case "empty-uuid": row["uuid"] = ""; break;
            case "unknown-state": row["state"] = "cancelled"; break;
            case "failed-state": row["state"] = "failed"; break;
            case "started-first": row["state"] = "started"; break;
            case "completed-first": row["state"] = "completed"; break;
            case "duplicate-queued": Lifecycle(state, "queued"); break;
            case "duplicate-started": Lifecycle(state, "queued"); Lifecycle(state, "started"); row["state"] = "started"; break;
            case "duplicate-event-uuid": Lifecycle(state, "queued", uuid: "command-mutation"); row["state"] = "started"; break;
            case "replay-before-start": Lifecycle(state, "queued"); Init(state); reject = () => Replay(state); break;
            case "completed-before-result":
                Lifecycle(state, "queued"); Lifecycle(state, "started"); Init(state); Replay(state); row["state"] = "completed"; break;
            case "result-missing-prompt":
            case "result-foreign-prompt":
            case "foreign-session-after-init":
            case "duplicate-completed":
                Lifecycle(state, "queued"); Lifecycle(state, "started"); Init(state); Replay(state);
                if (mutation == "result-missing-prompt") reject = () => Result(state);
                else if (mutation == "result-foreign-prompt") reject = () => CommandResult(state, "foreign-prompt");
                else
                {
                    CommandResult(state); row["state"] = "completed";
                    if (mutation == "foreign-session-after-init") row["session_id"] = "foreign-session";
                    else Lifecycle(state, "completed");
                }
                break;
            default: throw new ArgumentException("Unknown synthetic command mutation.");
        }
        Assert.Throws<IOException>(reject); Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess);
        Assert.Throws<IOException>(() => Idle(state));
    }
    [Fact]
    public void Init_first_command_sequence_is_supported_and_later_command_cannot_reuse_settlement()
    {
        var state = Admitted(); Lifecycle(state, "queued"); Lifecycle(state, "started"); Replay(state); CommandResult(state);
        Lifecycle(state, "completed"); Idle(state); state.RequireSuccess();
        Assert.Throws<IOException>(() => Lifecycle(state, "queued", uuid: "later-command")); Assert.False(state.Settled);
    }
    [Fact]
    public void A_result_carrying_a_foreign_prompt_cannot_settle_the_legacy_init_first_route()
    {
        var state = Started(); Assert.Throws<IOException>(() => CommandResult(state, "foreign-prompt"));
        Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess);
    }

    // Sanitized shape of CLI 2.1.306 telemetry: synthetic identities and no
    // retained account, control response, selected prompt or command content.
    static Dictionary<string, object?> Thinking(int estimated = 50, int delta = 50) => new()
    {
        ["type"] = "system",
        ["subtype"] = "thinking_tokens",
        ["session_id"] = Session,
        ["uuid"] = "313ba9fd-2c3b-44a9-a711-4e3cd76e87bc",
        ["user_message_uuid"] = PromptId,
        ["estimated_tokens"] = estimated,
        ["estimated_tokens_delta"] = delta
    };
    [Fact]
    public void Thinking_telemetry_is_inert_before_exact_replay_and_through_complete_command_correspondence()
    {
        var state = new ClaudeStreamState(PromptId, Prompt); state.AdmitPrompt(); Observe(state, PreInitRunning());
        Lifecycle(state, "queued"); Lifecycle(state, "started"); Init(state);
        foreach (var row in new[] { Thinking(50, 50), Thinking(150, 100), Thinking(318, 168) })
        {
            Observe(state, row); Assert.False(state.Acknowledged); Assert.False(state.Settled);
            Assert.Throws<IOException>(state.RequireSuccess);
        }
        Replay(state); Assert.True(state.Acknowledged); Assert.False(state.Settled);
        Observe(state, new { type = "system", subtype = "task_started", session_id = Session, task_id = "thinking-control-task" });
        CommandResult(state); Lifecycle(state, "completed"); Idle(state);
        Observe(state, Thinking(318, 0)); Assert.False(state.Settled); // telemetry cannot finish the active task
        Observe(state, new { type = "system", subtype = "task_notification", session_id = Session, task_id = "thinking-control-task", status = "completed" });
        state.RequireSuccess(); Observe(state, Thinking(int.MaxValue, int.MaxValue)); state.RequireSuccess();
    }
    [Theory]
    [InlineData("foreign-session")]
    [InlineData("foreign-prompt")]
    [InlineData("not-admitted")]
    [InlineData("pre-init")]
    [InlineData("missing-uuid")]
    [InlineData("malformed-uuid")]
    [InlineData("malformed-prompt")]
    [InlineData("negative-estimate")]
    [InlineData("negative-delta")]
    [InlineData("string-estimate")]
    [InlineData("fractional-delta")]
    [InlineData("overflow-estimate")]
    [InlineData("missing-delta")]
    public void Foreign_unadmitted_preinit_or_malformed_thinking_telemetry_permanently_refuses(string mutation)
    {
        var selectedPrompt = mutation == "malformed-prompt" ? "not-a-prompt-uuid" : PromptId;
        var state = new ClaudeStreamState(selectedPrompt, Prompt);
        if (mutation != "pre-init") Init(state);
        if (mutation != "not-admitted") state.AdmitPrompt();
        var row = Thinking();
        switch (mutation)
        {
            case "foreign-session": row["session_id"] = "foreign-session"; break;
            case "foreign-prompt": row["user_message_uuid"] = "66201fd7-2365-4b5f-86c0-4c6070f02976"; break;
            case "not-admitted": case "pre-init": break;
            case "missing-uuid": row.Remove("uuid"); break;
            case "malformed-uuid": row["uuid"] = "not-an-event-uuid"; break;
            case "malformed-prompt": row["user_message_uuid"] = selectedPrompt; break;
            case "negative-estimate": row["estimated_tokens"] = -1; break;
            case "negative-delta": row["estimated_tokens_delta"] = -1; break;
            case "string-estimate": row["estimated_tokens"] = "50"; break;
            case "fractional-delta": row["estimated_tokens_delta"] = 0.5; break;
            case "overflow-estimate": row["estimated_tokens"] = (long)int.MaxValue + 1; break;
            case "missing-delta": row.Remove("estimated_tokens_delta"); break;
            default: throw new ArgumentException("Unknown synthetic telemetry mutation.");
        }
        Assert.Throws<IOException>(() => Observe(state, row));
        Assert.False(state.Acknowledged); Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess);
        Assert.Throws<IOException>(() => Idle(state));
    }
}
