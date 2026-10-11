using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence.Tests;

public sealed class Crap4CSharpObservedContractTests
{
    private const string Version = "0.1.0+1129c9d3280168b294f9fed1542f81ca716c6d53";

    [Fact]
    public void Installed_syntax_only_empty_class_preserves_not_applicable()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Crap4CSharp", "not-applicable.json"));
        var result = Normalize(bytes);

        Assert.Equal(GateOutcome.NotApplicable, result.Outcome);
        Assert.Equal("crap.noKnownScores", result.Reason);
        Assert.True(result.Fixture);
        Assert.False(result.ProductionCapable);
    }

    [Theory]
    [InlineData(true, "pass", "completed", 0, 1, 1, 0, GateOutcome.Pass)]
    [InlineData(true, "fail", "completed", 2, 1, 1, 0, GateOutcome.Fail)]
    [InlineData(true, "fail", "completed", 2, 2, 1, 1, GateOutcome.Fail)]
    [InlineData(false, "unknown", "operationalError", 1, 1, 0, 1, GateOutcome.Incomplete)]
    [InlineData(false, "unknown", "cancelled", 1, 1, 0, 1, GateOutcome.Incomplete)]
    [InlineData(true, "pass", "completed", 0, 1, 0, 1, GateOutcome.Incomplete)]
    [InlineData(true, "notApplicable", "completed", 0, 0, 0, 0, GateOutcome.NotApplicable)]
    [InlineData(true, "notApplicable", "completed", 0, 2, 1, 0, GateOutcome.NotApplicable)]
    public void Documented_outcome_and_exit_contract_is_preserved(
        bool completed, string decision, string status, int exit, int methods, int known, int unknown,
        GateOutcome expected)
    {
        var result = Normalize(Report(completed, decision, status, exit, methods, known, unknown));

        Assert.Equal(expected, result.Outcome);
        Assert.False(result.ProductionCapable);
    }

    [Theory]
    [InlineData(true, "fail", "completed", 1, 1, 1, 0)]
    [InlineData(true, "pass", "completed", 2, 1, 1, 0)]
    [InlineData(false, "unknown", "operationalError", 2, 1, 0, 1)]
    [InlineData(false, "unknown", "cancelled", 2, 1, 0, 1)]
    [InlineData(true, "pass", "completed", 0, 1, 2, -1)]
    [InlineData(true, "pass", "completed", 0, 1, 0, 2)]
    public void Contradictory_exits_or_counts_are_orchestration_errors(
        bool completed, string decision, string status, int exit, int methods, int known, int unknown)
    {
        Assert.Equal(GateOutcome.Error,
            Normalize(Report(completed, decision, status, exit, methods, known, unknown)).Outcome);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"toolVersion\":\"0.1.0+1129c9d3280168b294f9fed1542f81ca716c6d53\",\"evaluation\":[],\"run\":{}}")]
    public void Non_object_report_sections_return_typed_error(string json)
    {
        Assert.Equal(GateOutcome.Error, Normalize(Encoding.UTF8.GetBytes(json)).Outcome);
    }

    private static NormalizedEvidence Normalize(byte[] report) =>
        new Crap4CSharpAdapter().Normalize(report,
            new ToolCapability("crap4csharp", Version, "1.0", true, true));

    // Synthetic complete protocol documents, not installed or compiled tool evidence.
    internal static byte[] Report(
        bool completed, string decision, string status, int exit, int methods, int known, int unknown)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Crap4CSharp", "not-applicable.json")))!.AsObject();
        var evaluation = root["evaluation"]!.AsObject();
        evaluation["scope"]!["sources"] = new JsonArray("Fixture.cs");
        evaluation["artifacts"] = new JsonArray();
        evaluation["contexts"]![0]!["id"] = "fixture-context";
        evaluation["coverage"] = JsonSerializer.SerializeToNode(new { methods, known, unknown, reasons = new { } });
        evaluation["decision"] = JsonSerializer.SerializeToNode(new
        { completed, policyDecision = decision, reason = "contract.fixture" });
        evaluation["checks"] = new JsonArray();
        var metrics = new JsonArray();
        var callables = new JsonArray();
        for (var index = 0; index < Math.Max(0, methods); index++)
        {
            metrics.Add(JsonSerializer.SerializeToNode(new
            {
                contextId = "fixture-context",
                path = "Fixture.cs",
                methodIdentity = $"fixture-{index}",
                signature = (string?)null,
                span = new { startLine = 1, endLine = 1 },
                complexity = 1,
                coverage = index < known ? (double?)1 : null,
                crap = index < known ? (double?)(decision == "fail" ? 10 : 1) : null,
                coverageReason = index < known ? null : "coverage.unavailable"
            }));
            callables.Add(JsonSerializer.SerializeToNode(new
            {
                callableId = $"fixture-{index}",
                applicability = index < known + unknown ? "applicable" : "not-applicable",
                coverageStatus = index < known ? "known" : index < known + unknown ? "unknown" : "not-applicable"
            }));
        }
        evaluation["metrics"] = metrics;
        evaluation["callables"] = callables;
        var run = root["run"]!.AsObject();
        run["status"] = status;
        run["exitCode"] = exit;
        run["cancellation"]!["cancelled"] = status == "cancelled";
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }
}
