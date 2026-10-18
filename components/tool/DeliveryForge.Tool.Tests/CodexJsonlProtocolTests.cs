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
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously); public int ActualWrites;
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) { Interlocked.Increment(ref ActualWrites); Entered.TrySetResult(); await Release.Task; await base.WriteAsync(buffer, offset, count, token); }
    }
    [Fact]
    public async Task Actual_writer_timeout_does_not_release_an_unacquired_permit_or_enter_second_write()
    {
        using var life = new ToolLifetime(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken); using var input = new HeldWriter(); using var output = new MemoryStream(); var codec = new CodexJsonlProtocol(input, output, life); var first = codec.WriteAsync(new { id = 1, method = "first" }, TestContext.Current.CancellationToken); await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); var second = codec.WriteAsync(new { id = 2, method = "second" }, TestContext.Current.CancellationToken); var errors = new List<Exception>();
        try { foreach (var actual in new[] { first, second }) { try { await actual; } catch (Exception error) { errors.Add(error); } } Assert.Equal(1, input.ActualWrites); Assert.Equal(1, codec.AvailableWritePermits); Assert.Equal(2, errors.Count); Assert.DoesNotContain(errors.SelectMany(x => x is AggregateException aggregate ? (IEnumerable<Exception>)aggregate.Flatten().InnerExceptions : new[] { x }), x => x is SemaphoreFullException); }
        finally { input.Release.TrySetResult(); foreach (var actual in codec.EnteredTasks) { try { await actual.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); } catch (OperationCanceledException) { } } }
    }

}
