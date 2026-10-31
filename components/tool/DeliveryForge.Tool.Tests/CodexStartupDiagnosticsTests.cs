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
