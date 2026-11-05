using System.Collections;
using System.Text;
using System.Text.Json;
using DeliveryForge.Tool.Codex;

namespace DeliveryForge.Tool.Tests;

public sealed class CodexProtocolFailureTests
{
    const string Prefix = "DFCLI_FIRST_CAUGHT|";

    [Fact(Timeout = 30000)]
    public async Task Actual_incoming_byte_guard_keeps_base_exception_and_reports_its_real_crossing_counts()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var input = new MemoryStream(); using var output = new MemoryStream();
        const string start = "{\"method\":\"x\",\"params\":{\"text\":\"";
        const string end = "\"}}\n";
        var frame = Encoding.UTF8.GetBytes(start + new string('x', 8192 - start.Length - end.Length) + end);
        Assert.Equal(8192, frame.Length);
        for (var i = 0; i < 2049; i++) output.Write(frame);
        output.Position = 0;
        var protocol = new CodexJsonlProtocol(input, output, life);
        var failure = await Assert.ThrowsAsync<IOException>(async () =>
        {
            while (true)
            {
                using var document = await protocol.ReadAsync(TestContext.Current.CancellationToken);
                Assert.NotNull(document);
            }
        });
        Assert.IsType<IOException>(failure);
        Assert.Equal("Codex complete incoming byte profile exceeded.", failure.Message);
        var aggregate = new AggregateException("private payload is not a diagnostic", failure);
        Assert.Same(failure, aggregate.InnerExceptions[0]);
        var facts = Assert.IsType<CodexProtocolFailureFacts>(CodexProtocolFailure.FindSingle(aggregate));
        Assert.Equal("IncomingBytesBound", facts.Code); Assert.Equal(16781312L, facts.ReadBytes); Assert.Equal(2048, facts.Messages);
        var (line, attention) = Emit(life, aggregate);
        using var json = JsonDocument.Parse(line[Prefix.Length..]);
        Assert.Equal("IncomingBytesBound", json.RootElement.GetProperty("CodexProtocol").GetProperty("Code").GetString());
        Assert.DoesNotContain("private payload", line, StringComparison.Ordinal);
        Assert.All(protocol.EnteredTasks, task => Assert.True(task.IsCompletedSuccessfully));
        Assert.False(protocol.Unknown); Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Theory]
    [InlineData("truncated", "TruncatedFrame")]
    [InlineData("line", "IncomingLineBound")]
    [InlineData("empty", "EmptyOrExcessFrame")]
    public async Task Actual_stream_frame_refusals_reach_first_caught_without_exporting_frame_text(string scenario, string code)
    {
        const string secret = "private-auth-config-payload";
        var text = scenario switch { "truncated" => "{\"private\":\"" + secret + "\"}", "line" => new string('x', 65537), _ => "\n" };
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var input = new MemoryStream(); using var output = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var protocol = new CodexJsonlProtocol(input, output, life);
        var failure = await Assert.ThrowsAsync<IOException>(async () => { using var document = await protocol.ReadAsync(TestContext.Current.CancellationToken); });
        var facts = Assert.IsType<CodexProtocolFailureFacts>(CodexProtocolFailure.FindSingle(failure));
        Assert.Equal(code, facts.Code); Assert.NotNull(facts.ReadBytes); Assert.Equal(0, facts.Messages);
        var (line, attention) = Emit(life, new AggregateException("private wrapper", failure));
        using var json = JsonDocument.Parse(line[Prefix.Length..]);
        Assert.Equal(code, json.RootElement.GetProperty("CodexProtocol").GetProperty("Code").GetString());
        Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
        Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Actual_envelope_refusal_has_no_invented_stream_counts_or_untrusted_member_text()
    {
        const string secret = "private-token-member";
        using var document = JsonDocument.Parse("{\"id\":1,\"result\":{},\"" + secret + "\":true}");
        var failure = Assert.Throws<IOException>(() => CodexJsonlProtocol.RequireEnvelope(document.RootElement));
        Assert.Equal("Unknown Codex envelope member.", failure.Message);
        var facts = Assert.IsType<CodexProtocolFailureFacts>(CodexProtocolFailure.FindSingle(failure));
        Assert.Equal("EnvelopeMember", facts.Code); Assert.Null(facts.ReadBytes); Assert.Null(facts.Messages);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var (line, attention) = Emit(life, new AggregateException("private envelope", failure));
        Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
        Assert.Contains("\"Code\":\"EnvelopeMember\"", line, StringComparison.Ordinal);
        Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Only_exact_factory_exception_references_can_supply_facts()
    {
        const string literal = "private-message-never-read";
        var tagged = CodexProtocolFailure.Create(CodexProtocolFailureKind.TruncatedFrame, literal, 17, 0);
        var facts = Assert.IsType<CodexProtocolFailureFacts>(CodexProtocolFailure.FindSingle(tagged));
        Assert.IsType<IOException>(tagged); Assert.Equal(literal, tagged.Message);
        Assert.Null(CodexProtocolFailure.FindSingle(new IOException(literal)));
        var repeated = new AggregateException(tagged, new IOException("private wrapper", tagged));
        Assert.Same(facts, CodexProtocolFailure.FindSingle(repeated));
        var distinct = CodexProtocolFailure.Create(CodexProtocolFailureKind.TruncatedFrame, literal, 17, 0);
        Assert.Equal(facts, CodexProtocolFailure.FindSingle(distinct));
        Assert.Null(CodexProtocolFailure.FindSingle(new AggregateException(tagged, distinct)));
        Exception chain = tagged;
        for (var i = 0; i < 15; i++) chain = new IOException("private", chain);
        Assert.Same(facts, CodexProtocolFailure.FindSingle(chain));
        Assert.Null(CodexProtocolFailure.FindSingle(new IOException("private", chain)));
        Assert.Null(CodexProtocolFailure.FindSingle(new AggregateException(Enumerable.Repeat<Exception>(tagged, 17))));
        var throwingText = new ThrowingTextException(tagged);
        Assert.Same(facts, CodexProtocolFailure.FindSingle(throwingText));
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var (line, attention) = Emit(life, throwingText);
        Assert.Contains("\"Code\":\"TruncatedFrame\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain(literal, line, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", line, StringComparison.Ordinal);
        Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Unrelated_ambiguous_or_overbound_errors_keep_exact_existing_default_marker_bytes()
    {
        var tagged = CodexProtocolFailure.Create(CodexProtocolFailureKind.UnexpectedEof, "private-secret");
        var distinct = CodexProtocolFailure.Create(CodexProtocolFailureKind.UnexpectedEof, "private-secret");
        foreach (var error in new Exception[] { new IOException("private-secret"), new AggregateException(tagged, distinct), new AggregateException(Enumerable.Repeat<Exception>(tagged, 17)) })
        {
            using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var (line, attention) = Emit(life, error);
            Assert.Equal(DefaultLine(error, "host-implement"), line);
            Assert.DoesNotContain("private-secret", line, StringComparison.Ordinal);
            Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
        }
    }

    [Fact]
    public void Oversized_optional_facts_are_omitted_without_changing_default_bytes_or_unknown_state()
    {
        var tagged = CodexProtocolFailure.Create(CodexProtocolFailureKind.IncomingBytesBound, "private-secret", 16781312, 4097);
        var stage = new string('s', 1024 - Encoding.UTF8.GetByteCount(DefaultLine(tagged, "")));
        var expected = DefaultLine(tagged, stage); Assert.Equal(1024, Encoding.UTF8.GetByteCount(expected));
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var started = life.OriginalStarted; var whole = life.WholeLimit;
        var (line, attention) = Emit(life, tagged, stage);
        Assert.Equal(expected, line); Assert.DoesNotContain("CodexProtocol", line, StringComparison.Ordinal);
        Assert.Equal(started, life.OriginalStarted); Assert.Equal(whole, life.WholeLimit);
        Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Existing_correlated_startup_facts_take_precedence_over_optional_protocol_facts()
    {
        var rpc = new CodexRemoteRpcException("private-token", 1);
        rpc.CaptureStartup(new(CodexStartupStage.ThreadStart, 1, 2,
            [new(1, CodexStartupStage.ThreadStart, CodexWireDirection.Client, CodexWireMethod.ThreadStart, CodexWireState.WriteReturned, CodexWireIdKind.ClientNumber, 1, false, null),
             new(2, CodexStartupStage.ThreadStart, CodexWireDirection.Server, CodexWireMethod.None, CodexWireState.Received, CodexWireIdKind.ClientNumber, 1, true, -32602)],
            false, 1, 1, [], null, null, TaskStatus.RanToCompletion, [], true, false));
        var tagged = CodexProtocolFailure.Create(CodexProtocolFailureKind.ResponseCorrelation, "private-token");
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var (line, attention) = Emit(life, new AggregateException(rpc, tagged));
        using var json = JsonDocument.Parse(line[Prefix.Length..]);
        Assert.Equal(JsonSerializer.Serialize(rpc.StartupFailure), json.RootElement.GetProperty("CodexStartup").GetRawText());
        Assert.False(json.RootElement.TryGetProperty("CodexProtocol", out _));
        Assert.DoesNotContain("private-token", line, StringComparison.Ordinal);
        Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Unsupported_numeric_observations_are_omitted_without_changing_the_guard_exception()
    {
        var error = CodexProtocolFailure.Create(CodexProtocolFailureKind.IncomingBytesBound, "existing fixed literal", long.MaxValue, int.MaxValue);
        Assert.IsType<IOException>(error); Assert.Equal("existing fixed literal", error.Message);
        var facts = Assert.IsType<CodexProtocolFailureFacts>(CodexProtocolFailure.FindSingle(error));
        Assert.Null(facts.ReadBytes); Assert.Null(facts.Messages);
    }

    static (string Line, ToolFailureAttention Attention) Emit(ToolLifetime life, Exception error, string stage = "host-implement")
    {
        using var errors = new StringWriter(); var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.RecordFirstCaught(stage, error); life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        var line = Assert.Single(errors.ToString().Split('\n'), value => value.StartsWith(Prefix, StringComparison.Ordinal)).TrimEnd('\r');
        Assert.True(Encoding.UTF8.GetByteCount(line) <= 1024);
        return (line, attention);
    }

    static string DefaultLine(Exception error, string stage) => Prefix + JsonSerializer.Serialize(new
    {
        Stage = stage,
        Type = error.GetType().FullName,
        error.HResult,
        InnerType = error.InnerException?.GetType().FullName,
        CleanupAlreadyBegun = false,
        MarkerAlreadyEntered = false
    });

    sealed class ThrowingTextException(Exception inner) : IOException("private-secret", inner)
    {
        public override string Message => throw new InvalidOperationException("Message was read");
        public override string? StackTrace => throw new InvalidOperationException("StackTrace was read");
        public override IDictionary Data => throw new InvalidOperationException("Data was read");
    }
}
