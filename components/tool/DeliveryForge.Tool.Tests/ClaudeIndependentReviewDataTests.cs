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
    [Theory]
    [InlineData("critical")]
    [InlineData("high")]
    public void Ship_with_material_unresolved_findings_refuses_instead_of_returning_PASS(string severity)
    {
        var text = JsonSerializer.Serialize(new { verdict = "ship", findings = new[] { new { code = "UNRESOLVED", severity, message = "Concrete unresolved defect", location = "file.cs:4" } } });
        Assert.Throws<IOException>(() => ClaudeIndependentReviewData.ParseReport(text));
    }
    [Theory]
    [InlineData("{\"verdict\":\"ship\"}")]
    [InlineData("{\"verdict\":\"ship\",\"findings\":null}")]
    [InlineData("{\"verdict\":\"needs-attention\",\"findings\":[{\"severity\":\"high\",\"message\":\"Defect\",\"location\":\"file.cs:4\"}]}")]
    [InlineData("{\"verdict\":\"needs-attention\",\"findings\":[{\"code\":\"DEFECT\",\"severity\":\"high\",\"message\":{},\"location\":\"file.cs:4\"}]}")]
    public void Malformed_complete_report_members_are_typed_IO_failures(string text)
    {
        Assert.Throws<IOException>(() => ClaudeIndependentReviewData.ParseReport(text));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"usage\":[]}")]
    [InlineData("{\"usage\":{\"input_tokens\":\"5\",\"output_tokens\":7,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0},\"total_cost_usd\":0}")]
    [InlineData("{\"usage\":{\"input_tokens\":5,\"output_tokens\":7,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0},\"total_cost_usd\":\"0\"}")]
    [InlineData("{\"usage\":{\"input_tokens\":9223372036854775807,\"output_tokens\":1,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0},\"total_cost_usd\":0}")]
    public void Malformed_or_overflowed_usage_is_typed_IO_failure_without_fabricated_accounting(string text)
    {
        using var doc = JsonDocument.Parse(text);
        Assert.Throws<IOException>(() => ClaudeIndependentReviewData.Usage(doc.RootElement));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"loggedIn\":true,\"authMethod\":{},\"apiProvider\":\"firstParty\"}")]
    [InlineData("{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"apiProvider\":{}}")]
    public void Malformed_subscription_observation_refuses_with_existing_typed_profile_error(string text)
    {
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequireSubscription(Encoding.UTF8.GetBytes(text)));
    }
}
