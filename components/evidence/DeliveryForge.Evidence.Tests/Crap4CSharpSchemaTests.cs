using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence.Tests;

public sealed class Crap4CSharpSchemaTests
{
    [Theory]
    [InlineData("complexityRulesetVersion")]
    [InlineData("evaluation.invocationMode")]
    [InlineData("evaluation.policy")]
    [InlineData("evaluation.scope")]
    [InlineData("evaluation.contexts")]
    [InlineData("evaluation.checks")]
    [InlineData("evaluation.metrics")]
    [InlineData("evaluation.findings")]
    [InlineData("evaluation.artifacts")]
    [InlineData("evaluation.coverage.reasons")]
    [InlineData("evaluation.decision.reason")]
    [InlineData("run.invocationId")]
    [InlineData("run.startedAt")]
    [InlineData("run.finishedAt")]
    [InlineData("run.durationMilliseconds")]
    [InlineData("run.commands")]
    [InlineData("run.diagnosticLocations")]
    [InlineData("run.artifacts")]
    [InlineData("run.cancellation")]
    public void Required_report_sections_cannot_be_omitted(string field)
    {
        var root = JsonNode.Parse(Crap4CSharpObservedContractTests.Report(true, "pass", "completed", 0, 1, 1, 0))!;
        var segments = field.Split('.');
        var parent = root;
        foreach (var segment in segments[..^1]) parent = parent[segment]!;
        Assert.True(parent.AsObject().Remove(segments[^1]));
        Assert.Equal(GateOutcome.Error, Normalize(root).Outcome);
    }

    [Fact]
    public void Coverage_summary_cannot_claim_an_absent_metric_inventory()
    {
        var root = JsonNode.Parse(Crap4CSharpObservedContractTests.Report(true, "pass", "completed", 0, 1, 1, 0))!;
        root["evaluation"]!["metrics"] = new JsonArray();
        Assert.Equal(GateOutcome.Error, Normalize(root).Outcome);
    }

    private static NormalizedEvidence Normalize(JsonNode root) =>
        new Crap4CSharpAdapter().Normalize(JsonSerializer.SerializeToUtf8Bytes(root),
            new ToolCapability("crap4csharp", "0.1.0+1129c9d3280168b294f9fed1542f81ca716c6d53", "1.0", true, true));
}
