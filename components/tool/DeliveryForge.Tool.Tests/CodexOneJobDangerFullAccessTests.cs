using System.Text.Json;
using DeliveryForge.Execution;
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
}
