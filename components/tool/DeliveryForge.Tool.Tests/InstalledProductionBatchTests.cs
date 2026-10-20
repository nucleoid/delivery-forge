using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Packaging;

namespace DeliveryForge.Tool.Tests;

// Explicit real acceptance, never selected implicitly by unit/fixture validation.
// Reuses the existing independently pinned installed-fixture input mechanism.
public sealed class InstalledProductionBatchTests
{
    [Fact(Timeout = 1_300_000)]
    [Trait("Category", "InstalledLocalRealAcceptance")]
    [Trait("Acceptance", "InstalledProductionBatch")]
    public async Task Actual_two_node_installed_batch_releases_first_real_owner_without_closing_shared_WORK()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity(); using var original = new ToolLifetime(TimeSpan.FromSeconds(1200), TestContext.Current.CancellationToken);
        var path = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LOCAL_REAL_FIXTURE") ?? throw new IOException("Explicit real installed batch fixture required; no skip or simulated production owner.");
        var sha = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LOCAL_REAL_FIXTURE_SHA256") ?? throw new IOException("Independent exact installed batch fixture pin required.");
        var raw = BoundedFiles.Read(path, original, 65536); Assert.Equal(sha, BoundedFiles.Sha(raw));
        var fixture = JsonSerializer.Deserialize<InstalledLocalRealGoldenTests.Fixture>(raw, BoundedFiles.Json) ?? throw new IOException("Null installed batch fixture.");
        Assert.Equal("RootPreparedInstalledProductionBatchFixture-v1", fixture.Kind);
        _ = InstalledHostAdmission.ReadExact(fixture.Package, original, 64 * 1024 * 1024);
        _ = InstalledHostAdmission.ReadExact(fixture.Plan, original, 524288); _ = InstalledHostAdmission.ReadExact(fixture.Configuration, original, 65536);
        Assert.Equal(fixture.Package.Sha256, fixture.Installation.PackageSha256);
        var manifest = PackageLifecycle.Verify(fixture.Installation, original);
        var config = TrustedConfiguration.LoadSelected(fixture.Configuration.Path, original, fixture.Configuration);
        var batch = config.ProductionBatch ?? throw new IOException("Real production batch selection required.");
        var authorModel = fixture.ExpectedAuthorModel ?? throw new IOException("Root-pinned expected actual author model required.");
        var reviewerModel = fixture.ExpectedReviewerModel ?? throw new IOException("Root-pinned expected configured reviewer model required.");
        Assert.StartsWith("gpt-", authorModel); Assert.StartsWith("claude-", reviewerModel); Assert.NotEqual(authorModel, reviewerModel);
        Assert.Equal(2, batch.Nodes.Count);
        Assert.True(config.OperationSeconds <= Math.Floor(original.Remaining.TotalSeconds) - 60, "The complete child must fit the same already-running outer original.");
        foreach (var node in batch.Nodes)
        {
            var leaf = TrustedConfiguration.LoadSelected(node.Configuration.Path, original, node.Configuration);
            Assert.NotNull(leaf.Coordination); Assert.NotNull(leaf.InstalledQuality?.Registry); Assert.NotNull(leaf.InstalledReview?.IndependentReviewer);
            Assert.NotNull(leaf.LocalRealGolden);
            BoundedFiles.Separate(fixture.Installation.Prefix, leaf.LocalRealGolden.ArtifactRoot);
        }
        LinuxScopeSelection? scope = OperatingSystem.IsLinux()
            ? JsonSerializer.Deserialize<LinuxScopeSelection>(InstalledHostAdmission.ReadExact(fixture.LinuxScopeProfile ?? throw new IOException("Actual Linux scope required."), original, 65536), BoundedFiles.Json) : null;
        var executor = scope is null ? new ProcessCommandExecutor() : new ProcessCommandExecutor(scope, original);
        var actual = await executor.ExecuteBoundedCaptureAsync(new(manifest.RuntimeHost,
            [Path.Combine(fixture.Installation.Prefix, "payload", "DeliveryForge.Tool.dll"), "implement", "--config", fixture.Configuration.Path, "--plan", fixture.Plan.Path, "--json"],
            config.RepositoryRoot, ActualOrdinaryCapture.ResourceEnvironment()), original.Remaining, 1048576, original.Token, new OwnedWorkPhaseBudget(original));
        ActualOrdinaryCapture.Require(actual); Assert.Equal(0, actual.Command.ExitCode);
        using var returned = JsonDocument.Parse(actual.StandardOutputBytes);
        Assert.Equal("COORDINATED_BATCH_LOCAL_COMPLETE", returned.RootElement.GetProperty("Code").GetString());
        Assert.False(returned.RootElement.GetProperty("RuntimeCertified").GetBoolean());
        var runId = returned.RootElement.GetProperty("Data").GetProperty("RunId").GetString()!;
        var store = new RunStore(batch.StoreRoot); var recovered = await store.RecoverAsync(runId, original.Token);
        var observations = recovered.Records.Select(store.ReadPayload<BatchObservation>).ToArray();
        Assert.True(observations.Length >= 5);
        var firstRetired = observations.First(row => row.CompletedHeads.Count == 1);
        Assert.True(firstRetired.ObservedWorkRemaining > TimeSpan.Zero);
        Assert.Contains(observations.SkipWhile(row => !ReferenceEquals(row, firstRetired)).Skip(1), row => row.CompletedHeads.Count == 1 && row.Phases.Values.Contains("Selected"));
        Assert.All(observations, row => { Assert.Equal(observations[0].OriginalStarted, row.OriginalStarted); Assert.Equal(observations[0].OriginalWhole, row.OriginalWhole); Assert.Equal(observations[0].OriginalWork, row.OriginalWork); });
        Assert.Equal(2, observations[^1].CompletedHeads.Count); Assert.All(observations[^1].Phases.Values, phase => Assert.Equal("Pass", phase));
        foreach (var node in batch.Nodes)
        {
            var leaf = TrustedConfiguration.LoadSelected(node.Configuration.Path, original, node.Configuration);
            var root = JsonSerializer.Deserialize<StandaloneRootSelection>(InstalledHostAdmission.ReadExact(leaf.StandaloneRoot!, original, 65536), BoundedFiles.Json)!;
            var execution = new RunStore(root.StoreRoot);
            var runs = (await execution.DiscoverRunIdsAsync(original.Token)).Where(id => !id.EndsWith("-scheduler", StringComparison.Ordinal)).ToArray();
            var actualRunId = Assert.Single(runs);
            var history = await execution.RecoverAsync(actualRunId, original.Token);
            Assert.Contains(history.Records, record => record.Kind == "worker-prepared");
            var completion = execution.ReadPayload<DeliveryForge.Execution.Host.AgentCompletionReceipt>(Assert.Single(history.Records.Where(record => record.Kind == "worker-completed")));
            Assert.Equal(observations[^1].CompletedHeads[node.NodeId], completion.HeadCommit);
            var nodeFixture = fixture with
            {
                Configuration = node.Configuration, Plan = node.Plan,
                RunDirectory = Path.Combine(leaf.LocalRealGolden!.ArtifactRoot, actualRunId),
                ResultDirectory = root.WorkerPaths.ResultDirectory
            };
            await InstalledLocalRealGoldenTests.AssertActualCompletedChainAsync(nodeFixture, nodeFixture.RunDirectory, actualRunId, executor, original);
        }
        _ = PackageLifecycle.Verify(fixture.Installation, original); // Preserve prefix, journals and every failure/success artifact.
    }
}
