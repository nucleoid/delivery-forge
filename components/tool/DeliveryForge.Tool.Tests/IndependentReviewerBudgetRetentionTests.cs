using System.Text.Json;
using DeliveryForge.Review;
using DeliveryForge.Tool.Claude;

namespace DeliveryForge.Tool.Tests;

// Complete report parsing and private reviewer incomplete-result DATA only.
// Existing ReviewBudgetDecisionTests separately cover ledger overruns in all
// three dimensions. No policy, capability or provider hard cap is issued here.
public sealed class IndependentReviewerBudgetRetentionTests
{
    [Fact]
    public void Budget_incomplete_result_keeps_the_complete_parsed_findings()
    {
        var report = ClaudeIndependentReviewData.ParseReport("""
            {"verdict":"needs-attention","findings":[{"code":"R1","severity":"high","message":"Retain the grounded finding.","location":"Source.cs:1"},{"code":"R2","severity":"low","message":"Retain the second finding.","location":"Other.cs:2"}]}
            """);
        var decision = new BudgetDecision(ReviewOutcome.Incomplete, "Budget refusal DATA.", report.Findings);
        var result = ClaudeIndependentReviewer.BudgetIncomplete("retained-run", decision);
        Assert.Equal("INCOMPLETE", result.Outcome); Assert.Equal("INDEPENDENT_REVIEW_BUDGET", result.Code);
        var data = JsonSerializer.SerializeToElement(result.Data, BoundedFiles.Json);
        Assert.Equal("retained-run", data.GetProperty("runId").GetString());
        var findings = data.GetProperty("Findings").EnumerateArray().ToArray();
        Assert.Equal(2, findings.Length);
        for (var index = 0; index < findings.Length; index++)
        {
            Assert.Equal(report.Findings[index].Code, findings[index].GetProperty("Code").GetString());
            Assert.Equal(report.Findings[index].Severity, findings[index].GetProperty("Severity").GetString());
            Assert.Equal(report.Findings[index].Message, findings[index].GetProperty("Message").GetString());
            Assert.Equal(report.Findings[index].Location, findings[index].GetProperty("Location").GetString());
        }
        Assert.False(result.RuntimeCertified);
    }
}
