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

    static DeliveryForge.Scheduling.ResourceBudget UsageBudget(long maximum = 100000)
    {
        var slots = new DeliveryForge.Scheduling.ResourceSlots(4, 2, 4096, 1, 1, 0);
        return DeliveryForge.Scheduling.ResourceBudget.Measure(slots, new("actual-containing-run", true, slots, TimeSpan.FromMinutes(10), maximum, 100));
    }
    static ReportedModelUsage.UsageNode.UsageSession UsageSession(long maximum = 100000) => ReportedModelUsage.Bind(UsageBudget(maximum), maximum, maximum).Register();
    static JsonElement UsageWire(long input, long output, string thread = "actual-thread", string turn = "actual-turn") => JsonSerializer.SerializeToElement(new
    {
        threadId = thread, turnId = turn,
        tokenUsage = new { total = new { inputTokens = input, outputTokens = output, cachedInputTokens = input, reasoningOutputTokens = output, totalTokens = checked(input + output) } }
    });
    static CodexReportedUsage BoundUsage(ReportedModelUsage.UsageNode.UsageSession session)
    {
        var observed = new CodexReportedUsage(session); observed.BindThread("actual-thread"); observed.BeginTurn(); observed.BindTurn("actual-turn"); return observed;
    }
    static JsonElement ClaudeUsage(string type, long input, long cacheCreate, long cacheRead, long output, string id = "m1") => JsonSerializer.SerializeToElement(new
    {
        type, session_id = "s1", message = new { id, usage = new { input_tokens = input, cache_creation_input_tokens = cacheCreate, cache_read_input_tokens = cacheRead, output_tokens = output } },
        usage = new { input_tokens = input, cache_creation_input_tokens = cacheCreate, cache_read_input_tokens = cacheRead, output_tokens = output }
    });
    [Theory]
    [InlineData(99999, false)]
    [InlineData(100000, true)]
    [InlineData(100001, true)]
    public void Reported_threshold_is_inclusive_and_is_not_a_provider_ceiling(long actual, bool stopped)
    {
        var session = UsageSession(); session.Observe(actual, 0, true);
        Assert.Equal(actual, session.Snapshot().Aggregate);
        if (stopped) Assert.Throws<ReportedUsageException>(session.RequireFinal); else session.RequireFinal();
    }
    [Fact]
    public void Different_nodes_and_reviewer_sessions_share_the_original_run_without_refund()
    {
        var budget = UsageBudget(); var worker = ReportedModelUsage.Bind(budget, 100000, 100000);
        var reviewer = ReportedModelUsage.Bind(budget, 100000, 100000);
        var a = worker.Register(); var b = reviewer.Register(50000);
        a.Observe(60000, 0, true); a.RequireFinal(); a.Finish(); b.Observe(40000, 0, true);
        Assert.Equal(100000, a.Snapshot().Aggregate);
        Assert.Throws<ReportedUsageException>(worker.Check);
        Assert.Throws<ReportedUsageException>(() => reviewer.Register());
        Assert.Throws<ReportedUsageException>(() => ReportedModelUsage.Bind(budget, 100000, 100000));
        b.Observe(45000, 0); Assert.Equal(105000, b.Snapshot().Aggregate); // Already in flight, still charged.
    }
    [Fact]
    public void Duplicate_cumulative_reports_and_Claude_final_do_not_charge_twice()
    {
        var codex = UsageSession(); var c = BoundUsage(codex);
        c.Observe(UsageWire(90, 10)); c.Observe(UsageWire(90, 10));
        c.Terminal(JsonSerializer.SerializeToElement(new { threadId = "actual-thread", turn = new { id = "actual-turn" } })); codex.RequireFinal();
        Assert.Equal((90L, 10L, 100L), codex.Snapshot()); // cached/reasoning are subsets.
        var claude = UsageSession(); var r = new ClaudeReportedUsage(claude);
        r.Observe(ClaudeUsage("assistant", 10, 20, 30, 4), "s1"); r.Observe(ClaudeUsage("assistant", 10, 20, 30, 4), "s1");
        r.Observe(ClaudeUsage("assistant", 1, 2, 3, 1, "m2"), "s1");
        r.Observe(ClaudeUsage("result", 11, 22, 33, 5), "s1"); claude.RequireFinal();
        Assert.Equal((66L, 5L, 71L), claude.Snapshot()); // Four disjoint Claude fields, once.
    }
    [Theory]
    [InlineData("true")]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("9223372036854775808")]
    [InlineData("null")]
    [InlineData("\"12\"")]
    public void Non_integer_or_missing_usage_cannot_become_zero(string value)
    {
        using var doc = JsonDocument.Parse("{\"n\":" + value + "}");
        Assert.Throws<ReportedUsageException>(() => UsageNumbers.Number(doc.RootElement, "n"));
        Assert.Throws<ReportedUsageException>(() => UsageNumbers.Number(JsonSerializer.SerializeToElement(new { }), "n"));
    }
    [Fact]
    public void Duplicate_numeric_keys_overflow_and_regression_fail_closed()
    {
        using var duplicate = JsonDocument.Parse("{\"n\":1,\"n\":2}");
        Assert.Throws<ReportedUsageException>(() => UsageNumbers.Number(duplicate.RootElement, "n"));
        var overflow = UsageSession(); overflow.Observe(long.MaxValue, 1, true);
        Assert.Throws<ReportedUsageException>(overflow.RequireFinal); Assert.Equal(0, overflow.Snapshot().Aggregate);
        var regression = UsageSession(); regression.Observe(100, 10); regression.Observe(99, 11, true);
        Assert.Throws<ReportedUsageException>(regression.RequireFinal); Assert.Equal(110, regression.Snapshot().Aggregate);
    }
    [Theory]
    [InlineData("foreign", "actual-turn")]
    [InlineData("actual-thread", "prior-turn")]
    public void Foreign_thread_or_turn_never_contributes_accepted_usage(string thread, string turn)
    {
        var session = UsageSession(); BoundUsage(session).Observe(UsageWire(10, 1, thread, turn));
        Assert.Throws<ReportedUsageException>(session.RequireFinal); Assert.Equal(0, session.Snapshot().Aggregate);
    }
    [Fact]
    public void Early_current_turn_usage_must_match_the_actual_later_reply()
    {
        var session = UsageSession(); var usage = new CodexReportedUsage(session); usage.BindThread("actual-thread"); usage.BeginTurn();
        usage.Observe(UsageWire(10, 1)); usage.Terminal(JsonSerializer.SerializeToElement(new { threadId = "actual-thread", turn = new { id = "actual-turn" } }));
        usage.BindTurn("actual-turn"); session.RequireFinal(); Assert.Equal(11, session.Snapshot().Aggregate);
        var wrong = UsageSession(); var mismatch = new CodexReportedUsage(wrong); mismatch.BindThread("actual-thread"); mismatch.BeginTurn();
        mismatch.Observe(UsageWire(10, 1, turn: "prior-turn")); mismatch.BindTurn("actual-turn"); Assert.Throws<ReportedUsageException>(wrong.RequireFinal);
    }
    [Fact]
    public void Pre_dispatch_usage_and_terminal_without_any_usage_are_not_final_evidence()
    {
        var early = UsageSession(); new CodexReportedUsage(early).Observe(UsageWire(1, 1)); Assert.Throws<ReportedUsageException>(early.RequireFinal);
        var missing = UsageSession(); var current = BoundUsage(missing);
        current.Terminal(JsonSerializer.SerializeToElement(new { threadId = "actual-thread", turn = new { id = "actual-turn" } })); Assert.Throws<ReportedUsageException>(missing.RequireFinal);
        var stale = UsageSession(); BoundUsage(stale).Observe(UsageWire(1, 1)); Assert.Throws<ReportedUsageException>(stale.RequireFinal);
    }
    [Fact]
    public void Claude_missing_foreign_or_regressing_final_usage_refuses_acceptance()
    {
        var missing = UsageSession(); new ClaudeReportedUsage(missing).Observe(ClaudeUsage("assistant", 1, 2, 3, 4), "s1"); Assert.Throws<ReportedUsageException>(missing.RequireFinal);
        var foreign = UsageSession(); new ClaudeReportedUsage(foreign).Observe(ClaudeUsage("result", 1, 2, 3, 4), "different"); Assert.Throws<ReportedUsageException>(foreign.RequireFinal);
        var regressed = UsageSession(); var usage = new ClaudeReportedUsage(regressed);
        usage.Observe(ClaudeUsage("assistant", 10, 20, 30, 4), "s1"); usage.Observe(ClaudeUsage("result", 9, 20, 30, 4), "s1"); Assert.Throws<ReportedUsageException>(regressed.RequireFinal);
        var malformed = UsageSession(); new ClaudeReportedUsage(malformed).Observe(JsonSerializer.SerializeToElement(new { type = "result", session_id = "s1", usage = new { input_tokens = 1, output_tokens = 2 } }), "s1"); Assert.Throws<ReportedUsageException>(malformed.RequireFinal);
    }
    [Fact]
    public void Smaller_session_limit_stops_the_shared_run_and_missing_budget_refuses_birth()
    {
        var node = ReportedModelUsage.Bind(UsageBudget(), 100000, 100000); var limited = node.Register(100);
        limited.Observe(99, 1, true); Assert.Throws<ReportedUsageException>(node.Check);
        Assert.Throws<ReportedUsageException>(() => ReportedModelUsage.Bind(null, 0, 0).Register());
    }
    [ThreadStatic] static bool observingUsage;
    [Fact]
    public async Task Stop_watchers_never_execute_on_the_observation_stack_and_all_active_work_is_signalled()
    {
        var node = ReportedModelUsage.Bind(UsageBudget(), 100000, 100000); var a = node.Register(); var b = node.Register(); var stops = 0;
        Task Stop() { Assert.False(observingUsage); Interlocked.Increment(ref stops); return Task.CompletedTask; }
        var one = a.WatchAsync(Stop); var two = b.WatchAsync(Stop);
        observingUsage = true; try { a.Observe(100000, 0, true); } finally { observingUsage = false; }
        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); Assert.Equal(2, stops);
        a.Finish(); Assert.Throws<ReportedUsageException>(a.RequireFinal); // Finish cannot erase a concurrently latched stop.
    }
    [Fact]
    public async Task Stop_failure_is_joined_with_the_original_failure_without_inventing_closure()
    {
        var session = UsageSession(); var cleanup = new IOException("actual retirement unknown"); var primary = new InvalidOperationException("actual protocol failure");
        var watcher = session.WatchAsync(() => Task.FromException(cleanup)); session.Refuse();
        var result = await Assert.ThrowsAsync<AggregateException>(() => session.JoinAsync(watcher, primary));
        Assert.Same(primary, result.InnerExceptions[0]); Assert.Same(cleanup, result.InnerExceptions[1]);
    }
    [Fact]
    public void Usage_projection_retains_numbers_but_not_provider_payload_secrets()
    {
        var wire = JsonSerializer.SerializeToElement(new { method = "thread/tokenUsage/updated", @params = new { threadId = "actual-thread", turnId = "actual-turn", secret = Secret, tokenUsage = new { total = new { inputTokens = 90, outputTokens = 10, cachedInputTokens = 80, reasoningOutputTokens = 2, totalTokens = 100 }, last = new { prompt = Secret } } } });
        var projected = CodexTrustProtocolData.Notification(wire);
        Assert.Equal(100, projected.GetProperty("params").GetProperty("tokenUsage").GetProperty("total").GetProperty("totalTokens").GetInt64());
        Assert.DoesNotContain(Secret, projected.GetRawText());
    }

    [Fact]
    public void Usage_arriving_after_Codex_terminal_is_counted_before_drained_acceptance()
    {
        var session = UsageSession(); var usage = BoundUsage(session);
        usage.Terminal(JsonSerializer.SerializeToElement(new { threadId = "actual-thread", turn = new { id = "actual-turn" } }));
        Assert.False(session.Evidence().FinalUsageKnown);
        usage.Observe(UsageWire(10, 2)); session.RequireFinal(); Assert.True(session.Evidence().FinalUsageKnown);
        Assert.Equal(12, session.Snapshot().Aggregate);
    }
    [Fact]
    public void Claude_partial_usage_updates_replace_components_and_reconcile_with_final()
    {
        var session = UsageSession(); var usage = new ClaudeReportedUsage(session);
        usage.Observe(JsonSerializer.SerializeToElement(new { type = "stream_event", session_id = "s1", @event = new { type = "message_start", message = new { id = "m1", usage = new { input_tokens = 10, cache_creation_input_tokens = 20, cache_read_input_tokens = 30, output_tokens = 1 } } } }), "s1");
        usage.Observe(JsonSerializer.SerializeToElement(new { type = "stream_event", session_id = "s1", @event = new { type = "message_delta", usage = new { output_tokens = 4 } } }), "s1");
        usage.Observe(ClaudeUsage("result", 10, 20, 30, 4), "s1"); session.RequireFinal(); Assert.Equal(64, session.Snapshot().Aggregate);
        usage.Observe(ClaudeUsage("result", 10, 20, 30, 4), "s1"); Assert.Throws<ReportedUsageException>(session.RequireFinal);
    }
    [Fact]
    public void Final_component_regression_is_not_hidden_by_equal_combined_input()
    {
        var session = UsageSession(); var usage = new ClaudeReportedUsage(session);
        usage.Observe(ClaudeUsage("assistant", 100, 0, 0, 1), "s1"); usage.Observe(ClaudeUsage("result", 0, 100, 0, 1), "s1");
        Assert.Throws<ReportedUsageException>(session.RequireFinal); Assert.Equal(101, session.Snapshot().Aggregate);
    }
    [Fact]
    public void Failure_snapshot_keeps_numeric_spend_and_final_unknown_separate_from_closure()
    {
        var session = UsageSession(); session.Observe(100000, 7); var evidence = session.Evidence();
        Assert.True(evidence.Stopped); Assert.True(evidence.StopThresholdReached); Assert.True(evidence.UsageObserved); Assert.False(evidence.FinalUsageKnown);
        Assert.Equal(100007, evidence.AggregateTokens); Assert.Equal(100000, evidence.AggregateStopAt);
        Assert.DoesNotContain("Closed", JsonSerializer.Serialize(evidence));
        Assert.Throws<ReportedUsageException>(() => session.Accept(() => "accepted"));
    }

    [Theory]
    [InlineData(11, 1, 11)]
    [InlineData(10, 2, 11)]
    [InlineData(10, 1, 12)]
    public void Codex_subset_and_total_inconsistency_are_not_accepted(long cached, long reasoning, long total)
    {
        var session = UsageSession(); var usage = BoundUsage(session);
        usage.Observe(JsonSerializer.SerializeToElement(new { threadId = "actual-thread", turnId = "actual-turn", tokenUsage = new { total = new { inputTokens = 10, outputTokens = 1, cachedInputTokens = cached, reasoningOutputTokens = reasoning, totalTokens = total } } }));
        Assert.Throws<ReportedUsageException>(session.RequireFinal);
    }
    [Fact]
    public void Acceptance_cannot_erase_a_reentrant_stop_and_unrelated_run_identity_does_not_share_spend()
    {
        var a = UsageSession(); var b = UsageSession(); a.Observe(1, 1, true); b.Observe(3, 4, true);
        Assert.Equal(2, a.Snapshot().Aggregate); Assert.Equal(7, b.Snapshot().Aggregate);
        Assert.Throws<ReportedUsageException>(() => a.Accept(() => { a.Refuse(); return "accepted"; }));
        b.RequireFinal(); Assert.Equal("accepted", b.Accept(() => "accepted"));
    }
}
