using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Codex;

namespace DeliveryForge.Tool.Tests;

public sealed class CodexStartupDiagnosticsTests
{
    [Fact]
    public void Already_failed_pump_refuses_later_reply_before_any_wait_can_be_orphaned()
    {
        var replies = new CodexPendingReplies(); var original = new IOException("Original actual pump failure."); replies.Fail(original);
        Assert.Same(original, Assert.Throws<IOException>(() => replies.Register(1)));
        Assert.Empty(replies.PendingIds);
    }

    [Fact]
    public async Task Actual_registered_waits_fail_and_late_registration_cannot_escape_the_same_latch()
    {
        var replies = new CodexPendingReplies(); var first = replies.Register(1); var second = replies.Register(2);
        var original = new IOException("Actual pump failed after two requests entered.");
        replies.Fail(original);
        foreach (var task in new[] { first.Task, second.Task })
            Assert.Same(original, await Assert.ThrowsAsync<IOException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)));
        Assert.Same(original, Assert.Throws<IOException>(() => replies.Register(3)));
        replies.Fail(new InvalidOperationException("Later cleanup cannot replace primary."));
        Assert.Same(original, replies.Failure); Assert.Empty(replies.PendingIds);
    }

    [Fact]
    public async Task Concurrent_failure_and_registration_always_return_a_faulted_reply_or_the_original_failure()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var replies = new CodexPendingReplies(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var original = new IOException("One actual concurrent failure.");
            var registration = Task.Run(async () => { await release.Task; try { return (Reply: (TaskCompletionSource<JsonElement>?)replies.Register(1), Error: (Exception?)null); } catch (Exception error) { return (Reply: (TaskCompletionSource<JsonElement>?)null, Error: (Exception?)error); } }, TestContext.Current.CancellationToken);
            var failure = Task.Run(async () => { await release.Task; replies.Fail(original); }, TestContext.Current.CancellationToken);
            release.SetResult(); var actual = await registration.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); await failure.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            if (actual.Reply is not null) Assert.Same(original, await Assert.ThrowsAsync<IOException>(async () => await actual.Reply.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)));
            else Assert.Same(original, actual.Error);
            Assert.Empty(replies.PendingIds);
        }
    }

    [Fact]
    public void Bounded_typed_journal_never_serializes_payload_account_string_ids_or_arbitrary_methods()
    {
        var journal = new CodexStartupJournal(Stopwatch.GetTimestamp()); journal.SetStage(CodexStartupStage.ThreadStart);
        journal.Client("thread/start", 2, CodexWireState.WriteReturned);
        const string secret = "private-writer-and-account@example.invalid";
        using var message = JsonDocument.Parse(JsonSerializer.Serialize(new { method = secret, id = secret, @params = new { token = secret, email = secret } }));
        for (var index = 0; index < 140; index++) journal.Server(message.RootElement);
        var snapshot = journal.Snapshot([2], new IOException(secret), new IOException(secret), Task.CompletedTask, [Task.CompletedTask], false, false);
        var text = JsonSerializer.Serialize(snapshot, BoundedFiles.Json);
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal); Assert.DoesNotContain("@example", text, StringComparison.Ordinal);
        Assert.Equal(CodexStartupStage.ThreadStart, snapshot.Stage); Assert.Equal(128, snapshot.Wire.Count); Assert.True(snapshot.WireBoundExceeded);
        Assert.Equal(140, snapshot.ReceivedMessages); Assert.Equal(1, snapshot.SentMessages);
        Assert.All(snapshot.Wire.Skip(1), row => { Assert.Equal(CodexWireMethod.Other, row.Method); Assert.Equal(CodexWireIdKind.String, row.IdKind); Assert.Null(row.ClientId); });
    }

    [Fact]
    public void Actual_numeric_RPC_error_context_survives_without_its_private_message_or_data()
    {
        var journal = new CodexStartupJournal(Stopwatch.GetTimestamp()); journal.SetStage(CodexStartupStage.Initialize);
        using var message = JsonDocument.Parse("{\"id\":1,\"error\":{\"code\":-32602,\"message\":\"private@example.invalid\",\"data\":{\"token\":\"private-writer\"}}}");
        journal.Server(message.RootElement); var snapshot = journal.Snapshot([], null, null, null, [], false, false);
        var row = Assert.Single(snapshot.Wire); Assert.True(row.HasError); Assert.Equal(-32602, row.ErrorCode); Assert.Equal(1L, row.ClientId);
        var text = JsonSerializer.Serialize(snapshot, BoundedFiles.Json); Assert.DoesNotContain("private", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("os error 11: resource temporarily unavailable", "OsError, Resource")]
    [InlineData("authentication failed: unauthorized", "Authentication")]
    [InlineData("model not found", "Model")]
    [InlineData("read-only database: permission denied (os error 13)", "OsError, ReadOnly, Database")]
    [InlineData("no such file or directory", "PathMissing")]
    [InlineData("arbitrary private refusal", "None")]
    public void Correlated_refusal_facts_before_projection_are_bounded_heuristics_without_payload(string message, string signals)
    {
        const string secret = "private@example.invalid/?token=never-export";
        var wire = JsonSerializer.SerializeToElement(new { id = 4, error = new { code = -32603, message = message + ":" + secret, data = new { auth = secret, path = secret } } });
        var facts = Assert.IsType<CodexRefusalFacts>(CodexTrustProtocolData.Refusal(wire, 4));
        Assert.Equal(signals, facts.HeuristicSignals); Assert.True(facts.HeuristicOnly);
        Assert.True(facts.MessageOmitted); Assert.True(facts.DataOmitted); Assert.False(facts.MessageBoundExceeded);
        var bytes = Encoding.UTF8.GetBytes(message + ":" + secret);
        Assert.Equal(bytes.Length, facts.MessageBytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), facts.MessageSha256);
        if (message.Contains("os error 13", StringComparison.Ordinal)) Assert.Equal(13, facts.OsErrorCode);
        var rpc = new CodexRemoteRpcException(CodexTrustProtocolData.ErrorMessage(CodexTrustProtocolData.Response(wire, "thread/start").GetProperty("error")), 4, facts);
        rpc.CaptureStartup(ThreadStartSnapshot());
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var (line, attention) = EmitRefusal(life, rpc);
        Assert.Contains("\"HeuristicOnly\":true", line, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, line, StringComparison.Ordinal); Assert.DoesNotContain(message, line, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, rpc.Message, StringComparison.Ordinal);
        Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Foreign_missing_and_oversized_refusal_messages_never_borrow_or_export_payload()
    {
        var wire = JsonSerializer.SerializeToElement(new { id = 4, error = new { code = -32603, message = new string('x', 4097), data = "secret" } });
        Assert.Null(CodexTrustProtocolData.Refusal(wire, 3)); Assert.Null(CodexTrustProtocolData.Refusal(wire, 65));
        Assert.Null(CodexTrustProtocolData.Refusal(JsonSerializer.SerializeToElement(new { id = "4", error = new { message = "secret" } }), 4));
        var facts = Assert.IsType<CodexRefusalFacts>(CodexTrustProtocolData.Refusal(wire, 4));
        Assert.Null(facts.MessageSha256); Assert.Null(facts.MessageBytes); Assert.True(facts.MessageBoundExceeded);
        Assert.Equal("None", facts.HeuristicSignals); Assert.DoesNotContain("secret", JsonSerializer.Serialize(facts), StringComparison.Ordinal);
        var missing = Assert.IsType<CodexRefusalFacts>(CodexTrustProtocolData.Refusal(JsonSerializer.SerializeToElement(new { id = 4, error = new { code = -32603 } }), 4));
        Assert.Null(missing.MessageSha256); Assert.False(missing.DataOmitted); Assert.False(missing.MessageBoundExceeded);
    }

    [Fact]
    public async Task Retirement_snapshot_does_not_wait_or_turn_pending_faulted_or_unknown_into_closed()
    {
        var actual = new TaskCompletionSource<DeliveryForge.Evidence.DuplexRetirement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = CodexStartupCleanupFacts.Observe(actual.Task, true, null);
        Assert.Equal("WaitingForActivation", pending.TaskState); Assert.Null(pending.KnownClosed); Assert.Null(pending.OutputEof); Assert.True(pending.HasUnknownIo);
        Assert.False(actual.Task.IsCompleted);
        actual.SetResult(new(true, true, 0, true, true, true, true, 0, []));
        var closed = CodexStartupCleanupFacts.Observe(actual.Task, false, null);
        Assert.Equal("RanToCompletion", closed.TaskState); Assert.True(closed.KnownClosed); Assert.True(closed.RootExited); Assert.Equal(0u, closed.ActiveProcesses);
        Assert.True(closed.DispositionReturned); Assert.False(closed.HasUnknownIo); Assert.Equal(0, closed.ExitCode);
        var unknown = Task.FromResult(new DeliveryForge.Evidence.DuplexRetirement(false, true, 1, false, true, true, false, 1, ["private failure text"]));
        Assert.False(CodexStartupCleanupFacts.Observe(unknown, true, null).KnownClosed);
        var cleanup = new IOException("private cleanup text");
        var faulted = Task.FromException<DeliveryForge.Evidence.DuplexRetirement>(cleanup);
        var observed = CodexStartupCleanupFacts.Observe(faulted, null, cleanup);
        Assert.Equal("Faulted", observed.TaskState); Assert.Null(observed.KnownClosed); Assert.Null(observed.ErrorEof);
        Assert.Equal(typeof(IOException).FullName, observed.FailureType); Assert.Equal(cleanup.HResult, observed.HResult);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(observed), StringComparison.Ordinal);
        Assert.Same(cleanup, await Assert.ThrowsAsync<IOException>(async () => await faulted));
    }

    [Fact]
    public async Task Existing_startup_retirement_preserves_primary_cleanup_references_and_freezes_optional_snapshot()
    {
        var primary = new CodexRemoteRpcException("private primary", 4); primary.CaptureStartup(ThreadStartSnapshot());
        var cleanup = new IOException("private cleanup"); var actual = Task.FromException<DeliveryForge.Evidence.DuplexRetirement>(cleanup);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => CodexAppServerPort.RetireFailedStartupAsync(primary, null, async () =>
        {
            try { await actual; }
            finally { primary.CaptureCleanup(actual, true, cleanup); }
        }));
        Assert.Same(primary, failure.InnerExceptions[0]); Assert.Same(cleanup, failure.InnerExceptions[1]);
        var first = Assert.IsType<CodexStartupCleanupFacts>(primary.StartupCleanup);
        primary.CaptureCleanup(Task.FromResult(new DeliveryForge.Evidence.DuplexRetirement(true, true, 0, true, true, true, true, 0, [])), false, null);
        Assert.Same(first, primary.StartupCleanup); Assert.Equal("Faulted", first.TaskState); Assert.Null(first.KnownClosed);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var (line, attention) = EmitRefusal(life, failure);
        Assert.Contains("\"Task\":\"Faulted\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("private", line, StringComparison.Ordinal); Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Oversized_optional_refusal_and_cleanup_facts_keep_existing_startup_marker_bytes()
    {
        var wire = JsonSerializer.SerializeToElement(new { id = 4, error = new { message = "private" } });
        var baseline = new CodexRemoteRpcException("private", 4); baseline.CaptureStartup(ThreadStartSnapshot());
        var expanded = new CodexRemoteRpcException("private", 4, CodexTrustProtocolData.Refusal(wire, 4)); expanded.CaptureStartup(ThreadStartSnapshot());
        expanded.CaptureCleanup(null, null, null);
        var stage = new string('s', 500);
        using var firstLife = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var secondLife = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var first = EmitRefusal(firstLife, baseline, stage); var second = EmitRefusal(secondLife, expanded, stage);
        Assert.Equal(first.Line, second.Line); Assert.Contains("CodexStartup", second.Line, StringComparison.Ordinal);
        Assert.DoesNotContain("CodexFailure", second.Line, StringComparison.Ordinal);
        Assert.False(first.Attention.Unknown); Assert.False(second.Attention.Unknown); Assert.False(secondLife.HasExplicitUnknown);
    }

    [Theory]
    [InlineData("(os error 0)", 0)]
    [InlineData("(os error 65535)", 65535)]
    [InlineData("(os error 65536)", null)]
    [InlineData("(os error -13)", null)]
    [InlineData("(os error 13.5)", null)]
    [InlineData("(os error 13secret)", null)]
    [InlineData("(os error 13) (os error 2)", null)]
    [InlineData("nos error 13", null)]
    public void Numeric_OS_hint_requires_one_complete_bounded_decimal_token(string message, int? expected)
    {
        var wire = JsonSerializer.SerializeToElement(new { id = 4, error = new { message } });
        Assert.Equal(expected, Assert.IsType<CodexRefusalFacts>(CodexTrustProtocolData.Refusal(wire, 4)).OsErrorCode);
    }

    [Fact]
    public void Actual_aggregate_startup_shape_keeps_refusal_hash_OS_code_and_full_cleanup_within_existing_marker()
    {
        const string secret = "private-token-and-path";
        var wire = JsonSerializer.SerializeToElement(new { id = 4, error = new { code = -32603, message = "read-only database: permission denied (os error 13); " + secret, data = new { token = secret } } });
        var rpc = new CodexRemoteRpcException("private", 4, CodexTrustProtocolData.Refusal(wire, 4));
        rpc.CaptureStartup(ThreadStartSnapshot() with { OriginalTasks = [TaskStatus.WaitingForActivation, TaskStatus.WaitingForActivation] });
        rpc.CaptureCleanup(Task.FromResult(new DeliveryForge.Evidence.DuplexRetirement(false, true, 0, true, true, true, false, 1, [secret])), false, new IOException(secret));
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var errors = new StringWriter(); var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.BeginFailureCleanup();
        life.RecordFirstCaught("host-implement", new AggregateException("private wrapper", rpc, new IOException(secret)));
        attention.EmitFirstCaughtOnce();
        var line = Assert.Single(errors.ToString().Split('\n'), value => value.StartsWith("DFCLI_FIRST_CAUGHT|", StringComparison.Ordinal)).TrimEnd('\r');
        Assert.True(Encoding.UTF8.GetByteCount(line) <= 1024);
        using var parsed = JsonDocument.Parse(line["DFCLI_FIRST_CAUGHT|".Length..]);
        var failure = parsed.RootElement.GetProperty("CodexFailure");
        Assert.Equal(rpc.Refusal!.MessageSha256, failure.GetProperty("Refusal").GetProperty("Sha256").GetString());
        Assert.Equal(13, failure.GetProperty("Refusal").GetProperty("OsErrorCode").GetInt32());
        Assert.Equal(typeof(IOException).FullName, failure.GetProperty("Cleanup").GetProperty("Failure").GetString());
        Assert.False(failure.GetProperty("Cleanup").GetProperty("KnownClosed").GetBoolean());
        Assert.True(failure.GetProperty("Cleanup").GetProperty("OutEof").GetBoolean());
        Assert.DoesNotContain(secret, line, StringComparison.Ordinal); Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    static CodexStartupDiagnostics ThreadStartSnapshot() => new(CodexStartupStage.ThreadStart, 1, 2,
        [new(1, CodexStartupStage.ThreadStart, CodexWireDirection.Client, CodexWireMethod.ThreadStart, CodexWireState.WriteReturned, CodexWireIdKind.ClientNumber, 4, false, null),
         new(2, CodexStartupStage.ThreadStart, CodexWireDirection.Server, CodexWireMethod.None, CodexWireState.Received, CodexWireIdKind.ClientNumber, 4, true, -32603)],
        false, 1, 1, [], null, null, TaskStatus.WaitingForActivation, [], false, false);

    static (string Line, ToolFailureAttention Attention) EmitRefusal(ToolLifetime life, Exception error, string stage = "host-implement")
    {
        using var errors = new StringWriter(); var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.RecordFirstCaught(stage, error); life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        var line = Assert.Single(errors.ToString().Split('\n'), value => value.StartsWith("DFCLI_FIRST_CAUGHT|", StringComparison.Ordinal)).TrimEnd('\r');
        Assert.True(Encoding.UTF8.GetByteCount(line) <= 1024); return (line, attention);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(7, true)]
    [InlineData(5, false)]
    [InlineData(7, false)]
    public void A_new_configuration_read_cannot_retain_an_earlier_pass_after_missing_or_refused_reply(long id, bool refusedReply)
    {
        var journal = new CodexStartupJournal(Stopwatch.GetTimestamp());
        using var supported = JsonDocument.Parse("{\"id\":2,\"result\":{\"routeDiagnostic\":\"Supported\",\"destinationOrigin\":\"Missing\"}}");
        journal.Server(supported.RootElement);
        Assert.Equal(CodexConfigReadDiagnostic.Supported, journal.Snapshot([], null, null, null, [], false, false).ConfigurationDiagnostic);
        journal.Client("thread/start", 4, CodexWireState.WriteEntered);
        Assert.Equal(CodexConfigReadDiagnostic.Supported, journal.Snapshot([], null, null, null, [], false, false).ConfigurationDiagnostic);
        journal.Client("config/read", id, CodexWireState.WriteEntered);
        if (refusedReply)
        {
            using var reply = JsonDocument.Parse(JsonSerializer.Serialize(new { id, error = new { code = -32602, message = "private-sentinel" } }));
            journal.Server(reply.RootElement);
        }
        var snapshot = journal.Snapshot([], null, null, null, [], false, false);
        Assert.Equal(CodexConfigReadDiagnostic.NotObserved, snapshot.ConfigurationDiagnostic);
        Assert.Equal(CodexConfigOriginClass.NotObserved, snapshot.ConfigurationDestinationOrigin);
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(snapshot, BoundedFiles.Json), StringComparison.Ordinal);
        if (refusedReply) Assert.True(snapshot.Wire[^1].HasError);
    }

    [Fact]
    public void Real_CreateNew_failure_snapshot_uses_closed_WORK_original_cleanup_and_preserves_existing_evidence()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var directory = Path.Combine(Path.GetTempPath(), "forge-native-failure-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "failure.json"); var bytes = Encoding.UTF8.GetBytes("{\"Failed\":true}");
        try
        {
            original.BeginFailureCleanup(); Assert.Throws<TimeoutException>(original.Check);
            var writer = new NativeFailureSnapshotWriter(original); writer.Write(path, bytes);
            Assert.True(writer.AcquisitionReturned && writer.WriteReturned && writer.FlushReturned && writer.CloseReturned && writer.PostReturned); Assert.False(writer.Unknown);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Throws<IOException>(() => writer.Write(path, Encoding.UTF8.GetBytes("replacement")));
            var other = new NativeFailureSnapshotWriter(original); Assert.Throws<IOException>(() => other.Write(path, Encoding.UTF8.GetBytes("replacement")));
            Assert.False(other.AcquisitionEntered); Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Throws<TimeoutException>(original.Check); _ = original.CleanupRemaining;
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); }
    }
}
