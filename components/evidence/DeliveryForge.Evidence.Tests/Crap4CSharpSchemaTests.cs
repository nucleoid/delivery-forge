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

    [Fact]
    public void Known_summary_cannot_hide_null_metric_coverage()
    {
        var root = Positive();
        root["evaluation"]!["metrics"]![0]!["coverage"] = null;
        Assert.Equal(GateOutcome.Error, Normalize(root).Outcome);
    }

    [Fact]
    public void Unbound_metric_context_stays_incomplete()
    {
        var root = Positive();
        root["evaluation"]!["metrics"]![0]!["contextId"] = "absent-context";
        Assert.Equal(GateOutcome.Incomplete, Normalize(root).Outcome);
    }

    [Fact]
    public void Applicable_unscored_metric_stays_incomplete()
    {
        var root = Positive();
        root["evaluation"]!["metrics"]![0]!["crap"] = null;
        Assert.Equal(GateOutcome.Incomplete, Normalize(root).Outcome);
    }

    [Fact]
    public void Count_gap_requires_reported_non_applicability()
    {
        var root = JsonNode.Parse(Crap4CSharpObservedContractTests.Report(true, "pass", "completed", 0, 2, 1, 0))!;
        root["evaluation"]!["callables"] = new JsonArray();
        Assert.Equal(GateOutcome.Incomplete, Normalize(root).Outcome);
    }

    private static JsonNode Positive() =>
        JsonNode.Parse(Crap4CSharpObservedContractTests.Report(true, "pass", "completed", 0, 1, 1, 0))!;

    [Theory]
    [InlineData(false, GateOutcome.Pass)]
    [InlineData(true, GateOutcome.Error)]
    public void Diagnostic_span_must_match_one_declared_schema_alternative(bool invalid, GateOutcome expected)
    {
        var root = Positive();
        root["evaluation"]!["coverageDiagnostics"] = JsonSerializer.SerializeToNode(new[] { new
        {
            id = new string('a', 64), code = "fixture.info", stage = "path", severity = "info", scope = "report",
            contextId = (string?)null, reportId = (string?)null, observationId = (string?)null,
            path = (string?)null, externalRootId = (string?)null, methodId = (string?)null,
            span = invalid ? (object)"invalid-span" : null,
            candidatePaths = Array.Empty<string>(), candidateMethodIds = Array.Empty<string>(),
            reportedType = (string?)null, reportedMethodName = (string?)null, reportedParameterCount = (int?)null,
            moduleIdentities = Array.Empty<string>(), mappingId = (string?)null, evidenceRefs = Array.Empty<string>(),
            remediationCode = "fixture", message = "fixture"
        } });
        Assert.Equal(expected, Normalize(root).Outcome);
    }

    private static NormalizedEvidence Normalize(JsonNode root) =>
        new Crap4CSharpAdapter().Normalize(JsonSerializer.SerializeToUtf8Bytes(root),
            new ToolCapability("crap4csharp", "0.1.0+1129c9d3280168b294f9fed1542f81ca716c6d53", "1.0", true, true));
}
