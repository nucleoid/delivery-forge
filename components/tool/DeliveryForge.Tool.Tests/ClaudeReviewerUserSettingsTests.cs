using System.Text;
using System.Text.Json;
using DeliveryForge.Tool.Claude;

namespace DeliveryForge.Tool.Tests;

// Pure parsing and actual fresh ordinary files. No real user settings,
// credentials, CLI, model, provider or capability acceptance is inspected.
public sealed class ClaudeReviewerUserSettingsTests
{
    static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    static string DirectoryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-reviewer-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }

    [Theory]
    [InlineData("env")]
    [InlineData("hooks")]
    [InlineData("apiKeyHelper")]
    [InlineData("permissions")]
    [InlineData("ENV")]
    [InlineData("awsAuthRefresh")]
    [InlineData("awsCredentialExport")]
    [InlineData("otelHeadersHelper")]
    public void Security_customization_refuses_without_exporting_key_values_or_credentials(string property)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { [property] = "secret-sentinel-value" });
        var error = Assert.Throws<ToolUsageException>(() => ClaudeReviewerUserSettings.RequireSafeSettings(raw));
        Assert.DoesNotContain("secret-sentinel-value", error.ToString());
        Assert.Null(error.InnerException);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"model\":\"one\",\"model\":\"two-secret-sentinel\"}")]
    [InlineData("{broken-secret-sentinel")]
    public void Malformed_or_ambiguous_complete_settings_refuse_without_raw_parser_diagnostics(string raw)
    {
        var error = Assert.Throws<ToolUsageException>(() => ClaudeReviewerUserSettings.RequireSafeSettings(Utf8(raw)));
        Assert.DoesNotContain("secret-sentinel", error.ToString()); Assert.Null(error.InnerException);
    }
    [Fact]
    public void Benign_preferences_and_existing_default_model_are_preserved_then_any_byte_drift_refuses()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = DirectoryRoot(); var path = Path.Combine(root, "settings.json");
        var bytes = Utf8("{\"model\":\"configured-existing-default\",\"theme\":\"dark\",\"effortLevel\":\"high\"}");
        File.WriteAllBytes(path, bytes);
        var selected = ClaudeReviewerUserSettings.Capture(original, root); selected.Check();
        Assert.Equal(bytes, File.ReadAllBytes(path));
        selected.RequireObservedConfigurationDirectory(JsonSerializer.SerializeToUtf8Bytes(new { configDirectory = root + Path.DirectorySeparatorChar, ignoredAccount = "private-sentinel" }));
        File.AppendAllText(path, " ");
        Assert.Throws<ToolUsageException>(selected.Check); Assert.False(original.HasExplicitUnknown);
    }
    [Fact]
    public void Absent_user_file_is_bound_and_later_creation_refuses_without_overriding_it()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = DirectoryRoot(); var selected = ClaudeReviewerUserSettings.Capture(original, root); selected.Check();
        var path = Path.Combine(root, "settings.json"); File.WriteAllText(path, "{}");
        Assert.Throws<ToolUsageException>(selected.Check); Assert.Equal("{}", File.ReadAllText(path));
        Assert.False(original.HasExplicitUnknown);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"configDirectory\":17}")]
    [InlineData("{\"configDirectory\":\"relative-secret-sentinel\"}")]
    public void Missing_or_unsupported_actual_config_directory_refuses_before_review_payload(string auth)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var selected = ClaudeReviewerUserSettings.Capture(original, DirectoryRoot());
        var error = Assert.Throws<ToolUsageException>(() => selected.RequireObservedConfigurationDirectory(Utf8(auth)));
        Assert.DoesNotContain("secret-sentinel", error.ToString()); Assert.False(original.HasExplicitUnknown);
    }
    [Fact]
    public void Another_actual_config_directory_cannot_adopt_the_captured_settings_namespace()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var selected = ClaudeReviewerUserSettings.Capture(original, DirectoryRoot());
        var foreign = JsonSerializer.SerializeToUtf8Bytes(new { configDirectory = DirectoryRoot() });
        Assert.Throws<ToolUsageException>(() => selected.RequireObservedConfigurationDirectory(foreign));
        Assert.False(original.HasExplicitUnknown);
    }
    [Theory]
    [InlineData("parent")]
    [InlineData("worker")]
    [InlineData("result")]
    [InlineData("review-artifacts")]
    public void Configuration_under_source_or_output_namespace_refuses_before_its_settings_are_read(string role)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var protectedRoot = Path.Combine(DirectoryRoot(), role);
        var config = Path.Combine(protectedRoot, "user-config"); Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "settings.json"), "malformed-secret-sentinel");
        var error = Assert.Throws<ToolUsageException>(() => ClaudeReviewerUserSettings.Capture(original, config, protectedRoot));
        Assert.Contains("overlap", error.Message); Assert.DoesNotContain("secret-sentinel", error.ToString());
        Assert.False(original.HasExplicitUnknown);
    }
    [Fact]
    public void Source_nested_inside_configuration_namespace_also_refuses_symmetrically()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var config = DirectoryRoot();
        Assert.Throws<ToolUsageException>(() => ClaudeReviewerUserSettings.Capture(original, config, Path.Combine(config, "worker")));
        Assert.False(original.HasExplicitUnknown);
    }
    [Fact]
    public void Expired_original_cannot_recheck_files_with_a_fresh_allowance()
    {
        using var stopped = new CancellationTokenSource();
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), stopped.Token);
        var selected = ClaudeReviewerUserSettings.Capture(original, DirectoryRoot()); stopped.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(selected.Check); Assert.False(original.HasExplicitUnknown);
    }
}
