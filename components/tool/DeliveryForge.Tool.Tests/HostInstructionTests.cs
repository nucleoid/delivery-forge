using System.Text;
using System.Text.Json;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Codex;
namespace DeliveryForge.Tool.Tests;

public sealed class HostInstructionTests
{
    static AgentRequest Request() => WorkerEnvelope.PrepareWorker(new("instruction-fixture", "sha256:" + new string('1', 64), new string('a', 40), "fixture/repo", Path.GetFullPath("."), Path.Combine(Path.GetTempPath(), "worker-instructions"), Path.Combine(Path.GetTempPath(), "instruction-results"), "PRIVATE-WRITER-DO-NOT-SEND", ["src/greeting.cs"], ["secrets/**"], "codex.cli", "fixture-v1", [AgentCapability.Launch, AgentCapability.CompletionEvidence]));
    static HostInstructionContext Context(AgentRequest r) => HostInstructionContext.FromParentSelection(r, "sha256:" + new string('b', 64), "Return the localized greeting from Greeting.Format.", ["French culture returns Bonjour", "Invariant culture retains Hello"], ["Update src/greeting.cs", "Add localized greeting regression"]);
    [Theory]
    [InlineData("codex")]
    [InlineData("pi")]
    public async Task Actual_production_wire_builder_carries_task_acceptance_and_excludes_private_writer(string format)
    {
        var r = Request(); if (format == "pi") { r = r with { AdapterId = "pi.cli" }; r = r with { RequestIdentity = WorkerEnvelope.ComputeRequestIdentity(r) }; }
        var context = Context(r); using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); using var input = new MemoryStream(); using var output = new MemoryStream(); var codec = new CodexJsonlProtocol(input, output, life);
        var payload = format == "codex" ? HostAssignmentPrompt.CodexTurnStart(r, context, "actual-thread") : HostAssignmentPrompt.PiPrompt(r, context, "acknowledged-prompt");
        await codec.WriteAsync(payload, TestContext.Current.CancellationToken); var text = Encoding.UTF8.GetString(input.ToArray()); Assert.Contains("localized greeting", text, StringComparison.Ordinal); Assert.Contains("French culture returns Bonjour", text, StringComparison.Ordinal); Assert.Contains(r.RequestIdentity, text, StringComparison.Ordinal); Assert.DoesNotContain(r.WriterToken, text, StringComparison.Ordinal); Assert.DoesNotContain("WriterToken", text, StringComparison.Ordinal);
        var foreign = r with { AuthorizationCeiling = "merge" }; foreign = foreign with { RequestIdentity = WorkerEnvelope.ComputeRequestIdentity(foreign) }; Assert.Throws<WorkerEnvelopeException>(() => HostAssignmentPrompt.Create(foreign, context)); Assert.All(codec.EnteredTasks, x => Assert.True(x.IsCompletedSuccessfully));
    }
    [Fact]
    public void Parent_selection_requires_real_task_acceptance_steps_and_immutable_binding()
    {
        var r = Request(); var context = Context(r); Assert.Throws<WorkerEnvelopeException>(() => HostInstructionContext.FromParentSelection(r, "sha256:" + new string('b', 64), "", ["accept"], ["step"]));
        Assert.Throws<WorkerEnvelopeException>(() => HostInstructionContext.FromParentSelection(r, "sha256:" + new string('b', 64), "task", [], ["step"]));
        var mutable = new[] { "Original acceptance" }; var copy = HostInstructionContext.FromParentSelection(r, "sha256:" + new string('b', 64), "task", mutable, ["step"]); mutable[0] = "mutated"; Assert.Contains("Original acceptance", copy.Project(r).GetRawText(), StringComparison.Ordinal);
        var changed = r with { AllowedPaths = ["elsewhere.cs"] }; changed = changed with { RequestIdentity = WorkerEnvelope.ComputeRequestIdentity(changed) }; Assert.Throws<WorkerEnvelopeException>(() => context.Require(changed));
    }
}
