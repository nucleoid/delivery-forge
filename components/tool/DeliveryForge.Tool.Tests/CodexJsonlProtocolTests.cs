using System.Text;
using System.Text.Json;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Codex;
namespace DeliveryForge.Tool.Tests;

public sealed class CodexJsonlProtocolTests
{
    [Fact]
    public async Task Genuine_BCL_stream_roundtrip_then_full_EOF()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); using var wire = new MemoryStream(); using var unused = new MemoryStream(); var writer = new CodexJsonlProtocol(wire, unused, life); await writer.WriteAsync(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "fixture", version = "1" } } }, TestContext.Current.CancellationToken); var raw = wire.ToArray(); Assert.Equal((byte)10, raw[^1]); wire.Position = 0;
        var reader = new CodexJsonlProtocol(unused, wire, life); using var message = await reader.ReadAsync(TestContext.Current.CancellationToken); Assert.NotNull(message); CodexJsonlProtocol.RequireEnvelope(message.RootElement); Assert.Equal(1, message.RootElement.GetProperty("id").GetInt32()); Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken)); Assert.True(reader.Eof); Assert.All(reader.EnteredTasks, x => Assert.True(x.IsCompleted));
    }
    [Theory]
    [InlineData("{\"id\":1,\"result\":{}}")]
    [InlineData("{\"id\":1,\"error\":{\"code\":1}}")]
    [InlineData("{\"method\":\"turn/completed\",\"params\":{}}")]
    public void Valid_exact_protocol_envelopes(string text) { using var doc = JsonDocument.Parse(text); CodexJsonlProtocol.RequireEnvelope(doc.RootElement); }
    [Theory]
    [InlineData("", true)]
    [InlineData(",\"emittedAtMs\":null", true)]
    [InlineData(",\"emittedAtMs\":1234", true)]
    [InlineData(",\"emittedAtMs\":-9223372036854775808", true)]
    [InlineData(",\"emittedAtMs\":9223372036854775807", true)]
    [InlineData(",\"emittedAtMs\":9223372036854775808", false)]
    [InlineData(",\"emittedAtMs\":1.5", false)]
    [InlineData(",\"emittedAtMs\":\"1234\"", false)]
    [InlineData(",\"emittedAtMs\":true", false)]
    [InlineData(",\"emittedAtMs\":{}", false)]
    [InlineData(",\"emittedAtMs\":[]", false)]
    [InlineData(",\"emittedAtMs\":1,\"unrelated\":true", false)]
    public async Task Actual_JSONL_notification_timestamp_matches_versioned_optional_signed_integer(string field, bool accepted)
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var input = new MemoryStream();
        using var output = new MemoryStream(Encoding.UTF8.GetBytes("{\"method\":\"configWarning\",\"params\":{}" + field + "}\n"));
        var codec = new CodexJsonlProtocol(input, output, life);
        using var message = await codec.ReadAsync(TestContext.Current.CancellationToken); Assert.NotNull(message);
        if (accepted) CodexJsonlProtocol.RequireEnvelope(message.RootElement);
        else Assert.Throws<IOException>(() => CodexJsonlProtocol.RequireEnvelope(message.RootElement));
        Assert.Null(await codec.ReadAsync(TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData("{\"id\":1,\"method\":\"server/request\",\"params\":{},\"emittedAtMs\":1}")]
    [InlineData("{\"id\":1,\"result\":{},\"emittedAtMs\":null}")]
    [InlineData("{\"id\":1,\"error\":{\"code\":1},\"emittedAtMs\":1}")]
    public void Notification_metadata_never_extends_request_or_response_envelopes(string text)
    {
        using var doc = JsonDocument.Parse(text);
        Assert.Throws<IOException>(() => CodexJsonlProtocol.RequireEnvelope(doc.RootElement));
    }
    [Theory]
    [InlineData("{\"id\":1}")]
    [InlineData("{\"id\":null,\"result\":{}}")]
    [InlineData("{\"id\":1,\"result\":{},\"error\":{}}")]
    [InlineData("{\"method\":\"x\",\"result\":{}}")]
    [InlineData("{\"id\":1,\"result\":{},\"extra\":true}")]
    public void Invalid_or_ambiguous_envelope_refuses(string text) { using var doc = JsonDocument.Parse(text); Assert.Throws<IOException>(() => CodexJsonlProtocol.RequireEnvelope(doc.RootElement)); }
    [Theory]
    [InlineData("{\"id\":1,\"id\":2,\"result\":{}}\n")]
    [InlineData("{\"id\":1,\"result\":{}}")]
    [InlineData("\n")]
    public async Task Duplicate_truncated_or_empty_real_stream_refuses(string text) { using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); using var input = new MemoryStream(); using var output = new MemoryStream(Encoding.UTF8.GetBytes(text)); var codec = new CodexJsonlProtocol(input, output, life); await Assert.ThrowsAnyAsync<Exception>(async () => { using var doc = await codec.ReadAsync(TestContext.Current.CancellationToken); }); }
    [Fact] public async Task Oversized_actual_outgoing_frame_refuses_before_original_stream_write() { using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); using var input = new MemoryStream(); using var output = new MemoryStream(); var codec = new CodexJsonlProtocol(input, output, life); await Assert.ThrowsAsync<IOException>(() => codec.WriteAsync(new { id = 1, method = "x", @params = new { text = new string('x', 70000) } }, TestContext.Current.CancellationToken)); Assert.Equal(0, input.Length); }
    [Fact] public async Task Malformed_UTF8_actual_bytes_refuse() { using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); using var input = new MemoryStream(); using var output = new MemoryStream(new byte[] { 0xff, 10 }); var codec = new CodexJsonlProtocol(input, output, life); await Assert.ThrowsAnyAsync<Exception>(async () => { using var doc = await codec.ReadAsync(TestContext.Current.CancellationToken); }); }
    sealed class HeldWriter : MemoryStream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously), SecondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); public int ActualWrites;
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) { if (Interlocked.Increment(ref ActualWrites) == 2) SecondEntered.TrySetResult(); Entered.TrySetResult(); await Release.Task; await base.WriteAsync(buffer, offset, count, token); }
    }
    [Fact]
    public async Task Actual_writer_timeout_does_not_release_an_unacquired_permit_or_enter_second_write()
    {
        using var life = new ToolLifetime(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken); using var input = new HeldWriter(); using var output = new MemoryStream(); var codec = new CodexJsonlProtocol(input, output, life); var first = codec.WriteAsync(new { id = 1, method = "first" }, TestContext.Current.CancellationToken); await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); var second = codec.WriteAsync(new { id = 2, method = "second" }, TestContext.Current.CancellationToken); var errors = new List<Exception>();
        try { foreach (var actual in new[] { first, second }) { try { await actual; } catch (Exception error) { errors.Add(error); } } Assert.Equal(1, input.ActualWrites); Assert.Equal(1, codec.AvailableWritePermits); Assert.Equal(2, errors.Count); Assert.True(codec.Unknown); Assert.True(life.HasRetainedUnknown); Assert.DoesNotContain(errors.SelectMany(x => x is AggregateException aggregate ? (IEnumerable<Exception>)aggregate.Flatten().InnerExceptions : new[] { x }), x => x is SemaphoreFullException); }
        finally { input.Release.TrySetResult(); foreach (var actual in codec.EnteredTasks) { try { await actual.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); } catch (OperationCanceledException) { } } }
        Assert.Equal(1, codec.AvailableWritePermits); Assert.True(codec.Unknown); Assert.True(life.HasRetainedUnknown);
    }


    sealed class ControlledOuterWaitTimeProvider : TimeProvider
    {
        readonly List<ControlledTimer> timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan || dueTime <= TimeSpan.Zero || dueTime > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("Only finite original one-shot outer observations are admitted.");
            lock (timers)
            {
                if (timers.Count >= 8) throw new InvalidOperationException("Finite observer timer profile exceeded.");
                var timer = new ControlledTimer(callback, state); timers.Add(timer); return timer;
            }
        }
        public int FirePending(int skip = 0)
        {
            if (skip < 0 || skip > 8) throw new ArgumentOutOfRangeException(nameof(skip));
            ControlledTimer[] actual; lock (timers) actual = timers.Skip(skip).ToArray();
            var fired = 0; foreach (var timer in actual) if (timer.Fire()) fired++; return fired;
        }
        sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            int disposed, fired;
            internal bool Fire()
            {
                if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref fired, 1) != 0) return false;
                callback(state); return true;
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new InvalidOperationException("Observer timer cannot be renewed.");
            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task Actual_semaphore_acquisition_cannot_outlive_outer_observation_and_orphan_its_permit()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        using var input = new HeldWriter(); using var output = new MemoryStream();
        var observer = new ControlledOuterWaitTimeProvider();
        var codec = new CodexJsonlProtocol(input, output, life, observer);
        var first = codec.WriteAsync(new { id = 1, method = "first" }, TestContext.Current.CancellationToken);
        await input.Entered.Task.WaitAsync(life.Remaining, TestContext.Current.CancellationToken);
        var second = codec.WriteAsync(new { id = 2, method = "second" }, TestContext.Current.CancellationToken);
        var errors = new List<Exception>();
        try
        {
            Assert.False(second.IsCompleted);
            // The first timer observes the held stream write. Skip that IO timer:
            // this regression targets only a possible second semaphore observer.
            // The real semaphore timeout and ToolLifetime clock remain unchanged.
            var fired = observer.FirePending(skip: 1);
            if (fired != 0)
            {
                try { await second.WaitAsync(life.Remaining, TestContext.Current.CancellationToken); }
                catch (Exception error) { errors.Add(error); }
                Assert.Equal(1, input.ActualWrites);
            }
            input.Release.TrySetResult();
            foreach (var actual in new[] { first, second })
            {
                try { await actual.WaitAsync(life.Remaining, TestContext.Current.CancellationToken); }
                catch (Exception error) { if (!errors.Contains(error)) errors.Add(error); }
            }
            // Settle every real captured acquisition/IO task before asserting permit
            // accounting; a descriptive timeout result never becomes permit authority.
            foreach (var actual in codec.EnteredTasks)
                await actual.WaitAsync(life.Remaining, TestContext.Current.CancellationToken);
            Assert.Equal(1, codec.AvailableWritePermits);
            Assert.Equal(2, input.ActualWrites);
            Assert.Empty(errors);
            Assert.False(codec.Unknown); Assert.False(life.HasRetainedUnknown);
        }
        finally
        {
            input.Release.TrySetResult();
            foreach (var actual in new[] { first, second }.Concat(codec.EnteredTasks))
            {
                try { await actual.WaitAsync(life.CleanupRemaining, TestContext.Current.CancellationToken); }
                catch (Exception) { /* Preserve actual failures; no retry, reset or UNKNOWN clearing. */ }
            }
        }
    }

    [Fact]
    public async Task Sticky_unknown_from_actual_writer_observation_refuses_queued_and_future_io()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        using var input = new HeldWriter(); using var output = new MemoryStream();
        var observer = new ControlledOuterWaitTimeProvider();
        var codec = new CodexJsonlProtocol(input, output, life, observer);
        var first = codec.WriteAsync(new { id = 1, method = "first" }, TestContext.Current.CancellationToken);
        await input.Entered.Task.WaitAsync(life.Remaining, TestContext.Current.CancellationToken);
        var second = codec.WriteAsync(new { id = 2, method = "second" }, TestContext.Current.CancellationToken);
        var errors = new List<Exception>();
        try
        {
            Assert.False(second.IsCompleted);
            // Fire the actual first stream-write WaitAsync observer in BOTH old and
            // repaired bodies. The real write remains held and semaphore is real.
            Assert.Equal(1, observer.FirePending());
            try { await first.WaitAsync(life.Remaining, TestContext.Current.CancellationToken); }
            catch (Exception error) { errors.Add(error); }
            Assert.True(codec.Unknown); Assert.True(life.HasRetainedUnknown);
            // Old admission enters a second real held write; repaired admission
            // completes second with failure. Either event is finite and observed.
            await Task.WhenAny(second, input.SecondEntered.Task).WaitAsync(life.Remaining, TestContext.Current.CancellationToken);
            input.Release.TrySetResult();
            try { await second.WaitAsync(life.Remaining, TestContext.Current.CancellationToken); }
            catch (Exception error) { errors.Add(error); }
            foreach (var actual in codec.EnteredTasks)
                await actual.WaitAsync(life.Remaining, TestContext.Current.CancellationToken);
            Assert.Equal(1, input.ActualWrites);
            Assert.Equal(1, codec.AvailableWritePermits);
            Assert.Equal(2, errors.Count);
            Assert.True(codec.Unknown); Assert.True(life.HasRetainedUnknown);
            var entered = codec.EnteredTasks.Count;
            await Assert.ThrowsAsync<IOException>(() => codec.WriteAsync(new { id = 3, method = "after-unknown" }, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<IOException>(async () => { using var ignored = await codec.ReadAsync(TestContext.Current.CancellationToken); });
            Assert.Equal(entered, codec.EnteredTasks.Count);
            Assert.Equal(1, input.ActualWrites); Assert.Equal(1, codec.AvailableWritePermits);
            Assert.DoesNotContain(errors.SelectMany(x => x is AggregateException aggregate ? (IEnumerable<Exception>)aggregate.Flatten().InnerExceptions : new[] { x }), x => x is SemaphoreFullException);
        }
        finally
        {
            input.Release.TrySetResult();
            foreach (var actual in new[] { first, second }.Concat(codec.EnteredTasks))
            {
                try { await actual.WaitAsync(life.CleanupRemaining, TestContext.Current.CancellationToken); }
                catch (Exception) { /* Preserve actual failures; no retry, reset or UNKNOWN clearing. */ }
            }
        }
    }
    sealed class PartialPromptStream(Func<bool> marker) : MemoryStream
    {
        internal readonly IOException Failure = new("Entered partial prompt write.");
        internal bool MarkerObserved;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            MarkerObserved = marker(); base.Write(buffer, offset, Math.Min(7, count));
            return Task.FromException(Failure);
        }
    }
    static ReportedModelUsage.UsageNode PromptNode()
    {
        var slots = new DeliveryForge.Scheduling.ResourceSlots(1, 1, 64, 1, 1, 0);
        var budget = DeliveryForge.Scheduling.ResourceBudget.Measure(slots, new("framing-control", true, slots, TimeSpan.FromSeconds(60), 100000, 0));
        return ReportedModelUsage.Bind(budget, 100000, 100000);
    }
    [Theory]
    [InlineData("codex")]
    [InlineData("claude-worker")]
    [InlineData("claude-reviewer")]
    public async Task Shared_actual_prompt_boundary_marks_before_partial_framing_write(string producer)
    {
        var node = PromptNode(); var session = node.Register();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var input = new PartialPromptStream(() => session.Evidence().ModelWriteEntered); using var output = new MemoryStream();
        var codec = new CodexJsonlProtocol(input, output, life);
        object frame = producer == "codex" ? new { id = 1, method = "turn/start", @params = new { threadId = "fixture" } } : (object)new { type = "user", message = new { role = "user", content = "fixed fixture" } };
        var failure = await Assert.ThrowsAsync<IOException>(() => ReportedModelUsage.WritePromptAsync(session, codec, frame, TestContext.Current.CancellationToken));
        Assert.Same(input.Failure, failure); Assert.True(input.MarkerObserved); Assert.Equal(7, input.Length);
        session.Finish(); Assert.True(session.Evidence().Stopped); Assert.False(session.Evidence().FinalUsageKnown);
        Assert.Throws<ReportedUsageException>(() => node.Register());
    }
    [Fact]
    public async Task Prewrite_observation_cutoff_refuses_actual_prompt_before_bytes_without_stopping_undispatched_run()
    {
        var node = PromptNode(); var session = node.Register();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var input = new MemoryStream(); using var output = new MemoryStream(); var codec = new CodexJsonlProtocol(input, output, life);
        session.MarkObservationUnavailable();
        await Assert.ThrowsAsync<ReportedUsageException>(() => ReportedModelUsage.WritePromptAsync(session, codec, new { type = "user", message = new { content = "fixed fixture" } }, TestContext.Current.CancellationToken));
        Assert.Equal(0, input.Length); Assert.False(session.Evidence().ModelWriteEntered); Assert.False(session.Evidence().Stopped);
        node.Register().Check(); // An undispatched failed session did not spend or stop the containing run.
    }
    [Fact]
    public async Task Actual_prompt_then_wire_EOF_cannot_be_zero_usage_or_admit_another_write()
    {
        var node = PromptNode(); var session = node.Register();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var input = new MemoryStream(); using var output = new MemoryStream(); var codec = new CodexJsonlProtocol(input, output, life);
        await ReportedModelUsage.WritePromptAsync(session, codec, new { type = "user", message = new { content = "fixed fixture" } }, TestContext.Current.CancellationToken);
        Assert.Null(await codec.ReadAsync(TestContext.Current.CancellationToken)); session.Finish(); var written = input.Length;
        Assert.True(session.Evidence().Stopped); Assert.False(session.Evidence().FinalUsageKnown);
        await Assert.ThrowsAsync<ReportedUsageException>(() => ReportedModelUsage.WritePromptAsync(session, codec, new { type = "user" }, TestContext.Current.CancellationToken));
        Assert.Equal(written, input.Length);
    }

}
