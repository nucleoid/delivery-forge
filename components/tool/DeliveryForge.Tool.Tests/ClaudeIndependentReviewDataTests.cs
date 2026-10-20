using System.Text;
using System.Text.Json;
using DeliveryForge.Tool.Claude;

namespace DeliveryForge.Tool.Tests;

// These controls inspect untrusted DATA only. No CLI, authentication command,
// model, production capability or review receipt is created by these tests.
public sealed class ClaudeIndependentReviewDataTests
{
    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_BASE_URL")]
    [InlineData("CLAUDE_CODE_USE_BEDROCK")]
    public void Provider_override_refuses_without_replacing_authentication(string key)
    {
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequireSubscriptionEnvironment(name => name == key ? "present" : null));
    }
    [Fact]
    public void Only_actual_first_party_subscription_status_is_accepted_as_route_data()
    {
        ClaudeIndependentReviewData.RequireSubscription("{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"apiProvider\":\"firstParty\"}"u8.ToArray());
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequireSubscription("{\"loggedIn\":true,\"authMethod\":\"apiKey\",\"apiProvider\":\"firstParty\"}"u8.ToArray()));
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequireSubscription("{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"apiProvider\":\"thirdParty\"}"u8.ToArray()));
    }
    [Fact]
    public void Model_family_requires_observed_first_party_Claude_model_name()
    {
        using var native = JsonDocument.Parse("{\"model\":\"claude-observed-test-model\"}");
        Assert.Equal("claude-observed-test-model", ClaudeIndependentReviewData.RequireModel(native.RootElement));
        using var ambiguous = JsonDocument.Parse("{\"model\":\"configured-default\"}");
        Assert.Throws<IOException>(() => ClaudeIndependentReviewData.RequireModel(ambiguous.RootElement));
    }
    [Fact]
    public void Complete_report_keeps_all_findings_and_refuses_prose_or_unknown_fields()
    {
        var report = ClaudeIndependentReviewData.ParseReport("{\"verdict\":\"needs-attention\",\"findings\":[{\"code\":\"BOUNDARY\",\"severity\":\"high\",\"message\":\"Concrete failure and fix\",\"location\":\"file.cs:4\"}]}");
        Assert.Equal("FAIL", report.Verdict); Assert.Single(report.Findings);
        Assert.ThrowsAny<Exception>(() => ClaudeIndependentReviewData.ParseReport("VERDICT: ship"));
        Assert.Throws<IOException>(() => ClaudeIndependentReviewData.ParseReport("{\"verdict\":\"ship\",\"findings\":[],\"productionAccepted\":true}"));
    }
    [Fact]
    public void Missing_usage_or_cost_cannot_be_replaced_by_zero_estimates()
    {
        using var complete = JsonDocument.Parse("{\"usage\":{\"input_tokens\":5,\"output_tokens\":7,\"cache_creation_input_tokens\":11,\"cache_read_input_tokens\":13},\"total_cost_usd\":0.01}");
        var cost = ClaudeIndependentReviewData.Usage(complete.RootElement); Assert.Equal(36L, cost.Tokens); Assert.Equal(0.01m, cost.Cost);
        using var missing = JsonDocument.Parse("{\"usage\":{\"input_tokens\":5,\"output_tokens\":7},\"total_cost_usd\":0}");
        Assert.Throws<IOException>(() => ClaudeIndependentReviewData.Usage(missing.RootElement));
        using var noCost = JsonDocument.Parse("{\"usage\":{\"input_tokens\":5,\"output_tokens\":7,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}");
        Assert.Throws<IOException>(() => ClaudeIndependentReviewData.Usage(noCost.RootElement));
    }
}
