using System.Text;
using System.Text.Json;
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
    [InlineData(false, "unknown", "operationalError", 1, 1, 0, 1, GateOutcome.Incomplete)]
    [InlineData(false, "unknown", "cancelled", 1, 1, 0, 1, GateOutcome.Incomplete)]
    [InlineData(true, "pass", "completed", 0, 1, 0, 1, GateOutcome.Incomplete)]
    [InlineData(true, "notApplicable", "completed", 0, 0, 0, 0, GateOutcome.NotApplicable)]
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
    [InlineData(true, "pass", "completed", 0, 2, 1, 0)]
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

    private static byte[] Report(
        bool completed, string decision, string status, int exit, int methods, int known, int unknown) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "1.0",
            toolVersion = Version,
            evaluation = new
            {
                coverage = new { methods, known, unknown },
                decision = new { completed, policyDecision = decision, reason = "contract.fixture" }
            },
            run = new
            {
                status,
                exitCode = exit,
                cancellation = new { timedOut = false, cancelled = status == "cancelled" }
            }
        });
}
