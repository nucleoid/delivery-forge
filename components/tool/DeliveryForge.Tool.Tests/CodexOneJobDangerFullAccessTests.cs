using System.Text.Json;
using DeliveryForge.Execution;
using DeliveryForge.Evidence;
using DeliveryForge.Execution.Host;
using DeliveryForge.Tool.Codex;
namespace DeliveryForge.Tool.Tests;

// Wire/refusal DATA only; these controls issue no scope, capability or model authority.
public sealed class CodexOneJobDangerFullAccessTests
{
    static AgentRequest Request() => WorkerEnvelope.PrepareWorker(new("onejob-data", "sha256:" + new string('1', 64), new string('a', 40), "fixture/repo", Path.GetFullPath("."), Path.GetFullPath("onejob-assigned"), Path.GetFullPath("onejob-results"), "PRIVATE-WRITER", ["control.txt"], [], "codex.cli", "0.163.0-alpha.10.2", [AgentCapability.Launch, AgentCapability.CompletionEvidence]));
    [Fact]
    public void Explicit_conformance_wire_selects_only_cwd_and_approved_stable_mode()
    {
        var actual = Request(); var wire = JsonSerializer.SerializeToElement(CodexAppServerPort.ConformanceDangerFullAccessParameters(actual));
        Assert.Equal(2, wire.EnumerateObject().Count()); Assert.Equal(actual.Worktree, wire.GetProperty("cwd").GetString());
        Assert.Equal("danger-full-access", wire.GetProperty("sandbox").GetString());
        Assert.DoesNotContain(actual.WriterToken, wire.GetRawText(), StringComparison.Ordinal);
    }
    [Fact]
    public void Exact_effective_policy_and_cwd_are_accepted_as_data_only()
    {
        var actual = Request();
        CodexAppServerPort.RequireConformanceDangerFullAccessThread(JsonSerializer.SerializeToElement(new { cwd = actual.Worktree, sandbox = new { type = "dangerFullAccess" } }), actual);
    }
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"workspaceWrite\"}")]
    [InlineData("{\"type\":\"externalSandbox\"}")]
    [InlineData("{\"type\":\"danger-full-access\"}")]
    [InlineData("{\"type\":false}")]
    [InlineData("{\"type\":\"dangerFullAccess\",\"writableRoots\":[]}")]
    public void Missing_malformed_or_different_policy_refuses_before_turn(string policy)
    {
        var actual = Request(); using var json = JsonDocument.Parse(policy);
        Assert.Throws<WorkerEnvelopeException>(() => CodexAppServerPort.RequireConformanceDangerFullAccessThread(JsonSerializer.SerializeToElement(new { cwd = actual.Worktree, sandbox = json.RootElement }), actual));
    }
    [Fact]
    public void Foreign_or_missing_cwd_and_missing_policy_refuse()
    {
        var actual = Request();
        Assert.Throws<WorkerEnvelopeException>(() => CodexAppServerPort.RequireConformanceDangerFullAccessThread(JsonSerializer.SerializeToElement(new { cwd = actual.ResultDirectory, sandbox = new { type = "dangerFullAccess" } }), actual));
        Assert.Throws<WorkerEnvelopeException>(() => CodexAppServerPort.RequireConformanceDangerFullAccessThread(JsonSerializer.SerializeToElement(new { sandbox = new { type = "dangerFullAccess" } }), actual));
        Assert.Throws<WorkerEnvelopeException>(() => CodexAppServerPort.RequireConformanceDangerFullAccessThread(JsonSerializer.SerializeToElement(new { cwd = actual.Worktree }), actual));
    }
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsWindows => OperatingSystem.IsWindows();
    static DeliveryForge.Evidence.LinuxScopeSelection Scope()
    {
        var image = new DeliveryForge.Evidence.LinuxScopeImage("/not-opened", 64, new string('a', 64));
        return new(image, image, "data-only", "data-only", image, [], image) { RestrictiveChildPolicy = "nnp-user1000-ancestor-v3" };
    }
    static CodexAppServerSelection Image() => new("/not-opened-codex", 296885960,
        "70ea33b7094a4c20c007d09b83bb50e33debb9bbf99bf066709f9eb85bad524a", "0.163.0-alpha.10.2");
    static AgentAdapterProfile Profile() => new("codex.cli", "0.163.0-alpha.10.2",
        [new(AgentCapability.Launch, AgentCapabilityStatus.Unverified, "DATA constructor control", "no authority")]);
    [Fact(Skip = "Positive one-job constructor selection requires Linux.", SkipUnless = nameof(IsLinux))]
    public void Linux_exact_unverified_constructor_does_not_create_native_or_authority()
    {
        var port = new CodexAppServerPort(Image(), new DeliveryForge.Execution.Git.WorktreeManager(), Profile(), Scope(), null, false, true);
        Assert.Null(port.OriginalProcess); Assert.Empty(port.OriginalTasks);
        Assert.All(port.Profile.Capabilities, row => Assert.Equal(AgentCapabilityStatus.Unverified, row.Status));
        Assert.Empty(typeof(InstalledHostAdmission.RuntimeTrust).GetConstructors());
    }
    [Fact(Skip = "Windows platform boundary requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Windows_refuses_one_job_DFA_before_native_creation() =>
        Assert.Throws<ToolUsageException>(() => new CodexAppServerPort(Image(), new DeliveryForge.Execution.Git.WorktreeManager(), Profile(), Scope(), null, false, true));
    [Theory(Skip = "Linux constructor boundary requires Linux.", SkipUnless = nameof(IsLinux))]
    [InlineData("null-scope")]
    [InlineData("scope-policy")]
    [InlineData("bytes")]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("supported")]
    [InlineData("trust-route")]
    public void Linux_refuses_nonselected_constructor_before_native_creation(string change)
    {
        DeliveryForge.Evidence.LinuxScopeSelection? scope = Scope(); var image = Image(); var profile = Profile();
        switch (change)
        {
            case "null-scope": scope = null; break;
            case "scope-policy": scope = scope with { RestrictiveChildPolicy = "other" }; break;
            case "bytes": image = image with { Bytes = image.Bytes - 1 }; break;
            case "hash": image = image with { Sha256 = new string('b', 64) }; break;
            case "version": image = image with { Version = "other" }; profile = profile with { AdapterVersion = "other" }; break;
            case "supported": profile = profile with { Capabilities = [new(AgentCapability.Launch, AgentCapabilityStatus.Supported, "not permitted", "DATA")] }; break;
            case "trust-route": break;
            default: throw new ArgumentException("Closed mutation list.", nameof(change));
        }
        Assert.Throws<ToolUsageException>(() => new CodexAppServerPort(image, new DeliveryForge.Execution.Git.WorktreeManager(), profile, scope, null, change == "trust-route", true));
    }
    [Fact]
    public void Sandbox_relaxed_trace_is_quarantined_by_actual_artifact_reader()
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(new { Kind = "ActualCodexAppServerTrace-v1", ConformanceDangerFullAccess = true, RuntimeCertified = false, Events = Array.Empty<object>() });
        Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireNonDfaConformanceArtifact(raw));
    }
    [Theory]
    [InlineData("{\"Kind\":\"ActualCodexAppServerTrace-v1\",\"ConformanceDangerFullAccess\":false}")]
    [InlineData("{\"Kind\":\"ActualCodexAppServerTrace-v1\"}")]
    public void Confined_false_and_legacy_absent_marker_remain_accepted_as_artifact_data(string raw) =>
        InstalledHostAdmission.RequireNonDfaConformanceArtifact(System.Text.Encoding.UTF8.GetBytes(raw));
    [Fact]
    public void Legacy_binary_artifact_is_not_reinterpreted_as_trace_json() =>
        InstalledHostAdmission.RequireNonDfaConformanceArtifact([0xff, 0, 1, 2]);
    [Theory]
    [InlineData("{\"ConformanceDangerFullAccess\":true}")]
    [InlineData("{\"ConformanceDangerFullAccess\":null}")]
    [InlineData("{\"ConformanceDangerFullAccess\":\"false\"}")]
    [InlineData("{\"ConformanceDangerFullAccess\":0}")]
    [InlineData("{\"ConformanceDangerFullAccess\":[]}")]
    [InlineData("{\"ConformanceDangerFullAccess\":{}}")]
    [InlineData("{\"ConformanceDangerFullAccess\":false,\"ConformanceDangerFullAccess\":false}")]
    [InlineData("{\"ConformanceDangerFullAccess\":true,\"ConformanceDangerFullAccess\":false}")]
    [InlineData("{\"ConformanceDangerFullAccess\":false")]
    public void Malformed_or_duplicate_DFA_marker_is_refused_without_raw_payload(string raw)
    {
        var error = Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireNonDfaConformanceArtifact(System.Text.Encoding.UTF8.GetBytes(raw)));
        Assert.Null(error.InnerException); Assert.DoesNotContain(raw, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_utf8_marked_artifact_is_refused_without_binary_legacy_fallback()
    {
        byte[] raw = [.. System.Text.Encoding.UTF8.GetBytes("{\"ConformanceDangerFullAccess\":false}"), 0xff];
        var error = Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireNonDfaConformanceArtifact(raw));
        Assert.Null(error.InnerException); Assert.Equal("Malformed marked conformance artifact.", error.Message);
    }

    [Fact]
    public void Typed_Linux_Codex_completion_accepts_only_explicit_confined_trace_data() =>
        InstalledHostAdmission.RequireLinuxCodexCompletionArtifact(JsonSerializer.SerializeToUtf8Bytes(new
        { Kind = "ActualCodexAppServerTrace-v1", ConformanceDangerFullAccess = false, RuntimeCertified = false, Events = Array.Empty<object>() }));
    [Theory]
    [InlineData("{\"Kind\":\"ActualCodexAppServerTrace-v1\",\"Events\":[{\"Message\":{\"result\":{\"sandbox\":{\"type\":\"dangerFullAccess\"}}}}]}")]
    [InlineData("{\"Kind\":\"ActualCodexAppServerTrace-v1\",\"ConformanceDangerFullAccess\":true}")]
    [InlineData("{\"Kind\":\"other\",\"ConformanceDangerFullAccess\":false}")]
    [InlineData("[]")]
    public void Typed_Linux_Codex_completion_refuses_unmarked_relaxed_or_nontrace_data(string raw)
    {
        var error = Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireLinuxCodexCompletionArtifact(System.Text.Encoding.UTF8.GetBytes(raw)));
        Assert.Null(error.InnerException); Assert.DoesNotContain(raw, error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void Typed_Linux_Codex_completion_refuses_binary_while_global_legacy_reader_accepts_it()
    {
        byte[] raw = [0xff, 0, 1, 2];
        InstalledHostAdmission.RequireNonDfaConformanceArtifact(raw);
        Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireLinuxCodexCompletionArtifact(raw));
    }

    static Dictionary<string, byte[]> CounterData() => new(StringComparer.Ordinal)
    {
        ["pids.current"] = "22\n"u8.ToArray(),
        ["pids.max"] = "48\n"u8.ToArray(),
        ["pids.events"] = "max 0\n"u8.ToArray(),
        ["memory.current"] = "4096\n"u8.ToArray(),
        ["memory.max"] = "max\n"u8.ToArray(),
        ["memory.events"] = "low 0\nhigh 0\nmax 1\noom 2\noom_kill 1\n"u8.ToArray()
    };
    [Fact]
    public void Leaf_counter_data_keeps_actual_events_unlimited_and_optional_absence_distinct()
    {
        var values = OwnedDuplexProcess.ParseLeafCounters(CounterData());
        Assert.Equal((ulong?)22, values["pids.current"]); Assert.Equal((ulong?)1, values["memory.events.oom_kill"]);
        Assert.Null(values["memory.max"]); Assert.False(values.ContainsKey("memory.events.oom_group_kill"));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("overflow")]
    [InlineData("negative")]
    [InlineData("duplicate")]
    [InlineData("missing-event")]
    [InlineData("unknown-invalid")]
    [InlineData("unknown-duplicate")]
    [InlineData("unknown-overflow")]
    [InlineData("incomplete")]
    [InlineData("oversize")]
    public void Leaf_counter_malformed_or_unavailable_data_never_becomes_zero(string mutation)
    {
        var raw = CounterData();
        switch (mutation)
        {
            case "missing": raw.Remove("pids.current"); break;
            case "overflow": raw["pids.current"] = "18446744073709551616\n"u8.ToArray(); break;
            case "negative": raw["memory.current"] = "-1\n"u8.ToArray(); break;
            case "duplicate": raw["pids.events"] = "max 0\nmax 1\n"u8.ToArray(); break;
            case "missing-event": raw["memory.events"] = "low 0\nhigh 0\nmax 0\noom 0\n"u8.ToArray(); break;
            case "unknown-invalid": raw["pids.events"] = "other? 0\n"u8.ToArray(); break;
            case "unknown-duplicate": raw["pids.events"] = "max 0\nfuture 1\nfuture 2\n"u8.ToArray(); break;
            case "unknown-overflow": raw["pids.events"] = "max 0\nfuture 18446744073709551616\n"u8.ToArray(); break;
            case "incomplete": raw["pids.max"] = "48"u8.ToArray(); break;
            case "oversize": raw["pids.events"] = new byte[1025]; break;
        }
        Assert.Throws<IOException>(() => OwnedDuplexProcess.ParseLeafCounters(raw));
    }
    [Theory]
    [InlineData(0, "after-terminal")]
    [InlineData(1, "before-turn")]
    [InlineData(2, "after-terminal")]
    [InlineData(0, "other")]
    [InlineData(1, "startup-failed")]
    [InlineData(2, "startup-failed")]
    public void Leaf_diagnostic_rejects_missing_repeated_or_foreign_phase(int previous, string phase) =>
        Assert.Throws<IOException>(() => CodexAppServerPort.RequireLeafCounterPhase(previous, phase));
    [Fact]
    public void Production_constructor_cannot_receive_conformance_leaf_observer() =>
        Assert.Throws<ToolUsageException>(() => new CodexAppServerPort(Image(), new DeliveryForge.Execution.Git.WorktreeManager(), Profile(), Scope(), null, false, false, _ => throw new Exception("Must not invoke")));

    [Fact]
    public void Leaf_diagnostic_accepts_only_the_two_complete_ordered_samples()
    {
        var first = CodexAppServerPort.RequireLeafCounterPhase(0, "before-turn");
        Assert.Equal(2, CodexAppServerPort.RequireLeafCounterPhase(first, "after-terminal"));
        var raw = CounterData(); raw["memory.current"] = "18446744073709551615\n"u8.ToArray();
        raw["memory.events"] = "low 0\nhigh 0\nmax 0\noom 0\noom_kill 0\noom_group_kill 4\n"u8.ToArray();
        var values = OwnedDuplexProcess.ParseLeafCounters(raw);
        Assert.Equal((ulong?)ulong.MaxValue, values["memory.current"]); Assert.Equal((ulong?)4, values["memory.events.oom_group_kill"]);
    }

    [Fact]
    public void Startup_failure_uses_the_unused_sample_and_closes_the_diagnostic_sequence()
    {
        var final = CodexAppServerPort.RequireLeafCounterPhase(0, "startup-failed");
        Assert.Equal(2, final);
        Assert.Throws<IOException>(() => CodexAppServerPort.RequireLeafCounterPhase(final, "before-turn"));
        Assert.Throws<IOException>(() => CodexAppServerPort.RequireLeafCounterPhase(final, "after-terminal"));
        Assert.Throws<IOException>(() => CodexAppServerPort.RequireLeafCounterPhase(final, "startup-failed"));
    }

    [Fact]
    public void Leaf_counter_future_numeric_events_are_validated_but_not_projected()
    {
        var raw = CounterData(); raw["pids.events"] = "max 3\nmax.imposed 7\n"u8.ToArray();
        raw["memory.events"] = "low 0\nhigh 0\nmax 1\noom 2\noom_kill 1\nfuture_key-1 9\n"u8.ToArray();
        var values = OwnedDuplexProcess.ParseLeafCounters(raw);
        Assert.Equal((ulong?)3, values["pids.events.max"]);
        Assert.False(values.ContainsKey("pids.events.max.imposed")); Assert.False(values.ContainsKey("memory.events.future_key-1"));
        raw["pids.events"] = "max.imposed 7\n"u8.ToArray();
        Assert.Throws<IOException>(() => OwnedDuplexProcess.ParseLeafCounters(raw));
    }
    [Theory]
    [InlineData("failed")]
    [InlineData("interrupted")]
    public void Terminal_failure_is_sampled_and_preserved_with_counter_failure(string status)
    {
        using var terminal = JsonDocument.Parse("{\"params\":{\"turn\":{\"status\":\"" + status + "\"}}}");
        var captured = 0; var counter = new IOException("Counter refused");
        var error = Assert.Throws<AggregateException>(() => CodexAppServerPort.RequireSuccessfulTerminalWithLeafCounters(terminal.RootElement, () => { captured++; throw counter; }));
        Assert.Equal(1, captured); Assert.IsType<WorkerEnvelopeException>(error.InnerExceptions[0]); Assert.Same(counter, error.InnerExceptions[1]);
    }
    [Fact]
    public void Completed_terminal_is_sampled_and_counter_failure_is_not_swallowed()
    {
        using var terminal = JsonDocument.Parse("{\"params\":{\"turn\":{\"status\":\"completed\"}}}");
        var captured = 0; CodexAppServerPort.RequireSuccessfulTerminalWithLeafCounters(terminal.RootElement, () => captured++);
        Assert.Equal(1, captured); var counter = new IOException("Counter refused");
        Assert.Same(counter, Assert.Throws<IOException>(() => CodexAppServerPort.RequireSuccessfulTerminalWithLeafCounters(terminal.RootElement, () => throw counter)));
    }

}
