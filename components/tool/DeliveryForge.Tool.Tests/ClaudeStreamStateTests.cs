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
    [Fact] public void Later_activity_revokes_old_idle_before_real_owner_retirement() { var state = Started(); Result(state); Idle(state); state.RequireSuccess(); Observe(state, new { type = "assistant", session_id = Session }); Assert.False(state.Settled); Assert.Throws<IOException>(state.RequireSuccess); }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unknown_system_subtype_permanently_refuses_even_a_previously_settled_stream(bool settled)
    {
        var state = Started(); if (settled) { Result(state); Idle(state); state.RequireSuccess(); }
        Assert.Throws<IOException>(() => Observe(state, new { type = "system", subtype = "unqualified-new-subtype", session_id = Session }));
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
            ["type"] = "user", ["session_id"] = Session, ["uuid"] = PromptId,
            ["parent_tool_use_id"] = null, ["message"] = new { role = "user", content = Prompt }, ["origin"] = new { kind = "human" },
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
            ["type"] = "result", ["session_id"] = Session, ["subtype"] = "success", ["terminal_reason"] = "completed",
            ["uuid"] = "result-test-id", ["is_error"] = false, ["origin"] = new { kind = "human" },
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
}
