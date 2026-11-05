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
    static readonly string SyntheticRuntime = OperatingSystem.IsLinux() ? IndependentLinuxRuntimeDirectory() : "/run/user/1000";
    static string IndependentLinuxRuntimeDirectory()
    {
        // Kernel status lists real, effective, saved and filesystem UIDs.
        // Keep the expectation independent of the production libc import.
        var fields = File.ReadLines("/proc/self/status").Single(line => line.StartsWith("Uid:", StringComparison.Ordinal))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, fields.Length);
        return "/run/user/" + uint.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    static string SyntheticOsValue(string name)
    {
        return name switch
        {
            "XDG_RUNTIME_DIR" => SyntheticRuntime,
            "DBUS_SESSION_BUS_ADDRESS" => "unix:path=" + SyntheticRuntime + "/bus",
            _ => "synthetic-os-value"
        };
    }
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
            ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(SyntheticOsValue), StringComparer.Ordinal);
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
    [InlineData("/run/user/1000", "unix:path=/run/user/1000/bus", true)]
    [InlineData("/run/user/1000", null, true)]
    [InlineData(null, null, false)]
    [InlineData(null, "unix:path=/run/user/1000/bus", false)]
    [InlineData("", "unix:path=/run/user/1000/bus", false)]
    [InlineData("run/user/1000", "unix:path=/run/user/1000/bus", false)]
    [InlineData("/run/user/1001", "unix:path=/run/user/1001/bus", false)]
    [InlineData("/tmp/runtime", "unix:path=/tmp/runtime/bus", false)]
    [InlineData("/run/user/1000", "", false)]
    [InlineData("/run/user/1000", "unixexec:path=/secret-sentinel", false)]
    [InlineData("/run/user/1000", "tcp:host=secret-sentinel", false)]
    [InlineData("/run/user/1000", "x-machine-unix:machine=secret-sentinel", false)]
    [InlineData("/run/user/1000", "unix:path=/run/user/1001/bus", false)]
    [InlineData("/run/user/1000", "unix:path=/run/user/1000/bus;unixexec:path=/secret-sentinel", false)]
    public void Linux_user_bus_selector_refuses_nonstandard_or_ambiguous_endpoints(string? runtime, string? address, bool accepted)
    {
        var selected = new Dictionary<string, string>();
        if (runtime is not null) selected.Add("XDG_RUNTIME_DIR", runtime);
        if (address is not null) selected.Add("DBUS_SESSION_BUS_ADDRESS", address);
        if (accepted) ClaudeIndependentReviewData.RequireLinuxUserBusEnvironment(selected, "/run/user/1000");
        else
        {
            var failure = Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequireLinuxUserBusEnvironment(selected, "/run/user/1000"));
            Assert.DoesNotContain("secret-sentinel", failure.ToString()); Assert.Null(failure.InnerException);
        }
    }
    [Theory]
    [InlineData("XDG_RUNTIME_DIR", true)]
    [InlineData("XDG_RUNTIME_DIR", false)]
    [InlineData("DBUS_SESSION_BUS_ADDRESS", true)]
    [InlineData("DBUS_SESSION_BUS_ADDRESS", false)]
    public void Linux_bus_capture_is_complete_and_refuses_each_prebirth_drift(string name, bool missing)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Actual Linux environment capture branch only.");
        var selected = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(SyntheticOsValue);
        ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(selected, SyntheticOsValue);
        var invocation = ClaudeIndependentReviewData.PinnedSettingsInvocation("fixture-image", ["--version"], "fixture-cwd", selected);
        var child = new ProcessStartInfo(); CommandEnvironment.Complete(child, invocation);
        Assert.Equal(SyntheticOsValue("XDG_RUNTIME_DIR"), child.Environment["XDG_RUNTIME_DIR"]);
        Assert.Equal(SyntheticOsValue("DBUS_SESSION_BUS_ADDRESS"), child.Environment["DBUS_SESSION_BUS_ADDRESS"]);
        var failure = Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(selected,
            key => key == name ? missing ? null : "secret-sentinel-drift" : SyntheticOsValue(key)));
        Assert.DoesNotContain("secret-sentinel", failure.ToString());
    }
    [Fact]
    public void Linux_bus_runtime_uses_the_actual_effective_kernel_uid()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Actual Linux effective UID only.");
        Assert.Equal(IndependentLinuxRuntimeDirectory(), ClaudeIndependentReviewData.LinuxUserRuntimeDirectory());
    }
    [Theory]
    [InlineData("exec")]
    [InlineData("tcp")]
    [InlineData("wrong-user")]
    [InlineData("missing-runtime")]
    public void Linux_actual_environment_capture_refuses_unsafe_bus_inputs(string fault)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Actual Linux environment capture branch only.");
        string? Read(string name) => (fault, name) switch
        {
            ("exec", "DBUS_SESSION_BUS_ADDRESS") => "unixexec:path=/secret-sentinel",
            ("tcp", "DBUS_SESSION_BUS_ADDRESS") => "tcp:host=secret-sentinel",
            ("wrong-user", "XDG_RUNTIME_DIR") => "/run/user/" +
                (ulong.Parse(SyntheticRuntime[10..], System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ("missing-runtime", "XDG_RUNTIME_DIR") => null,
            _ => SyntheticOsValue(name)
        };
        var failure = Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(Read));
        Assert.DoesNotContain("secret-sentinel", failure.ToString()); Assert.Null(failure.InnerException);
    }
    [Fact]
    public void Linux_actual_environment_capture_keeps_absent_bus_address_and_refuses_its_later_appearance()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Actual Linux environment capture branch only.");
        string? Read(string name) => name == "DBUS_SESSION_BUS_ADDRESS" ? null : SyntheticOsValue(name);
        var selected = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(Read);
        Assert.False(selected.ContainsKey("DBUS_SESSION_BUS_ADDRESS"));
        ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(selected, Read);
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(selected, SyntheticOsValue));
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
        var selected = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(SyntheticOsValue);
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
        var selected = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(SyntheticOsValue);
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
        var selected = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(SyntheticOsValue);
        ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(selected, SyntheticOsValue);
        var error = Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(selected, _ => "secret-sentinel-drift"));
        Assert.DoesNotContain("secret-sentinel", error.ToString());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("auth")]
    [InlineData("review")]
    public void Owner_child_environment_carries_both_strict_Linux_server_flags(string purpose)
    {
        var selected = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(SyntheticOsValue);
        var arguments = purpose == "review" ? ClaudeIndependentReviewData.RestrictedReviewArguments(5)
            : ClaudeIndependentReviewData.RestrictedObservationArguments(purpose == "auth" ? ["auth", "status"] : ["--version"]);
        var invocation = ClaudeIndependentReviewData.PinnedSettingsInvocation("fixture-image", arguments, "fixture-cwd", selected);
        // Apply the actual complete environment without creating a child. These
        // are the exact two required values in LinuxCommandScope.Prepare.
        var child = new ProcessStartInfo();
        CommandEnvironment.Complete(child, invocation);
        Assert.False(invocation.InheritEnvironment); Assert.True(invocation.EnvironmentIsComplete);
        Assert.True(child.Environment.TryGetValue("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", out var server));
        Assert.Equal("1", server);
        Assert.True(child.Environment.TryGetValue("MSBUILDDISABLENODEREUSE", out var reuse));
        Assert.Equal("1", reuse);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("auth")]
    [InlineData("review")]
    public void Linux_reviewer_child_uses_loaded_runtime_roots_and_ignores_ambient_roots(string purpose)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Actual Linux loaded runtime only.");
        // Derive the expectation from the loaded core assembly, independently
        // of production RuntimeEnvironment.GetRuntimeDirectory.
        var version = new FileInfo(typeof(object).Assembly.Location).Directory!;
        Assert.Equal("Microsoft.NETCore.App", version.Parent!.Name);
        Assert.Equal("shared", version.Parent.Parent!.Name);
        var expected = Path.TrimEndingDirectorySeparator(version.Parent.Parent.Parent!.FullName);
        var rootReads = 0;
        string? Read(string name)
        {
            if (name is "DOTNET_ROOT" or "DOTNET_ROOT_X64") { rootReads++; return "/secret-sentinel-runtime"; }
            return SyntheticOsValue(name);
        }
        var selected = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(Read);
        ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(selected, Read);
        var arguments = purpose == "review" ? ClaudeIndependentReviewData.RestrictedReviewArguments(5)
            : ClaudeIndependentReviewData.RestrictedObservationArguments(purpose == "auth" ? ["auth", "status"] : ["--version"]);
        var invocation = ClaudeIndependentReviewData.PinnedSettingsInvocation("fixture-image", arguments, "fixture-cwd", selected);
        var child = new ProcessStartInfo(); child.Environment["DOTNET_ROOT"] = "/secret-sentinel-runtime";
        child.Environment["DOTNET_ROOT_X64"] = "/secret-sentinel-runtime";
        CommandEnvironment.Complete(child, invocation);
        Assert.Equal(0, rootReads);
        Assert.Equal(expected, selected["DOTNET_ROOT"]); Assert.Equal(expected, selected["DOTNET_ROOT_X64"]);
        Assert.Equal(expected, child.Environment["DOTNET_ROOT"]); Assert.Equal(expected, child.Environment["DOTNET_ROOT_X64"]);
        Assert.Equal(selected.Count, child.Environment.Count);
        Assert.False(invocation.InheritEnvironment); Assert.True(invocation.EnvironmentIsComplete);
    }
    [Theory]
    [InlineData("DOTNET_ROOT", true)]
    [InlineData("DOTNET_ROOT", false)]
    [InlineData("DOTNET_ROOT_X64", true)]
    [InlineData("DOTNET_ROOT_X64", false)]
    public void Linux_reviewer_child_refuses_missing_or_changed_loaded_runtime_roots(string name, bool missing)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Actual Linux loaded runtime only.");
        var selected = new Dictionary<string, string>(
            ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(SyntheticOsValue), StringComparer.Ordinal);
        if (missing) selected.Remove(name); else selected[name] = "/secret-sentinel-runtime";
        var error = Assert.Throws<ToolUsageException>(() =>
            ClaudeIndependentReviewData.PinnedSettingsInvocation("fixture-image", ["--version"], "fixture-cwd", selected));
        Assert.DoesNotContain("secret-sentinel", error.ToString()); Assert.Null(error.InnerException);
    }
    [Fact]
    public void Windows_reviewer_child_still_excludes_runtime_root_overrides()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Unchanged Windows environment branch only.");
        var selected = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(SyntheticOsValue);
        Assert.False(selected.ContainsKey("DOTNET_ROOT")); Assert.False(selected.ContainsKey("DOTNET_ROOT_X64"));
        var changed = new Dictionary<string, string>(selected, StringComparer.Ordinal) { ["DOTNET_ROOT"] = "secret-sentinel-runtime" };
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.RequirePinnedSettingsReviewEnvironment(changed));
    }
    [Theory]
    [InlineData("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", true)]
    [InlineData("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", false)]
    [InlineData("MSBUILDDISABLENODEREUSE", true)]
    [InlineData("MSBUILDDISABLENODEREUSE", false)]
    public void Owner_child_environment_refuses_missing_or_changed_strict_Linux_server_flags(string name, bool missing)
    {
        var selected = new Dictionary<string, string>(
            ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(SyntheticOsValue), StringComparer.Ordinal);
        if (missing) selected.Remove(name); else selected[name] = "0";
        var error = Assert.Throws<ToolUsageException>(() =>
            ClaudeIndependentReviewData.PinnedSettingsInvocation("fixture-image", ["--version"], "fixture-cwd", selected));
        Assert.DoesNotContain(name, error.Message, StringComparison.Ordinal);
    }

}
