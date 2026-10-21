using DeliveryForge.Evidence.Adapters;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Evidence.Tests;

public sealed class DeterministicEvaluationPluginTests
{
    [Fact]
    public void Reviewed_installed_anti_slop_runtime_closure_is_exact()
    {
        if (!OperatingSystem.IsLinux() ||
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64)
            return;
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "anti-slop"));
        var nodePath = TestEvidence.ResolveNodeExecutable();
        var baseIdentity = AntiSlopEvaluationPlugin.ComputeRuntimeClosureIdentity(root, nodePath, false);
        var effectIdentity = AntiSlopEvaluationPlugin.ComputeRuntimeClosureIdentity(root, nodePath, true);
        Assert.Equal(AntiSlopEvaluationPlugin.ReviewedLinuxX64GnuRuntimeClosureSha256, baseIdentity);
        Assert.Equal(AntiSlopEvaluationPlugin.ReviewedLinuxX64GnuEffectRuntimeClosureSha256, effectIdentity);
    }

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
    public void Rewrapped_builtin_instances_do_not_inherit_code_owned_registry_identity()
    {
        var builtIns = DeterministicEvaluationPluginRegistry.CreateBuiltIns();
        var rewrapped = new DeterministicEvaluationPluginRegistry(
        [
            builtIns.Resolve(EvidenceAdapterKind.DotNet, null),
            builtIns.Resolve(EvidenceAdapterKind.Crap4CSharp, null),
            builtIns.Resolve(EvidenceAdapterKind.Mutate4CSharp, null),
            builtIns.Resolve(
                EvidenceAdapterKind.External,
                new EvaluationPluginSelection("dmmulroy.anti-slop", Enabled: true))
        ]);

        Assert.True(builtIns.IsCodeOwnedBuiltInRegistry);
        Assert.False(rewrapped.IsCodeOwnedBuiltInRegistry);
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
    public async Task Public_custom_registry_is_one_truth_for_runner_and_authority_binding()
    {
        using var temp = new TempDirectory();
        var custom = new StubEvaluationPlugin("host.custom", "2.0.0", "custom-tool");
        var registry = new TrustedEvaluationPluginRegistry(
            [new TrustedEvaluationPluginRegistration("custom", custom, "3.0.0", "custom-v1")]);
        var request = TestEvidence.GateRequest(temp.Path) with
        {
            GateId = "custom",
            Adapter = EvidenceAdapterKind.External,
            Plugin = new EvaluationPluginSelection("host.custom", Enabled: false)
        };
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository()),
            TestEvidence.WorkingDirectory(temp.Path),
            TestEvidence.Commit('a'),
            registry);

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("host.custom", result.Plugin.Id);
        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Throws<ArgumentException>(() => new TrustedEvaluationPluginRegistry(
        [
            new TrustedEvaluationPluginRegistration("custom", custom),
            new TrustedEvaluationPluginRegistration("custom", custom)
        ]));
    }

    [Fact]
    public async Task Same_process_not_applicable_cannot_mint_v2_authority()
    {
        using var temp = new TempDirectory();
        var executable = System.IO.Path.Combine(temp.Path, "custom-tool");
        await File.WriteAllTextAsync(executable, "fixture executable", TestContext.Current.CancellationToken);
        var executableIdentity = $"sha256:{Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(
                executable, TestContext.Current.CancellationToken)))}";
        var customPlugin = new StubEvaluationPlugin(
            "host.custom", "2.0.0", "custom-tool",
            new NormalizedEvidence(
                GateOutcome.NotApplicable, "No applicable custom units.", false, true, [],
                "The frozen scope contains no custom units."));
        var pluginRegistry = new TrustedEvaluationPluginRegistry(
            [new TrustedEvaluationPluginRegistration("custom", customPlugin, "3.0.0", "custom-v1")]);
        var original = TestEvidence.GateRequest(temp.Path);
        var request = original with
        {
            GateId = "custom",
            Adapter = EvidenceAdapterKind.External,
            Plugin = new EvaluationPluginSelection("host.custom", Enabled: true, Inputs: ["scope.custom"]),
            Invocation = original.Invocation with { FileName = executable, Arguments = ["evaluate"] },
            Capability = ToolCapability.FromDetection(
                "custom-tool", "3.0.0", "custom-v1", false, [], executable,
                executableIdentity, ["evaluate"], original.Invocation.WorkingDirectory),
            Policy = new ResolvedEvidencePolicy(
                TestEvidence.Identity('1'), TestEvidence.Identity('2'), TestEvidence.Commit('a'),
                PolicyAuthorityKind.ProtectedGitBase, 0m, ["custom"], authorityVerified: true,
                sourceRepositoryRoot: original.Invocation.WorkingDirectory,
                requiredGatePluginBindings:
                [new RequiredGatePluginBinding("custom", "host.custom", "2.0.0", "custom-tool")],
                allowedNotApplicable: new HashSet<string>(["custom"], StringComparer.Ordinal))
        };
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            original.Invocation.WorkingDirectory, TestEvidence.Commit('a'), pluginRegistry);

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);
        var receipt = ContractValidator.ParseAndValidate(await File.ReadAllBytesAsync(
            result.ReceiptPath, TestContext.Current.CancellationToken));
        using var document = System.Text.Json.JsonDocument.Parse(receipt.CanonicalBytes);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Equal(EvaluationPluginStatus.NotApplicable, result.Plugin.Status);
        Assert.Equal(0, result.Execution.ExitCode);
        Assert.Contains("advisory", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(document.RootElement.TryGetProperty("authorityBinding", out _));
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

    [Theory]
    [InlineData("{\"diagnostics\":[],\"number_of_files\":\"one\",\"number_of_rules\":19}")]
    [InlineData("{\"diagnostics\":[],\"number_of_files\":1,\"number_of_rules\":{}}")]
    [InlineData("{\"diagnostics\":[\"not-an-object\"],\"number_of_files\":1,\"number_of_rules\":19}")]
    [InlineData("{\"diagnostics\":[null],\"number_of_files\":1,\"number_of_rules\":19}")]
    [InlineData("{\"diagnostics\":[7],\"number_of_files\":1,\"number_of_rules\":19}")]
    public void Anti_slop_malformed_json_shapes_normalize_incomplete(string report)
    {
        var result = AntiSlopEvaluationPlugin.CreateBundled().NormalizeReport(
            System.Text.Encoding.UTF8.GetBytes(report), ["app.ts"], 0);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
    }

    [Fact]
    public async Task Anti_slop_not_applicable_requires_non_fixture_production_authority()
    {
        using var temp = new TempDirectory();
        var (config, nodePath, entryPoint) = CreateFixtureAntiSlopClosure(temp.Path);
        var original = TestEvidence.GateRequest(temp.Path);
        var executableIdentity = $"sha256:{Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(nodePath)))}";
        var invocation = original.Invocation with
        {
            FileName = nodePath,
            Arguments = [entryPoint, "--config", config, "--format", "json", "--no-ignore", "--disable-nested-config"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            InheritEnvironment = false
        };
        var request = original with
        {
            GateId = "anti-slop",
            Policy = new ResolvedEvidencePolicy(
                TestEvidence.Identity('1'), TestEvidence.Identity('2'), TestEvidence.Commit('a'),
                PolicyAuthorityKind.ProtectedGitBase, 0m, ["anti-slop"], authorityVerified: true,
                sourceRepositoryRoot: original.Invocation.WorkingDirectory,
                requiredGatePluginBindings:
                [new RequiredGatePluginBinding(
                    "anti-slop", "dmmulroy.anti-slop",
                    $"0.1.2+{AntiSlopEvaluationPlugin.UpstreamCommit}", "oxlint")],
                allowedNotApplicable: new HashSet<string>(["anti-slop"], StringComparer.Ordinal)),
            Capability = ToolCapability.FromDetection(
                "oxlint", "1.78.0", "oxlint-json-v1", true, [], nodePath,
                executableIdentity, ["lint"], original.Invocation.WorkingDirectory),
            Adapter = EvidenceAdapterKind.External,
            ConfigurationIdentity = AntiSlopEvaluationPlugin.ComputeConfigurationIdentity(invocation, false),
            Invocation = invocation,
            Plugin = new EvaluationPluginSelection("dmmulroy.anti-slop", true, [])
        };
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new ScopedSequenceRepositoryIdentityReader(
                new RepositoryEvaluationScope([], new HashSet<string>(StringComparer.Ordinal)),
                TestEvidence.Repository()),
            request.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'),
            DeterministicEvaluationPluginRegistry.CreateBuiltIns(
                System.IO.Path.GetDirectoryName(config)!, nodePath));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("advisory", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.ProductionCapable);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Real_pinned_anti_slop_runs_but_same_uid_success_cannot_authorize(
        bool fixtureCapability,
        bool violation)
    {
        if (!OperatingSystem.IsLinux() ||
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64)
            return;
        using var repository = new TempDirectory();
        using var artifacts = new TempDirectory();
        await Git(repository.Path, "init");
        await Git(repository.Path, "config", "user.name", "Delivery Forge Test");
        await Git(repository.Path, "config", "user.email", "forge@example.invalid");
        await File.WriteAllTextAsync(System.IO.Path.Combine(repository.Path, "Subject.cs"),
            "public static class Subject { }\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(System.IO.Path.Combine(repository.Path, "app.ts"),
            violation
                ? "export function h(input: object) { return input; }\n"
                : "export const value = 1;\n",
            TestContext.Current.CancellationToken);
        await Git(repository.Path, "add", ".");
        await Git(repository.Path, "commit", "-m", "base");
        var commit = (await Git(repository.Path, "rev-parse", "HEAD")).Trim();
        var antiSlopRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "anti-slop"));
        var nodePath = TestEvidence.ResolveNodeExecutable();
        var reader = new GitRepositoryIdentityReader(new ProcessCommandExecutor(), repository.Path, commit);
        var expected = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var executableIdentity = $"sha256:{Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(nodePath)))}";
        var policy = new ResolvedEvidencePolicy(
                TestEvidence.Identity('1'), TestEvidence.Identity('2'), commit,
                PolicyAuthorityKind.ProtectedGitBase, 0m, ["anti-slop"], authorityVerified: true,
                sourceRepositoryRoot: repository.Path,
                requiredGatePluginBindings:
                [new RequiredGatePluginBinding(
                    "anti-slop", "dmmulroy.anti-slop",
                    $"0.1.2+{AntiSlopEvaluationPlugin.UpstreamCommit}", "oxlint")],
                allowedNotApplicable: new HashSet<string>(["anti-slop"], StringComparer.Ordinal));
        var capability = ToolCapability.FromDetection(
                "oxlint", AntiSlopEvaluationPlugin.OxlintVersion, "oxlint-json-v1", fixtureCapability, [],
                nodePath, executableIdentity, ["lint"], repository.Path);
        var request = AntiSlopRequestFactory.Create(
            "anti-slop", policy, capability, expected, repository.Path, antiSlopRoot, nodePath,
            ["app.ts"], "frozen-javascript-family", TimeSpan.FromSeconds(30),
            System.IO.Path.Combine(artifacts.Path, "gate-output"));
        var selection = Assert.IsType<EvaluationPluginSelection>(request.Plugin);
        Assert.Equal(["app.ts"], selection.Inputs);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(request.Invocation.Environment));
        Assert.False(request.Invocation.InheritEnvironment);
        var result = await new GateRunner(
                new ProcessCommandExecutor(), reader, repository.Path, commit,
                DeterministicEvaluationPluginRegistry.CreateBuiltIns(antiSlopRoot, nodePath))
            .RunAsync(request, TestContext.Current.CancellationToken);

        var expectedOutcome = violation ? GateOutcome.Fail : GateOutcome.Incomplete;
        Assert.True(result.Outcome == expectedOutcome,
            $"Expected {expectedOutcome}, received {result.Outcome}: {result.Reason}; exit={result.Execution.ExitCode}; stderr={result.Execution.StandardError}");
        Assert.False(result.ProductionCapable);
        Assert.Equal(fixtureCapability, result.Fixture);
        Assert.Equal(violation ? 1 : 0, result.Execution.ExitCode);
        if (!violation)
            Assert.Contains("advisory", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.ArtifactHashes.TryGetValue(GateRunner.AntiSlopBindingArtifact, out var bindingHash));
        var gateDirectory = System.IO.Path.GetDirectoryName(
            System.IO.Path.GetDirectoryName(result.ReceiptPath)!)!;
        var bindingPath = System.IO.Path.Combine(gateDirectory, GateRunner.AntiSlopBindingArtifact);
        var bindingBytes = await File.ReadAllBytesAsync(bindingPath, TestContext.Current.CancellationToken);
        Assert.Equal($"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bindingBytes))}",
            bindingHash);
        using (var binding = System.Text.Json.JsonDocument.Parse(bindingBytes))
        {
            Assert.Equal("anti-slop-evaluation-binding", binding.RootElement.GetProperty("kind").GetString());
            Assert.Equal("advisory", binding.RootElement.GetProperty("authority").GetString());
            Assert.False(binding.RootElement.GetProperty("productionCapable").GetBoolean());
            Assert.Equal("dmmulroy.anti-slop",
                binding.RootElement.GetProperty("plugin").GetProperty("id").GetString());
            Assert.Equal(expected.HeadCommit,
                binding.RootElement.GetProperty("repository").GetProperty("headCommit").GetString());
            Assert.Equal(expected.TreeId,
                binding.RootElement.GetProperty("repository").GetProperty("treeId").GetString());
            Assert.Equal(result.Plugin.InputSetIdentity,
                binding.RootElement.GetProperty("plugin").GetProperty("inputSetIdentity").GetString());
            Assert.Equal(result.Plugin.RuntimeClosureIdentity,
                binding.RootElement.GetProperty("plugin").GetProperty("runtimeClosureIdentity").GetString());
            Assert.Equal("<host-owned-frozen-inputs>",
                binding.RootElement.GetProperty("logicalWorkingDirectory").GetString());
            Assert.Equal(violation ? "FAIL" : "PASS",
                binding.RootElement.GetProperty("plugin").GetProperty("pluginRawOutcome").GetString());
            Assert.Equal(violation ? "FAIL" : "INCOMPLETE",
                binding.RootElement.GetProperty("finalOutcome").GetString());
        }
        using (var receipt = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllBytesAsync(result.ReceiptPath, TestContext.Current.CancellationToken)))
        using (var command = System.Text.Json.JsonDocument.Parse(
            receipt.RootElement.GetProperty("command").GetString()!))
        {
            Assert.Equal("<host-owned-frozen-inputs>",
                command.RootElement.GetProperty("workingDirectory").GetString());
            Assert.Equal(violation ? "FAIL" : "INCOMPLETE",
                receipt.RootElement.GetProperty("outcome").GetString());
        }
        var error = Assert.Throws<NotSupportedException>(() => new AuthoritativeEvaluationHost(
            new ProcessCommandExecutor(), reader, repository.Path, commit,
            DeterministicEvaluationPluginRegistry.CreateBuiltIns(antiSlopRoot, nodePath)));
        Assert.Contains("separate-principal", error.Message, StringComparison.OrdinalIgnoreCase);
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

        var plugin = AntiSlopEvaluationPlugin.CreateBundledForFixture(
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
        var mismatched = canonical with
        {
            ConfigurationIdentity = TestEvidence.Identity('9')
        };
        var mismatchError = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await plugin.CaptureBeforeAsync(mismatched, TestContext.Current.CancellationToken));
        Assert.Contains("configuration closure", mismatchError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Anti_slop_accepts_code_owned_manifest_batch_host_without_whole_tree_argv()
    {
        using var temp = new TempDirectory();
        var (config, nodePath, entryPoint) = CreateFixtureAntiSlopClosure(temp.Path);
        var root = System.IO.Path.GetDirectoryName(config)!;
        var manifest = System.IO.Path.Combine(temp.Path, "inputs.json");
        File.WriteAllText(manifest, "[\"src/app.ts\"]");
        var invocation = new CommandInvocation(
            nodePath,
            [System.IO.Path.Combine(root, "run-oxlint-batches.mjs"), "--oxlint", entryPoint,
                "--config", config, "--inputs", manifest],
            temp.Path,
            new Dictionary<string, string>(StringComparer.Ordinal),
            InheritEnvironment: false);
        var original = TestEvidence.GateRequest(temp.Path);
        var request = original with
        {
            Capability = new ToolCapability("oxlint", "1.78.0", "oxlint-json-v1", true, false),
            Adapter = EvidenceAdapterKind.External,
            Invocation = invocation,
            ConfigurationIdentity = AntiSlopEvaluationPlugin.ComputeConfigurationIdentity(invocation, false),
            Plugin = new EvaluationPluginSelection("dmmulroy.anti-slop", true, ["src/app.ts"])
        };
        var plugin = AntiSlopEvaluationPlugin.CreateBundledForFixture(root, nodePath);

        Assert.Null(plugin.ValidateBeforeExecution(request));
    }

    [Fact]
    public async Task Anti_slop_reuses_the_deadline_bounded_configuration_and_runtime_snapshot()
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
        var plugin = AntiSlopEvaluationPlugin.CreateBundledForFixture(
            System.IO.Path.GetDirectoryName(config)!, nodePath);
        var snapshot = await plugin.CaptureBeforeAsync(request, TestContext.Current.CancellationToken);
        File.AppendAllText(
            System.IO.Path.Combine(temp.Path, "anti-slop-bundle", "vendor", "anti-slop", "index.ts"),
            "// changed");

        var result = plugin.ValidateAfterExecution(request, snapshot);

        Assert.Null(result);
    }

    [Fact]
    public async Task Anti_slop_runtime_snapshot_honors_cancellation_before_closure_reads()
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
            Plugin = new EvaluationPluginSelection("dmmulroy.anti-slop", true, ["src/app.ts"])
        };
        var plugin = AntiSlopEvaluationPlugin.CreateBundledForFixture(
            System.IO.Path.GetDirectoryName(config)!, nodePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await plugin.CaptureBeforeAsync(request, cancellation.Token));
    }

    [Theory]
    [InlineData("closure-file")]
    [InlineData("binding")]
    [InlineData("node")]
    public async Task Broken_anti_slop_runtime_closure_persists_incomplete_without_execution(string missing)
    {
        using var temp = new TempDirectory();
        var (config, nodePath, entryPoint) = CreateFixtureAntiSlopClosure(temp.Path);
        var root = System.IO.Path.GetDirectoryName(config)!;
        var original = TestEvidence.GateRequest(temp.Path);
        var invocation = original.Invocation with
        {
            FileName = nodePath,
            Arguments = [entryPoint, "--config", config, "--format", "json", "--no-ignore", "--disable-nested-config", "src/app.ts"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
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
        switch (missing)
        {
            case "closure-file":
                File.Delete(System.IO.Path.Combine(root, "vendor", "anti-slop", "index.ts"));
                break;
            case "binding":
                Directory.Delete(System.IO.Path.Combine(root, "node_modules", "@oxlint", "binding-fixture"), true);
                break;
            case "node":
                File.Delete(nodePath);
                break;
        }
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new ScopedSequenceRepositoryIdentityReader(
                new RepositoryEvaluationScope(["src/app.ts"], new HashSet<string>(StringComparer.Ordinal)),
                TestEvidence.Repository()),
            invocation.WorkingDirectory,
            TestEvidence.Commit('a'),
            DeterministicEvaluationPluginRegistry.CreateBuiltIns(root, nodePath));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains(
            missing == "node" ? "capability executable" : "closure",
            result.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Execution.ExitCode);
        Assert.True(File.Exists(result.ReceiptPath));
        using var receipt = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllBytesAsync(result.ReceiptPath, TestContext.Current.CancellationToken));
        Assert.Equal("INCOMPLETE", receipt.RootElement.GetProperty("outcome").GetString());
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
        var toolErrorWithFindings = plugin.NormalizeReport(
            "{\"diagnostics\":[{\"code\":\"anti-slop(no-runtime-typeof)\",\"severity\":\"error\",\"filename\":\"bad.ts\"}],\"number_of_files\":1,\"number_of_rules\":19}"u8,
            ["bad.ts"],
            2);

        Assert.Equal(GateOutcome.Pass, clean.Outcome);
        Assert.False(clean.ProductionCapable);
        Assert.Equal(GateOutcome.Fail, violation.Outcome);
        Assert.Equal(GateOutcome.Incomplete, parserFailure.Outcome);
        Assert.Equal(GateOutcome.Incomplete, missingFile.Outcome);
        Assert.Equal(GateOutcome.Fail, dotDirectory.Outcome);
        Assert.Equal(GateOutcome.Incomplete, fakeEslintShape.Outcome);
        Assert.Equal(GateOutcome.Incomplete, malformed.Outcome);
        Assert.Equal(GateOutcome.Incomplete, toolErrorWithFindings.Outcome);
    }

    [Theory]
    [InlineData("10.0.401")]
    [InlineData("10.0.402")]
    public async Task Code_owned_builtins_with_anti_slop_configuration_retain_legacy_v1_dotnet_authority(
        string detectedVersion)
    {
        using var temp = new TempDirectory();
        var original = TestEvidence.GateRequest(temp.Path);
        var trx = System.IO.Path.Combine(original.Invocation.WorkingDirectory, "builtins.trx");
        var request = original with
        {
            Policy = new ResolvedEvidencePolicy(
                TestEvidence.Identity('1'), TestEvidence.Identity('2'), TestEvidence.Commit('a'),
                PolicyAuthorityKind.ProtectedGitBase, 0m, ["test"], authorityVerified: true,
                sourceRepositoryRoot: original.Invocation.WorkingDirectory,
                requiredGatePluginBindings:
                [new RequiredGatePluginBinding("test", "delivery-forge.dotnet", "1.0.0", "dotnet")]),
            Capability = ToolCapability.FromDetection(
                "dotnet", detectedVersion, "trx-v1", false, [],
                original.Capability.ExecutablePath!, original.Capability.ExecutableIdentity!, ["test"],
                original.Invocation.WorkingDirectory),
            Plugin = new EvaluationPluginSelection(
                "delivery-forge.dotnet", Enabled: true, Inputs: ["DeliveryForge.Tests.csproj"]),
            Invocation = original.Invocation with
            {
                Arguments = [.. original.Invocation.Arguments, "--logger", $"trx;LogFileName={trx}"]
            }
        };
        var execution = TestEvidence.CommandResult() with
        {
            StandardOutput = "Subject -> Subject.dll"
        };
        var runner = new GateRunner(
            new TrxWritingCommandExecutor(execution, trx),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            request.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'),
            DeterministicEvaluationPluginRegistry.CreateBuiltIns());

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.Outcome == GateOutcome.Pass,
            $"Expected PASS, received {result.Outcome}: {result.Reason}");
        Assert.True(result.ProductionCapable);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("test")]
    [InlineData("coverage")]
    [InlineData("crap")]
    [InlineData("crap4csharp")]
    [InlineData("mutation")]
    public void Legacy_v1_bindings_retain_exact_future_v2_pins_without_narrowing_v1(string gateId)
    {
        Assert.True(TrustedGatePluginRegistry.BuiltIns.TryGet(gateId, out var binding));
        Assert.True(binding.LegacyV1Authority);
        Assert.NotNull(binding.ExpectedToolVersion);
        Assert.NotNull(binding.ExpectedFormatVersion);

        Assert.True(TrustedGatePluginRegistry.BuiltIns.TryGet("anti-slop", out var antiSlop));
        Assert.Equal("1.78.0", antiSlop.ExpectedToolVersion);
        Assert.Equal("oxlint-json-v1", antiSlop.ExpectedFormatVersion);
        Assert.Equal(AntiSlopEvaluationPlugin.ReviewedLinuxX64GnuRuntimeClosureSha256,
            antiSlop.ExpectedRuntimeClosureIdentity);
        Assert.False(antiSlop.LegacyV1Authority);
    }

    [Theory]
    [InlineData("test", "dotnet", "10.0.402", "trx-v1")]
    [InlineData("crap4csharp", "crap4csharp", "0.1.0+compatible-build", "1.0")]
    [InlineData("mutation", "mutate4csharp", "0.1.0-format-only+compatible-build", "2")]
    public void Legacy_v1_capabilities_use_detector_range_and_exact_report_format(
        string gateId,
        string tool,
        string compatibleVersion,
        string format)
    {
        Assert.True(TrustedGatePluginRegistry.BuiltIns.TryGet(gateId, out var binding));
        Assert.NotEqual(binding.ExpectedToolVersion, compatibleVersion);
        Assert.True(GateRunner.LegacyV1CapabilityMatches(
            binding, new ToolCapability(tool, compatibleVersion, format, true, false)));
        Assert.False(GateRunner.LegacyV1CapabilityMatches(
            binding, new ToolCapability(tool, compatibleVersion, format + "-wrong", true, false)));
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
        Assert.Null(result.Plugin.PluginRawOutcome);
        Assert.Contains("required-gate binding", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plugin_inputs_reject_option_like_first_segments()
    {
        Assert.Throws<EvaluationPluginException>(() =>
            AntiSlopEvaluationPlugin.DetectSupportedInputs(["--config.ts"]));
    }

    [Theory]
    [InlineData(false, GateOutcome.Incomplete)]
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
            Assert.Contains("protected-base scope", result.Reason, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains("runtime closure", result.Reason, StringComparison.OrdinalIgnoreCase);
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
    public async Task Canonical_v1_envelope_remains_legacy_shaped_while_result_exposes_plugin_raw_outcome()
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
            TestEvidence.Commit('a'),
            plugins: null, authorityRegistry: null,
            hostOwnedClaimRoot: System.IO.Path.Combine(temp.Path, "host-claims"));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(EvaluationPluginStatus.Executed, result.Plugin.Status);
        Assert.Equal("delivery-forge.dotnet", result.Plugin.Id);
        Assert.Equal("1.0.0", result.Plugin.Version);
        Assert.Equal(TestEvidence.Identity('6'), result.Plugin.ConfigurationIdentity);
        Assert.Equal(["DeliveryForge.Tests.csproj"], result.Plugin.Inputs);
        Assert.StartsWith("sha256:", result.Plugin.InputSetIdentity);
        Assert.Equal("INCOMPLETE", result.Plugin.PluginRawOutcome);

        var receiptBytes = await File.ReadAllBytesAsync(result.ReceiptPath, TestContext.Current.CancellationToken);
        var validatedReceipt = ContractValidator.ParseAndValidate(receiptBytes);
        Assert.Equal("1.0.0", validatedReceipt.SchemaVersion);
        Assert.Null(result.TrustedReceiptProvenance);
        using var receipt = System.Text.Json.JsonDocument.Parse(receiptBytes);
        using var command = System.Text.Json.JsonDocument.Parse(
            receipt.RootElement.GetProperty("command").GetString()!);
        Assert.False(command.RootElement.TryGetProperty("plugin", out _));
        Assert.False(command.RootElement.TryGetProperty("inheritEnvironment", out _));
        Assert.Equal(request.Invocation.WorkingDirectory,
            command.RootElement.GetProperty("workingDirectory").GetString());
        var expectedLegacyEnvelope = System.Text.Json.JsonSerializer.Serialize(new
        {
            request.Invocation.FileName,
            request.Invocation.Arguments,
            request.Invocation.WorkingDirectory,
            Environment = request.Invocation.Environment ?? new Dictionary<string, string>(),
            request.Scope,
            request.Capability.ExecutableIdentity,
            CapabilityTool = request.Capability.Tool,
            CapabilityVersion = request.Capability.Version,
            CapabilityFormat = request.Capability.FormatVersion,
            CapabilityExecutable = request.Capability.ExecutablePath,
            CapabilityFixture = request.Capability.Fixture,
            CapabilityProbeWorkingDirectory = request.Capability.ProbeWorkingDirectory,
            PolicySourceRevision = request.Policy.SourceRevision,
            PolicyContentIdentity = request.Policy.ContentIdentity,
            request.ConfigurationIdentity
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(expectedLegacyEnvelope, receipt.RootElement.GetProperty("command").GetString());
        Assert.Equal(TestEvidence.Repository().HeadCommit, receipt.RootElement.GetProperty("headCommit").GetString());
    }

    [Fact]
    public void Producer_built_future_v2_envelope_round_trips_through_authority_consumer()
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
        var inputIdentity = DeterministicEvaluationPluginRegistry.ComputeInputSetIdentity(
            ["DeliveryForge.Tests.csproj"]);
        var plugin = new EvaluationPluginReceiptMetadata(
            "delivery-forge.dotnet", "1.0.0", true, EvaluationPluginStatus.Executed,
            request.ConfigurationIdentity, request.ConfigurationIdentity,
            ["DeliveryForge.Tests.csproj"], inputIdentity, "PASS");
        var produced = GateRunner.BuildFutureV2Receipt(
            request, TestEvidence.Repository(), TestEvidence.CommandResult(),
            new Dictionary<string, string>(),
            new NormalizedEvidence(GateOutcome.Pass, "complete", false, true, []),
            sourceChanged: false, [], plugin);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(
            produced, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.True(GateReceiptAuthority.TryValidateAuthoritativeOutcome(
            json, out var reason, TrustedGatePluginRegistry.BuiltIns), reason);
        using var command = System.Text.Json.JsonDocument.Parse(
            json.GetProperty("command").GetString()!);
        Assert.Equal(request.ConfigurationIdentity,
            command.RootElement.GetProperty("plugin").GetProperty("runtimeClosureIdentity").GetString());
        Assert.Equal("PASS",
            command.RootElement.GetProperty("plugin").GetProperty("pluginRawOutcome").GetString());
        Assert.False(command.RootElement.GetProperty("plugin").TryGetProperty("toolOutcome", out _));
    }

    [Fact]
    public async Task Integrity_override_retains_only_explicit_plugin_raw_pass_metadata()
    {
        using var temp = new TempDirectory();
        var original = TestEvidence.GateRequest(temp.Path);
        var executable = original.Capability.ExecutablePath!;
        var plugin = new StubEvaluationPlugin(
            "host.custom", "2.0.0", "custom-tool",
            new NormalizedEvidence(GateOutcome.Pass, "raw pass", false, true, []));
        var registry = new TrustedEvaluationPluginRegistry(
            [new TrustedEvaluationPluginRegistration("custom", plugin, "3.0.0", "custom-v1")]);
        var request = original with
        {
            GateId = "custom",
            Adapter = EvidenceAdapterKind.External,
            Plugin = new EvaluationPluginSelection("host.custom", Enabled: true, Inputs: ["scope.custom"]),
            Capability = ToolCapability.FromDetection(
                "custom-tool", "3.0.0", "custom-v1", false, [], executable,
                original.Capability.ExecutableIdentity!, ["evaluate"], original.Invocation.WorkingDirectory),
            Invocation = original.Invocation with { Arguments = ["evaluate"] },
            Policy = new ResolvedEvidencePolicy(
                TestEvidence.Identity('1'), TestEvidence.Identity('2'), TestEvidence.Commit('a'),
                PolicyAuthorityKind.ProtectedGitBase, 0m, ["custom"], authorityVerified: true,
                sourceRepositoryRoot: original.Invocation.WorkingDirectory,
                requiredGatePluginBindings:
                [new RequiredGatePluginBinding("custom", "host.custom", "2.0.0", "custom-tool")])
        };
        var changed = TestEvidence.Repository() with { TreeId = TestEvidence.Commit('d') };
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), changed),
            original.Invocation.WorkingDirectory, TestEvidence.Commit('a'), registry);

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.Equal("PASS", result.Plugin.PluginRawOutcome);
        using var receipt = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(
            result.ReceiptPath, TestContext.Current.CancellationToken));
        using var envelope = System.Text.Json.JsonDocument.Parse(
            receipt.RootElement.GetProperty("command").GetString()!);
        Assert.False(envelope.RootElement.TryGetProperty("plugin", out _));
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
        var fixturePlugin = AntiSlopEvaluationPlugin.CreateBundledForFixture(
            System.IO.Path.GetDirectoryName(config)!, nodePath);
        var fixtureRegistry = new DeterministicEvaluationPluginRegistry([fixturePlugin]);
        var fixtureAuthority = new TrustedGatePluginRegistry(
        [
            new("test", fixturePlugin.Id, fixturePlugin.Version, fixturePlugin.ExpectedTool,
                "1.78.0", "oxlint-json-v1")
        ]);
        var runner = new GateRunner(
            new StubCommandExecutor(new CommandResult(0, oversized, oversized, false, false, now, now)),
            new ScopedSequenceRepositoryIdentityReader(
                new RepositoryEvaluationScope(["src/app.ts"], new HashSet<string>(StringComparer.Ordinal)),
                TestEvidence.Repository(), TestEvidence.Repository()),
            invocation.WorkingDirectory,
            TestEvidence.Commit('a'),
            plugins: fixtureRegistry,
            authorityRegistry: fixtureAuthority,
            hostOwnedClaimRoot: null);

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
    public async Task Non_fixture_required_gate_executes_even_without_separate_principal()
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
        Assert.Contains("TRX", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.Execution.ExitCode);
    }

    [Fact]
    public async Task Non_fixture_required_existing_adapter_preserves_fail()
    {
        using var temp = new TempDirectory();
        var original = TestEvidence.GateRequest(temp.Path);
        var request = original with
        {
            GateId = "coverage",
            Policy = new ResolvedEvidencePolicy(
                TestEvidence.Identity('1'), TestEvidence.Identity('2'), TestEvidence.Commit('a'),
                PolicyAuthorityKind.ProtectedGitBase, 0m, ["coverage"], authorityVerified: true,
                sourceRepositoryRoot: original.Invocation.WorkingDirectory,
                requiredGatePluginBindings:
                [new RequiredGatePluginBinding(
                    "coverage", "delivery-forge.crap4csharp", "1.0.0", "crap4csharp")]),
            Capability = ToolCapability.FromDetection(
                "crap4csharp", "1.2.0", "1.2", false, [],
                original.Capability.ExecutablePath!, original.Capability.ExecutableIdentity!, ["check"],
                original.Invocation.WorkingDirectory),
            Adapter = EvidenceAdapterKind.Crap4CSharp,
            Invocation = original.Invocation with { Arguments = ["check", "--format", "json"] },
            Plugin = new EvaluationPluginSelection("delivery-forge.crap4csharp", Enabled: true)
        };
        var execution = TestEvidence.CommandResult() with
        {
            ExitCode = 2,
            StandardOutput = TestEvidence.CrapReport(completed: true, decision: "fail", unknown: 0)
        };
        var runner = new GateRunner(
            new StubCommandExecutor(execution),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            original.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Fail, result.Outcome);
        Assert.Equal(2, result.Execution.ExitCode);
        Assert.False(result.ProductionCapable);
    }

    private sealed class StubEvaluationPlugin(
        string id,
        string version,
        string expectedTool,
        NormalizedEvidence? normalized = null)
        : IDeterministicEvaluationPlugin
    {
        public string Id => id;
        public string Version => version;
        public string ExpectedTool => expectedTool;
        public EvidenceAdapterKind CompatibilityAdapter => EvidenceAdapterKind.External;

        public ValueTask<EvaluationPluginSnapshot> CaptureBeforeAsync(
            GateRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(EvaluationPluginSnapshot.Empty);
        }

        public NormalizedEvidence Normalize(
            GateRequest request,
            CommandResult execution,
            EvaluationPluginSnapshot snapshot) =>
            normalized ?? NormalizedEvidence.Incomplete("stub");

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
            ["run-oxlint-batches.mjs"] = "// fixture batch runner",
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

    private static async Task<string> Git(string root, params string[] arguments)
    {
        var result = await new ProcessCommandExecutor().ExecuteAsync(
            new CommandInvocation("git", arguments, root,
                new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" }),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        return result.StandardOutput;
    }
}
