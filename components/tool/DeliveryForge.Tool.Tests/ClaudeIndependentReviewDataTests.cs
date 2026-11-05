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
    [InlineData("medium")]
    [InlineData("low")]
    public void Ship_with_any_unresolved_findings_retains_DATA_and_returns_semantic_FAIL(string severity)
    {
        var text = JsonSerializer.Serialize(new { verdict = "ship", findings = new[] { new { code = "UNRESOLVED", severity, message = "Concrete unresolved defect", location = "file.cs:4" } } });
        var report = ClaudeIndependentReviewData.ParseReport(text);
        Assert.Equal("ship", report.RawVerdict); Assert.Equal("FAIL", report.Verdict);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("UNRESOLVED", finding.Code); Assert.Equal(severity, finding.Severity);
        Assert.Equal("Concrete unresolved defect", finding.Message); Assert.Equal("file.cs:4", finding.Location);
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

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Raw_or_one_entire_json_fence_keeps_the_same_complete_strict_report(string newline)
    {
        const string raw = "{\"verdict\":\"needs-attention\",\"findings\":[{\"code\":\"MISSING_GATE_EVIDENCE\",\"severity\":\"medium\",\"message\":\"Required evidence is absent\",\"location\":\"source.txt:1\"}]}";
        var presented = newline.Length == 0 ? raw : "```json" + newline + raw + newline + "```";
        var report = ClaudeIndependentReviewData.ParseReport(presented);
        Assert.Equal("needs-attention", report.RawVerdict); Assert.Equal("FAIL", report.Verdict);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("MISSING_GATE_EVIDENCE", finding.Code); Assert.Equal("medium", finding.Severity);
        Assert.Equal("Required evidence is absent", finding.Message); Assert.Equal("source.txt:1", finding.Location);
    }
    [Theory]
    [InlineData("prose-before")]
    [InlineData("prose-after")]
    [InlineData("multiple-fences")]
    [InlineData("nested-fence")]
    [InlineData("unknown-language")]
    [InlineData("unknown-member")]
    [InlineData("duplicate-member")]
    [InlineData("trailing-json")]
    [InlineData("outer-whitespace")]
    [InlineData("mixed-fence-newline")]
    [InlineData("mixed-fence-newline-reverse")]
    [InlineData("empty-fence")]
    [InlineData("overlapping-fence")]
    public void Fenced_presentation_never_selects_a_report_from_prose_or_bypasses_strict_inner_validation(string mutation)
    {
        const string raw = "{\"verdict\":\"ship\",\"findings\":[]}";
        var fenced = "```json\n" + raw + "\n```";
        var text = mutation switch
        {
            "prose-before" => "Selected report:\n" + fenced,
            "prose-after" => fenced + "\nAdditional report prose",
            "multiple-fences" => fenced + "\n" + fenced,
            "nested-fence" => "```json\n" + fenced + "\n```",
            "unknown-language" => "```JSON\n" + raw + "\n```",
            "unknown-member" => "```json\n{\"verdict\":\"ship\",\"findings\":[],\"productionAccepted\":true}\n```",
            "duplicate-member" => "```json\n{\"verdict\":\"ship\",\"verdict\":\"ship\",\"findings\":[]}\n```",
            "trailing-json" => "```json\n" + raw + "{}\n```",
            "outer-whitespace" => " " + fenced,
            "empty-fence" => "```json\n\n```",
            "overlapping-fence" => "```json\n```",
            "mixed-fence-newline-reverse" => "```json\n" + raw + "\r\n```",
            "mixed-fence-newline" => "```json\r\n" + raw + "\n```",
            _ => throw new ArgumentException("Unknown synthetic presentation mutation.")
        };
        Assert.ThrowsAny<Exception>(() => ClaudeIndependentReviewData.ParseReport(text));
    }
    [Fact]
    public void Fenced_presentation_keeps_the_original_total_report_length_limit()
    {
        var text = "```json\n" + new string(' ', 32768) + "\n```";
        Assert.Throws<IOException>(() => ClaudeIndependentReviewData.ParseReport(text));
    }
}
