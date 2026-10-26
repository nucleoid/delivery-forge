using DeliveryForge.Contracts.Models;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;

namespace DeliveryForge.Tool.Tests;

// These are pure DATA/refusal controls. No approved issuer or native gate is faked.
// The actual positive remains InstalledLocalRealGoldenTests with independent owner pins.
public sealed class ProtectedQualityCompletionBoundaryTests
{
    sealed class Data
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "forge-protected-quality-" + Guid.NewGuid().ToString("N"));
        internal readonly RepositoryIdentity Expected = new(new('1', 40), new('2', 40), new('3', 40), true);
        internal InstalledQualitySelection Selection;
        internal InstalledBaselineComparison Comparison;
        internal readonly GateRunResult[] Gates;
        internal Data()
        {
            Directory.CreateDirectory(Root);
            var pin = new AdmissionFile(Path.Combine(Root, "profile-data.json"), 1, new('a', 64));
            var registry = new ProtectedBaselineRegistrySelection(new('4', 40), new('5', 40), "refs/heads/main",
                "quality/registry.json", "sha256:" + new string('6', 64), Path.Combine(Root, "historical-artifacts"),
                new(Path.Combine(Root, "unissued-owner-data.json"), 1, new('7', 64)));
            Selection = new("quality/policy.json", "sha256:" + new string('8', 64), Path.Combine(Root, "current-artifacts"),
                [new("mutation", "mutate4csharp", pin, "base", null, 600),
                 new("callables", "crap4csharp", pin, "all", "tests/Subject.Tests.csproj", 120)], registry);
            Comparison = new(GateOutcome.Pass, "DATA only", registry.RegistryCommit, Expected.BaseCommit,
                registry.RegistryIdentity, "sha256:" + new string('9', 64));
            Gates = Selection.Gates.Select(gate => new GateRunResult(GateOutcome.Pass, "DATA only", false, false, [], false,
                new("not-executable", [], Root), new(0, "", "", false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                Expected, Expected, Selection.PolicyIdentity, "DATA", "DATA", gate.Scope,
                new Dictionary<string, string>(), "unissued-receipt.json", null)).ToArray();
        }
        internal void Validate() => InstalledQualityHost.ValidateProtectedQualityTuple(Selection, Comparison, Expected, Gates);
    }

    [Fact]
    public async Task Coherent_advisory_flag_tuple_does_not_issue_private_completion_or_baseline_authority()
    {
        var data = new Data(); data.Validate();
        Assert.All(data.Gates, row => Assert.False(row.ProductionCapable));
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var config = new TrustedConfiguration("DATA/repository", data.Root, "refs/heads/main", "DATA", InstalledQuality: data.Selection);
        var configurationPin = new AdmissionFile(Path.Combine(data.Root, "unissued-config.json"), 1, new('b', 64));
        if (OperatingSystem.IsWindows())
        {
            var host = new InstalledQualityHost(config, configurationPin, original, new WorktreeManager());
            // Passing a pure tuple validator cannot populate the private actual-worker,
            // current-execution and closed-protected-issuer state required by this API.
            Assert.Null(await host.LocalRealEvidenceAsync(null!, null!));
            Assert.Null(await host.LocalRealEvidenceWithReviewAsync(null!, null!, null!));
            var git = new AdmissionFile(Path.Combine(data.Root, "unissued-git.exe"), 1, new('c', 64));
            var reviewSelection = new InstalledReviewSelection("quality/review.json", data.Selection.PolicyRelativePath,
                data.Selection.PolicyIdentity, git, "git version 2.50.0", Path.Combine(data.Root, "review-artifacts"));
            using var review = new InstalledReviewHost(config with { GitExecutable = git.Path, InstalledReview = reviewSelection },
                configurationPin, original, new ProcessCommandExecutor());
            // DATA-only quality completion cannot substitute for the review's
            // private retained source/output binding, even on the paired path.
            await Assert.ThrowsAsync<WorkerEnvelopeException>(() => review.LocalRealEvidenceForQualityAsync(null!, null!, host));
        }
        else
        {
            // Public DATA/refusal CI has no selected Linux native scope. Verify that
            // refusal instead of inventing a scope to reach the private completion API.
            Assert.Throws<PlatformNotSupportedException>(() =>
                new InstalledQualityHost(config, configurationPin, original, new WorktreeManager()));
        }
        Assert.False(Directory.Exists(data.Selection.ArtifactRoot));
        Assert.False(Directory.Exists(data.Selection.Registry!.ArtifactRoot));
    }

    public static bool Windows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(Windows), Skip = "No Linux native scope is invented for this object-binding DATA component.")]
    public async Task Golden_uses_actual_bound_configuration_after_independent_deserialization()
    {
        var data = new Data();
        var repository = Path.Combine(data.Root, "repository"); Directory.CreateDirectory(repository);
        var git = Path.Combine(data.Root, "inert-git.data"); File.WriteAllBytes(git, [1]);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var identity = "sha256:" + new string('a', 64); var contractIdentity = "sha256:" + new string('b', 64);
        var configPath = Path.Combine(data.Root, "config.json");
        var supplied = new TrustedConfiguration("fixture/config-binding", repository, "refs/heads/main", git,
            AdapterVersion: "object-binding-DATA", MaximumCeiling: "pr", OperationSeconds: 30,
            ExpectedBundleIdentity: identity, ExpectedContractIdentity: contractIdentity, ExpectedWorkItem: "#1", ExpectedPlanCeiling: "pr",
            InstalledQuality: data.Selection, LocalRealGolden: new(Path.Combine(data.Root, "golden")));
        File.WriteAllBytes(configPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(supplied, BoundedFiles.Json));
        var composition = TrustedConfiguration.LoadBound(configPath, original, out var pin);
        var bound = TrustedConfiguration.LoadSelected(configPath, original, pin);
        var equalButForeign = TrustedConfiguration.LoadSelected(configPath, original, pin);
        Assert.NotSame(composition, bound); Assert.NotSame(bound, equalButForeign);
        Assert.Equal(composition.Repository, bound.Repository);
        Assert.Equal(composition.ExpectedBundleIdentity, bound.ExpectedBundleIdentity);
        // Explicit test-only frozen-plan DATA. Reflection avoids minting or
        // pretending to have reader provenance/DownstreamReady/native ownership.
        // The actual Bind methods below consume only the exact identity/gate data.
        var constructor = Assert.Single(typeof(DeliveryForge.Planning.FrozenPlan).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        var canonical = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            plan = new { gates = data.Selection.Gates.Select(gate => new { id = gate.GateId }).ToArray() }
        });
        var frozen = Assert.IsType<DeliveryForge.Planning.FrozenPlan>(constructor.Invoke(new object?[]
        {
            identity, contractIdentity, "DATA", "DATA", "sha256:" + new string('c', 64), null,
            composition.Repository, "#1", repository, "refs/heads/main", false, false,
            data.Expected.BaseCommit, new string('d', 40), canonical, canonical, false,
            new[] { "Object-binding DATA only; no reader provenance, completion or runtime authority." }
        }));
        Assert.False(frozen.DownstreamReady);
        var worker = new FrozenWorkerPlan("run-" + new string('a', 32), contractIdentity, frozen.BaseCommit,
            composition.Repository, repository, Path.Combine(data.Root, "assigned"), Path.Combine(data.Root, "results"),
            "unissued-DATA-writer", ["source.cs"], [], "codex.cli", "object-binding-DATA",
            [AgentCapability.Launch, AgentCapability.CompletionEvidence], "pr");
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(data.Root, "store")), new WorktreeManager(), processes);
        var frame = new HostBridgeFrame(worker.RunId, original, coordinator, () => false, false)
        {
            Request = WorkerEnvelope.PrepareWorker(worker)
        };
        var quality = new InstalledQualityHost(composition, pin, original, new WorktreeManager());
        var golden = new LocalRealGoldenOwner(composition, pin, original);
        Assert.Throws<WorkerEnvelopeException>(() => golden.RequireQualityConfiguration(frame, quality));
        // Same order as the actual bridge composition: quality then golden.
        quality.Bind(frozen, worker, bound, original);
        golden.Bind(frozen, worker, bound, original);
        Assert.True(quality.MatchesRetained(frame, bound, original));
        Assert.False(quality.MatchesRetained(frame, composition, original));
        Assert.False(quality.MatchesRetained(frame, equalButForeign, original));
        golden.RequireQualityConfiguration(frame, quality);
        var foreignQuality = new InstalledQualityHost(composition, pin, original, new WorktreeManager());
        foreignQuality.Bind(frozen, worker, equalButForeign, original);
        Assert.Throws<WorkerEnvelopeException>(() => golden.RequireQualityConfiguration(frame, foreignQuality));
        var foreignFrame = new HostBridgeFrame("run-" + new string('b', 32), original, coordinator, () => false, false)
        {
            Request = frame.Request
        };
        Assert.Throws<WorkerEnvelopeException>(() => golden.RequireQualityConfiguration(foreignFrame, quality));
        Assert.False(frame.OwnerCompleted); Assert.Null(frame.Completion);
        Assert.Empty(frame.EnteredTasks); Assert.False(Directory.Exists(data.Selection.ArtifactRoot));
        Assert.False(Directory.Exists(supplied.LocalRealGolden!.ArtifactRoot));
    }

    [Theory]
    [InlineData("fixture")]
    [InlineData("no-baseline")]
    [InlineData("comparison-incomplete")]
    [InlineData("foreign-registry")]
    [InlineData("stale-baseline")]
    [InlineData("stale-policy")]
    [InlineData("stale-source")]
    [InlineData("source-changed")]
    [InlineData("scope-changed")]
    [InlineData("unknown-native")]
    [InlineData("cancelled")]
    [InlineData("nonzero")]
    public void Incomplete_or_foreign_tuple_refuses_even_when_public_production_flag_is_true(string change)
    {
        var data = new Data(); data.Validate();
        data.Gates[0] = data.Gates[0] with { ProductionCapable = true };
        switch (change)
        {
            case "fixture": data.Gates[0] = data.Gates[0] with { Fixture = true }; break;
            case "no-baseline": data.Selection = data.Selection with { Registry = null }; break;
            case "comparison-incomplete": data.Comparison = data.Comparison with { Outcome = GateOutcome.Incomplete }; break;
            case "foreign-registry": data.Comparison = data.Comparison with { RegistryIdentity = "sha256:" + new string('a', 64) }; break;
            case "stale-baseline": data.Comparison = data.Comparison with { BaselineSourceRevision = new('a', 40) }; break;
            case "stale-policy": data.Gates[0] = data.Gates[0] with { PolicyIdentity = "sha256:" + new string('a', 64) }; break;
            case "stale-source": data.Gates[0] = data.Gates[0] with { After = data.Expected with { HeadCommit = new('a', 40) } }; break;
            case "source-changed": data.Gates[0] = data.Gates[0] with { SourceChanged = true }; break;
            case "scope-changed": data.Gates[0] = data.Gates[0] with { Scope = "all" }; break;
            case "unknown-native": data.Gates[0] = data.Gates[0] with { Execution = data.Gates[0].Execution with { OwnedProcessQuiescent = false } }; break;
            case "cancelled": data.Gates[0] = data.Gates[0] with { Execution = data.Gates[0].Execution with { Cancelled = true } }; break;
            case "nonzero": data.Gates[0] = data.Gates[0] with { Execution = data.Gates[0].Execution with { ExitCode = 1 } }; break;
        }
        Assert.Throws<WorkerEnvelopeException>(data.Validate);
    }
}
