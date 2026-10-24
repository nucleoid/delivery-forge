using System.Text;
using System.Diagnostics;
using DeliveryForge.Evidence;
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
    [Fact]
    public void Selected_profile_parser_preserves_only_inert_customizations_without_granting_capture_authority()
    {
        var raw = Utf8("""{"env":{"ANTHROPIC_CUSTOM_MODEL_OPTION":"claude-synthetic[1m]","ANTHROPIC_CUSTOM_MODEL_OPTION_NAME":"Synthetic menu entry","ANTHROPIC_CUSTOM_MODEL_OPTION_DESCRIPTION":"Synthetic description","CLAUDE_AUTOCOMPACT_PCT_OVERRIDE":"50"},"permissions":{"allow":["Read(*)"],"ask":[],"defaultMode":"bypassPermissions"},"hooks":{"Stop":[]}}""");
        ClaudeReviewerUserSettings.RequireOwnerSelectedSettings(raw); // DATA parsing only.
        Assert.Throws<ToolUsageException>(() => ClaudeReviewerUserSettings.RequireSafeSettings(raw));
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = DirectoryRoot(); var path = Path.Combine(root, "settings.json"); File.WriteAllBytes(path, raw);
        Assert.Throws<ToolUsageException>(() => ClaudeReviewerUserSettings.Capture(original, root));
        Assert.Equal(raw, File.ReadAllBytes(path)); // Ordinary capture never gets a boolean bypass.
    }
    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("ANTHROPIC_BASE_URL")]
    [InlineData("ANTHROPIC_MODEL")]
    [InlineData("ANTHROPIC_DEFAULT_OPUS_MODEL")]
    [InlineData("CLAUDE_CODE_USE_BEDROCK")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("PATH")]
    [InlineData("unknown")]
    [InlineData("anthropic_custom_model_option")]
    public void Selected_profile_still_refuses_every_unlisted_environment_name_without_raw_values(string name)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(new { env = new Dictionary<string, string> { [name] = "secret-sentinel-value" } });
        var error = Assert.Throws<ToolUsageException>(() => ClaudeReviewerUserSettings.RequireOwnerSelectedSettings(raw));
        Assert.DoesNotContain("secret-sentinel", error.ToString()); Assert.Null(error.InnerException);
    }
    [Theory]
    [InlineData("apiKeyHelper")]
    [InlineData("APIKEYHELPER")]
    [InlineData("awsAuthRefresh")]
    [InlineData("awsCredentialExport")]
    [InlineData("otelHeadersHelper")]
    public void Selected_profile_never_accepts_credential_or_command_helpers(string name)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { [name] = "secret-sentinel-value" });
        var error = Assert.Throws<ToolUsageException>(() => ClaudeReviewerUserSettings.RequireOwnerSelectedSettings(raw));
        Assert.DoesNotContain("secret-sentinel", error.ToString()); Assert.Null(error.InnerException);
    }
    [Theory]
    [InlineData("{\"env\":{\"CLAUDE_AUTOCOMPACT_PCT_OVERRIDE\":\"101\"}}")]
    [InlineData("{\"env\":{\"CLAUDE_AUTOCOMPACT_PCT_OVERRIDE\":\"0\"}}")]
    [InlineData("{\"env\":{\"CLAUDE_AUTOCOMPACT_PCT_OVERRIDE\":\" 50\"}}")]
    [InlineData("{\"env\":{\"ANTHROPIC_CUSTOM_MODEL_OPTION\":\"https://secret-sentinel.invalid\"}}")]
    [InlineData("{\"env\":{\"ANTHROPIC_CUSTOM_MODEL_OPTION_NAME\":17}}")]
    [InlineData("{\"env\":{\"ANTHROPIC_CUSTOM_MODEL_OPTION_NAME\":\"one\",\"ANTHROPIC_CUSTOM_MODEL_OPTION_NAME\":\"secret-sentinel\"}}")]
    [InlineData("{\"Env\":{}}")]
    [InlineData("{\"hooks\":[]}")]
    [InlineData("{\"permissions\":{\"additionalDirectories\":[\"secret-sentinel\"]}}")]
    public void Selected_profile_refuses_invalid_or_ambiguous_inert_data(string raw)
    {
        var error = Assert.Throws<ToolUsageException>(() => ClaudeReviewerUserSettings.RequireOwnerSelectedSettings(Utf8(raw)));
        Assert.DoesNotContain("secret-sentinel", error.ToString()); Assert.Null(error.InnerException);
    }
    [Theory]
    [InlineData("safe")]
    [InlineData("tools")]
    [InlineData("permission")]
    [InlineData("model")]
    [InlineData("settings")]
    [InlineData("mcp")]
    [InlineData("hooks")]
    [InlineData("default-mode")]
    [InlineData("inline-missing")]
    [InlineData("restricted")]
    [InlineData("setting-sources")]
    public void Pinned_profile_argv_regression_refuses_before_any_native_invocation(string mutation)
    {
        var actual = ClaudeIndependentReviewData.RestrictedReviewArguments(5).ToList();
        switch (mutation)
        {
            case "safe": actual.Remove("--safe-mode"); break;
            case "restricted": actual.Remove("--restricted"); break;
            case "setting-sources": actual.AddRange(["--setting-sources", "user"]); break;
            case "tools": actual[actual.IndexOf("--tools") + 1] = "default"; break;
            case "permission": actual[actual.IndexOf("--permission-mode") + 1] = "bypassPermissions"; break;
            case "model": actual.AddRange(["--model", "other"]); break;
            case "settings": actual.AddRange(["--settings", "other.json"]); break;
            case "mcp": actual.Remove("--strict-mcp-config"); break;
            case "hooks": actual[actual.IndexOf("--settings") + 1] = "{\"disableAllHooks\":false,\"permissions\":{\"defaultMode\":\"dontAsk\"}}"; break;
            case "default-mode": actual[actual.IndexOf("--settings") + 1] = "{\"disableAllHooks\":true,\"permissions\":{\"defaultMode\":\"bypassPermissions\"}}"; break;
            case "inline-missing": var at = actual.IndexOf("--settings"); actual.RemoveAt(at); actual.RemoveAt(at); break;
            default: throw new ArgumentException("Unknown mutation.");
        }
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequirePinnedSettingsIsolation(actual, observation: false));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pinned_profile_readonly_observation_keeps_exact_safe_route(bool auth)
    {
        var actual = ClaudeIndependentReviewData.RestrictedObservationArguments(auth ? ["auth", "status"] : ["--version"]);
        ClaudeIndependentReviewData.RequirePinnedSettingsIsolation(actual, observation: true);
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequirePinnedSettingsIsolation(actual.Skip(1).ToArray(), observation: true));
        ClaudeIndependentReviewData.RequirePinnedSettingsIsolation(ClaudeIndependentReviewData.RestrictedReviewArguments(5), observation: false);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("resource")]
    public void Pinned_profile_actual_child_environment_refuses_missing_or_tampered_restrictions(string mutation)
    {
        var environment = new Dictionary<string, string>(
            ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(_ => "synthetic-os-value"), StringComparer.Ordinal);
        ClaudeIndependentReviewData.RequirePinnedSettingsReviewEnvironment(environment);
        switch (mutation)
        {
            case "missing": environment.Remove("DISABLE_COMPACT"); break;
            case "disabled": environment["DISABLE_COMPACT"] = "0"; break;
            case "provider": environment["ANTHROPIC_BASE_URL"] = "secret-sentinel-endpoint"; break;
            case "model": environment["ANTHROPIC_MODEL"] = "secret-sentinel-model"; break;
            case "resource": environment["DOTNET_PROCESSOR_COUNT"] = "3"; break;
            default: throw new ArgumentException("Unknown mutation.");
        }
        var error = Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequirePinnedSettingsReviewEnvironment(environment));
        Assert.DoesNotContain("secret-sentinel", error.ToString()); Assert.Null(error.InnerException);
    }
    [Fact]
    public void Pinned_profile_review_cannot_omit_the_actual_environment()
    {
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequirePinnedSettingsReviewEnvironment(null));
    }
    [Theory]
    [InlineData("model")]
    [InlineData("MODEL")]
    [InlineData("fallbackModel")]
    [InlineData("outputStyle")]
    [InlineData("unknown")]
    [InlineData("StatusLine")]
    [InlineData("plugins")]
    public void Owner_profile_refuses_new_settings_and_model_selection_without_exporting_values(string name)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { [name] = "secret-sentinel-value" });
        var error = Assert.Throws<ToolUsageException>(() => ClaudeReviewerUserSettings.RequireOwnerSelectedSettings(raw));
        Assert.DoesNotContain("secret-sentinel", error.ToString()); Assert.Null(error.InnerException);
    }
    [Fact]
    public void Ordinary_capture_keeps_existing_user_model_source_instead_of_restricted_variant()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = DirectoryRoot(); File.WriteAllText(Path.Combine(root, "settings.json"), "{\"model\":\"configured-existing-default\"}");
        var captured = ClaudeReviewerUserSettings.Capture(original, root);
        Assert.Equal(ClaudeIndependentReviewData.IsolatedObservationArguments(["--version"]), captured.ObservationArguments(["--version"]));
        Assert.Equal(ClaudeIndependentReviewData.IsolatedReviewArguments(5), captured.ReviewArguments(5));
        Assert.DoesNotContain("--restricted", captured.ReviewArguments(5));
        var ordinary = new Dictionary<string, string> { ["ordinary-existing-key"] = "unchanged" };
        var invocation = captured.Invocation("fixture-image", captured.ReviewArguments(5), root, ordinary);
        Assert.True(invocation.InheritEnvironment); Assert.False(invocation.EnvironmentIsComplete);
        Assert.Same(ordinary, invocation.Environment);
        Assert.Equal("{\"model\":\"configured-existing-default\"}", File.ReadAllText(Path.Combine(root, "settings.json")));
    }
    [Fact]
    public void Owner_profile_closed_argv_excludes_user_source_bare_and_model_options()
    {
        var arguments = ClaudeIndependentReviewData.RestrictedReviewArguments(5);
        ClaudeIndependentReviewData.RequirePinnedSettingsIsolation(arguments, observation: false);
        Assert.Contains("--restricted", arguments); Assert.DoesNotContain("--setting-sources", arguments);
        Assert.DoesNotContain("--bare", arguments); Assert.DoesNotContain("--model", arguments); Assert.DoesNotContain("--fallback-model", arguments);
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequirePinnedSettingsIsolation(ClaudeIndependentReviewData.IsolatedReviewArguments(5), observation: false));
    }
    [Theory]
    [InlineData("version")]
    [InlineData("auth")]
    [InlineData("review")]
    public void Owner_observations_and_review_use_complete_actual_child_block_without_ambient_injection(string purpose)
    {
        var selected = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(_ => "synthetic-os-value");
        var arguments = purpose == "review" ? ClaudeIndependentReviewData.RestrictedReviewArguments(5)
            : ClaudeIndependentReviewData.RestrictedObservationArguments(purpose == "auth" ? ["auth", "status"] : ["--version"]);
        var invocation = ClaudeIndependentReviewData.PinnedSettingsInvocation("fixture-image", arguments, "fixture-cwd", selected);
        ClaudeIndependentReviewData.RequirePinnedSettingsIsolation(invocation.Arguments, purpose != "review");
        ClaudeIndependentReviewData.RequirePinnedSettingsInvocation(invocation, selected);
        Assert.False(invocation.InheritEnvironment); Assert.True(invocation.EnvironmentIsComplete);
        Assert.Same(selected, invocation.Environment);
        // Exercise the real environment application on an unstarted child.
        // No RuntimeTrust factory, global variable mutation or CLI is involved.
        var child = new ProcessStartInfo(); child.Environment.Clear();
        string[] ambient = ["ANTHROPIC_MODEL", "ANTHROPIC_CUSTOM_HEADERS", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_SUBAGENT_MODEL", "NODE_OPTIONS", "BUN_OPTIONS", "HTTPS_PROXY", "MAX_THINKING_TOKENS", "CLAUDE_CONFIG_DIR", "PATH"];
        foreach (var name in ambient) child.Environment[name] = "synthetic-must-not-reach-child";
        CommandEnvironment.Complete(child, invocation);
        Assert.Equal(selected.Count, child.Environment.Count);
        foreach (var row in selected) Assert.Equal(row.Value, child.Environment[row.Key]);
        foreach (var name in ambient) Assert.False(child.Environment.ContainsKey(name));
        Assert.Equal("2", child.Environment["DOTNET_PROCESSOR_COUNT"]);
        Assert.Equal("1", child.Environment["DISABLE_COMPACT"]);
        var mutable = Assert.IsAssignableFrom<IDictionary<string, string>>(selected);
        Assert.Throws<NotSupportedException>(() => mutable.Add("ANTHROPIC_MODEL", "synthetic"));
    }
    [Theory]
    [InlineData("inherit")]
    [InlineData("incomplete")]
    [InlineData("replacement")]
    public void Owner_actual_invocation_refuses_inheritance_flags_and_rebuilt_environment(string fault)
    {
        var selected = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(_ => "synthetic-os-value");
        var actual = ClaudeIndependentReviewData.PinnedSettingsInvocation("fixture-image", ["--version"], "fixture-cwd", selected);
        actual = fault switch
        {
            "inherit" => actual with { InheritEnvironment = true },
            "incomplete" => actual with { EnvironmentIsComplete = false },
            _ => actual with { Environment = new Dictionary<string, string>(selected, StringComparer.Ordinal) }
        };
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequirePinnedSettingsInvocation(actual, selected));
    }
    [Fact]
    public void Captured_owner_OS_values_cannot_drift_between_observations_and_review_birth()
    {
        var selected = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(_ => "synthetic-os-value");
        ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(selected, _ => "synthetic-os-value");
        var error = Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(selected, _ => "secret-sentinel-drift"));
        Assert.DoesNotContain("secret-sentinel", error.ToString());
    }

}
