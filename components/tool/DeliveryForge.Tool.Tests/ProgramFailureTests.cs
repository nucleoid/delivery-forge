using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DeliveryForge.Planning;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Codex;
namespace DeliveryForge.Tool.Tests;

public sealed class ProgramFailureTests
{
    // Diagnostic fixture only. The inert image/manager is never launched; reflection
    // positions the real Port's existing private state without a production test seam.
    static FieldInfo DiagnosticField(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new IOException("Actual diagnostic fixture field missing.");
    static (CodexAppServerPort Port, CodexStartupJournal Journal) ActiveStartup(ToolLifetime original, bool active = true)
    {
        var image = Environment.ProcessPath ?? throw new IOException("Actual test process image required.");
        var port = new CodexAppServerPort(new(image, 1, new string('a', 64), "diagnostic-fixture"),
            new WorktreeManager(image), KnownAgentProfiles.CodexCli("diagnostic-fixture"));
        port.BindOriginal(original);
        var journal = (CodexStartupJournal)DiagnosticField(port, "startup").GetValue(port)!;
        journal.SetStage(CodexStartupStage.PrivateObjectInitialization);
        DiagnosticField(port, "startupActive").SetValue(port, active ? 1 : 0);
        DiagnosticField(port, "pump").SetValue(port, Task.CompletedTask);
        return (port, journal);
    }
    static string FirstCaughtLine(StringWriter errors) => Assert.Single(errors.ToString().Split('\n'),
        line => line.StartsWith("DFCLI_FIRST_CAUGHT|", StringComparison.Ordinal)).TrimEnd('\r');

    [Fact]
    public async Task Independent_outer_timeout_freezes_only_same_original_startup_context_without_changing_failure()
    {
        const string secret = "private-config-auth-and-task@example.invalid";
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        var startup = ActiveStartup(life); var stillPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => stillPending.Task.WaitAsync(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken));
        timeout.Data["unexported"] = secret; var started = life.OriginalStarted;
        life.RecordFirstCaught("standalone", timeout);
        startup.Journal.SetStage(CodexStartupStage.Accepted); DiagnosticField(startup.Port, "startupActive").SetValue(startup.Port, 0);
        life.RecordFirstCaught("host-implement", new IOException(secret));
        Assert.False(stillPending.Task.IsCompleted); Assert.Equal(started, life.OriginalStarted); life.Check();
        var rethrown = await Assert.ThrowsAsync<TimeoutException>(() => CodexAppServerPort.RetireFailedStartupAsync(timeout, null, () => Task.CompletedTask));
        Assert.Same(timeout, rethrown);
        life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce(); attention.EmitFirstCaughtOnce();
        var line = FirstCaughtLine(errors); using var json = JsonDocument.Parse(line["DFCLI_FIRST_CAUGHT|".Length..]);
        Assert.Equal(typeof(TimeoutException).FullName, json.RootElement.GetProperty("Type").GetString());
        var facts = json.RootElement.GetProperty("CodexStartup");
        Assert.Equal("PrivateObjectInitialization", facts.GetProperty("Stage").GetString());
        Assert.Equal("ActiveStartupContextAtFirstCatch", facts.GetProperty("Observation").GetString());
        Assert.Equal("RanToCompletion", facts.GetProperty("PumpStatus").GetString());
        foreach (var name in new[] { "Method", "RpcId", "ErrorCode", "PendingTaskCount" }) Assert.Equal(JsonValueKind.Null, facts.GetProperty(name).ValueKind);
        Assert.DoesNotContain(secret, line, StringComparison.Ordinal); Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 1024);
        Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("inactive")]
    [InlineData("accepted")]
    [InlineData("ambiguous")]
    [InlineData("unrelated")]
    [InlineData("nonstartup-stage")]
    [InlineData("oversized")]
    public void Startup_context_omits_foreign_inactive_ambiguous_unrelated_and_nonstartup_failures(string scenario)
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var foreign = new ToolLifetime(TimeSpan.FromSeconds(120));
        using var errors = new StringWriter(); var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        var startup = ActiveStartup(scenario == "foreign" ? foreign : life, scenario != "inactive");
        if (scenario == "foreign") life.RegisterCodexStartup(startup.Port);
        if (scenario == "accepted") startup.Journal.SetStage(CodexStartupStage.Accepted);
        if (scenario == "ambiguous")
        {
            _ = ActiveStartup(life, false); startup.Port.BindOriginal(life);
            Assert.Null(life.ObserveActiveCodexStartup());
        }
        Exception error = scenario == "unrelated" ? new IOException("private") : new OperationCanceledException("private");
        if (scenario == "oversized") for (var index = 0; index < 16; index++) error = new IOException("private", error);
        var stage = scenario == "nonstartup-stage" ? "standalone-owner-close" : "standalone";
        life.RecordFirstCaught(stage, error); life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        var line = FirstCaughtLine(errors);
        var expected = "DFCLI_FIRST_CAUGHT|" + JsonSerializer.Serialize(new
        {
            Stage = stage,
            Type = error.GetType().FullName,
            error.HResult,
            InnerType = error.InnerException?.GetType().FullName,
            CleanupAlreadyBegun = false,
            MarkerAlreadyEntered = false
        });
        Assert.Equal(expected, line); Assert.DoesNotContain("CodexStartup", line, StringComparison.Ordinal);
        Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Correlated_RPC_facts_keep_exact_bytes_and_precede_active_original_context()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention); _ = ActiveStartup(life);
        var rpc = new CodexRemoteRpcException("private", 5); rpc.CaptureStartup(StartupFailureSnapshot(5, -32602, "private"));
        Assert.Equal("{\"Stage\":\"ThreadStart\",\"Method\":\"ConfigRead\",\"RpcId\":5,\"ErrorCode\":-32602,\"PumpStatus\":\"RanToCompletion\",\"PendingTaskCount\":1}", JsonSerializer.Serialize(rpc.StartupFailure));
        life.RecordFirstCaught("standalone", new AggregateException(new TimeoutException("private"), rpc));
        life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        using var json = JsonDocument.Parse(FirstCaughtLine(errors)["DFCLI_FIRST_CAUGHT|".Length..]);
        var facts = json.RootElement.GetProperty("CodexStartup");
        Assert.Equal("ConfigRead", facts.GetProperty("Method").GetString()); Assert.False(facts.TryGetProperty("Observation", out _));
        Assert.Equal(JsonSerializer.Serialize(rpc.StartupFailure), facts.GetRawText());
    }

    [Fact(Timeout = 10000)]
    public async Task Original_context_read_does_not_wait_for_Port_tasks_or_journal_locks()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention); var startup = ActiveStartup(life);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var owner = Task.Run(() =>
        {
            lock (DiagnosticField(startup.Port, "gate").GetValue(startup.Port)!)
            lock (DiagnosticField(startup.Port, "tasks").GetValue(startup.Port)!)
            lock (DiagnosticField(startup.Journal, "gate").GetValue(startup.Journal)!)
            {
                held.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) throw new TimeoutException("Diagnostic test release absent.");
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await Task.Run(() => life.RecordFirstCaught("host-implement", new TimeoutException("private")), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
        finally { release.Set(); await owner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
        life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        using var json = JsonDocument.Parse(FirstCaughtLine(errors)["DFCLI_FIRST_CAUGHT|".Length..]);
        Assert.Equal("ActiveStartupContextAtFirstCatch", json.RootElement.GetProperty("CodexStartup").GetProperty("Observation").GetString());
        Assert.False(attention.Unknown);
    }

    [Fact]
    public void Long_error_types_and_original_startup_context_keep_existing_line_ceiling()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention); _ = ActiveStartup(life);
        Exception Wrap(Exception error) => new BoundaryABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.LayerAABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.LayerBABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.LayerCABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.EscapingBudgetException(error);
        life.RecordFirstCaught("standalone", Wrap(Wrap(new TimeoutException("private-auth-config"))));
        life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        var line = FirstCaughtLine(errors); Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 1024);
        using var json = JsonDocument.Parse(line["DFCLI_FIRST_CAUGHT|".Length..]);
        Assert.Equal("ActiveStartupContextAtFirstCatch", json.RootElement.GetProperty("CodexStartup").GetProperty("Observation").GetString());
        Assert.DoesNotContain("private-auth-config", line, StringComparison.Ordinal); Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    static CodexStartupDiagnostics StartupFailureSnapshot(long id, int code, string sentinel)
    {
        var journal = new CodexStartupJournal(Stopwatch.GetTimestamp());
        // config/read can occur while the stage still describes thread startup.
        journal.SetStage(CodexStartupStage.ThreadStart);
        journal.Client("config/read", id, CodexWireState.WriteEntered);
        journal.Client("config/read", id, CodexWireState.WriteReturned);
        using var actual = JsonDocument.Parse(JsonSerializer.Serialize(new { id, error = new { code, message = sentinel, data = new { auth = sentinel } } }));
        journal.Server(actual.RootElement);
        journal.Client("thread/start", 7, CodexWireState.WriteReturned);
        using var other = JsonDocument.Parse(JsonSerializer.Serialize(new { id = 7, error = new { code = -32603, message = sentinel } }));
        journal.Server(other.RootElement);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return journal.Snapshot([7], new IOException(sentinel), new IOException(sentinel), Task.CompletedTask,
            [Task.CompletedTask, pending.Task], false, false);
    }

    [Fact]
    public void Caught_Codex_RPC_id_selects_only_its_actual_method_and_code_in_existing_attention()
    {
        const string secret = "private-prompt-auth-and-error@example.invalid";
        var rpc = new CodexRemoteRpcException(secret, 5);
        rpc.CaptureStartup(StartupFailureSnapshot(5, -32602, secret));
        var first = rpc.StartupFailure;
        rpc.CaptureStartup(StartupFailureSnapshot(5, -32000, "later-observation"));
        Assert.Same(first, rpc.StartupFailure);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.RecordFirstCaught("host-implement", new AggregateException(rpc, new IOException(secret)));
        attention.EmitFirstCaughtOnce(); life.Check(); Assert.Empty(errors.ToString());
        life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce(); attention.EmitFirstCaughtOnce();
        var lines = errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Equal(2, lines.Length);
        using var json = JsonDocument.Parse(lines[1]["DFCLI_FIRST_CAUGHT|".Length..]);
        var facts = json.RootElement.GetProperty("CodexStartup");
        Assert.Equal("ThreadStart", facts.GetProperty("Stage").GetString());
        Assert.Equal("ConfigRead", facts.GetProperty("Method").GetString());
        Assert.Equal(5, facts.GetProperty("RpcId").GetInt64()); Assert.Equal(-32602, facts.GetProperty("ErrorCode").GetInt32());
        Assert.Equal("RanToCompletion", facts.GetProperty("PumpStatus").GetString()); Assert.Equal(1, facts.GetProperty("PendingTaskCount").GetInt32());
        Assert.DoesNotContain(secret, errors.ToString(), StringComparison.Ordinal); Assert.DoesNotContain("later-observation", errors.ToString(), StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(lines[1]) <= 1024); Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_overflowed_actual_RPC_wire_never_borrows_another_error(bool overflow)
    {
        var journal = new CodexStartupJournal(Stopwatch.GetTimestamp()); journal.SetStage(CodexStartupStage.Initialize);
        journal.Client("config/read", 5, CodexWireState.WriteReturned);
        using var other = JsonDocument.Parse("{\"id\":4,\"error\":{\"code\":-32603,\"message\":\"private\"}}");
        for (var index = 0; index < (overflow ? 128 : 1); index++) journal.Server(other.RootElement);
        if (overflow)
        {
            using var omitted = JsonDocument.Parse("{\"id\":5,\"error\":{\"code\":-32602,\"message\":\"private\"}}");
            journal.Server(omitted.RootElement);
        }
        var rpc = new CodexRemoteRpcException("private", 5); rpc.CaptureStartup(journal.Snapshot([], null, null, null, [], false, false));
        Assert.NotNull(rpc.StartupFailure); Assert.Equal(5, rpc.StartupFailure.RpcId);
        Assert.Null(rpc.StartupFailure.Method); Assert.Null(rpc.StartupFailure.ErrorCode);
    }

    [Fact]
    public void Unrelated_Codex_facts_do_not_change_generic_first_caught_output_bytes()
    {
        var unrelated = new CodexRemoteRpcException("unrelated-private", 5); unrelated.CaptureStartup(StartupFailureSnapshot(5, -32602, "unrelated-private"));
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        var actual = new IOException("original-private"); life.RecordFirstCaught("host-implement", actual);
        life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        var line = Assert.Single(errors.ToString().Split('\n'), value => value.StartsWith("DFCLI_FIRST_CAUGHT|", StringComparison.Ordinal)).TrimEnd('\r');
        var expected = "DFCLI_FIRST_CAUGHT|" + JsonSerializer.Serialize(new
        {
            Stage = "host-implement",
            Type = typeof(IOException).FullName,
            actual.HResult,
            InnerType = (string?)null,
            CleanupAlreadyBegun = false,
            MarkerAlreadyEntered = false
        });
        Assert.Equal(expected, line); Assert.DoesNotContain("CodexStartup", line, StringComparison.Ordinal);
        Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Codex_causal_reference_walk_rejects_distinct_errors_and_oversized_graphs()
    {
        var rpc = new CodexRemoteRpcException("private", 5); rpc.CaptureStartup(StartupFailureSnapshot(5, -32602, "private"));
        Assert.Same(rpc, CodexRemoteRpcException.FindSingle(new AggregateException(rpc, rpc)));
        Assert.Null(CodexRemoteRpcException.FindSingle(new AggregateException(rpc, new CodexRemoteRpcException("other", 7))));
        Assert.Null(CodexRemoteRpcException.FindSingle(new AggregateException(Enumerable.Repeat<Exception>(rpc, 17))));
        Exception large = rpc;
        for (var index = 0; index < 16; index++) large = new IOException("private", large);
        Assert.Null(CodexRemoteRpcException.FindSingle(large));
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.RecordFirstCaught("host-implement", large); life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        Assert.DoesNotContain("CodexStartup", errors.ToString(), StringComparison.Ordinal); Assert.False(attention.Unknown);
    }

    [Fact]
    public void Long_actual_error_types_and_Codex_facts_fit_the_existing_attention_line_ceiling()
    {
        var rpc = new CodexRemoteRpcException("private", 5); rpc.CaptureStartup(StartupFailureSnapshot(5, int.MinValue, "private"));
        Exception Wrap(Exception error) => new BoundaryABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.LayerAABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.LayerBABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.LayerCABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.EscapingBudgetException(error);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.RecordFirstCaught("review-source-preparation", Wrap(Wrap(rpc))); life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        var line = Assert.Single(errors.ToString().Split('\n'), value => value.StartsWith("DFCLI_FIRST_CAUGHT|", StringComparison.Ordinal));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 1024);
        using var json = JsonDocument.Parse(line["DFCLI_FIRST_CAUGHT|".Length..]);
        Assert.Equal(int.MinValue, json.RootElement.GetProperty("CodexStartup").GetProperty("ErrorCode").GetInt32());
        Assert.DoesNotContain("private", line, StringComparison.Ordinal); Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    sealed class NestedDiagnosticException(Exception? inner) : Exception("must-not-export", inner);
    sealed class GenericDiagnosticException<T>(Exception? inner) : Exception("must-not-export", inner);
    sealed class ÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉ(Exception? inner) : Exception("must-not-export", inner);
    sealed class BoundaryABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789
    {
        internal sealed class LayerAABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789
        {
            internal sealed class LayerBABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789
            {
                internal sealed class LayerCABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789
                {
                    internal sealed class EscapingBudgetException(Exception? inner) : Exception("must-not-export", inner);
                }
            }
        }
    }
    [Fact]
    public void First_caught_type_names_bound_actual_JSON_escaping_without_output_uncertainty()
    {
        static Exception EscapingBudget(Exception? inner) => new BoundaryABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.LayerAABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.LayerBABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.LayerCABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.EscapingBudgetException(inner);
        var escaping = EscapingBudget(EscapingBudget(null)); var escapingName = escaping.GetType().FullName!;
        var nonAscii = new ÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉ(new ÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉÉ(null)); var nonAsciiName = nonAscii.GetType().FullName!;
        Assert.True(nonAsciiName.Length <= 256);
        var oldLine = "DFCLI_FIRST_CAUGHT|" + JsonSerializer.Serialize(new
        {
            Stage = "review-source-preparation",
            Type = nonAsciiName,
            nonAscii.HResult,
            InnerType = nonAscii.InnerException!.GetType().FullName,
            CleanupAlreadyBegun = false,
            MarkerAlreadyEntered = false
        });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(oldLine) > 1024);
        Assert.True(escapingName.Length <= 256); Assert.True(JsonSerializer.Serialize(escapingName).Length - 2 > 256);
        (Exception Error, string Expected)[] cases =
        [
            (new NestedDiagnosticException(new NestedDiagnosticException(null)), typeof(NestedDiagnosticException).FullName!),
            (new GenericDiagnosticException<int>(new GenericDiagnosticException<int>(null)), "ExceptionTypeUnavailable"),
            (nonAscii, "ExceptionTypeUnavailable"),
            (escaping, "ExceptionTypeUnavailable")
        ];
        foreach (var item in cases)
        {
            using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
            var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
            life.RecordFirstCaught("review-source-preparation", item.Error); life.BeginFailureCleanup();
            attention.EmitFirstCaughtOnce(); attention.EmitFirstCaughtOnce();
            var lines = errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
            Assert.Equal(2, lines.Length); Assert.StartsWith("DFCLI_FAILURE|", lines[0], StringComparison.Ordinal);
            const string prefix = "DFCLI_FIRST_CAUGHT|"; Assert.StartsWith(prefix, lines[1], StringComparison.Ordinal);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(lines[1]) <= 1024);
            using var json = JsonDocument.Parse(lines[1][prefix.Length..]); var diagnostic = json.RootElement;
            Assert.Equal(item.Expected, diagnostic.GetProperty("Type").GetString()); Assert.Equal(item.Expected, diagnostic.GetProperty("InnerType").GetString());
            Assert.DoesNotContain("must-not-export", errors.ToString(), StringComparison.Ordinal);
            Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
        }
    }
    [Fact]
    public void First_caught_capture_does_not_enter_cleanup_or_output_and_is_nonsecret_once_only()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        var actual = new IOException("do-not-export-this-secret", new InvalidOperationException("nor-this-secret"));
        life.RecordFirstCaught("review-source-preparation", actual); attention.EmitFirstCaughtOnce();
        life.Check(); Assert.Equal("", errors.ToString()); // Capture did not latch cleanup; Check would throw if it had.
        life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        life.RecordFirstCaught("standalone", new TimeoutException("later endpoint must not replace first catch"));
        attention.EmitFirstCaughtOnce();
        var lines = errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row => row.TrimEnd('\r')).ToArray();
        Assert.Equal(2, lines.Length); Assert.StartsWith("DFCLI_FAILURE|", lines[0], StringComparison.Ordinal);
        const string prefix = "DFCLI_FIRST_CAUGHT|"; Assert.StartsWith(prefix, lines[1], StringComparison.Ordinal);
        using var json = JsonDocument.Parse(lines[1][prefix.Length..]); var diagnostic = json.RootElement;
        Assert.Equal("review-source-preparation", diagnostic.GetProperty("Stage").GetString());
        Assert.Equal(typeof(IOException).FullName, diagnostic.GetProperty("Type").GetString());
        Assert.Equal(actual.HResult, diagnostic.GetProperty("HResult").GetInt32());
        Assert.Equal(typeof(InvalidOperationException).FullName, diagnostic.GetProperty("InnerType").GetString());
        Assert.False(diagnostic.GetProperty("CleanupAlreadyBegun").GetBoolean()); Assert.False(diagnostic.GetProperty("MarkerAlreadyEntered").GetBoolean());
        Assert.DoesNotContain("secret", errors.ToString(), StringComparison.Ordinal); Assert.False(diagnostic.TryGetProperty("Message", out _));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(lines[1]) <= 1024); Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }
    [Fact]
    public void Marker_before_capture_records_cleanup_consequence_flags_at_later_root_emission()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.BeginFailureCleanup(); var consequence = Assert.Throws<TimeoutException>(() => life.Check());
        life.RecordFirstCaught("review-source-preparation", consequence);
        life.BeginFailureCleanup(); Assert.DoesNotContain("DFCLI_FIRST_CAUGHT|", errors.ToString(), StringComparison.Ordinal);
        attention.EmitFirstCaughtOnce(); attention.EmitFirstCaughtOnce();
        var line = Assert.Single(errors.ToString().Split('\n'), row => row.StartsWith("DFCLI_FIRST_CAUGHT|", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(line["DFCLI_FIRST_CAUGHT|".Length..]); var diagnostic = json.RootElement;
        Assert.True(diagnostic.GetProperty("CleanupAlreadyBegun").GetBoolean()); Assert.True(diagnostic.GetProperty("MarkerAlreadyEntered").GetBoolean());
        Assert.Equal(typeof(TimeoutException).FullName, diagnostic.GetProperty("Type").GetString()); Assert.False(life.HasExplicitUnknown);
        Assert.Throws<TimeoutException>(() => life.Check()); // Diagnostic cannot reopen WORK or refund a cleanup latch.
    }
    sealed class BlockingDiagnosticWriter : StringWriter
    {
        readonly object writeGate = new();
        internal readonly TaskCompletionSource DiagnosticStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource PermitDiagnostic = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DiagnosticWrites;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            var text = buffer.ToString(); lock (writeGate) base.WriteLine(text);
            if (!text.StartsWith("DFCLI_FIRST_CAUGHT|", StringComparison.Ordinal)) return Task.CompletedTask;
            Interlocked.Increment(ref DiagnosticWrites); DiagnosticStarted.TrySetResult(); return PermitDiagnostic.Task;
        }
    }
    [Fact(Timeout = 10000)]
    public async Task Concurrent_marker_and_root_diagnostic_do_not_turn_a_settled_marker_into_unknown()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new BlockingDiagnosticWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.RecordFirstCaught("host-implement", new IOException("actual caught host failure"));
        using var start = new Barrier(3); var token = TestContext.Current.CancellationToken;
        Task Invoke() => Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5), token)); life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce(); }, token);
        var a = Invoke(); var b = Invoke(); Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5), token));
        try
        {
            await errors.DiagnosticStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.True(attention.Unknown); Assert.False(life.HasExplicitUnknown);
            life.BeginFailureCleanup(); Assert.False(life.HasExplicitUnknown); // Marker-only callback while the distinct diagnostic is pending.
        }
        finally { errors.PermitDiagnostic.TrySetResult(); }
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(1, errors.DiagnosticWrites); Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
        Assert.Equal(2, errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
    sealed class FailedDiagnosticWriter : StringWriter
    {
        internal int DiagnosticWrites;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Span.StartsWith("DFCLI_FIRST_CAUGHT|", StringComparison.Ordinal))
            { DiagnosticWrites++; return Task.FromException(new IOException("actual diagnostic writer failed")); }
            return base.WriteLineAsync(buffer, cancellationToken);
        }
    }
    [Fact]
    public void Failed_root_diagnostic_retains_actual_task_uncertainty_without_retry_or_fallback()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new FailedDiagnosticWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.RecordFirstCaught("host-implement", new IOException("actual host failure"));
        life.BeginFailureCleanup(); Assert.Equal(0, errors.DiagnosticWrites);
        attention.EmitFirstCaughtOnce(); attention.EmitFirstCaughtOnce(); life.BeginFailureCleanup();
        Assert.Equal(1, errors.DiagnosticWrites); Assert.True(attention.Unknown); Assert.True(life.HasExplicitUnknown);
        Assert.Single(errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
    [Fact(Timeout = 10000)]
    public async Task Actual_standalone_catch_emits_nonsecret_diagnostic_before_root_failure_delivery()
    {
        using var output = new StringWriter(); using var errors = new StringWriter();
        var exit = await StandaloneEntrypoint.RunAsync(["not-a-tool-mode"], output, errors).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, exit); Assert.Equal("", output.ToString());
        var lines = errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row => row.TrimEnd('\r')).ToArray();
        Assert.StartsWith("DFCLI_FAILURE|", lines[0], StringComparison.Ordinal);
        const string prefix = "DFCLI_FIRST_CAUGHT|"; Assert.StartsWith(prefix, lines[1], StringComparison.Ordinal);
        using var json = JsonDocument.Parse(lines[1][prefix.Length..]); var diagnostic = json.RootElement;
        Assert.Equal("standalone", diagnostic.GetProperty("Stage").GetString()); Assert.Equal(typeof(ToolUsageException).FullName, diagnostic.GetProperty("Type").GetString());
        Assert.False(diagnostic.GetProperty("CleanupAlreadyBegun").GetBoolean()); Assert.False(diagnostic.GetProperty("MarkerAlreadyEntered").GetBoolean());
        using var report = JsonDocument.Parse(string.Join('\n', lines.Skip(2))); Assert.Equal("ACTUAL_OPERATION_FAILED", report.RootElement.GetProperty("Code").GetString());
    }
    sealed class CancellingIssue(CancellationTokenSource caller) : IIssueContentPort
    {
        public Task<PlanIntent>? Actual { get; private set; }
        public Task<PlanIntent> ReadPublicIssueAsync(string issue, TimeSpan remaining, CancellationToken token) { caller.Cancel(); return Actual = Task.FromException<PlanIntent>(new IOException("actual issue-read primary")); }
    }
    [Fact]
    public async Task Genuine_tool_Observe_dual_primary_and_endpoint_failure_returns_bounded_structured_ERROR()
    {
        var directory = Path.Combine(Path.GetTempPath(), "delivery-forge-error-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var config = Path.Combine(directory, "config.json"); File.WriteAllBytes(config, JsonSerializer.SerializeToUtf8Bytes(new TrustedConfiguration("fixture/repo", directory, "refs/heads/main", Path.Combine(directory, "explicit-git-never-invoked")), BoundedFiles.Json)); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var issue = new CancellingIssue(cancel); var application = new ToolApplication(issues: issue); using var output = new StringWriter(); using var errors = new StringWriter();
        var exit = await Program.ExecuteAsync(["plan", "--repo", directory, "--issue", "fixture/repo#1", "--config", config, "--json"], application, output, errors, cancel.Token); Assert.Equal(1, exit); Assert.Empty(output.ToString()); using var report = JsonDocument.Parse(errors.ToString()); Assert.Equal("ERROR", report.RootElement.GetProperty("Outcome").GetString()); var root = report.RootElement.GetProperty("Failure"); Assert.Equal(typeof(AggregateException).FullName, root.GetProperty("Type").GetString()); var causes = root.GetProperty("Causes"); Assert.Equal(2, causes.GetArrayLength()); Assert.Equal("actual issue-read primary", causes[0].GetProperty("Message").GetString()); Assert.Contains("Canceled", causes[1].GetProperty("Type").GetString(), StringComparison.Ordinal); Assert.Single(application.RetainedExternalCalls); Assert.NotNull(application.RetainedExternalCalls[0].Primary); Assert.NotNull(application.RetainedExternalCalls[0].Post); Assert.NotNull(issue.Actual); Assert.True(issue.Actual.IsFaulted); Assert.Same(issue.Actual, application.RetainedExternalCalls[0].ActualTask); Assert.IsType<IOException>(application.RetainedExternalCalls[0].Primary);
    }
    [Fact]
    public void Huge_actual_error_message_and_deep_aggregate_are_explicitly_unavailable_never_silently_truncated()
    {
        var actual = new AggregateException(new IOException(new string('a', 8192)), new TimeoutException("independent post")); var report = ToolFailureReport.Create(actual); Assert.True(report.DetailsUnavailable); Assert.True(report.Failure.Causes[0].MessageUnavailable); Assert.Null(report.Failure.Causes[0].Message); Assert.Equal("independent post", report.Failure.Causes[1].Message); Assert.True(JsonSerializer.SerializeToUtf8Bytes(report).Length < 65536);
    }
    [Fact]
    public void Aggregate_collection_admission_and_complete_output_node_bound_are_explicit()
    {
        var many = new AggregateException(Enumerable.Range(0, 17).Select(x => new IOException("cause-" + x))); var refused = ToolFailureReport.Create(many); Assert.True(refused.DetailsUnavailable); Assert.Empty(refused.Failure.Causes); Assert.Equal(17, many.InnerExceptions.Count);
        var nested = new AggregateException(new AggregateException(Enumerable.Range(0, 14).Select(x => new IOException("nested-" + x))), new IOException("later-sibling")); var bounded = ToolFailureReport.Create(nested); Assert.True(bounded.DetailsUnavailable); Assert.Equal(16, Count(bounded.Failure)); Assert.Equal(2, nested.InnerExceptions.Count);
        static int Count(ToolFailureNode node) => 1 + node.Causes.Sum(Count);
    }

    sealed class CancellingAssignment(CancellationTokenSource caller) : IWorkerAssignmentPort
    {
        public Task<DeliveryForge.Execution.FrozenWorkerPlan>? Actual { get; private set; }
        public ParentAssignmentRequest? Request { get; private set; }
        public readonly IOException Failure = new("actual assignment primary");
        public Task<DeliveryForge.Execution.FrozenWorkerPlan> SelectAsync(ParentAssignmentRequest request, TimeSpan remaining, CancellationToken cancellationToken)
        {
            Request = request; caller.Cancel(); return Actual = Task.FromException<DeliveryForge.Execution.FrozenWorkerPlan>(Failure);
        }
    }
    sealed class UncalledHost : IHostToolSessionPort
    {
        public DeliveryForge.Execution.Host.AgentAdapterProfile Profile => DeliveryForge.Execution.Host.KnownAgentProfiles.CodexCli("assignment-fixture");
        public HostObservationRealm Realm => HostObservationRealm.ContractFixture;
        public int Calls { get; private set; }
        public Task<DeliveryForge.Execution.Host.AgentAcceptedReceipt> StartAsync(DeliveryForge.Execution.Host.AgentRequest request, TimeSpan remaining, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("Host must not start after assignment failure."); }
        public Task<DeliveryForge.Execution.Host.AgentCompletionReceipt> WaitAsync(DeliveryForge.Execution.Host.AgentRunBinding binding, TimeSpan remaining, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("No actual host was started."); }
        public Task<DeliveryForge.Execution.Host.AgentControlResult> ControlAsync(DeliveryForge.Execution.Host.AgentRunBinding binding, DeliveryForge.Execution.Host.AgentControlAction action, TimeSpan remaining, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("No actual host was started."); }
    }
    [Fact]
    public async Task Genuine_public_implement_import_preserves_already_faulted_assignment_after_caller_cancel()
    {
        var token = TestContext.Current.CancellationToken;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120), cancel.Token);
        var root = Path.Combine(Path.GetTempPath(), "forge-assignment-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repository"); life.Check(); Directory.CreateDirectory(repo); life.Check();
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "git.exe" : "git")).First(File.Exists);
        var executor = new DeliveryForge.Evidence.ProcessCommandExecutor();
        async Task Git(params string[] arguments)
        {
            life.Check(); var nullPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var argv = new[] { "--no-replace-objects", "-c", "core.hooksPath=" + nullPath, "-c", "core.fsmonitor=false", "-c", "user.name=AssignmentFixture", "-c", "user.email=fixture@example.invalid" }.Concat(arguments).ToArray();
            var env = new Dictionary<string, string> { { "GIT_CONFIG_NOSYSTEM", "1" }, { "GIT_CONFIG_GLOBAL", nullPath }, { "GIT_CONFIG_SYSTEM", nullPath }, { "GIT_NO_REPLACE_OBJECTS", "1" }, { "GIT_TERMINAL_PROMPT", "0" }, { "GIT_OPTIONAL_LOCKS", "0" } };
            var result = await executor.ExecuteAsync(new(git, argv, repo, env), life.Remaining, life.Token); life.Check();
            Assert.Equal(0, result.ExitCode); Assert.False(result.TimedOut); Assert.False(result.Cancelled); Assert.True(result.OwnedProcessQuiescent);
        }
        BoundedFiles.WriteNew(Path.Combine(repo, "tracked.txt"), System.Text.Encoding.UTF8.GetBytes("actual committed assignment input\n"), life);
        await Git("init", "-q", "-b", "main"); await Git("add", "--", "tracked.txt"); await Git("commit", "-q", "-m", "real assignment baseline");
        var reader = new GitRepositoryContextReader(git);
        var context = await reader.ReadAsync(repo, "refs/heads/main", life.Token); life.Check();
        var file = await reader.ReadFileAsync(context, "tracked.txt", life.Token); life.Check();
        var now = DateTimeOffset.UtcNow;
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, now, [], EvidenceRequirement.Required)];
        var request = new PlanningRequest("fixture/assignment", "#1", "implement", "Preserve actual assignment failure after cancellation", ["tracked.txt"], ["production"], ["Actual assignment fault is retained and host never starts"], "implement");
        var frozen = PlanFreezer.Freeze(new(request, context, evidence, [new("tracked.txt", "content", "update")], [new("base", [], "current source")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", ["inspect diagnostics"]), [], IntakePlanner.Assess(request, evidence)), "assignment-primary", now);
        Assert.True(frozen.DownstreamReady); life.Check();
        var plan = Path.Combine(root, "plan.json"); BoundedFiles.WriteNew(plan, frozen.CanonicalBytes, life);
        var config = Path.Combine(root, "config.json"); BoundedFiles.WriteNew(config, JsonSerializer.SerializeToUtf8Bytes(new TrustedConfiguration("fixture/assignment", repo, "refs/heads/main", git,
            AdapterVersion: "assignment-fixture", MaximumCeiling: "implement", OperationSeconds: 120, ExpectedBundleIdentity: frozen.Identity, ExpectedContractIdentity: frozen.ContractIdentity, ExpectedWorkItem: "#1", ExpectedPlanCeiling: "implement"), BoundedFiles.Json), life);
        await using var processes = new DeliveryForge.Execution.ProcessControl();
        using var coordinator = new DeliveryForge.Execution.ExecutionCoordinator(new DeliveryForge.Execution.RunStore(Path.Combine(root, "store")), new DeliveryForge.Execution.Git.WorktreeManager(git), processes);
        var host = new UncalledHost(); var bridge = new OwnerHostBridge(coordinator, new CodexHostAdapter(host)); var assignment = new CancellingAssignment(cancel);
        var application = new ToolApplication(host: bridge, assignments: assignment, actualOriginal: life);
        var command = CommandLine.Parse(["implement", "--plan", plan, "--config", config]);
        var failure = await Assert.ThrowsAsync<IOException>(() => application.ExecuteAsync(command, token));
        Assert.Same(assignment.Failure, failure); Assert.NotNull(assignment.Actual); Assert.True(assignment.Actual.IsFaulted);
        var observation = Assert.Single(application.RetainedAssignments); Assert.True(observation.Entered); Assert.True(observation.InvocationReturned); Assert.False(observation.Unknown);
        Assert.Same(assignment.Actual, observation.ActualTask); Assert.Same(failure, observation.Primary); Assert.Null(observation.Result);
        Assert.NotNull(assignment.Request); Assert.Equal(frozen.Identity, assignment.Request.Plan.Identity); Assert.True(assignment.Request.Plan.DownstreamReady);
        Assert.Equal(0, host.Calls); Assert.Empty(bridge.RetainedFrames);
        Assert.Contains(application.RetainedExternalCalls, row => row.Kind == "ActualPlanImport" && row.Primary is null && row.Post is null);
        // Preserve the genuine fixture and its plan/import/cancellation evidence;
        // no fresh work/disposition is attempted through the cancelled original.
    }
}
