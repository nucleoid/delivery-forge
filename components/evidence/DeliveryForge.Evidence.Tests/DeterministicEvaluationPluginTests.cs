using DeliveryForge.Evidence.Adapters;
using DeliveryForge.Contracts.Validation;

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
        Assert.Equal(TestEvidence.Repository(), result.Before);
        Assert.False(result.SourceChanged);
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
            "{\"diagnostics\":[],\"number_of_files\":0,\"number_of_rules\":19}"u8,
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
        var (config, nodePath, entryPoint) = CreateFixtureAntiSlopClosure(temp.Path);
        var original = TestEvidence.GateRequest(temp.Path);
        var executableIdentity = $"sha256:{Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(
                nodePath, TestContext.Current.CancellationToken)))}";
        var canonicalInvocation = original.Invocation with
        {
            FileName = nodePath,
            Arguments = [entryPoint, "--config", config, "--format", "json", "--no-ignore", "--disable-nested-config", "src/app.ts"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            InheritEnvironment = false
        };
        var configIdentity = AntiSlopEvaluationPlugin.ComputeConfigurationIdentity(
            canonicalInvocation,
            effectRulesEnabled: false);
        var request = original with
        {
            Capability = ToolCapability.FromDetection(
                "oxlint", "1.78.0", "oxlint-json-v1", false, [],
                nodePath, executableIdentity, ["lint"],
                original.Invocation.WorkingDirectory),
            Adapter = EvidenceAdapterKind.External,
            ConfigurationIdentity = configIdentity,
            Invocation = canonicalInvocation with
            {
                Arguments = [entryPoint, "--fix", "--config", config, "--format", "json", "--no-ignore", "--disable-nested-config", "src/app.ts"]
            },
            Plugin = new EvaluationPluginSelection(
                "dmmulroy.anti-slop", Enabled: true, Inputs: ["src/app.ts"])
        };
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new ScopedSequenceRepositoryIdentityReader(
                new RepositoryEvaluationScope(
                    ["src/app.ts"], new HashSet<string>(StringComparer.Ordinal)),
                TestEvidence.Repository()),
            request.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'),
            DeterministicEvaluationPluginRegistry.CreateBuiltIns(
                System.IO.Path.GetDirectoryName(config)!, nodePath));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("autofix", result.Reason, StringComparison.OrdinalIgnoreCase);

        var plugin = AntiSlopEvaluationPlugin.CreateBundled(
            System.IO.Path.GetDirectoryName(config)!, nodePath);
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
        Assert.Contains("allow-listed", poisoned?.Reason, StringComparison.OrdinalIgnoreCase);
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
        var (config, nodePath, entryPoint) = CreateFixtureAntiSlopClosure(temp.Path);
        var original = TestEvidence.GateRequest(temp.Path);
        var invocation = original.Invocation with
        {
            FileName = nodePath,
            Arguments = [entryPoint, "--config", config, "--format", "json", "--no-ignore", "--disable-nested-config", "src/app.ts"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            InheritEnvironment = false
        };
        var request = original with
        {
            Adapter = EvidenceAdapterKind.External,
            ConfigurationIdentity = AntiSlopEvaluationPlugin.ComputeConfigurationIdentity(invocation, false),
            Invocation = invocation,
            Plugin = new EvaluationPluginSelection(
                "dmmulroy.anti-slop", Enabled: true, Inputs: ["src/app.ts"])
        };
        var plugin = AntiSlopEvaluationPlugin.CreateBundled(
            System.IO.Path.GetDirectoryName(config)!, nodePath);
        var snapshot = plugin.CaptureBefore(request);
        File.AppendAllText(
            System.IO.Path.Combine(temp.Path, "anti-slop-bundle", "vendor", "anti-slop", "index.ts"),
            "// changed");

        var result = plugin.ValidateAfterExecution(request, snapshot);

        Assert.Equal(GateOutcome.Incomplete, result?.Outcome);
        Assert.Contains("changed", result?.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Anti_slop_preserves_clean_findings_and_parser_failure_outcomes()
    {
        var plugin = AntiSlopEvaluationPlugin.CreateBundled();

        var clean = plugin.NormalizeReport(
            "{\"diagnostics\":[],\"number_of_files\":1,\"number_of_rules\":19}"u8,
            ["clean.ts"],
            0);
        var violation = plugin.NormalizeReport(
            "{\"diagnostics\":[{\"code\":\"anti-slop(no-runtime-typeof)\",\"severity\":\"error\",\"filename\":\"bad.ts\"}],\"number_of_files\":1,\"number_of_rules\":19}"u8,
            ["bad.ts"],
            1);
        var parserFailure = plugin.NormalizeReport(
            "{\"diagnostics\":[{\"severity\":\"error\",\"filename\":\"broken.ts\"}],\"number_of_files\":1,\"number_of_rules\":19}"u8,
            ["broken.ts"],
            1);
        var missingFile = plugin.NormalizeReport(
            "{\"diagnostics\":[{\"code\":\"anti-slop(no-runtime-typeof)\",\"severity\":\"error\"}],\"number_of_files\":1,\"number_of_rules\":19}"u8,
            ["bad.ts"],
            1);
        var dotDirectory = plugin.NormalizeReport(
            "{\"diagnostics\":[{\"code\":\"anti-slop(no-runtime-typeof)\",\"severity\":\"error\",\"filename\":\".storybook/main.ts\"}],\"number_of_files\":1,\"number_of_rules\":19}"u8,
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
    public void Anti_slop_rejects_unexpected_diagnostic_ids_as_incomplete()
    {
        var plugin = AntiSlopEvaluationPlugin.CreateBundled();

        var result = plugin.NormalizeReport(
            "{\"diagnostics\":[{\"code\":\"eslint(no-debugger)\",\"severity\":\"error\",\"filename\":\"bad.ts\"}],\"number_of_files\":1,\"number_of_rules\":19}"u8,
            ["bad.ts"],
            1);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("unexpected", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Anti_slop_normalizes_exact_pinned_oxlint_1_78_fixture_output()
    {
        var plugin = AntiSlopEvaluationPlugin.CreateBundled();
        const string clean = """
            { "diagnostics": [],
              "number_of_files": 1,
              "number_of_rules": 19,
              "threads_count": 1,
              "start_time": 0.325023302
            }
            """;
        const string violation = """
            { "diagnostics": [{"message": "Parameter `value` leaves input unparsed. Accept a named domain type; run the expected schema or parser at the I/O boundary before calling this function.","code": "anti-slop(no-unknown-parameters)","severity": "error","filename": "fixtures/violation.ts","labels": [{"span": {"offset": 33,"length": 7,"line": 1,"column": 34}}]},
            {"message": "A `typeof` check narrows a representation without establishing its contract. Parse input at its I/O boundary, then branch on the domain value.","code": "anti-slop(no-runtime-typeof)","severity": "error","filename": "fixtures/violation.ts","labels": [{"span": {"offset": 61,"length": 12,"line": 2,"column": 10}}]}],
              "number_of_files": 1,
              "number_of_rules": 19,
              "threads_count": 1,
              "start_time": 0.504229757
            }
            """;
        const string malformed = """
            { "diagnostics": [{"message": "Unexpected token","severity": "error","filename": "fixtures/malformed.ts","labels": [{"span": {"offset": 23,"length": 1,"line": 1,"column": 24}}]}],
              "number_of_files": 1,
              "number_of_rules": 19,
              "threads_count": 1,
              "start_time": 0.343900535
            }
            """;

        Assert.Equal(
            GateOutcome.Pass,
            plugin.NormalizeReport(System.Text.Encoding.UTF8.GetBytes(clean), ["fixtures/clean.ts"], 0).Outcome);
        Assert.Equal(
            GateOutcome.Fail,
            plugin.NormalizeReport(System.Text.Encoding.UTF8.GetBytes(violation), ["fixtures/violation.ts"], 1).Outcome);
        Assert.Equal(
            GateOutcome.Incomplete,
            plugin.NormalizeReport(System.Text.Encoding.UTF8.GetBytes(malformed), ["fixtures/malformed.ts"], 1).Outcome);
    }

    [Fact]
    public async Task Anti_slop_cannot_impersonate_a_protected_test_gate()
    {
        using var temp = new TempDirectory();
        var (config, nodePath, entryPoint) = CreateFixtureAntiSlopClosure(temp.Path);
        var original = TestEvidence.GateRequest(temp.Path);
        var invocation = original.Invocation with
        {
            FileName = nodePath,
            Arguments = [entryPoint, "--config", config, "--format", "json", "--no-ignore", "--disable-nested-config", "src/app.ts"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            InheritEnvironment = false
        };
        var executableIdentity = $"sha256:{Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(
                invocation.FileName, TestContext.Current.CancellationToken)))}";
        var request = original with
        {
            Policy = new ResolvedEvidencePolicy(
                TestEvidence.Identity('1'), TestEvidence.Identity('2'), TestEvidence.Commit('a'),
                PolicyAuthorityKind.ProtectedGitBase, 0m, ["test"], authorityVerified: true,
                sourceRepositoryRoot: invocation.WorkingDirectory,
                requiredGatePluginBindings:
                [new RequiredGatePluginBinding(
                    "test", "delivery-forge.dotnet", "1.0.0", "dotnet")]),
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
                "{\"diagnostics\":[],\"number_of_files\":1,\"number_of_rules\":19}",
                string.Empty,
                false,
                false,
                now,
                now)),
            new ScopedSequenceRepositoryIdentityReader(
                new RepositoryEvaluationScope(
                    ["src/app.ts"], new HashSet<string>(StringComparer.Ordinal)),
                TestEvidence.Repository(), TestEvidence.Repository()),
            request.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.Null(result.Plugin.ToolOutcome);
        Assert.Contains("required-gate binding", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plugin_inputs_reject_option_like_first_segments()
    {
        Assert.Throws<EvaluationPluginException>(() =>
            AntiSlopEvaluationPlugin.DetectSupportedInputs(["--config.ts"]));
    }

    [Theory]
    [InlineData(false, GateOutcome.Pass)]
    [InlineData(true, GateOutcome.Incomplete)]
    public async Task Protected_anti_slop_binding_requires_the_complete_frozen_scope(
        bool omitTrackedInput,
        GateOutcome expectedOutcome)
    {
        using var temp = new TempDirectory();
        var (config, nodePath, entryPoint) = CreateFixtureAntiSlopClosure(temp.Path);
        var original = TestEvidence.GateRequest(temp.Path);
        var selected = omitTrackedInput ? new[] { "src/app.ts" } : new[] { "src/app.ts", "src/extra.ts" };
        var invocation = original.Invocation with
        {
            FileName = nodePath,
            Arguments = [entryPoint, "--config", config, "--format", "json", "--no-ignore", "--disable-nested-config", .. selected],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            InheritEnvironment = false
        };
        var executableIdentity = $"sha256:{Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(nodePath)))}";
        var request = original with
        {
            GateId = "anti-slop",
            Policy = new ResolvedEvidencePolicy(
                TestEvidence.Identity('1'), TestEvidence.Identity('2'), TestEvidence.Commit('a'),
                PolicyAuthorityKind.ProtectedGitBase, 0m, ["anti-slop"], authorityVerified: true,
                sourceRepositoryRoot: invocation.WorkingDirectory,
                requiredGatePluginBindings:
                [new RequiredGatePluginBinding(
                    "anti-slop", "dmmulroy.anti-slop",
                    $"0.1.2+{AntiSlopEvaluationPlugin.UpstreamCommit}", "oxlint")]),
            Capability = ToolCapability.FromDetection(
                "oxlint", "1.78.0", "oxlint-json-v1", false, [], nodePath,
                executableIdentity, ["lint"], invocation.WorkingDirectory),
            Adapter = EvidenceAdapterKind.External,
            ConfigurationIdentity = AntiSlopEvaluationPlugin.ComputeConfigurationIdentity(invocation, false),
            Invocation = invocation,
            Plugin = new EvaluationPluginSelection(
                "dmmulroy.anti-slop", Enabled: true, Inputs: selected)
        };
        var now = DateTimeOffset.UtcNow;
        var runner = new GateRunner(
            new StubCommandExecutor(new CommandResult(
                0,
                $"{{\"diagnostics\":[],\"number_of_files\":{selected.Length},\"number_of_rules\":19}}",
                string.Empty, false, false, now, now)),
            new ScopedSequenceRepositoryIdentityReader(
                new RepositoryEvaluationScope(
                    ["src/app.ts", "src/extra.ts"], new HashSet<string>(StringComparer.Ordinal)),
                TestEvidence.Repository(), TestEvidence.Repository()),
            invocation.WorkingDirectory,
            TestEvidence.Commit('a'),
            DeterministicEvaluationPluginRegistry.CreateBuiltIns(
                System.IO.Path.GetDirectoryName(config)!, nodePath));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(
            result.Outcome == expectedOutcome,
            $"Expected {expectedOutcome}, received {result.Outcome}: {result.Reason}");
        if (omitTrackedInput)
            Assert.Contains("every tracked JavaScript-family file", result.Reason, StringComparison.OrdinalIgnoreCase);
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

        var receiptBytes = await File.ReadAllBytesAsync(result.ReceiptPath, TestContext.Current.CancellationToken);
        var validatedReceipt = ContractValidator.ParseAndValidate(receiptBytes);
        Assert.Equal("2.0.0", validatedReceipt.SchemaVersion);
        using var receipt = System.Text.Json.JsonDocument.Parse(receiptBytes);
        using var command = System.Text.Json.JsonDocument.Parse(
            receipt.RootElement.GetProperty("command").GetString()!);
        var plugin = command.RootElement.GetProperty("plugin");
        Assert.Equal("delivery-forge.dotnet", plugin.GetProperty("id").GetString());
        Assert.Equal(result.Plugin.InputSetIdentity, plugin.GetProperty("inputSetIdentity").GetString());
        Assert.Equal(TestEvidence.Repository().HeadCommit, receipt.RootElement.GetProperty("headCommit").GetString());
    }

    [Fact]
    public async Task Anti_slop_fails_closed_when_both_output_streams_exceed_its_bound()
    {
        using var temp = new TempDirectory();
        var (config, nodePath, entryPoint) = CreateFixtureAntiSlopClosure(temp.Path);
        var original = TestEvidence.GateRequest(temp.Path);
        var invocation = original.Invocation with
        {
            FileName = nodePath,
            Arguments = [entryPoint, "--config", config, "--format", "json", "--no-ignore", "--disable-nested-config", "src/app.ts"],
            Environment = new Dictionary<string, string>(),
            InheritEnvironment = false
        };
        var executableIdentity = $"sha256:{Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(nodePath)))}";
        var request = original with
        {
            Capability = ToolCapability.FromDetection(
                "oxlint", "1.78.0", "oxlint-json-v1", false, [], nodePath,
                executableIdentity, ["lint"], invocation.WorkingDirectory),
            Adapter = EvidenceAdapterKind.External,
            ConfigurationIdentity = AntiSlopEvaluationPlugin.ComputeConfigurationIdentity(invocation, false),
            Invocation = invocation,
            Plugin = new EvaluationPluginSelection("dmmulroy.anti-slop", true, ["src/app.ts"])
        };
        var oversized = new string('x', GateRunner.AntiSlopMaximumCapturedBytesPerStream + 1);
        var now = DateTimeOffset.UtcNow;
        var runner = new GateRunner(
            new StubCommandExecutor(new CommandResult(0, oversized, oversized, false, false, now, now)),
            new ScopedSequenceRepositoryIdentityReader(
                new RepositoryEvaluationScope(["src/app.ts"], new HashSet<string>(StringComparer.Ordinal)),
                TestEvidence.Repository(), TestEvidence.Repository()),
            invocation.WorkingDirectory,
            TestEvidence.Commit('a'),
            DeterministicEvaluationPluginRegistry.CreateBuiltIns(
                System.IO.Path.GetDirectoryName(config)!, nodePath));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("capture limit", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GateRunner.AntiSlopMaximumCapturedBytesPerStream,
            System.Text.Encoding.UTF8.GetByteCount(result.Execution.StandardOutput));
        Assert.Equal(GateRunner.AntiSlopMaximumCapturedBytesPerStream,
            System.Text.Encoding.UTF8.GetByteCount(result.Execution.StandardError));
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

    [Fact]
    public async Task Unmapped_required_gate_is_unsatisfied_without_orchestration_error()
    {
        using var temp = new TempDirectory();
        var original = TestEvidence.GateRequest(temp.Path);
        var request = original with
        {
            GateId = "custom",
            Policy = new ResolvedEvidencePolicy(
                TestEvidence.Identity('1'), TestEvidence.Identity('2'), TestEvidence.Commit('a'),
                PolicyAuthorityKind.ProtectedGitBase, 0m, ["custom"], authorityVerified: true,
                sourceRepositoryRoot: original.Invocation.WorkingDirectory,
                requiredGatePluginBindings: [])
        };
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            original.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("unsatisfied", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_supplied_executor_cannot_authorize_a_non_fixture_required_gate()
    {
        using var temp = new TempDirectory();
        var original = TestEvidence.GateRequest(temp.Path);
        var request = original with
        {
            Policy = new ResolvedEvidencePolicy(
                TestEvidence.Identity('1'), TestEvidence.Identity('2'), TestEvidence.Commit('a'),
                PolicyAuthorityKind.ProtectedGitBase, 0m, ["test"], authorityVerified: true,
                sourceRepositoryRoot: original.Invocation.WorkingDirectory,
                requiredGatePluginBindings:
                [new RequiredGatePluginBinding("test", "delivery-forge.dotnet", "1.0.0", "dotnet")]),
            Capability = ToolCapability.FromDetection(
                "dotnet", "10.0.401", "trx-v1", false, [],
                original.Capability.ExecutablePath!, original.Capability.ExecutableIdentity!, ["test"],
                original.Invocation.WorkingDirectory)
        };
        var runner = new GateRunner(
            new QueueCommandExecutor(TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository()),
            original.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("caller-supplied", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Execution.ExitCode);
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

    private static (string Config, string NodePath, string EntryPoint) CreateFixtureAntiSlopClosure(string parent)
    {
        var root = System.IO.Path.Combine(parent, "anti-slop-bundle");
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
            ["node_modules/tsx/index.js"] = "export {};",
            ["node_modules/typescript/index.js"] = "export {};",
            ["runtime/node"] = "fixture node"
        };
        foreach (var (relative, contents) in files)
        {
            var path = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }
        return (
            System.IO.Path.Combine(root, "oxlint.config.ts"),
            System.IO.Path.Combine(nodeDirectory, "node"),
            System.IO.Path.Combine(root, "node_modules", "oxlint", "bin", "oxlint"));
    }
}
