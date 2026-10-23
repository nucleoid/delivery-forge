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

    [Fact]
    public void Owner_trust_cannot_reuse_only_the_five_legacy_conformance_cases()
    {
        var names = new[] { "actual_launch_assignment", "actual_completion_commit", "actual_stop_same_original", "foreign_completion_refused", "malformed_protocol_refused" };
        var cases = names.Select(name => new InstalledHostCase(name, "PASS", "fixture-artifact")).ToArray();
        var artifacts = new HashSet<string>(StringComparer.Ordinal) { "fixture-artifact" };
        InstalledHostAdmission.RequireConformanceCases(cases, artifacts, false);
        Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireConformanceCases(cases, artifacts, true));
        var selected = cases.Append(new InstalledHostCase("actual_runtime_trust_route", "PASS", "actual-route-artifact")).ToArray();
        Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireConformanceCases(selected, artifacts, true));
        artifacts.Add("actual-route-artifact");
        // DATA admission only; no actual conformance is performed by this fixture.
        InstalledHostAdmission.RequireConformanceCases(selected, artifacts, true);
        Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireConformanceCases(selected, artifacts, false));
    }
    [Fact]
    public void Shared_original_capacity_admission_accounts_actual_prior_file_work_without_refunding_it()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var file = Path.Combine(Path.GetTempPath(), "forge-trust-credit-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(file, [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, BoundedFiles.Read(file, life)); // two existing operations, four read bytes
        Assert.Throws<ToolUsageException>(() => BoundedFiles.RequireReadCapacity(life, 4095, 0));
        Assert.Throws<ToolUsageException>(() => BoundedFiles.RequireReadCapacity(life, 0, 1073741821));
        BoundedFiles.RequireReadCapacity(life, 4094, 1073741820);
        BoundedFiles.RequireReadCapacity(life, 4094, 1073741820); // admission neither consumes nor refunds
        Assert.Equal(new byte[] { 1, 2, 3 }, BoundedFiles.Read(file, life));
        Assert.Throws<ToolUsageException>(() => BoundedFiles.RequireReadCapacity(life, 4094, 0));
        Assert.True(File.Exists(file));
    }
    [Fact]
    public void Cancelled_original_wins_before_capacity_metadata()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), cancel.Token);
        Assert.Throws<OperationCanceledException>(() => BoundedFiles.RequireReadCapacity(life, int.MaxValue, long.MaxValue));
    }
    static (InstalledHostRootAcceptance Host, TrustedConfiguration Config, AdmissionFile Root, AdmissionFile ConfigPin) DeclaredTrust(int hostFiles)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "forge-declared-trust-" + Guid.NewGuid().ToString("N")));
        var hash = new string('a', 64);
        AdmissionFile Pin(string leaf, long bytes) => new(Path.Combine(root, leaf), bytes, hash);
        var host = new InstalledHostRootAcceptance("RootActualInstalledHostConformanceAcceptance-v1", "windows", "codex.cli", "observed",
            new(Path.Combine(root, "codex.exe"), 100, hash, "observed"), Pin("tool.dll", 100), [Pin("artifact.json", 100)], [],
            new("Issue17OwnerTrustedPersonalHost-v1", Pin("owner.json", 100),
                Enumerable.Range(0, hostFiles).Select(i => new Issue17HostInput(Path.Combine(root, "input" + i), 100, hash)).ToArray()));
        var review = new InstalledReviewSelection("policy.json", "evidence.json", "fixture", Pin("git.exe", 100), "observed", Path.Combine(root, "review"),
            IndependentReviewer: new(Pin("claude.exe", 100), "observed"));
        var config = new TrustedConfiguration("fixture/repo", Path.Combine(root, "repo"), "main", Path.Combine(root, "git.exe"), InstalledReview: review);
        return (host, config, Pin("root.json", 100), Pin("config.json", 100));
    }
    [Fact]
    public void Worst_case_declared_trust_batch_is_refused_without_opening_any_declared_input()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var selected = DeclaredTrust(16);
        var plan = InstalledHostAdmission.RuntimeTrust.ReadCapacity(selected.Host, selected.Root, selected.ConfigPin, selected.Config);
        Assert.True(plan.Operations > 2000);
        Assert.Throws<ToolUsageException>(() => BoundedFiles.RequireReadCapacity(life, checked(2 * plan.Operations), checked(2 * plan.Bytes)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(selected.Root.Path)));
        life.Check(); // Metadata rejection did not invent a cleanup or allocate a file owner.
    }

    static StandaloneHostSelection TraceHost => new("C:/selected/codex.exe", 123, new string('a', 64), "observed");
    static System.Text.Json.Nodes.JsonNode RouteTrace()
    {
        string[] methods = ["initialize", "config/read", "account/read", "thread/start", "config/read", "turn/start", "config/read"];
        return System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            Kind = "ActualCodexAppServerTrace-v1",
            Host = TraceHost,
            Version = TraceHost.Version,
            TrustRouteConformanceOnly = true,
            RuntimeCertified = false,
            Events = methods.Select((method, index) => new
            {
                TrustRpcMethod = method,
                Message = new { id = index + 1, result = new { routeStatus = "owner-trusted-no-known-override" } }
            }).ToArray()
        }, BoundedFiles.Json)!;
    }
    [Fact]
    public void Exact_route_trace_structure_is_data_only_not_native_conformance()
    {
        var raw = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(RouteTrace(), BoundedFiles.Json);
        InstalledHostAdmission.RequireRuntimeTrustRouteArtifact(raw, TraceHost, TraceHost.Version);
    }
    [Theory]
    [InlineData("kind")]
    [InlineData("probe")]
    [InlineData("authority")]
    [InlineData("version")]
    [InlineData("host-path")]
    [InlineData("host-size")]
    [InlineData("host-hash")]
    [InlineData("host-version")]
    [InlineData("method")]
    [InlineData("id")]
    [InlineData("missing")]
    [InlineData("route")]
    [InlineData("error")]
    public void Relabelled_or_wrong_image_route_artifacts_are_refused(string mutation)
    {
        var trace = RouteTrace();
        switch (mutation)
        {
            case "kind": trace["Kind"] = "ActualLegacyCompletion-v1"; break;
            case "probe": trace["TrustRouteConformanceOnly"] = false; break;
            case "authority": trace["RuntimeCertified"] = true; break;
            case "version": trace["Version"] = "other"; break;
            case "host-path": trace["Host"]!["Executable"] = "C:/other/codex.exe"; break;
            case "host-size": trace["Host"]!["Bytes"] = 124; break;
            case "host-hash": trace["Host"]!["Sha256"] = new string('b', 64); break;
            case "host-version": trace["Host"]!["Version"] = "other"; break;
            case "method": trace["Events"]![1]!["TrustRpcMethod"] = "account/read"; break;
            case "id": trace["Events"]![6]!["Message"]!["id"] = 6; break;
            case "missing": trace["Events"]!.AsArray().RemoveAt(6); break;
            case "route": trace["Events"]![4]!["Message"]!["result"]!["routeStatus"] = "unknown"; break;
            case "error": trace["Events"]![0]!["Message"]!["error"] = new System.Text.Json.Nodes.JsonObject(); break;
            default: throw new ArgumentException("Unknown DATA mutation.", nameof(mutation));
        }
        var raw = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(trace, BoundedFiles.Json);
        Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireRuntimeTrustRouteArtifact(raw, TraceHost, TraceHost.Version));
    }
    [Fact]
    public void Sixth_case_cannot_reuse_a_legacy_completion_artifact()
    {
        string[] names = ["actual_launch_assignment", "actual_completion_commit", "actual_stop_same_original", "foreign_completion_refused", "malformed_protocol_refused", "actual_runtime_trust_route"];
        var rows = names.Select(name => new InstalledHostCase(name, "PASS", "same-artifact")).ToArray();
        Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireConformanceCases(rows, new HashSet<string> { "same-artifact" }, true));
    }
    [Theory]
    [InlineData("no-reviewer")]
    [InlineData("other-worker")]
    [InlineData("linux")]
    public void Invalid_trust_node_profile_is_refused_without_opening_selected_files(string mismatch)
    {
        var selected = DeclaredTrust(2);
        var config = mismatch switch
        {
            "no-reviewer" => selected.Config with { InstalledReview = selected.Config.InstalledReview! with { IndependentReviewer = null } },
            "other-worker" => selected.Config with { Adapter = "claude-code.cli" },
            _ => selected.Config
        };
        Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RuntimeTrust.RequireSelectedProfile(selected.Host, config, mismatch == "linux" ? "linux" : "windows"));
        var host = mismatch == "linux" ? selected.Host with { Platform = "linux" } : selected.Host;
        Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RuntimeTrust.ReadCapacity(host, selected.Root, selected.ConfigPin, config));
        Assert.False(Directory.Exists(Path.GetDirectoryName(selected.Root.Path)));
        // Default roots retain their existing profile admission, without trust.
        InstalledHostAdmission.RuntimeTrust.RequireSelectedProfile(selected.Host with { RuntimeTrust = null }, config, "linux");
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"Kind\":\"first\",\"Kind\":\"second\"}")]
    [InlineData("[]")]
    public void Malformed_route_artifact_is_a_fixed_message_admission_refusal(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var failure = Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireRuntimeTrustRouteArtifact(bytes, TraceHost, TraceHost.Version));
        Assert.Equal("Malformed actual runtime-trust trace.", failure.Message);
        Assert.Null(failure.InnerException); // No raw malformed payload/parser text retained.
    }
}
