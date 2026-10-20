using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence.Tests;

public sealed class DeterministicEvaluationPluginTests
{
    [Fact]
    public void Builtins_are_registered_by_stable_identity_and_legacy_adapter()
    {
        var registry = DeterministicEvaluationPluginRegistry.CreateBuiltIns();

        Assert.Equal("delivery-forge.dotnet", registry.Resolve(EvidenceAdapterKind.DotNet, null).Id);
        Assert.Equal("delivery-forge.crap4csharp", registry.Resolve(EvidenceAdapterKind.Crap4CSharp, null).Id);
        Assert.Equal("delivery-forge.mutate4csharp", registry.Resolve(EvidenceAdapterKind.Mutate4CSharp, null).Id);
        Assert.Equal("dmmulroy.anti-slop", registry.Resolve(
            EvidenceAdapterKind.External,
            new EvaluationPluginSelection("dmmulroy.anti-slop", Enabled: true)).Id);
    }

    [Fact]
    public void Unknown_or_legacy_mismatched_plugin_is_rejected()
    {
        var registry = DeterministicEvaluationPluginRegistry.CreateBuiltIns();

        Assert.Throws<EvaluationPluginException>(() => registry.Resolve(
            EvidenceAdapterKind.External,
            new EvaluationPluginSelection("not.registered", Enabled: true)));
        Assert.Throws<EvaluationPluginException>(() => registry.Resolve(
            EvidenceAdapterKind.DotNet,
            new EvaluationPluginSelection("dmmulroy.anti-slop", Enabled: true)));
    }

