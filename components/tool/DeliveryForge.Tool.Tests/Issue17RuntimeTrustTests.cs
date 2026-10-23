using DeliveryForge.Tool.Claude;
using DeliveryForge.Tool.Codex;

namespace DeliveryForge.Tool.Tests;

// Ordinary file bindings and actual route DATA only. No production capability,
// accepted host session, reviewer receipt or native execution is manufactured.
public sealed class Issue17RuntimeTrustTests
{
    [Theory]
    [InlineData("OPENAI_API_KEY", "0")]
    [InlineData("CODEX_API_KEY", "secret")]
    [InlineData("OPENAI_BASE_URL", "https://example.invalid")]
    public void Worker_override_is_refused_before_route_dispatch(string key, string value) =>
        Assert.Throws<ToolUsageException>(() => CodexRuntimeRoute.RequireSubscriptionEnvironment(name => name == key ? value : null));
    [Theory]
    [InlineData(false, "openai", "gpt-observed")]
    [InlineData(true, "custom", "gpt-observed")]
    [InlineData(true, "openai", "other")]
    [InlineData(true, "openai", null)]
    public void Worker_observation_must_bind_subscription_provider_and_family(bool account, string provider, string? model) =>
        Assert.Throws<IOException>(() => CodexRuntimeRoute.RequireObserved(account, provider, model));
    [Fact]
    public void Correct_route_data_does_not_issue_host_or_review_authority() =>
        CodexRuntimeRoute.RequireObserved(true, "openai", "gpt-observed");
    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("ANTHROPIC_BASE_URL")]
    public void Credential_or_endpoint_zero_is_not_a_disabled_provider_flag(string key) =>
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequireSubscriptionEnvironment(name => name == key ? "0" : null));
    [Fact]
    public void Disabled_boolean_provider_flag_does_not_change_login() =>
        ClaudeIndependentReviewData.RequireSubscriptionEnvironment(name => name == "CLAUDE_CODE_USE_BEDROCK" ? "false" : null);
    [Fact]
    public void Public_selection_data_cannot_bypass_default_production_refusal()
    {
        Assert.NotNull(ClaudeIndependentReviewer.UnsupportedProductionProfile());
        Assert.Empty(typeof(InstalledHostAdmission.RuntimeTrust).GetConstructors());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selected_complete_file_or_absence_drift_revokes_existing_work_stickily(bool initiallyPresent)
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "forge-host-trust-drift-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var selected = Path.Combine(root, "selected"); var absent = Path.Combine(root, "other");
        if (initiallyPresent) File.WriteAllBytes(selected, [1, 2, 3]);
        var row = initiallyPresent ? new Issue17HostInput(selected, 3, BoundedFiles.Sha([1, 2, 3])) : new Issue17HostInput(selected, null, null);
        var inputs = Issue17HostInputs.Capture(life, [row, new(absent, null, null)]);
        inputs.Check(); File.WriteAllBytes(selected, [3, 2, 1]);
        var failure = Assert.ThrowsAny<Exception>(inputs.Check);
        if (initiallyPresent) Assert.IsType<IOException>(failure);
        else Assert.IsType<ToolUsageException>(failure);
        Assert.Throws<TimeoutException>(life.Check);
        if (initiallyPresent) File.WriteAllBytes(selected, [1, 2, 3]);
        Assert.Throws<TimeoutException>(inputs.Check); // Restoring DATA cannot renew this original.
        Assert.True(File.Exists(selected)); // Failed namespace remains evidence.
    }
    [Fact]
    public void Duplicate_and_source_overlapping_host_inputs_are_refused()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "forge-host-trust-roles-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "host.json");
        Assert.Throws<ToolUsageException>(() => Issue17HostInputs.Capture(life, [new(path, null, null), new(path, null, null)]));
        var inputs = Issue17HostInputs.Capture(life, [new(path, null, null), new(Path.Combine(root, "other"), null, null)]);
        Assert.Throws<ToolUsageException>(() => inputs.Separate(root));
    }
}