    [Fact]
    public async Task Disabled_plugin_is_distinct_and_cannot_satisfy_a_required_gate()
    {
        using var temp = new TempDirectory();
        var original = TestEvidence.GateRequest(temp.Path);
        var request = original with
        {
            Plugin = new EvaluationPluginSelection("delivery-forge.dotnet", Enabled: false)
        };
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository()),
            TestEvidence.WorkingDirectory(temp.Path),
            TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Equal(EvaluationPluginStatus.Disabled, result.Plugin.Status);
        Assert.False(result.Plugin.Enabled);
        Assert.Equal("delivery-forge.dotnet", result.Plugin.Id);
        Assert.Contains("disabled", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a.js")]
    [InlineData("a.jsx")]
    [InlineData("a.ts")]
    [InlineData("a.tsx")]
    [InlineData("a.mjs")]
    [InlineData("a.cjs")]
    [InlineData("a.mts")]
    [InlineData("a.cts")]
    public void Anti_slop_supports_all_javascript_family_extensions(string path)
    {
        Assert.True(AntiSlopEvaluationPlugin.IsSupportedInput(path));
    }

    [Fact]
    public void Anti_slop_empty_analysis_never_passes()
    {
        var plugin = AntiSlopEvaluationPlugin.CreateBundled();
        var noInputs = plugin.NormalizeReport("[]"u8, analyzedInputs: [], processExitCode: 0);
        var noAnalyzedFiles = plugin.NormalizeReport(
            "{\"diagnostics\":[],\"number_of_files\":0,\"number_of_rules\":115}"u8,
            analyzedInputs: ["clean.ts"],
            processExitCode: 0);

        Assert.Equal(GateOutcome.Incomplete, noInputs.Outcome);
        Assert.Contains("zero", noInputs.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GateOutcome.Incomplete, noAnalyzedFiles.Outcome);
        Assert.Contains("complete frozen input set", noAnalyzedFiles.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anti_slop_rejects_autofix_before_execution_and_binds_config_bytes()
    {
        using var temp = new TempDirectory();
        var (config, nodeDirectory) = CreateFixtureAntiSlopClosure(temp.Path);
        var original = TestEvidence.GateRequest(temp.Path);
        var executableIdentity = $"sha256:{Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(
                original.Invocation.FileName, TestContext.Current.CancellationToken)))}";
        var canonicalInvocation = original.Invocation with
        {
            Arguments = ["--config", config, "--format", "json", "src/app.ts"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = nodeDirectory
            }
        };
        var configIdentity = AntiSlopEvaluationPlugin.ComputeConfigurationIdentity(
            canonicalInvocation,
            effectRulesEnabled: false);
        var request = original with
        {
            Capability = ToolCapability.FromDetection(
                "oxlint", "1.78.0", "oxlint-json-v1", false, [],
                original.Invocation.FileName, executableIdentity, ["lint"],
                original.Invocation.WorkingDirectory),
            Adapter = EvidenceAdapterKind.External,
            ConfigurationIdentity = configIdentity,
            Invocation = canonicalInvocation with
            {
                Arguments = ["--fix", "--config", config, "--format", "json", "src/app.ts"]
            },
            Plugin = new EvaluationPluginSelection(
                "dmmulroy.anti-slop", Enabled: true, Inputs: ["src/app.ts"])
        };
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository()),
            request.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("autofix", result.Reason, StringComparison.OrdinalIgnoreCase);

        var plugin = AntiSlopEvaluationPlugin.CreateBundled();
        var canonical = request with
        {
            Invocation = canonicalInvocation
        };
        Assert.Null(plugin.ValidateBeforeExecution(canonical));
        var poisonedEnvironment = new Dictionary<string, string>(canonical.Invocation.Environment!, StringComparer.Ordinal)
        {
            ["NODE_OPTIONS"] = "--require untrusted.js"
        };
        var poisoned = plugin.ValidateBeforeExecution(canonical with
        {
            Invocation = canonical.Invocation with { Environment = poisonedEnvironment }
        });
        Assert.Equal(GateOutcome.Incomplete, poisoned?.Outcome);
        Assert.Contains("NODE_OPTIONS", poisoned?.Reason, StringComparison.Ordinal);
        var mismatched = plugin.ValidateBeforeExecution(canonical with
        {
            ConfigurationIdentity = TestEvidence.Identity('9')
        });
        Assert.Equal(GateOutcome.Incomplete, mismatched?.Outcome);
        Assert.Contains("configuration closure", mismatched?.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Anti_slop_revalidates_the_configuration_and_runtime_closure_after_execution()
    {
        using var temp = new TempDirectory();
        var (config, nodeDirectory) = CreateFixtureAntiSlopClosure(temp.Path);
        var original = TestEvidence.GateRequest(temp.Path);
        var invocation = original.Invocation with
        {
            Arguments = ["--config", config, "--format", "json", "src/app.ts"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = nodeDirectory }
        };
        var request = original with
        {
            Adapter = EvidenceAdapterKind.External,
            ConfigurationIdentity = AntiSlopEvaluationPlugin.ComputeConfigurationIdentity(invocation, false),
            Invocation = invocation,
            Plugin = new EvaluationPluginSelection(
                "dmmulroy.anti-slop", Enabled: true, Inputs: ["src/app.ts"])
        };
        var plugin = AntiSlopEvaluationPlugin.CreateBundled();
        var snapshot = plugin.CaptureBefore(request);
        File.AppendAllText(
            System.IO.Path.Combine(temp.Path, "anti-slop", "vendor", "anti-slop", "index.ts"),
            "// changed");

        var result = plugin.ValidateAfterExecution(request, snapshot);

        Assert.Equal(GateOutcome.Error, result?.Outcome);
        Assert.Contains("changed", result?.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Anti_slop_preserves_clean_findings_and_parser_failure_outcomes()
    {
        var plugin = AntiSlopEvaluationPlugin.CreateBundled();

        var clean = plugin.NormalizeReport(
            "{\"diagnostics\":[],\"number_of_files\":1,\"number_of_rules\":115}"u8,
            ["clean.ts"],
            0);
        var violation = plugin.NormalizeReport(
            "{\"diagnostics\":[{\"code\":\"anti-slop(no-runtime-typeof)\",\"severity\":\"error\",\"filename\":\"bad.ts\"}],\"number_of_files\":1,\"number_of_rules\":115}"u8,
            ["bad.ts"],
            1);
        var parserFailure = plugin.NormalizeReport(
            "{\"diagnostics\":[{\"severity\":\"error\",\"filename\":\"broken.ts\"}],\"number_of_files\":1,\"number_of_rules\":115}"u8,
            ["broken.ts"],
            1);
        var missingFile = plugin.NormalizeReport(
            "{\"diagnostics\":[{\"code\":\"anti-slop(no-runtime-typeof)\",\"severity\":\"error\"}],\"number_of_files\":1,\"number_of_rules\":115}"u8,
            ["bad.ts"],
            1);
        var dotDirectory = plugin.NormalizeReport(
            "{\"diagnostics\":[{\"code\":\"anti-slop(no-runtime-typeof)\",\"severity\":\"error\",\"filename\":\".storybook/main.ts\"}],\"number_of_files\":1,\"number_of_rules\":115}"u8,
            [".storybook/main.ts"],
            1);
        var fakeEslintShape = plugin.NormalizeReport(
            "[{\"filename\":\"clean.ts\",\"messages\":[]}]"u8,
            ["clean.ts"],
            0);
        var malformed = plugin.NormalizeReport("not json"u8, ["broken.ts"], 1);

        Assert.Equal(GateOutcome.Pass, clean.Outcome);
        Assert.False(clean.ProductionCapable);
        Assert.Equal(GateOutcome.Fail, violation.Outcome);
        Assert.Equal(GateOutcome.Incomplete, parserFailure.Outcome);
        Assert.Equal(GateOutcome.Incomplete, missingFile.Outcome);
        Assert.Equal(GateOutcome.Fail, dotDirectory.Outcome);
        Assert.Equal(GateOutcome.Incomplete, fakeEslintShape.Outcome);
        Assert.Equal(GateOutcome.Incomplete, malformed.Outcome);
    }

    [Fact]
    public async Task Anti_slop_clean_pass_is_advisory_and_cannot_satisfy_a_protected_required_gate()
    {
        using var temp = new TempDirectory();
        var (config, nodeDirectory) = CreateFixtureAntiSlopClosure(temp.Path);
        var original = TestEvidence.GateRequest(temp.Path);
        var invocation = original.Invocation with
        {
            Arguments = ["--config", config, "--format", "json", "src/app.ts"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = nodeDirectory }
        };
        var executableIdentity = $"sha256:{Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(
                invocation.FileName, TestContext.Current.CancellationToken)))}";
        var request = original with
        {
            Adapter = EvidenceAdapterKind.External,
            Capability = ToolCapability.FromDetection(
                "oxlint", "1.78.0", "oxlint-json-v1", false, [], invocation.FileName,
                executableIdentity, ["lint"], invocation.WorkingDirectory),
            ConfigurationIdentity = AntiSlopEvaluationPlugin.ComputeConfigurationIdentity(invocation, false),
            Invocation = invocation,
            Plugin = new EvaluationPluginSelection(
                "dmmulroy.anti-slop", Enabled: true, Inputs: ["src/app.ts"])
        };
        var now = DateTimeOffset.UtcNow;
        var runner = new GateRunner(
            new StubCommandExecutor(new CommandResult(
                0,
                "{\"diagnostics\":[],\"number_of_files\":1,\"number_of_rules\":115}",
                string.Empty,
                false,
                false,
                now,
                now)),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            request.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Equal("PASS", result.Plugin.ToolOutcome);
        Assert.Contains("advisory", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plugin_inputs_reject_option_like_first_segments()
    {
        Assert.Throws<EvaluationPluginException>(() =>
            AntiSlopEvaluationPlugin.DetectSupportedInputs(["--config.ts"]));
    }

    [Fact]
    public void Anti_slop_detects_javascript_family_inputs_in_a_mixed_language_scope_and_respects_ignores()
    {
        var registry = DeterministicEvaluationPluginRegistry.CreateBuiltIns();
        var selections = registry.SelectApplicable(
            [new EvaluationPluginConfiguration(
                "dmmulroy.anti-slop", Enabled: true, EnableEffectRules: true)],
            ["src/Z.cs", "src/a.ts", "src/b.mjs", "src/c.cs", "src/ignored.jsx"],
            new HashSet<string>(["src/ignored.jsx"], StringComparer.Ordinal));

        var selection = Assert.Single(selections);
        Assert.Equal("dmmulroy.anti-slop", selection.Id);
        Assert.True(selection.Enabled);
        Assert.True(selection.EnableEffectRules);
        Assert.Equal(["src/a.ts", "src/b.mjs"], selection.Inputs);
    }

    [Fact]
    public void Automatic_selection_retains_explicit_disabled_metadata_for_detected_inputs()
    {
        var registry = DeterministicEvaluationPluginRegistry.CreateBuiltIns();

        var selection = Assert.Single(registry.SelectApplicable(
            [new EvaluationPluginConfiguration("dmmulroy.anti-slop", Enabled: false)],
            ["src/app.ts", "src/app.cs"]));

        Assert.False(selection.Enabled);
        Assert.Equal(["src/app.ts"], selection.Inputs);
    }

    [Fact]
    public async Task Receipt_command_envelope_binds_plugin_version_config_inputs_and_tool_outcome()
    {
        using var temp = new TempDirectory();
        var original = TestEvidence.GateRequest(temp.Path);
        var request = original with
        {
            Plugin = new EvaluationPluginSelection(
                "delivery-forge.dotnet", Enabled: true, Inputs: ["DeliveryForge.Tests.csproj"])
        };
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            TestEvidence.WorkingDirectory(temp.Path),
            TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(EvaluationPluginStatus.Executed, result.Plugin.Status);
        Assert.Equal("delivery-forge.dotnet", result.Plugin.Id);
        Assert.Equal("1.0.0", result.Plugin.Version);
        Assert.Equal(TestEvidence.Identity('6'), result.Plugin.ConfigurationIdentity);
        Assert.Equal(["DeliveryForge.Tests.csproj"], result.Plugin.Inputs);
        Assert.StartsWith("sha256:", result.Plugin.InputSetIdentity);
        Assert.Equal("INCOMPLETE", result.Plugin.ToolOutcome);

        using var receipt = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(
            result.ReceiptPath, TestContext.Current.CancellationToken));
        using var command = System.Text.Json.JsonDocument.Parse(
            receipt.RootElement.GetProperty("command").GetString()!);
        var plugin = command.RootElement.GetProperty("plugin");
        Assert.Equal("delivery-forge.dotnet", plugin.GetProperty("id").GetString());
        Assert.Equal(result.Plugin.InputSetIdentity, plugin.GetProperty("inputSetIdentity").GetString());
        Assert.Equal(TestEvidence.Repository().HeadCommit, receipt.RootElement.GetProperty("headCommit").GetString());
    }

    [Fact]
    public void External_registration_is_code_owned_and_duplicate_ids_fail_closed()
    {
        var plugin = new StubEvaluationPlugin("example.custom", "1.2.3", "custom-tool");
        var registry = new DeterministicEvaluationPluginRegistry([plugin]);

        Assert.Same(plugin, registry.Resolve(
            EvidenceAdapterKind.External,
            new EvaluationPluginSelection("example.custom", Enabled: true)));
        Assert.Throws<EvaluationPluginException>(() =>
            new DeterministicEvaluationPluginRegistry([plugin, plugin]));
    }

    private sealed class StubEvaluationPlugin(string id, string version, string expectedTool)
        : IDeterministicEvaluationPlugin
    {
        public string Id => id;
        public string Version => version;
        public string ExpectedTool => expectedTool;
        public EvidenceAdapterKind CompatibilityAdapter => EvidenceAdapterKind.External;

        public EvaluationPluginSnapshot CaptureBefore(GateRequest request) => EvaluationPluginSnapshot.Empty;

        public NormalizedEvidence Normalize(
            GateRequest request,
            CommandResult execution,
            EvaluationPluginSnapshot snapshot) =>
            NormalizedEvidence.Incomplete("stub");

        public void AppendBoundArtifacts(
            GateRequest request,
            EvaluationPluginSnapshot snapshot,
            string gateDirectory,
            IDictionary<string, string> artifactHashes)
        {
        }
    }

    private static (string Config, string NodeDirectory) CreateFixtureAntiSlopClosure(string parent)
    {
        var root = System.IO.Path.Combine(parent, "anti-slop");
        var nodeDirectory = System.IO.Path.Combine(root, "runtime");
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["package.json"] = "{}",
            ["package-lock.json"] = "{}",
            ["oxlint.config.ts"] = "export default {};",
            ["vendor/anti-slop/index.ts"] = "export default {};",
            ["node_modules/oxlint/bin/oxlint"] = "#!/usr/bin/env node",
            ["node_modules/oxlint/dist/index.js"] = "export {};",
            ["node_modules/@oxlint/plugins/index.js"] = "export {};",
            ["node_modules/@oxlint/binding-fixture/binding.node"] = "fixture binding",
            ["runtime/node"] = "fixture node"
        };
        foreach (var (relative, contents) in files)
        {
            var path = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }
        return (System.IO.Path.Combine(root, "oxlint.config.ts"), nodeDirectory);
    }
}
